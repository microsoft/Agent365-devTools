// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System.CommandLine;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Blueprint subcommand - Creates agent blueprint (Entra ID application)
/// Required Permissions: Agent ID Developer role
/// COMPLETE IMPLEMENTATION of A365SetupRunner Phase 2 blueprint creation
/// </summary>
internal static class BlueprintSubcommand
{
    /// <summary>
    /// Validates blueprint prerequisites without performing any actions.
    /// </summary>
    public static Task<List<string>> ValidateAsync(
        Models.Agent365Config config,
        IAzureValidator azureValidator,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.ClientAppId))
        {
            errors.Add("clientAppId is required in configuration");
            errors.Add("Please configure a custom client app in your tenant with required permissions");
            errors.Add($"See {ConfigConstants.Agent365CliDocumentationUrl} for setup instructions");
        }

        return Task.FromResult(errors);
    }

    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        IAzureValidator azureValidator,
        AzureWebAppCreator webAppCreator,
        PlatformDetector platformDetector,
        IBotConfigurator botConfigurator,
        GraphApiService graphApiService)
    {
        var command = new Command("blueprint", 
            "Create agent blueprint (Entra ID application registration)\n" +
            "Minimum required permissions: Agent ID Developer role\n");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Configuration file path");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Show detailed output");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            description: "Show what would be done without executing");

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);

        command.SetHandler(async (config, verbose, dryRun) =>
        {
            var setupConfig = await configService.LoadAsync(config.FullName);

            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Create Agent Blueprint");
                logger.LogInformation("Would create Entra ID application:");
                logger.LogInformation("  - Display Name: {DisplayName}", setupConfig.AgentBlueprintDisplayName);
                logger.LogInformation("  - Tenant: {TenantId}", setupConfig.TenantId);
                logger.LogInformation("  - Would request admin consent for Graph and Connectivity APIs");
                return;
            }

            await CreateBlueprintImplementationAsync(
                setupConfig,
                config,
                executor,
                azureValidator,
                logger,
                false,
                false,
                configService,
                botConfigurator,
                platformDetector,
                graphApiService
                );

        }, configOption, verboseOption, dryRunOption);

        return command;
    }

    public static async Task<bool> CreateBlueprintImplementationAsync(
        Models.Agent365Config setupConfig,
        FileInfo config,
        CommandExecutor executor,
        IAzureValidator azureValidator,
        ILogger logger,
        bool skipInfrastructure,
        bool isSetupAll,
        IConfigService configService,
        IBotConfigurator botConfigurator,
        PlatformDetector platformDetector,
        GraphApiService graphApiService,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("");
        logger.LogInformation("==> Creating Agent Blueprint");

        // Validate Azure authentication
        if (!await azureValidator.ValidateAllAsync(setupConfig.SubscriptionId))
        {
            return false;
        }

        var generatedConfigPath = Path.Combine(
            config.DirectoryName ?? Environment.CurrentDirectory,
            "a365.generated.config.json");

        // Load existing generated config (for MSI Principal ID)
        JsonObject generatedConfig = new JsonObject();
        string? principalId = null;

        if (File.Exists(generatedConfigPath))
        {
            try
            {
                generatedConfig = JsonNode.Parse(await File.ReadAllTextAsync(generatedConfigPath))?.AsObject() ?? new JsonObject();

                if (generatedConfig.TryGetPropertyValue("managedIdentityPrincipalId", out var existingPrincipalId))
                {
                    principalId = existingPrincipalId?.GetValue<string>();
                    logger.LogInformation("Found existing Managed Identity Principal ID: {Id}", principalId ?? "(none)");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not load existing config: {Message}. Starting fresh.", ex.Message);
            }
        }
        else
        {
            logger.LogInformation("No existing configuration found - blueprint will be created without managed identity");
        }

        // Create required services
        var cleanLoggerFactory = LoggerFactoryHelper.CreateCleanLoggerFactory();
        var delegatedConsentService = new DelegatedConsentService(
            cleanLoggerFactory.CreateLogger<DelegatedConsentService>(),
            new GraphApiService(
                cleanLoggerFactory.CreateLogger<GraphApiService>(),
                executor));

        // Use DI-provided GraphApiService which already has MicrosoftGraphTokenProvider configured
        var graphService = graphApiService;

        // ========================================================================
        // Phase 2.1: Delegated Consent
        // ========================================================================

        logger.LogInformation("");
        logger.LogInformation("==> Creating Agent Blueprint");

        // CRITICAL: Grant AgentApplication.Create permission BEFORE creating blueprint
        // This replaces the PowerShell call to DelegatedAgentApplicationCreateConsent.ps1
        logger.LogInformation("");
        logger.LogInformation("==> Ensuring AgentApplication.Create Permission");
        logger.LogInformation("This permission is required to create Agent Blueprints");

        var consentResult = await EnsureDelegatedConsentWithRetriesAsync(
            delegatedConsentService,
            setupConfig.ClientAppId,
            setupConfig.TenantId,
            logger);

        if (!consentResult)
        {
            logger.LogError("Failed to ensure AgentApplication.Create permission after multiple attempts");
            return false;
        }

        // ========================================================================
        // Phase 2.2: Create Blueprint
        // ========================================================================

        logger.LogInformation("");
        logger.LogInformation("==> Creating Agent Blueprint Application");

        // Validate required config
        if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintDisplayName))
        {
            throw new InvalidOperationException("agentBlueprintDisplayName missing in configuration");
        }

        var useManagedIdentity = (setupConfig.NeedDeployment && !skipInfrastructure) || skipInfrastructure;

        var blueprintResult = await CreateAgentBlueprintAsync(
                logger,
                executor,
                graphService,
                setupConfig.TenantId,
                setupConfig.AgentBlueprintDisplayName,
                setupConfig.AgentIdentityDisplayName,
                principalId,
                useManagedIdentity,
                generatedConfig,
                setupConfig,
                cancellationToken);

        if (!blueprintResult.success)
        {
            logger.LogError("Failed to create agent blueprint");
            return false;
        }

        var blueprintAppId = blueprintResult.appId;
        var blueprintObjectId = blueprintResult.objectId;

        logger.LogInformation("Agent Blueprint Details:");
        logger.LogInformation("  - Display Name: {Name}", setupConfig.AgentBlueprintDisplayName);
        logger.LogInformation("  - App ID: {Id}", blueprintAppId);
        logger.LogInformation("  - Object ID: {Id}", blueprintObjectId);
        logger.LogInformation("  - Identifier URI: api://{Id}", blueprintAppId);

        // Convert to camelCase and save
        var camelCaseConfig = new JsonObject
        {
            ["managedIdentityPrincipalId"] = generatedConfig["managedIdentityPrincipalId"]?.DeepClone(),
            ["agentBlueprintId"] = blueprintAppId,
            ["agentBlueprintObjectId"] = blueprintObjectId,
            ["displayName"] = setupConfig.AgentBlueprintDisplayName,
            ["servicePrincipalId"] = blueprintResult.servicePrincipalId,
            ["identifierUri"] = $"api://{blueprintAppId}",
            ["tenantId"] = setupConfig.TenantId,
            ["resourceConsents"] = generatedConfig["resourceConsents"]?.DeepClone() ?? new JsonArray(),
        };

        await File.WriteAllTextAsync(generatedConfigPath, camelCaseConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        generatedConfig = camelCaseConfig;

        // ========================================================================
        // Phase 2.5: Create Client Secret (logging handled by method)
        // ========================================================================

        logger.LogInformation("");
        logger.LogInformation("==> Creating Client Secret for Agent Blueprint");

        await CreateBlueprintClientSecretAsync(
            blueprintObjectId!,
            blueprintAppId!,
            generatedConfig,
            generatedConfigPath,
            graphService,
            setupConfig,
            logger);

        logger.LogInformation("");
        logger.LogInformation("Agent blueprint created successfully");
        logger.LogInformation("Generated config saved: {Path}", generatedConfigPath);
        logger.LogInformation("");

        await RegisterEndpointAndSyncAsync(
            configPath: config.FullName,
            logger: logger,
            configService: configService,
            botConfigurator: botConfigurator,
            platformDetector: platformDetector);

        // Display verification info and summary
        await SetupHelpers.DisplayVerificationInfoAsync(config, logger);

        if (!isSetupAll)
        {
            logger.LogInformation("Next steps:");
            logger.LogInformation("  1. Run 'a365 setup permissions mcp' to configure MCP permissions");
            logger.LogInformation("  2. Run 'a365 setup permissions bot' to configure Bot API permissions");
        }

        return true;
    }

    /// <summary>
    /// Ensures AgentApplication.Create permission with retry logic
    /// Used by: BlueprintSubcommand and A365SetupRunner Phase 2.1
    /// </summary>
    public static async Task<bool> EnsureDelegatedConsentWithRetriesAsync(
        DelegatedConsentService delegatedConsentService,
        string clientAppId,
        string tenantId,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var retryHelper = new RetryHelper(logger);

        try
        {
            var success = await retryHelper.ExecuteWithRetryAsync(
                async ct =>
                {
                    return await delegatedConsentService.EnsureBlueprintPermissionGrantAsync(
                        clientAppId,
                        tenantId,
                        ct);
                },
                result => !result,
                maxRetries: 3,
                baseDelaySeconds: 5,
                cancellationToken);

            if (success)
            {
                logger.LogInformation("Successfully ensured delegated application consent");
                return true;
            }

            logger.LogWarning("Consent failed after retries");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during delegated consent: {Message}", ex.Message);
            logger.LogError("Common causes:");
            logger.LogError("  1. Insufficient permissions - You need Application.ReadWrite.All and DelegatedPermissionGrant.ReadWrite.All");
            logger.LogError("  2. Not a Global Administrator or similar privileged role");
            logger.LogError("  3. Azure CLI authentication expired - Run 'az login' and retry");
            logger.LogError("  4. Network connectivity issues");
            return false;
        }
    }

    /// <summary>
    /// Creates Agent Blueprint application using Graph API
    /// Used by: BlueprintSubcommand and A365SetupRunner Phase 2.2
    /// Returns: (success, appId, objectId, servicePrincipalId)
    /// </summary>
    public static async Task<(bool success, string? appId, string? objectId, string? servicePrincipalId)> CreateAgentBlueprintAsync(
        ILogger logger,
        CommandExecutor executor,
        GraphApiService graphApiService,
        string tenantId,
        string displayName,
        string? agentIdentityDisplayName,
        string? managedIdentityPrincipalId,
        bool useManagedIdentity,
        JsonObject generatedConfig,
        Models.Agent365Config setupConfig,
        CancellationToken ct)
    {
        try
        {
            logger.LogInformation("Creating Agent Blueprint using Microsoft Graph SDK...");

            using GraphServiceClient graphClient = await GetAuthenticatedGraphClientAsync(logger, setupConfig, tenantId, ct);

            // Get current user for sponsors field (mimics PowerShell script behavior)
            string? sponsorUserId = null;
            try
            {
                var me = await graphClient.Me.GetAsync(cancellationToken: ct);
                if (me != null && !string.IsNullOrEmpty(me.Id))
                {
                    sponsorUserId = me.Id;
                    logger.LogInformation("Current user: {DisplayName} <{UPN}>", me.DisplayName, me.UserPrincipalName);
                    logger.LogInformation("Sponsor: https://graph.microsoft.com/v1.0/users/{UserId}", sponsorUserId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not retrieve current user for sponsors field: {Message}", ex.Message);
            }

            // Define the application manifest with @odata.type for Agent Identity Blueprint
            var appManifest = new JsonObject
            {
                ["@odata.type"] = "Microsoft.Graph.AgentIdentityBlueprint", // CRITICAL: Required for Agent Blueprint type
                ["displayName"] = displayName,
                ["signInAudience"] = "AzureADMultipleOrgs" // Multi-tenant
            };

            // Add sponsors field if we have the current user (PowerShell script includes this)
            if (!string.IsNullOrEmpty(sponsorUserId))
            {
                appManifest["sponsors@odata.bind"] = new JsonArray
                {
                    $"https://graph.microsoft.com/v1.0/users/{sponsorUserId}"
                };
            }

            // Create the application using Microsoft Graph SDK
            using var httpClient = new HttpClient();
            var graphToken = await GetTokenFromGraphClient(logger, graphClient, tenantId, setupConfig.ClientAppId);
            if (string.IsNullOrEmpty(graphToken))
            {
                logger.LogError("Failed to extract access token from Graph client");
                return (false, null, null, null);
            }

            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);
            httpClient.DefaultRequestHeaders.Add("ConsistencyLevel", "eventual");
            httpClient.DefaultRequestHeaders.Add("OData-Version", "4.0"); // Required for @odata.type

            var createAppUrl = "https://graph.microsoft.com/beta/applications";

            logger.LogInformation("Creating Agent Blueprint application...");
            logger.LogInformation("  - Display Name: {DisplayName}", displayName);
            if (!string.IsNullOrEmpty(sponsorUserId))
            {
                logger.LogInformation("  - Sponsor: User ID {UserId}", sponsorUserId);
            }

            var appResponse = await httpClient.PostAsync(
                createAppUrl,
                new StringContent(appManifest.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct);

            if (!appResponse.IsSuccessStatusCode)
            {
                var errorContent = await appResponse.Content.ReadAsStringAsync(ct);

                // If sponsors field causes error (Bad Request 400), retry without it
                if (appResponse.StatusCode == System.Net.HttpStatusCode.BadRequest &&
                    !string.IsNullOrEmpty(sponsorUserId))
                {
                    logger.LogWarning("Agent Blueprint creation with sponsors failed (Bad Request). Retrying without sponsors...");

                    // Remove sponsors field and retry
                    appManifest.Remove("sponsors@odata.bind");

                    appResponse = await httpClient.PostAsync(
                        createAppUrl,
                        new StringContent(appManifest.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                        ct);

                    if (!appResponse.IsSuccessStatusCode)
                    {
                        errorContent = await appResponse.Content.ReadAsStringAsync(ct);
                        logger.LogError("Failed to create application (fallback): {Status} - {Error}", appResponse.StatusCode, errorContent);
                        return (false, null, null, null);
                    }
                }
                else
                {
                    logger.LogError("Failed to create application: {Status} - {Error}", appResponse.StatusCode, errorContent);
                    return (false, null, null, null);
                }
            }

            var appJson = await appResponse.Content.ReadAsStringAsync(ct);
            var app = JsonNode.Parse(appJson)!.AsObject();
            var appId = app["appId"]!.GetValue<string>();
            var objectId = app["id"]!.GetValue<string>();

            logger.LogInformation("Application created successfully");
            logger.LogInformation("  - App ID: {AppId}", appId);
            logger.LogInformation("  - Object ID: {ObjectId}", objectId);

            // Wait for application propagation
            const int maxRetries = 30;
            const int delayMs = 4000;
            bool appAvailable = false;
            for (int i = 0; i < maxRetries; i++)
            {
                var checkResp = await httpClient.GetAsync($"https://graph.microsoft.com/v1.0/applications/{objectId}", ct);
                if (checkResp.IsSuccessStatusCode)
                {
                    appAvailable = true;
                    break;
                }
                logger.LogInformation("Waiting for application object to be available in directory (attempt {Attempt}/{Max})...", i + 1, maxRetries);
                await Task.Delay(delayMs, ct);
            }

            if (!appAvailable)
            {
                logger.LogError("App object not available after creation. Aborting setup.");
                return (false, null, null, null);
            }

            // Update application with identifier URI
            var identifierUri = $"api://{appId}";
            var patchAppUrl = $"https://graph.microsoft.com/v1.0/applications/{objectId}";
            var patchBody = new JsonObject
            {
                ["identifierUris"] = new JsonArray { identifierUri }
            };

            var patchResponse = await httpClient.PatchAsync(
                patchAppUrl,
                new StringContent(patchBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct);

            if (!patchResponse.IsSuccessStatusCode)
            {
                var patchError = await patchResponse.Content.ReadAsStringAsync(ct);
                logger.LogInformation("Waiting for application propagation before setting identifier URI...");
                logger.LogDebug("Identifier URI update deferred (propagation delay): {Error}", patchError);
            }
            else
            {
                logger.LogInformation("Identifier URI set to: {Uri}", identifierUri);
            }

            // Create service principal
            logger.LogInformation("Creating service principal...");

            var spManifest = new JsonObject
            {
                ["appId"] = appId
            };

            var createSpUrl = "https://graph.microsoft.com/v1.0/servicePrincipals";
            var spResponse = await httpClient.PostAsync(
                createSpUrl,
                new StringContent(spManifest.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct);

            string? servicePrincipalId = null;
            if (spResponse.IsSuccessStatusCode)
            {
                var spJson = await spResponse.Content.ReadAsStringAsync(ct);
                var sp = JsonNode.Parse(spJson)!.AsObject();
                servicePrincipalId = sp["id"]!.GetValue<string>();
                logger.LogInformation("Service principal created: {SpId}", servicePrincipalId);
            }
            else
            {
                var spError = await spResponse.Content.ReadAsStringAsync(ct);
                logger.LogInformation("Waiting for application propagation before creating service principal...");
                logger.LogDebug("Service principal creation deferred (propagation delay): {Error}", spError);
            }

            // Wait for service principal propagation
            logger.LogInformation("Waiting 10 seconds to ensure Service Principal is fully propagated...");
            await Task.Delay(10000, ct);

            // Create Federated Identity Credential ONLY when MSI is relevant (if managed identity provided)
            if (useManagedIdentity && !string.IsNullOrWhiteSpace(managedIdentityPrincipalId))
            {
                logger.LogInformation("Creating Federated Identity Credential for Managed Identity...");
                var credentialName = $"{displayName.Replace(" ", "")}-MSI";

                var ficSuccess = await CreateFederatedIdentityCredentialAsync(
                    tenantId,
                    objectId,
                    credentialName,
                    managedIdentityPrincipalId,
                    graphToken,
                    logger,
                    ct);

                if (ficSuccess)
                {
                    logger.LogInformation("Federated Identity Credential created successfully");
                }
                else
                {
                    logger.LogWarning("Failed to create Federated Identity Credential");
                }
            }
            else if (!useManagedIdentity)
            {
                logger.LogInformation("Skipping Federated Identity Credential creation (external hosting / no MSI configured)");
            }
            else
            {
                logger.LogInformation("Skipping Federated Identity Credential creation (no MSI Principal ID provided)");
            }

            // Request admin consent
            logger.LogInformation("Requesting admin consent for application");

            // Get application scopes from config (fallback to hardcoded defaults)
            var applicationScopes = new List<string>();

            var appScopesFromConfig = setupConfig.AgentApplicationScopes;
            if (appScopesFromConfig != null && appScopesFromConfig.Count > 0)
            {
                logger.LogInformation("  Found 'agentApplicationScopes' in typed config");
                applicationScopes.AddRange(appScopesFromConfig);
            }
            else
            {
                logger.LogInformation("  'agentApplicationScopes' not found in config, using hardcoded defaults");
                applicationScopes.AddRange(ConfigConstants.DefaultAgentApplicationScopes);
            }

            // Final fallback (should not happen with proper defaults)
            if (applicationScopes.Count == 0)
            {
                logger.LogWarning("No application scopes available, falling back to User.Read");
                applicationScopes.Add("User.Read");
            }

            logger.LogInformation("  - Application scopes: {Scopes}", string.Join(", ", applicationScopes));

            // Generate consent URL for Graph API
            var applicationScopesJoined = string.Join(' ', applicationScopes);
            var consentUrlGraph = $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent?client_id={appId}&scope={Uri.EscapeDataString(applicationScopesJoined)}&redirect_uri=https://entra.microsoft.com/TokenAuthorize&state=xyz123";

            logger.LogInformation("Opening browser for Graph API admin consent...");
            TryOpenBrowser(consentUrlGraph);

            var consentSuccess = await AdminConsentHelper.PollAdminConsentAsync(executor, logger, appId, "Graph API Scopes", 180, 5, ct);

            if (consentSuccess)
            {
                logger.LogInformation("Graph API admin consent granted successfully!");
            }
            else
            {
                logger.LogWarning("Graph API admin consent may not have completed");
            }

            // Set inheritable permissions for Microsoft Graph so agent instances can access Graph on behalf of users
            if (consentSuccess)
            {
                logger.LogInformation("Configuring inheritable permissions for Microsoft Graph...");
                try
                {
                    // Update config with blueprint ID so EnsureResourcePermissionsAsync can use it
                    setupConfig.AgentBlueprintId = appId;

                    await SetupHelpers.EnsureResourcePermissionsAsync(
                        graph: graphApiService,
                        config: setupConfig,
                        resourceAppId: AuthenticationConstants.MicrosoftGraphResourceAppId,
                        resourceName: "Microsoft Graph",
                        scopes: applicationScopes.ToArray(),
                        logger: logger,
                        addToRequiredResourceAccess: false,
                        setInheritablePermissions: true,
                        setupResults: null,
                        ct: ct);

                    logger.LogInformation("Microsoft Graph inheritable permissions configured successfully");
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Failed to configure Microsoft Graph inheritable permissions: {Message}", ex.Message);
                    logger.LogWarning("Agent instances may not be able to access Microsoft Graph resources");
                    logger.LogWarning("You can configure these manually later with: a365 setup permissions");
                }
            }

            // Add Graph API consent to the resource consents collection
            var resourceConsents = new JsonArray();
            resourceConsents.Add(new JsonObject
            {
                ["resourceName"] = "Microsoft Graph",
                ["resourceAppId"] = "00000003-0000-0000-c000-000000000000",
                ["consentUrl"] = consentUrlGraph,
                ["consentGranted"] = consentSuccess,
                ["consentTimestamp"] = consentSuccess ? DateTime.UtcNow.ToString("O") : null,
                ["scopes"] = new JsonArray(applicationScopes.Select(s => JsonValue.Create(s)).ToArray())
            });

            generatedConfig["resourceConsents"] = resourceConsents;

            if (!consentSuccess)
            {
                logger.LogWarning("");
                logger.LogWarning("Admin consent may not have been detected");
                logger.LogWarning("The setup will continue, but you may need to grant consent manually.");
                logger.LogWarning("Consent URL: {Url}", consentUrlGraph);
            }

            return (true, appId, objectId, servicePrincipalId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create agent blueprint: {Message}", ex.Message);
            return (false, null, null, null);
        }
    }

    /// <summary>
    /// Extracts the access token from a GraphServiceClient for use in direct HTTP calls.
    /// This uses InteractiveBrowserCredential directly which is simpler and more reliable.
    /// </summary>
    private static async Task<string?> GetTokenFromGraphClient(ILogger logger, GraphServiceClient graphClient, string tenantId, string clientAppId)
    {
        try
        {
            // Use Azure.Identity to get the token directly
            // This is cleaner and more reliable than trying to extract it from GraphServiceClient
            var credential = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
            {
                TenantId = tenantId,
                ClientId = clientAppId
            });

            var tokenRequestContext = new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" });
            var token = await credential.GetTokenAsync(tokenRequestContext, CancellationToken.None);

            return token.Token;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get access token");
            return null;
        }
    }

    /// <summary>
    /// Creates and authenticates a GraphServiceClient using InteractiveGraphAuthService.
    /// This common method consolidates the authentication logic used across multiple methods.
    /// </summary>
    private async static Task<GraphServiceClient> GetAuthenticatedGraphClientAsync(ILogger logger, Models.Agent365Config setupConfig, string tenantId, CancellationToken ct)
    {
        logger.LogInformation("Authenticating to Microsoft Graph using interactive browser authentication...");
        logger.LogInformation("IMPORTANT: Agent Blueprint operations require Application.ReadWrite.All permission.");
        logger.LogInformation("This will open a browser window for interactive authentication.");
        logger.LogInformation("Please sign in with a Global Administrator account.");
        logger.LogInformation("");

        // Use InteractiveGraphAuthService to get proper authentication
        using var cleanLoggerFactory = LoggerFactoryHelper.CreateCleanLoggerFactory();
        var interactiveAuth = new InteractiveGraphAuthService(
            cleanLoggerFactory.CreateLogger<InteractiveGraphAuthService>(),
            setupConfig.ClientAppId);

        try
        {
            var graphClient = await interactiveAuth.GetAuthenticatedGraphClientAsync(tenantId, ct);
            logger.LogInformation("Successfully authenticated to Microsoft Graph");
            return graphClient;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to authenticate to Microsoft Graph: {Message}", ex.Message);
            logger.LogError("");
            logger.LogError("TROUBLESHOOTING:");
            logger.LogError("1. Ensure you are a Global Administrator or have Application.ReadWrite.All permission");
            logger.LogError("2. The account must have already consented to these permissions");
            logger.LogError("");
            throw new InvalidOperationException($"Microsoft Graph authentication failed: {ex.Message}", ex);
        }
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            using var p = new System.Diagnostics.Process();
            p.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            p.Start();
        }
        catch
        {
            // non-fatal
        }
    }

    /// <summary>
    /// Creates client secret for Agent Blueprint (Phase 2.5)
    /// Used by: BlueprintSubcommand and A365SetupRunner
    /// </summary>
    public static async Task CreateBlueprintClientSecretAsync(
        string blueprintObjectId,
        string blueprintAppId,
        JsonObject generatedConfig,
        string generatedConfigPath,
        GraphApiService graphService,
        Models.Agent365Config setupConfig,
        ILogger logger,
        CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("Creating client secret for Agent Blueprint using Graph API...");

            var graphToken = await graphService.GetGraphAccessTokenAsync(
                generatedConfig["tenantId"]?.GetValue<string>() ?? string.Empty, ct);

            if (string.IsNullOrWhiteSpace(graphToken))
            {
                logger.LogError("Failed to acquire Graph API access token");
                throw new InvalidOperationException("Cannot create client secret without Graph API token");
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);

            var secretBody = new JsonObject
            {
                ["passwordCredential"] = new JsonObject
                {
                    ["displayName"] = "Agent 365 CLI Generated Secret",
                    ["endDateTime"] = DateTime.UtcNow.AddYears(2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                }
            };

            var addPasswordUrl = $"https://graph.microsoft.com/v1.0/applications/{blueprintObjectId}/addPassword";
            var passwordResponse = await httpClient.PostAsync(
                addPasswordUrl,
                new StringContent(secretBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct);

            if (!passwordResponse.IsSuccessStatusCode)
            {
                var errorContent = await passwordResponse.Content.ReadAsStringAsync(ct);
                logger.LogError("Failed to create client secret: {Status} - {Error}", passwordResponse.StatusCode, errorContent);
                throw new InvalidOperationException($"Failed to create client secret: {errorContent}");
            }

            var passwordJson = await passwordResponse.Content.ReadAsStringAsync(ct);
            var passwordResult = JsonNode.Parse(passwordJson)!.AsObject();

            var secretTextNode = passwordResult["secretText"];
            if (secretTextNode == null || string.IsNullOrWhiteSpace(secretTextNode.GetValue<string>()))
            {
                logger.LogError("Client secret text is empty in response");
                throw new InvalidOperationException("Client secret creation returned empty secret");
            }

            var protectedSecret = ProtectSecret(secretTextNode.GetValue<string>(), logger);

            var isProtected = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            generatedConfig["agentBlueprintClientSecret"] = protectedSecret;
            generatedConfig["agentBlueprintClientSecretProtected"] = isProtected;
            setupConfig.AgentBlueprintClientSecret = protectedSecret;
            setupConfig.AgentBlueprintClientSecretProtected = isProtected;

            await File.WriteAllTextAsync(
                    generatedConfigPath,
                    generatedConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    ct);

            logger.LogInformation("Client secret created successfully!");
            logger.LogInformation($"  - Secret stored in generated config (encrypted: {isProtected})");
            logger.LogWarning("IMPORTANT: The client secret has been stored in {Path}", generatedConfigPath);
            logger.LogWarning("Keep this file secure and do not commit it to source control!");

            if (!isProtected)
            {
                logger.LogWarning("WARNING: Secret encryption is only available on Windows. The secret is stored in plaintext.");
                logger.LogWarning("Consider using environment variables or Azure Key Vault for production deployments.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create client secret: {Message}", ex.Message);
            logger.LogInformation("You can create a client secret manually:");
            logger.LogInformation("  1. Go to Azure Portal > App Registrations");
            logger.LogInformation("  2. Find your Agent Blueprint: {AppId}", blueprintAppId);
            logger.LogInformation("  3. Navigate to Certificates & secrets > Client secrets");
            logger.LogInformation("  4. Click 'New client secret' and save the value");
            logger.LogInformation("  5. Add it to {Path} as 'agentBlueprintClientSecret'", generatedConfigPath);
        }
    }

    /// <summary>
    /// Registers blueprint messaging endpoint and syncs project settings.
    /// Public method that can be called by AllSubcommand.
    /// </summary>
    public static async Task RegisterEndpointAndSyncAsync(
        string configPath,
        ILogger logger,
        IConfigService configService,
        IBotConfigurator botConfigurator,
        PlatformDetector platformDetector,
        CancellationToken cancellationToken = default)
    {
        var setupConfig = await configService.LoadAsync(configPath);

        if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
        {
            logger.LogError("Blueprint ID not found. Please confirm agent blueprint id is in config file.");
            Environment.Exit(1);
        }

        if (string.IsNullOrWhiteSpace(setupConfig.WebAppName))
        {
            logger.LogError("Web App Name not found. Run 'a365 setup infrastructure' first.");
            Environment.Exit(1);
        }

        logger.LogInformation("Registering blueprint messaging endpoint...");
        logger.LogInformation("");

        await SetupHelpers.RegisterBlueprintMessagingEndpointAsync(
            setupConfig, logger, botConfigurator);


        setupConfig.Completed = true;
        setupConfig.CompletedAt = DateTime.UtcNow;

        await configService.SaveStateAsync(setupConfig);

        logger.LogInformation("");
        logger.LogInformation("Blueprint messaging endpoint registered successfully");

        // Sync generated config to project settings (appsettings.json or .env)
        logger.LogInformation("");
        logger.LogInformation("Syncing configuration to project settings...");

        var configFileInfo = new FileInfo(configPath);
        var generatedConfigPath = Path.Combine(
            configFileInfo.DirectoryName ?? Environment.CurrentDirectory,
            "a365.generated.config.json");

        try
        {
            await ProjectSettingsSyncHelper.ExecuteAsync(
                a365ConfigPath: configPath,
                a365GeneratedPath: generatedConfigPath,
                configService: configService,
                platformDetector: platformDetector,
                logger: logger);

            logger.LogInformation("Configuration synced to project settings successfully");
        }
        catch (Exception syncEx)
        {
            logger.LogWarning(syncEx, "Project settings sync failed (non-blocking). Please sync settings manually if needed.");
        }
    }

    #region Private Helper Methods

    private static async Task<bool> CreateFederatedIdentityCredentialAsync(
        string tenantId,
        string blueprintObjectId,
        string credentialName,
        string msiPrincipalId,
        string graphToken,
        ILogger logger,
        CancellationToken ct)
    {
        const int maxRetries = 5;
        const int initialDelayMs = 2000;

        try
        {
            var federatedCredential = new JsonObject
            {
                ["name"] = credentialName,
                ["issuer"] = $"https://login.microsoftonline.com/{tenantId}/v2.0",
                ["subject"] = msiPrincipalId,
                ["audiences"] = new JsonArray { "api://AzureADTokenExchange" }
            };

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);
            httpClient.DefaultRequestHeaders.Add("ConsistencyLevel", "eventual");

            var urls = new []
            {
                $"https://graph.microsoft.com/beta/applications/{blueprintObjectId}/federatedIdentityCredentials",
                $"https://graph.microsoft.com/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/federatedIdentityCredentials"
            };

            string? lastError = null;

            foreach (var url in urls)
            {
                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    var response = await httpClient.PostAsync(
                        url,
                        new StringContent(federatedCredential.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                        ct);

                    if (response.IsSuccessStatusCode)
                    {
                        logger.LogInformation("  - Credential Name: {Name}", credentialName);
                        logger.LogInformation("  - Issuer: https://login.microsoftonline.com/{TenantId}/v2.0", tenantId);
                        logger.LogInformation("  - Subject (MSI Principal ID): {MsiId}", msiPrincipalId);
                        return true;
                    }

                    var error = await response.Content.ReadAsStringAsync(ct);
                    lastError = error;

                    if ((error.Contains("Request_ResourceNotFound") || error.Contains("does not exist")) && attempt < maxRetries)
                    {
                        var delayMs = initialDelayMs * (int)Math.Pow(2, attempt - 1);
                        logger.LogWarning("Application object not yet propagated (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}ms...",
                            attempt, maxRetries, delayMs);
                        await Task.Delay(delayMs, ct);
                        continue;
                    }

                    if (error.Contains("Agent Blueprints are not supported on the API version"))
                    {
                        logger.LogDebug("Standard endpoint not supported, trying Agent Blueprint-specific path...");
                        break;
                    }

                    logger.LogDebug("FIC creation failed with error: {Error}", error);
                    break;
                }
            }

            logger.LogDebug("Failed to create federated identity credential after trying all endpoints: {Error}", lastError);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception creating federated identity credential: {Message}", ex.Message);
            return false;
        }
    }

    private static string ProtectSecret(string plaintext, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return plaintext;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
                var protectedBytes = ProtectedData.Protect(
                    plaintextBytes,
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);

                return Convert.ToBase64String(protectedBytes);
            }
            else
            {
                logger.LogWarning("DPAPI encryption not available on this platform. Secret will be stored in plaintext.");
                return plaintext;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to encrypt secret, storing in plaintext: {Message}", ex.Message);
            return plaintext;
        }
    }

    #endregion
}
