// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
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
    private const string MicrosoftGraphCommandLineToolsAppId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        IAzureValidator azureValidator,
        AzureWebAppCreator webAppCreator,
        PlatformDetector platformDetector)
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
                false);

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
        var delegatedConsentService = new DelegatedConsentService(
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<DelegatedConsentService>(),
            new GraphApiService(
                LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<GraphApiService>(),
                executor));

        var interactiveAuth = new InteractiveGraphAuthService(
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<InteractiveGraphAuthService>());

        var graphService = new GraphApiService(
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<GraphApiService>(),
            executor);

        // ========================================================================
        // Phase 2.1: Delegated Consent
        // ========================================================================

        logger.LogInformation("");
        logger.LogInformation("==> [2/5] Creating Agent Blueprint");

        // CRITICAL: Grant AgentApplication.Create permission BEFORE creating blueprint
        // This replaces the PowerShell call to DelegatedAgentApplicationCreateConsent.ps1
        logger.LogInformation("");
        logger.LogInformation("==> [2.1/5] Ensuring AgentApplication.Create Permission");
        logger.LogInformation("This permission is required to create Agent Blueprints");

        var consentResult = await EnsureDelegatedConsentWithRetriesAsync(
            delegatedConsentService,
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
        logger.LogInformation("==> [2.2/5] Creating Agent Blueprint Application");

        // Validate required config
        if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintDisplayName))
        {
            throw new InvalidOperationException("agentBlueprintDisplayName missing in configuration");
        }

        if (setupConfig.NeedDeployment && skipInfrastructure)
        {
            logger.LogError("Invalid configuration: both needDeployment and blueprintOnly are true. This is not supported, as it may result in attempting to use a managed identity that was not created.");
            return false;
        }

        var useManagedIdentity = (setupConfig.NeedDeployment && !skipInfrastructure) || skipInfrastructure;

        var blueprintResult = await CreateAgentBlueprintAsync(
                logger,
                executor,
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
            ["tenantId"] = setupConfig.TenantId
        };

        await File.WriteAllTextAsync(generatedConfigPath, camelCaseConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        generatedConfig = camelCaseConfig;

        // ========================================================================
        // Phase 2.5: Create Client Secret (logging handled by method)
        // ========================================================================

        logger.LogInformation("");
        logger.LogInformation("==> [2.5/5] Creating Client Secret for Agent Blueprint");

        await CreateBlueprintClientSecretAsync(
            blueprintObjectId!,
            blueprintAppId!,
            generatedConfig,
            generatedConfigPath,
            graphService,
            logger);

        // Final summary
        logger.LogInformation("");
        logger.LogInformation("Agent blueprint created successfully");
        logger.LogInformation("Generated config saved: {Path}", generatedConfigPath);
        logger.LogInformation("");
        if (!isSetupAll)
        {
            logger.LogInformation("Next steps:");
            logger.LogInformation("  1. Run 'a365 setup permissions mcp' to configure MCP permissions");
            logger.LogInformation("  2. Run 'a365 setup permissions bot' to configure Bot API permissions");
            logger.LogInformation("  3. Run 'a365 setup endpoint' to register messaging endpoint");
        }

        return true;
    }

    /// <summary>
    /// Ensures AgentApplication.Create permission with retry logic (3 attempts, 5-second delays)
    /// Used by: BlueprintSubcommand and A365SetupRunner Phase 2.1
    /// </summary>
    public static async Task<bool> EnsureDelegatedConsentWithRetriesAsync(
        DelegatedConsentService delegatedConsentService,
        string tenantId,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        const int maxRetries = 3;
        const int retryDelaySeconds = 5;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (attempt > 1)
                {
                    logger.LogInformation("Retry attempt {Attempt} of {MaxRetries} for delegated consent", attempt, maxRetries);
                    await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), cancellationToken);
                }

                var success = await delegatedConsentService.EnsureAgentApplicationCreateConsentAsync(
                    MicrosoftGraphCommandLineToolsAppId,
                    tenantId,
                    cancellationToken);

                if (success)
                {
                    logger.LogInformation("Successfully ensured delegated application consent on attempt {Attempt}", attempt);
                    return true;
                }

                logger.LogWarning("Consent attempt {Attempt} returned false", attempt);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Consent attempt {Attempt} failed: {Message}", attempt, ex.Message);

                if (attempt == maxRetries)
                {
                    logger.LogError("All retry attempts exhausted for delegated consent");
                    logger.LogError("Common causes:");
                    logger.LogError("  1. Insufficient permissions - You need Application.ReadWrite.All and DelegatedPermissionGrant.ReadWrite.All");
                    logger.LogError("  2. Not a Global Administrator or similar privileged role");
                    logger.LogError("  3. Azure CLI authentication expired - Run 'az login' and retry");
                    logger.LogError("  4. Network connectivity issues");
                    return false;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Creates Agent Blueprint application using Graph API
    /// Used by: BlueprintSubcommand and A365SetupRunner Phase 2.2
    /// Returns: (success, appId, objectId, servicePrincipalId)
    /// </summary>
    public static async Task<(bool success, string? appId, string? objectId, string? servicePrincipalId)> CreateAgentBlueprintAsync(
        ILogger logger,
        CommandExecutor executor,
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

            GraphServiceClient graphClient;
            try
            {
                graphClient = await GetAuthenticatedGraphClientAsync(logger, tenantId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get authenticated Graph client: {Message}", ex.Message);
                return (false, null, null, null);
            }

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
            var graphToken = await GetTokenFromGraphClient(logger, graphClient, tenantId);
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

            // Generate consent URLs for Graph and Connectivity
            var applicationScopesJoined = string.Join(' ', applicationScopes);
            var consentUrlGraph = $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent?client_id={appId}&scope={Uri.EscapeDataString(applicationScopesJoined)}&redirect_uri=https://entra.microsoft.com/TokenAuthorize&state=xyz123";
            var consentUrlConnectivity = $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent?client_id={appId}&scope=0ddb742a-e7dc-4899-a31e-80e797ec7144/Connectivity.Connections.Read&redirect_uri=https://entra.microsoft.com/TokenAuthorize&state=xyz123";

            logger.LogInformation("Opening browser for Graph API admin consent...");
            TryOpenBrowser(consentUrlGraph);

            var consent1Success = await AdminConsentHelper.PollAdminConsentAsync(executor, logger, appId, "Graph API Scopes", 180, 5, ct);

            if (consent1Success)
            {
                logger.LogInformation("Graph API admin consent granted successfully!");
            }
            else
            {
                logger.LogWarning("Graph API admin consent may not have completed");
            }

            logger.LogInformation("");
            logger.LogInformation("Opening browser for Connectivity admin consent...");
            TryOpenBrowser(consentUrlConnectivity);

            var consent2Success = await AdminConsentHelper.PollAdminConsentAsync(executor, logger, appId, "Connectivity Scope", 180, 5, ct);

            if (consent2Success)
            {
                logger.LogInformation("Connectivity admin consent granted successfully!");
            }
            else
            {
                logger.LogWarning("Connectivity admin consent may not have completed");
            }

            // Save consent URLs and status to generated config
            generatedConfig["consentUrlGraph"] = consentUrlGraph;
            generatedConfig["consentUrlConnectivity"] = consentUrlConnectivity;
            generatedConfig["consent1Granted"] = consent1Success;
            generatedConfig["consent2Granted"] = consent2Success;

            if (!consent1Success || !consent2Success)
            {
                logger.LogWarning("");
                logger.LogWarning("One or more consents may not have been detected");
                logger.LogWarning("The setup will continue, but you may need to grant consent manually.");
                logger.LogWarning("Consent URL (Graph): {Url}", consentUrlGraph);
                logger.LogWarning("Consent URL (Connectivity): {Url}", consentUrlConnectivity);
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
    private static async Task<string?> GetTokenFromGraphClient(ILogger logger, GraphServiceClient graphClient, string tenantId)
    {
        try
        {
            // Use Azure.Identity to get the token directly
            // This is cleaner and more reliable than trying to extract it from GraphServiceClient
            var credential = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
            {
                TenantId = tenantId,
                ClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e" // Microsoft Graph PowerShell app ID
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
    private async static Task<GraphServiceClient> GetAuthenticatedGraphClientAsync(ILogger logger,string tenantId, CancellationToken ct)
    {
        logger.LogInformation("Authenticating to Microsoft Graph using interactive browser authentication...");
        logger.LogInformation("IMPORTANT: Agent Blueprint operations require Application.ReadWrite.All permission.");
        logger.LogInformation("This will open a browser window for interactive authentication.");
        logger.LogInformation("Please sign in with a Global Administrator account.");
        logger.LogInformation("");

        // Use InteractiveGraphAuthService to get proper authentication
        var interactiveAuth = new InteractiveGraphAuthService(
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<InteractiveGraphAuthService>());

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

            generatedConfig["agentBlueprintClientSecret"] = protectedSecret;
            generatedConfig["agentBlueprintClientSecretProtected"] = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            await File.WriteAllTextAsync(
                generatedConfigPath,
                generatedConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                ct);

            logger.LogInformation("Client secret created successfully!");
            logger.LogInformation("  - Secret stored in generated config (encrypted: {IsProtected})", RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
            logger.LogWarning("IMPORTANT: The client secret has been stored in {Path}", generatedConfigPath);
            logger.LogWarning("Keep this file secure and do not commit it to source control!");

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
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

    #region Private Helper Methods

    private static async Task WaitForApplicationPropagationAsync(HttpClient httpClient, string objectId, ILogger logger, CancellationToken ct)
    {
        const int maxRetries = 30;
        const int delayMs = 4000;

        for (int i = 0; i < maxRetries; i++)
        {
            var checkResp = await httpClient.GetAsync($"https://graph.microsoft.com/v1.0/applications/{objectId}", ct);
            if (checkResp.IsSuccessStatusCode)
            {
                return;
            }
            logger.LogInformation("Waiting for application object to be available in directory (attempt {Attempt}/{Max})...", i + 1, maxRetries);
            await Task.Delay(delayMs, ct);
        }

        logger.LogError("App object not available after creation. Aborting setup.");
        throw new InvalidOperationException("Application object did not propagate in Azure AD");
    }

    private static async Task SetIdentifierUriAsync(HttpClient httpClient, string objectId, string appId, ILogger logger, CancellationToken ct)
    {
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
    }

    private static async Task<string?> CreateServicePrincipalAsync(HttpClient httpClient, string appId, ILogger logger, CancellationToken ct)
    {
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

        if (spResponse.IsSuccessStatusCode)
        {
            var spJson = await spResponse.Content.ReadAsStringAsync(ct);
            var sp = JsonNode.Parse(spJson)!.AsObject();
            var servicePrincipalId = sp["id"]!.GetValue<string>();
            logger.LogInformation("Service principal created: {SpId}", servicePrincipalId);
            return servicePrincipalId;
        }
        else
        {
            var spError = await spResponse.Content.ReadAsStringAsync(ct);
            logger.LogInformation("Waiting for application propagation before creating service principal...");
            logger.LogDebug("Service principal creation deferred (propagation delay): {Error}", spError);
            return null;
        }
    }

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

    private static async Task RequestAdminConsentAsync(
        string tenantId,
        string appId,
        JsonObject setupConfig,
        JsonObject generatedConfig,
        ILogger logger,
        CommandExecutor executor,
        CancellationToken ct)
    {
        logger.LogInformation("Requesting admin consent for application");

        // Get application scopes from config
        var applicationScopes = new List<string>();
        if (setupConfig.TryGetPropertyValue("agentApplicationScopes", out var appScopesNode) &&
            appScopesNode is JsonArray appScopesArr)
        {
            logger.LogInformation("  Found 'agentApplicationScopes' in config");
            foreach (var scopeItem in appScopesArr)
            {
                var scope = scopeItem?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    applicationScopes.Add(scope);
                }
            }
        }
        else
        {
            logger.LogInformation("  'agentApplicationScopes' not found in config, using hardcoded defaults");
            applicationScopes.AddRange(ConfigConstants.DefaultAgentApplicationScopes);
        }

        if (applicationScopes.Count == 0)
        {
            logger.LogWarning("No application scopes available, falling back to User.Read");
            applicationScopes.Add("User.Read");
        }

        logger.LogInformation("  - Application scopes: {Scopes}", string.Join(", ", applicationScopes));

        // Generate consent URLs
        var applicationScopesJoined = string.Join(' ', applicationScopes);
        var consentUrlGraph = $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent?client_id={appId}&scope={Uri.EscapeDataString(applicationScopesJoined)}&redirect_uri=https://entra.microsoft.com/TokenAuthorize&state=xyz123";
        var consentUrlConnectivity = $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent?client_id={appId}&scope=0ddb742a-e7dc-4899-a31e-80e797ec7144/Connectivity.Connections.Read&redirect_uri=https://entra.microsoft.com/TokenAuthorize&state=xyz123";

        // Request Graph consent
        logger.LogInformation("Opening browser for Graph API admin consent...");
        TryOpenBrowser(consentUrlGraph, logger);

        var consent1Success = await AdminConsentHelper.PollAdminConsentAsync(executor, logger, appId, "Graph API Scopes", 180, 5, ct);

        if (consent1Success)
        {
            logger.LogInformation("Graph API admin consent granted successfully!");
        }
        else
        {
            logger.LogWarning("Graph API admin consent may not have completed");
        }

        // Request Connectivity consent
        logger.LogInformation("");
        logger.LogInformation("Opening browser for Connectivity admin consent...");
        TryOpenBrowser(consentUrlConnectivity, logger);

        var consent2Success = await AdminConsentHelper.PollAdminConsentAsync(executor, logger, appId, "Connectivity Scope", 180, 5, ct);

        if (consent2Success)
        {
            logger.LogInformation("Connectivity admin consent granted successfully!");
        }
        else
        {
            logger.LogWarning("Connectivity admin consent may not have completed");
        }

        // Save consent URLs and status
        generatedConfig["consentUrlGraph"] = consentUrlGraph;
        generatedConfig["consentUrlConnectivity"] = consentUrlConnectivity;
        generatedConfig["consent1Granted"] = consent1Success;
        generatedConfig["consent2Granted"] = consent2Success;

        if (!consent1Success || !consent2Success)
        {
            logger.LogWarning("");
            logger.LogWarning("One or more consents may not have been detected");
            logger.LogWarning("The setup will continue, but you may need to grant consent manually.");
            logger.LogWarning("Consent URL (Graph): {Url}", consentUrlGraph);
            logger.LogWarning("Consent URL (Connectivity): {Url}", consentUrlConnectivity);
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

    private static async Task<string?> GetTokenFromGraphClientAsync(string tenantId)
    {
        try
        {
            var credential = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
            {
                TenantId = tenantId,
                ClientId = MicrosoftGraphCommandLineToolsAppId
            });

            var tokenRequestContext = new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" });
            var token = await credential.GetTokenAsync(tokenRequestContext, CancellationToken.None);

            return token.Token;
        }
        catch
        {
            return null;
        }
    }

    private static void TryOpenBrowser(string url, ILogger logger)
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
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to open browser for URL: {Url}", url);
        }
    }

    #endregion
}
