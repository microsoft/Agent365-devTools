// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System.CommandLine;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Result of blueprint creation including endpoint registration status
/// </summary>
internal class BlueprintCreationResult
{
    public bool BlueprintCreated { get; set; }
    public bool BlueprintAlreadyExisted { get; set; }
    public bool EndpointRegistered { get; set; }
    public bool EndpointAlreadyExisted { get; set; }
    /// <summary>
    /// Indicates whether endpoint registration was attempted (vs. skipped via --no-endpoint or missing config)
    /// </summary>
    public bool EndpointRegistrationAttempted { get; set; }

    /// <summary>
    /// The reason endpoint registration failed, when EndpointRegistered is false and EndpointRegistrationAttempted is true.
    /// Null if registration succeeded or was not attempted.
    /// </summary>
    public string? EndpointRegistrationFailureReason { get; set; }

    /// <summary>
    /// Indicates whether Graph admin consent (OAuth2 permissions) was granted.
    /// </summary>
    public bool GraphPermissionsConfigured { get; set; }
    /// <summary>
    /// Indicates whether Graph inheritable permissions failed to be configured.
    /// This is critical for agent token exchange functionality.
    /// </summary>
    public bool GraphInheritablePermissionsFailed { get; set; }
    /// <summary>
    /// Error message when Graph inheritable permissions fail.
    /// </summary>
    public string? GraphInheritablePermissionsError { get; set; }

    /// <summary>
    /// True when the client secret could not be created automatically (e.g. Forbidden) and
    /// the user must create it manually and re-run setup. The summary should surface this as
    /// an Action Required item.
    /// </summary>
    public bool ClientSecretManualActionRequired { get; set; }

    /// <summary>
    /// Indicates whether the Federated Identity Credential was successfully configured.
    /// When false and MSI was expected, agent token exchange will not work at runtime.
    /// </summary>
    public bool FederatedCredentialConfigured { get; set; }

    /// <summary>
    /// Error message when Federated Identity Credential configuration fails.
    /// </summary>
    public string? FederatedCredentialError { get; set; }

    /// <summary>
    /// The admin consent URL when consent was not granted because the current user lacks an admin role.
    /// Non-null indicates a tenant administrator must complete consent at this URL.
    /// </summary>
    public string? AdminConsentUrl { get; set; }
}

/// <summary>
/// Blueprint subcommand - Creates agent blueprint (Entra ID application)
/// Required Permissions: Agent ID Developer role
/// COMPLETE IMPLEMENTATION of A365SetupRunner Phase 2 blueprint creation
/// </summary>
internal static class BlueprintSubcommand
{
    // Client secret validation constants
    private const int ClientSecretValidationMaxRetries = 2;

    /// <summary>
    /// Returns the requirement checks for <c>setup blueprint</c>.
    /// Composes SetupCommand base checks + Location + ClientApp.
    /// </summary>
    public static List<Services.Requirements.IRequirementCheck> GetChecks(
        AzureAuthValidator auth,
        IClientAppValidator clientAppValidator)
    {
        var checks = new List<Services.Requirements.IRequirementCheck>(SetupCommand.GetBaseChecks(auth))
        {
            new ClientAppRequirementCheck(clientAppValidator),
        };

        return checks;
    }
    private const int ClientSecretValidationRetryDelayMs = 1000;
    private const int ClientSecretValidationTimeoutSeconds = 10;
    private const string MicrosoftLoginOAuthTokenEndpoint = "https://login.microsoftonline.com/{0}/oauth2/v2.0/token";

    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        AzureAuthValidator authValidator,
        PlatformDetector platformDetector,
        IBotConfigurator botConfigurator,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        IClientAppValidator clientAppValidator,
        BlueprintLookupService blueprintLookupService,
        FederatedCredentialService federatedCredentialService)
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

        var skipEndpointRegistrationOption = new Option<bool>(
            "--no-endpoint",
            description: "Do not register messaging endpoint (blueprint only)");

        var endpointOnlyOption = new Option<bool>(
            "--endpoint-only",
            description: "Register messaging endpoint only (requires existing blueprint)");

        var updateEndpointOption = new Option<string?>(
            "--update-endpoint",
            description: "Delete the existing messaging endpoint and register a new one with the specified URL");

        var skipRequirementsOption = new Option<bool>(
            "--skip-requirements",
            description: "Skip requirements validation check\n" +
                        "Use with caution: setup may fail if prerequisites are not met");

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(skipEndpointRegistrationOption);
        command.AddOption(endpointOnlyOption);
        command.AddOption(updateEndpointOption);
        command.AddOption(skipRequirementsOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var config = context.ParseResult.GetValueForOption(configOption)!;
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var skipEndpointRegistration = context.ParseResult.GetValueForOption(skipEndpointRegistrationOption);
            var endpointOnly = context.ParseResult.GetValueForOption(endpointOnlyOption);
            var updateEndpoint = context.ParseResult.GetValueForOption(updateEndpointOption);
            var skipRequirements = context.ParseResult.GetValueForOption(skipRequirementsOption);
            var ct = context.GetCancellationToken();

            // Generate correlation ID at workflow entry point
            var correlationId = HttpClientFactory.GenerateCorrelationId();
            logger.LogDebug("Starting blueprint setup (CorrelationId: {CorrelationId})", correlationId);

            // Validate mutually exclusive options
            if (!ValidateMutuallyExclusiveOptions(
                updateEndpoint: updateEndpoint,
                endpointOnly: endpointOnly,
                skipEndpointRegistration: skipEndpointRegistration,
                logger: logger))
            {
                Environment.Exit(1);
            }

            var setupConfig = await configService.LoadAsync(config.FullName);

            // Configure GraphApiService with custom client app ID if available
            // This ensures inheritable permissions operations use the validated custom app
            if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
            {
                graphApiService.CustomClientAppId = setupConfig.ClientAppId;
            }

            // Wire the sovereign/government cloud base URL from config so all Graph calls
            // target the correct national cloud endpoint (commercial by default).
            graphApiService.GraphBaseUrl = setupConfig.GraphBaseUrl;

            // Handle --update-endpoint flag
            if (!string.IsNullOrWhiteSpace(updateEndpoint))
            {
                logger.LogInformation("Endpoint registration via the CLI is not supported for blueprint-based agents.");
                logger.LogInformation("Configure the messaging endpoint directly in the Teams Developer Portal:");
                logger.LogInformation("  https://learn.microsoft.com/microsoft-agent-365/developer/create-instance#1-configure-agent-in-teams-developer-portal");
                return;
            }

            // Run all requirements checks: system checks (PowerShell modules, Frontier Preview)
            // and config checks (Location, ClientApp — includes isFallbackPublicClient auto-fix
            // required for device code auth on macOS/Linux/WSL).
            // Skip when dryRun is true: ClientAppRequirementCheck can mutate the app registration
            // (e.g., set isFallbackPublicClient), which violates dry-run semantics.
            if (!skipRequirements && !dryRun)
            {
                try
                {
                    var checks = BlueprintSubcommand.GetChecks(authValidator, clientAppValidator);
                    await RequirementsSubcommand.RunChecksOrExitAsync(
                        checks, setupConfig, logger, ct);
                }
                catch (Exception reqEx) when (reqEx is not OperationCanceledException && reqEx is not CleanExitException)
                {
                    logger.LogError("Requirements check failed: {Message}", reqEx.Message);
                    logger.LogDebug(reqEx, "Requirements check exception details");
                    logger.LogInformation("To bypass requirement validation, rerun with --skip-requirements.");
                    ExceptionHandler.ExitWithCleanup(1);
                }
            }

            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Create Agent Blueprint");
                logger.LogInformation("Would create Entra ID application:");
                logger.LogInformation("  - Display Name: {DisplayName}", setupConfig.AgentBlueprintDisplayName);
                logger.LogInformation("  - Tenant: {TenantId}", setupConfig.TenantId);
                logger.LogInformation("  - Would request admin consent for Graph and Connectivity APIs");
                if (!skipEndpointRegistration)
                {
                    logger.LogInformation("  - Would register messaging endpoint");
                }
                return;
            }

            logger.LogInformation("Starting blueprint setup... (TraceId: {TraceId})", correlationId);

            // Handle --endpoint-only flag
            if (endpointOnly)
            {
                logger.LogInformation("Endpoint registration via the CLI is not supported for blueprint-based agents.");
                logger.LogInformation("Configure the messaging endpoint directly in the Teams Developer Portal:");
                logger.LogInformation("  https://learn.microsoft.com/microsoft-agent-365/developer/create-instance#1-configure-agent-in-teams-developer-portal");
                return;
            }

            // Normal blueprint creation (with optional endpoint skipping)
            await CreateBlueprintImplementationAsync(
                setupConfig,
                config,
                executor,
                authValidator,
                logger,
                false,
                false,
                configService,
                botConfigurator,
                platformDetector,
                graphApiService,
                blueprintService,
                blueprintLookupService,
                federatedCredentialService,
                skipEndpointRegistration,
                correlationId: correlationId
                );

        });

        return command;
    }

    /// <summary>
    /// Validates that mutually exclusive command options are not used together.
    /// </summary>
    /// <returns>True if validation passes, false if conflicting options are detected.</returns>
    internal static bool ValidateMutuallyExclusiveOptions(
        string? updateEndpoint,
        bool endpointOnly,
        bool skipEndpointRegistration,
        ILogger logger)
    {
        var hasUpdateEndpoint = !string.IsNullOrWhiteSpace(updateEndpoint);

        // --update-endpoint cannot be used with --endpoint-only or --no-endpoint
        if (hasUpdateEndpoint)
        {
            if (endpointOnly)
            {
                logger.LogError("Options --update-endpoint and --endpoint-only cannot be used together.");
                logger.LogError("Use --update-endpoint if the endpoint URL needs to be updated, otherwise use --endpoint-only to register a new endpoint.");
                return false;
            }
            if (skipEndpointRegistration)
            {
                logger.LogError("Options --update-endpoint and --no-endpoint cannot be used together.");
                logger.LogError("--update-endpoint updates an endpoint, which conflicts with --no-endpoint.");
                return false;
            }
        }

        // --endpoint-only cannot be used with --no-endpoint
        if (endpointOnly && skipEndpointRegistration)
        {
            logger.LogError("Options --endpoint-only and --no-endpoint cannot be used together.");
            logger.LogError("--endpoint-only registers an endpoint, which conflicts with --no-endpoint.");
            return false;
        }

        return true;
    }

    public static async Task<BlueprintCreationResult> CreateBlueprintImplementationAsync(
        Models.Agent365Config setupConfig,
        FileInfo config,
        CommandExecutor executor,
        AzureAuthValidator authValidator,
        ILogger logger,
        bool skipInfrastructure,
        bool isSetupAll,
        IConfigService configService,
        IBotConfigurator botConfigurator,
        PlatformDetector platformDetector,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        BlueprintLookupService blueprintLookupService,
        FederatedCredentialService federatedCredentialService,
        bool skipEndpointRegistration = false,
        string? correlationId = null,
        CancellationToken cancellationToken = default,
        BlueprintCreationOptions? options = null,
        Func<Task<string?>>? loginHintResolver = null)
    {
        logger.LogInformation("");
        logger.LogInformation("Creating agent blueprint...");

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
                    logger.LogDebug("Found existing Managed Identity Principal ID: {Id}", principalId ?? "(none)");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not load existing config: {Message}. Starting fresh.", ex.Message);
            }
        }
        else
        {
            logger.LogDebug("No existing configuration found - blueprint will be created without managed identity");
        }

        using var blueprintOuterScope = logger.Indent();

        // Create required services.
        // Pass the caller's logger so consent messages appear in the correct indent scope.
        var cleanLoggerFactory = LoggerFactoryHelper.CreateCleanLoggerFactory();
        var delegatedConsentService = new DelegatedConsentService(
            logger,
            new GraphApiService(
                cleanLoggerFactory.CreateLogger<GraphApiService>(),
                executor,
                new AuthenticationService(cleanLoggerFactory.CreateLogger<AuthenticationService>()),
                graphBaseUrl: setupConfig.GraphBaseUrl));

        // Use DI-provided GraphApiService which already has MicrosoftGraphTokenProvider configured
        var graphService = graphApiService;

        // ========================================================================
        // Phase 2.1: Delegated Consent
        // ========================================================================

        // CRITICAL: Grant AgentApplication.Create permission BEFORE creating blueprint
        // This replaces the PowerShell call to DelegatedAgentApplicationCreateConsent.ps1
        logger.LogDebug("Ensuring AgentApplication.Create permission");

        var consentResult = await EnsureDelegatedConsentWithRetriesAsync(
            delegatedConsentService,
            setupConfig.ClientAppId,
            setupConfig.TenantId,
            logger,
            correlationId: correlationId);

        if (!consentResult)
        {
            logger.LogError("Failed to ensure AgentApplication.Create permission after multiple attempts");
            return new BlueprintCreationResult 
            { 
                BlueprintCreated = false, 
                EndpointRegistered = false, 
                EndpointRegistrationAttempted = false 
            };
        }

        // ========================================================================
        // Phase 2.2: Create Blueprint
        // ========================================================================

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
                blueprintService,
                blueprintLookupService,
                federatedCredentialService,
                setupConfig.TenantId,
                setupConfig.AgentBlueprintDisplayName,
                setupConfig.AgentIdentityDisplayName,
                principalId,
                useManagedIdentity,
                generatedConfig,
                setupConfig,
                configService,
                config,
                cancellationToken,
                options,
                loginHintResolver: loginHintResolver);

        if (!blueprintResult.success)
        {
            return new BlueprintCreationResult
            {
                BlueprintCreated = false,
                EndpointRegistered = false,
                EndpointRegistrationAttempted = false
            };
        }

        var blueprintAppId = blueprintResult.appId;
        var blueprintObjectId = blueprintResult.objectId;
        var blueprintAlreadyExisted = blueprintResult.alreadyExisted;

        logger.LogDebug("Blueprint created: {Name} (Object ID: {ObjectId}, App ID: {AppId})",
            setupConfig.AgentBlueprintDisplayName, blueprintObjectId, blueprintAppId);

        // Update generated config with blueprint details, preserving all existing fields
        generatedConfig["agentBlueprintId"] = blueprintAppId;
        generatedConfig["agentBlueprintObjectId"] = blueprintObjectId;
        generatedConfig["agentBlueprintServicePrincipalObjectId"] = blueprintResult.servicePrincipalId;
        if (generatedConfig["resourceConsents"] == null)
        {
            generatedConfig["resourceConsents"] = new JsonArray();
        }

        // Always write messagingEndpoint to the generated config so it's available
        // for Developer Portal configuration regardless of whether endpoint registration ran.
        // NeedDeployment=true: derive from WebAppName; NeedDeployment=false: copy from static config.
        var derivedMessagingEndpoint = setupConfig.NeedDeployment && !string.IsNullOrWhiteSpace(setupConfig.WebAppName)
            ? $"https://{setupConfig.WebAppName}.azurewebsites.net/api/messages"
            : setupConfig.MessagingEndpoint;
        if (!string.IsNullOrWhiteSpace(derivedMessagingEndpoint))
        {
            generatedConfig["messagingEndpoint"] = derivedMessagingEndpoint;
            setupConfig.BotMessagingEndpoint = derivedMessagingEndpoint;
        }

        await File.WriteAllTextAsync(generatedConfigPath, generatedConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

        // ========================================================================
        // Phase 2.5: Create Client Secret (logging handled by method)
        // ========================================================================

        // Skip secret creation if blueprint already existed and secret is already configured
        bool clientSecretManualActionRequired;
        if (blueprintAlreadyExisted && !string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintClientSecret))
        {
            logger.LogInformation("Validating existing client secret...");
            var isValid = await ValidateClientSecretAsync(
                blueprintAppId!,
                setupConfig.AgentBlueprintClientSecret,
                setupConfig.AgentBlueprintClientSecretProtected,
                setupConfig.TenantId!,
                logger,
                cancellationToken);

            if (isValid)
            {
                logger.LogInformation("Client secret is valid, skipping creation");
                clientSecretManualActionRequired = false;
            }
            else
            {
                logger.LogInformation("Client secret is invalid or expired, creating new secret...");
                var secretCreated = await CreateBlueprintClientSecretAsync(
                    blueprintObjectId!,
                    blueprintAppId!,
                    graphService,
                    setupConfig,
                    configService,
                    logger,
                    loginHintResolver: loginHintResolver);
                clientSecretManualActionRequired = !secretCreated;
            }
        }
        else
        {
            var secretCreated = await CreateBlueprintClientSecretAsync(
                blueprintObjectId!,
                blueprintAppId!,
                graphService,
                setupConfig,
                configService,
                logger,
                loginHintResolver: loginHintResolver);
            clientSecretManualActionRequired = !secretCreated;
        }

        blueprintOuterScope.Dispose();
        logger.LogInformation("");
        logger.LogDebug("Generated config saved: {Path}", generatedConfigPath);

        // Endpoint registration is temporarily disabled pending a backend fix.
        // Re-enable by restoring the registration block here and in the --endpoint-only / --update-endpoint
        // paths in CreateCommand. Documentation will be updated when the backend issue is resolved.
        bool endpointRegistered = false;
        bool endpointAlreadyExisted = false;
        string? endpointFailureReason = null;

        // Display verification info — skipped when called from 'setup all' (AllSubcommand shows it at the end)
        if (!isSetupAll)
            await SetupHelpers.DisplayVerificationInfoAsync(config, logger);

        // Reconcile custom blueprint permissions — apply desired and remove stale entries.
        // Always run (even when config is empty) so that permissions removed from config are
        // also removed from Azure AD.
        // (When isSetupAll, AllSubcommand handles this at Step 5 — do not apply twice.)
        if (!isSetupAll)
        {
            await PermissionsSubcommand.ConfigureCustomPermissionsAsync(
                config.FullName,
                logger,
                configService,
                executor,
                graphApiService,
                blueprintService,
                setupConfig,
                isSetupAll: false,
                cancellationToken: cancellationToken);
        }

        if (!isSetupAll)
        {
            logger.LogInformation("Next steps:");
            logger.LogInformation("  1. Run 'a365 setup permissions mcp' to configure MCP permissions");
            logger.LogInformation("  2. Run 'a365 setup permissions bot' to configure Bot API permissions");
        }

        return new BlueprintCreationResult
        {
            BlueprintCreated = true,
            BlueprintAlreadyExisted = blueprintAlreadyExisted,
            ClientSecretManualActionRequired = clientSecretManualActionRequired,
            EndpointRegistered = endpointRegistered,
            EndpointAlreadyExisted = endpointAlreadyExisted,
            EndpointRegistrationAttempted = !skipEndpointRegistration,
            EndpointRegistrationFailureReason = endpointFailureReason,
            GraphPermissionsConfigured = blueprintResult.graphPermissionsConfigured,
            GraphInheritablePermissionsFailed = blueprintResult.graphInheritablePermissionsFailed,
            GraphInheritablePermissionsError = blueprintResult.graphInheritablePermissionsError,
            FederatedCredentialConfigured = blueprintResult.ficConfigured,
            FederatedCredentialError = blueprintResult.ficError,
            AdminConsentUrl = blueprintResult.adminConsentUrl
        };
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
        CancellationToken cancellationToken = default,
        string? correlationId = null)
    {
        // Fast fail on invalid config — these are configuration errors, not transient failures.
        // Retrying would waste 35+ seconds with no chance of success.
        if (!Guid.TryParse(clientAppId, out _))
        {
            logger.LogError("Invalid Client App ID format: {AppId}. Configure a valid GUID in a365.config.json.", clientAppId ?? "(null)");
            return false;
        }

        if (!Guid.TryParse(tenantId, out _))
        {
            logger.LogError("Invalid Tenant ID format: {TenantId}. Configure a valid GUID in a365.config.json.", tenantId ?? "(null)");
            return false;
        }

        var retryHelper = new RetryHelper(logger);

        try
        {
            var success = await retryHelper.ExecuteWithRetryAsync(
                async ct =>
                {
                    return await delegatedConsentService.EnsureBlueprintPermissionGrantAsync(
                        clientAppId,
                        tenantId,
                        ct,
                        correlationId: correlationId);
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
    /// Implements displayName-first discovery for idempotency: always searches by displayName from a365.config.json (the source of truth).
    /// Cached objectIds are only used for dependent resources (FIC, etc.) after blueprint existence is confirmed.
    /// Used by: BlueprintSubcommand and A365SetupRunner Phase 2.2
    /// Returns: (success, appId, objectId, servicePrincipalId, alreadyExisted, graphPermissionsConfigured, graphInheritablePermissionsFailed, graphInheritablePermissionsError, ficConfigured, ficError, adminConsentUrl)
    /// </summary>
    public static async Task<(bool success, string? appId, string? objectId, string? servicePrincipalId, bool alreadyExisted, bool graphPermissionsConfigured, bool graphInheritablePermissionsFailed, string? graphInheritablePermissionsError, bool ficConfigured, string? ficError, string? adminConsentUrl)> CreateAgentBlueprintAsync(
        ILogger logger,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        BlueprintLookupService blueprintLookupService,
        FederatedCredentialService federatedCredentialService,
        string tenantId,
        string displayName,
        string? agentIdentityDisplayName,
        string? managedIdentityPrincipalId,
        bool useManagedIdentity,
        JsonObject generatedConfig,
        Models.Agent365Config setupConfig,
        IConfigService configService,
        FileInfo configFile,
        CancellationToken ct,
        BlueprintCreationOptions? options = null,
        Func<Task<string?>>? loginHintResolver = null)
    {
        // ========================================================================
        // Idempotency Check: DisplayName-First Discovery
        // ========================================================================
        // IMPORTANT: a365.config.json is the source of truth for displayName.
        // We always search by displayName first to handle scenarios where the user
        // changes displayName in a365.config.json. Cached objectIds are only used
        // for dependent resources (FIC, etc.) after blueprint is confirmed to exist.

        string? existingObjectId = null;
        string? existingAppId = null;
        string? existingServicePrincipalId = setupConfig.AgentBlueprintServicePrincipalObjectId;
        bool blueprintAlreadyExists = false;
        bool requiresPersistence = false;

        // Always search by displayName from a365.config.json (the master source of truth)
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            logger.LogDebug("Searching for existing blueprint by display name: {DisplayName}...", displayName);
            var lookupResult = await blueprintLookupService.GetApplicationByDisplayNameAsync(tenantId, displayName, cancellationToken: ct);

            if (lookupResult.Found)
            {
                logger.LogInformation("Found existing blueprint by display name");
                logger.LogInformation("  Blueprint ID: {AppId}", lookupResult.AppId);
                logger.LogDebug("  Object ID: {ObjectId}", lookupResult.ObjectId);

                existingObjectId = lookupResult.ObjectId;
                existingAppId = lookupResult.AppId;
                blueprintAlreadyExists = true;
                requiresPersistence = lookupResult.RequiresPersistence;
            }
        }

        // If blueprint exists, verify service principal still exists (cached ID may be stale if SP was deleted externally)
        if (blueprintAlreadyExists && !string.IsNullOrWhiteSpace(existingAppId))
        {
            logger.LogDebug("Looking up service principal for blueprint...");
            var spLookup = await blueprintLookupService.GetServicePrincipalByAppIdAsync(
                tenantId, existingAppId, ct,
                scopes: AuthenticationConstants.RequiredPermissionGrantScopes);

            if (spLookup.Found)
            {
                if (spLookup.ObjectId != existingServicePrincipalId)
                {
                    logger.LogDebug("Service principal ID updated (was: {OldId}, now: {NewId})", existingServicePrincipalId ?? "(none)", spLookup.ObjectId);
                    requiresPersistence = true;
                }
                existingServicePrincipalId = spLookup.ObjectId;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(existingServicePrincipalId))
                    logger.LogDebug("Cached service principal {CachedId} no longer exists — will recreate.", existingServicePrincipalId);
                existingServicePrincipalId = null;
                // SP missing for an existing app — attempt creation so downstream steps have a valid SP.
                logger.LogInformation("Service principal not found for existing blueprint — attempting to create it...");
                var spToken = await graphApiService.GetGraphAccessTokenAsync(tenantId, ct: ct);
                if (!string.IsNullOrWhiteSpace(spToken))
                {
                    using var spHttpClient = Services.Internal.HttpClientFactory.CreateAuthenticatedClient(spToken);
                    var spRetryHelper = new Services.Helpers.RetryHelper(logger);
                    existingServicePrincipalId = await CreateServicePrincipalAsync(existingAppId, spHttpClient, spRetryHelper, logger, ct);
                    if (!string.IsNullOrWhiteSpace(existingServicePrincipalId))
                    {
                        requiresPersistence = true;
                        // Wait for SP to replicate before OAuth2 grants are attempted.
                        // Directory_ObjectNotFound on oauth2PermissionGrants POST means the SP's
                        // clientId is not yet visible to the grants API replica. Polling GET /servicePrincipals
                        // is insufficient — the object is readable almost immediately, but oauth2PermissionGrants
                        // requires the SP to appear in a different replication index.
                        // Probe oauth2PermissionGrants directly: a 200 (even empty list) means the grants
                        // API can now see the SP's clientId and creation will succeed.
                        logger.LogInformation("Waiting for service principal to propagate in directory...");
                        var spPropagated = await spRetryHelper.ExecuteWithRetryAsync(
                            async token =>
                            {
                                using var checkResp = await spHttpClient.GetAsync(
                                    $"{Constants.GraphApiConstants.BaseUrl}/v1.0/oauth2PermissionGrants?$filter=clientId eq '{existingServicePrincipalId}'", token);
                                return checkResp.IsSuccessStatusCode;
                            },
                            result => !result,
                            maxRetries: 12,
                            baseDelaySeconds: 5,
                            ct);
                        if (spPropagated)
                            logger.LogDebug("Service principal propagated and verified");
                        else
                            logger.LogWarning("Service principal propagation check timed out — grants may fail");
                    }
                }
                else
                {
                    logger.LogWarning("Could not acquire Graph token to create missing service principal");
                }
            }

            // Persist objectIds if needed (migration scenario or new discovery)
            if (requiresPersistence)
            {
                logger.LogDebug("Persisting blueprint metadata to config for faster future lookups...");
                setupConfig.AgentBlueprintObjectId = existingObjectId;
                setupConfig.AgentBlueprintServicePrincipalObjectId = existingServicePrincipalId;
                setupConfig.AgentBlueprintId = existingAppId;
                
                await configService.SaveStateAsync(setupConfig);
                logger.LogDebug("Config updated with blueprint identifiers");
            }

            // Blueprint exists - complete configuration (FIC validation + admin consent)
            // Validate required identifiers before proceeding
            if (string.IsNullOrWhiteSpace(existingAppId) || string.IsNullOrWhiteSpace(existingObjectId))
            {
                logger.LogError("Existing blueprint found but required identifiers are missing (AppId: {AppId}, ObjectId: {ObjectId})", 
                    existingAppId, existingObjectId);
                return (false, null, null, null, alreadyExisted: false, graphPermissionsConfigured: false, graphInheritablePermissionsFailed: false, graphInheritablePermissionsError: null, ficConfigured: false, ficError: null, adminConsentUrl: null);
            }

            return await CompleteBlueprintConfigurationAsync(
                logger,
                executor,
                graphApiService,
                blueprintService,
                blueprintLookupService,
                federatedCredentialService,
                tenantId,
                displayName,
                managedIdentityPrincipalId,
                useManagedIdentity,
                generatedConfig,
                setupConfig,
                existingAppId,
                existingObjectId,
                existingServicePrincipalId,
                alreadyExisted: true,
                ct,
                options);
        }

        // ========================================================================
        // Blueprint Creation: No existing blueprint found
        // ========================================================================
        try
        {
            logger.LogInformation("Creating blueprint application...");
            using var blueprintAppScope = logger.Indent();

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
                    logger.LogDebug("Sponsor: {BaseUrl}/v1.0/users/{UserId}", Constants.GraphApiConstants.BaseUrl, sponsorUserId);
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

            // Add sponsors and owners fields if we have the current user
            // IMPORTANT: Setting owners during creation is required to avoid 2-call pattern that will fail due to Entra bug fix
            // See: https://learn.microsoft.com/en-us/entra/agent-id/identity-platform/create-blueprint?tabs=microsoft-graph-api#create-an-agent-identity-blueprint-1
            if (!string.IsNullOrEmpty(sponsorUserId))
            {
                appManifest["sponsors@odata.bind"] = new JsonArray
                {
                    $"{Constants.GraphApiConstants.BaseUrl}/v1.0/users/{sponsorUserId}"
                };
                appManifest["owners@odata.bind"] = new JsonArray
                {
                    $"{Constants.GraphApiConstants.BaseUrl}/v1.0/users/{sponsorUserId}"
                };
            }

            var blueprintLoginHint = loginHintResolver != null
                ? await loginHintResolver()
                : await InteractiveGraphAuthService.ResolveAzLoginHintAsync();
            // Explicit scopes — NOT .default. Using .default bundles all consented scopes including
            // AgentIdentityBlueprint.*, which Entra rejects for POST /v1.0/servicePrincipals
            // ("backing application must be in the local tenant").
            // AgentIdentityBlueprintPrincipal.Create is the correct scope per Agent ID team (Kyle Marsh).
            logger.LogDebug("Acquiring blueprint httpClient token — scope: AgentIdentityBlueprintPrincipal.Create, loginHint: {LoginHint}", blueprintLoginHint ?? "(none)");
            var graphToken = await AcquireMsalGraphTokenAsync(tenantId, setupConfig.ClientAppId, logger, ct,
                scope: AuthenticationConstants.AgentIdentityBlueprintPrincipalCreateScope,
                loginHint: blueprintLoginHint);
            if (string.IsNullOrEmpty(graphToken))
            {
                logger.LogError("Failed to extract access token from Graph client");
                return (false, null, null, null, alreadyExisted: false, graphPermissionsConfigured: false, graphInheritablePermissionsFailed: false, graphInheritablePermissionsError: null, ficConfigured: false, ficError: null, adminConsentUrl: null);
            }

            // Create the application using Microsoft Graph SDK
            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(graphToken);
            httpClient.DefaultRequestHeaders.Add("ConsistencyLevel", "eventual");
            httpClient.DefaultRequestHeaders.Add("OData-Version", "4.0"); // Required for @odata.type

            var createAppUrl = $"{Constants.GraphApiConstants.BaseUrl}/beta/applications";

            logger.LogInformation("Display Name: {DisplayName}", displayName);
            if (!string.IsNullOrEmpty(sponsorUserId))
            {
                logger.LogInformation("Sponsor and Owner: User ID {UserId}", sponsorUserId);
            }

            var appResponse = await httpClient.PostAsync(
                createAppUrl,
                new StringContent(appManifest.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct);

            if (!appResponse.IsSuccessStatusCode)
            {
                var errorContent = await appResponse.Content.ReadAsStringAsync(ct);

                // If sponsors/owners fields cause error (Bad Request 400), retry selectively.
                // First drop only sponsors — this preserves ownership if sponsors was the sole cause.
                // Only drop owners as a last resort, since losing ownership breaks addPassword for non-admins.
                if (appResponse.StatusCode == System.Net.HttpStatusCode.BadRequest &&
                    !string.IsNullOrEmpty(sponsorUserId))
                {
                    logger.LogWarning("Blueprint creation failed (Bad Request). Error: {Error}. Retrying without sponsors field...", errorContent);
                    appManifest.Remove("sponsors@odata.bind");
                    appResponse.Dispose();

                    appResponse = await httpClient.PostAsync(
                        createAppUrl,
                        new StringContent(appManifest.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                        ct);

                    if (!appResponse.IsSuccessStatusCode)
                    {
                        errorContent = await appResponse.Content.ReadAsStringAsync(ct);

                        if (appResponse.StatusCode == System.Net.HttpStatusCode.BadRequest)
                        {
                            logger.LogWarning("Blueprint creation without sponsors also failed (Bad Request). Error: {Error}. Retrying without owners field...", errorContent);
                            appManifest.Remove("owners@odata.bind");
                            appResponse.Dispose();

                            appResponse = await httpClient.PostAsync(
                                createAppUrl,
                                new StringContent(appManifest.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                                ct);

                            if (!appResponse.IsSuccessStatusCode)
                            {
                                errorContent = await appResponse.Content.ReadAsStringAsync(ct);
                                logger.LogError("Failed to create application (all fallbacks exhausted): {Status} - {Error}", appResponse.StatusCode, errorContent);
                                appResponse.Dispose();
                                return (false, null, null, null, alreadyExisted: false, graphPermissionsConfigured: false, graphInheritablePermissionsFailed: false, graphInheritablePermissionsError: null, ficConfigured: false, ficError: null, adminConsentUrl: null);
                            }

                            logger.LogWarning("Agent Blueprint created without owner assignment. Client secret creation may fail — ensure you have Application Administrator role or the blueprint owner is set correctly.");
                        }
                        else
                        {
                            logger.LogError("Failed to create application (fallback): {Status} - {Error}", appResponse.StatusCode, errorContent);
                            appResponse.Dispose();
                            return (false, null, null, null, alreadyExisted: false, graphPermissionsConfigured: false, graphInheritablePermissionsFailed: false, graphInheritablePermissionsError: null, ficConfigured: false, ficError: null, adminConsentUrl: null);
                        }
                    }
                }
                else
                {
                    logger.LogError("Failed to create application: {Status} - {Error}", appResponse.StatusCode, errorContent);
                    appResponse.Dispose();
                    return (false, null, null, null, alreadyExisted: false, graphPermissionsConfigured: false, graphInheritablePermissionsFailed: false, graphInheritablePermissionsError: null, ficConfigured: false, ficError: null, adminConsentUrl: null);
                }
            }

            var appJson = await appResponse.Content.ReadAsStringAsync(ct);
            appResponse.Dispose();
            var app = JsonNode.Parse(appJson)!.AsObject();
            var appId = app["appId"]!.GetValue<string>();
            var objectId = app["id"]!.GetValue<string>();

            blueprintAppScope.Dispose();
            logger.LogInformation("Blueprint application created successfully");
            using (logger.Indent())
            {
                logger.LogInformation("Blueprint ID: {AppId}", appId);
                logger.LogDebug("Object ID: {ObjectId}", objectId);
            }

            // Wait for application propagation using RetryHelper
            var retryHelper = new RetryHelper(logger);
            logger.LogInformation("Waiting for application to propagate in directory...");
            var appAvailable = await retryHelper.ExecuteWithRetryAsync(
                async ct =>
                {
                    var checkResp = await httpClient.GetAsync($"{Constants.GraphApiConstants.BaseUrl}/v1.0/applications/{objectId}", ct);
                    return checkResp.IsSuccessStatusCode;
                },
                result => !result,
                maxRetries: 10,
                baseDelaySeconds: 5,
                ct);

            if (!appAvailable)
            {
                logger.LogError("Application object not available after creation and retries. Aborting setup.");
                return (false, null, null, null, alreadyExisted: false, graphPermissionsConfigured: false, graphInheritablePermissionsFailed: false, graphInheritablePermissionsError: null, ficConfigured: false, ficError: null, adminConsentUrl: null);
            }
            
            logger.LogDebug("Application object verified in directory");

            // Update application with identifier URI
            var identifierUri = $"api://{appId}";
            var patchAppUrl = $"{Constants.GraphApiConstants.BaseUrl}/v1.0/applications/{objectId}";
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
                logger.LogDebug("Waiting for application propagation before setting identifier URI...");
                logger.LogDebug("Identifier URI update deferred (propagation delay): {Error}", patchError);
            }
            else
            {
                logger.LogDebug("Identifier URI set to: {Uri}", identifierUri);
            }

            // Create service principal
            // Retry on 400 NoBackingApplicationObject: Agent Blueprint apps may not yet be indexed
            // by appId in all Graph API replicas even after the application object is visible by
            // objectId. Retry with backoff until the appId index is replicated.
            logger.LogInformation("");
            logger.LogInformation("Creating blueprint service principal...");
            string? servicePrincipalId = await CreateServicePrincipalAsync(appId, httpClient, retryHelper, logger, ct);
            if (string.IsNullOrWhiteSpace(servicePrincipalId))
            {
                logger.LogError("Service principal creation failed after retries");
            }
            else
            {
                using (logger.Indent())
                    logger.LogInformation("Blueprint service principal ID: {SpId}", servicePrincipalId);
            }

            // Wait for service principal propagation using RetryHelper
            if (!string.IsNullOrWhiteSpace(servicePrincipalId))
            {
                logger.LogDebug("Verifying blueprint service principal...");
                var spPropagated = await retryHelper.ExecuteWithRetryAsync(
                    async ct =>
                    {
                        // Probe oauth2PermissionGrants via GraphApiService with explicit delegated
                        // scopes (DelegatedPermissionGrant.ReadWrite.All). A non-null response —
                        // even an empty list — confirms the SP's clientId is visible to the grants
                        // API replication layer. Using the raw httpClient here (Application.ReadWrite.All
                        // scope only) caused 403s on every probe, wasting 8+ minutes of retries.
                        using var checkDoc = await graphApiService.GraphGetAsync(
                            setupConfig.TenantId!,
                            $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{servicePrincipalId}'",
                            ct,
                            scopes: AuthenticationConstants.RequiredPermissionGrantScopes);
                        return checkDoc != null;
                    },
                    result => !result,
                    maxRetries: 12,
                    baseDelaySeconds: 5,
                    ct);

                if (spPropagated)
                {
                    logger.LogDebug("Service principal verified in directory");
                }
                else
                {
                    logger.LogWarning("Service principal not fully propagated after retries. This may cause issues with federated credentials.");
                }
            }

            // Store blueprint identifiers in config object (will be persisted after secret creation)
            setupConfig.AgentBlueprintObjectId = objectId;
            setupConfig.AgentBlueprintServicePrincipalObjectId = servicePrincipalId;
            setupConfig.AgentBlueprintId = appId;

            logger.LogDebug("Blueprint identifiers staged for persistence: ObjectId={ObjectId}, SPObjectId={SPObjectId}, AppId={AppId}",
                objectId, servicePrincipalId, appId);

            // Complete configuration (FIC validation + admin consent)
            return await CompleteBlueprintConfigurationAsync(
                logger,
                executor,
                graphApiService,
                blueprintService,
                blueprintLookupService,
                federatedCredentialService,
                tenantId,
                displayName,
                managedIdentityPrincipalId,
                useManagedIdentity,
                generatedConfig,
                setupConfig,
                appId,
                objectId,
                servicePrincipalId,
                alreadyExisted: false,
                ct,
                options,
                ownerSetAtCreation: !string.IsNullOrEmpty(sponsorUserId));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Blueprint creation failed: {Message}", ex.Message);
            return (false, null, null, null, alreadyExisted: false, graphPermissionsConfigured: false, graphInheritablePermissionsFailed: false, graphInheritablePermissionsError: null, ficConfigured: false, ficError: null, adminConsentUrl: null);
        }
    }

    /// <summary>
    /// Creates a service principal for the given appId, retrying on replication lag (400/403).
    /// Returns the SP object ID on success, or null on failure.
    /// </summary>
    private static async Task<string?> CreateServicePrincipalAsync(
        string appId,
        HttpClient httpClient,
        Services.Helpers.RetryHelper retryHelper,
        ILogger logger,
        CancellationToken ct)
    {
        var createSpUrl = $"{Constants.GraphApiConstants.BaseUrl}/v1.0/servicePrincipals";
        var spManifestJson = new JsonObject { ["appId"] = appId }.ToJsonString();
        int forbiddenRetries = 0;
        const int maxForbiddenRetries = 3;

        using var spResponse = await retryHelper.ExecuteWithRetryAsync(
            async token => await httpClient.PostAsync(
                createSpUrl,
                new StringContent(spManifestJson, System.Text.Encoding.UTF8, "application/json"),
                token),
            async (response, token) =>
            {
                if (response.IsSuccessStatusCode) return false;
                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    logger.LogDebug("SP creation returned 400 BadRequest — Entra appId index not yet replicated, retrying...");
                    return true;
                }
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    await response.Content.LoadIntoBufferAsync();
                    var body = await response.Content.ReadAsStringAsync(token);
                    if (body.Contains("Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase)
                        && body.Contains("backing application", StringComparison.OrdinalIgnoreCase)
                        && forbiddenRetries < maxForbiddenRetries)
                    {
                        forbiddenRetries++;
                        logger.LogDebug("SP creation returned 403 Forbidden (replication lag, attempt {Attempt}/{Max}) — retrying...", forbiddenRetries, maxForbiddenRetries);
                        return true;
                    }
                }
                return false;
            },
            maxRetries: 10,
            baseDelaySeconds: 8,
            cancellationToken: ct);

        if (spResponse.IsSuccessStatusCode)
        {
            var spJson = await spResponse.Content.ReadAsStringAsync(ct);
            var sp = JsonNode.Parse(spJson)!.AsObject();
            var spId = sp["id"]!.GetValue<string>();
            logger.LogDebug("Service principal created: {SpId}", spId);
            return spId;
        }

        var spError = await spResponse.Content.ReadAsStringAsync(ct);
        logger.LogError("Service principal creation failed after retries: {StatusCode} — {Error}", (int)spResponse.StatusCode, spError);
        return null;
    }

    /// <summary>
    /// Completes blueprint configuration by validating/creating federated credentials and requesting admin consent.
    /// Called by both existing blueprint and new blueprint paths to ensure consistent configuration.
    /// </summary>
    private static async Task<(bool success, string? appId, string? objectId, string? servicePrincipalId, bool alreadyExisted, bool graphPermissionsConfigured, bool graphInheritablePermissionsFailed, string? graphInheritablePermissionsError, bool ficConfigured, string? ficError, string? adminConsentUrl)> CompleteBlueprintConfigurationAsync(
        ILogger logger,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        BlueprintLookupService blueprintLookupService,
        FederatedCredentialService federatedCredentialService,
        string tenantId,
        string displayName,
        string? managedIdentityPrincipalId,
        bool useManagedIdentity,
        JsonObject generatedConfig,
        Models.Agent365Config setupConfig,
        string appId,
        string objectId,
        string? servicePrincipalId,
        bool alreadyExisted,
        CancellationToken ct,
        BlueprintCreationOptions? options = null,
        bool ownerSetAtCreation = false)
    {
        // ========================================================================
        // Application Owner Validation
        // ========================================================================

        // Owner assignment is handled during blueprint creation via owners@odata.bind
        // NOTE: The 2-call pattern (POST blueprint, then POST owner) will fail due to Entra bug fix
        //       For existing blueprints, owners must be manually managed via Azure Portal or Graph API
        //       We cannot add owners after blueprint creation

        if (!alreadyExisted)
        {
            if (ownerSetAtCreation)
            {
                // owners@odata.bind was included in the creation payload and creation returned 201.
                // Trust the response — skip post-creation verification.
                // Agent Blueprint owner endpoints reject tokens that include Directory.AccessAsUser.All
                // (bundled with Application.ReadWrite.All delegated), making any GET/POST to owners/$ref
                // unreliable. The 201 from creation is authoritative.
                logger.LogDebug("Owner set at creation via owners@odata.bind — skipping post-creation verification");
            }
            else
            {
            // owners@odata.bind was not set at creation (current user could not be resolved).
            // Attempt owner assignment as a fallback.
            logger.LogDebug("Validating blueprint owner assignment...");
            var isOwner = await graphApiService.IsApplicationOwnerAsync(
                tenantId,
                objectId,
                userObjectId: null,
                ct,
                scopes: AuthenticationConstants.RequiredClientAppPermissions);

            if (isOwner)
            {
                logger.LogDebug("Current user is confirmed as blueprint owner");
            }
            else
            {
                logger.LogWarning("Current user is NOT set as blueprint owner — this may have occurred if the owners@odata.bind field was rejected during creation");
                logger.LogInformation("Attempting to assign current user as blueprint owner...");

                // Retrieve the current user's object ID, then POST to owners/$ref
                var meDoc = await graphApiService.GraphGetAsync(tenantId, "/v1.0/me?$select=id", ct);
                var currentUserObjectId = meDoc?.RootElement.TryGetProperty("id", out var idEl) == true
                    ? idEl.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(currentUserObjectId))
                {
                    logger.LogError("Could not retrieve current user ID — cannot assign blueprint owner");
                }
                else
                {
                    var ownerPayload = new Dictionary<string, string>
                    {
                        ["@odata.id"] = $"{Constants.GraphApiConstants.BaseUrl}/v1.0/users/{currentUserObjectId}"
                    };

                    var ownerResponse = await graphApiService.GraphPostWithResponseAsync(
                        tenantId,
                        $"/v1.0/applications/{objectId}/owners/$ref",
                        ownerPayload,
                        ct);

                    if (ownerResponse.IsSuccess)
                    {
                        logger.LogInformation("Owner assignment succeeded — current user is now a blueprint owner");
                    }
                    else
                    {
                        logger.LogError("Failed to assign current user as blueprint owner: {Status} {Reason}", ownerResponse.StatusCode, ownerResponse.ReasonPhrase);
                        logger.LogError("Owner assignment error detail: {Body}", ownerResponse.Body);
                        logger.LogWarning("Without owner permissions, federated credential creation will fail for this blueprint");
                        logger.LogWarning("You may need to manually add yourself as owner via Azure Portal:");
                        logger.LogWarning("  1. Go to Azure Portal -> Entra ID -> App registrations");
                        logger.LogWarning("  2. Find application: {DisplayName}", displayName);
                        logger.LogWarning("  3. Navigate to Owners blade and add yourself");
                    }
                }
            }
            } // end else (sponsorUserId was null at creation)
        }
        else
        {
            logger.LogDebug("Skipping owner validation for existing blueprint (owners@odata.bind not applied to existing blueprints)");
        }

        // ========================================================================
        // Federated Identity Credential Validation/Creation
        // ========================================================================
        
        // Create Federated Identity Credential ONLY when MSI is relevant (if managed identity provided)
        bool ficConfigured = false;
        string? ficError = null;

        if (useManagedIdentity && !string.IsNullOrWhiteSpace(managedIdentityPrincipalId))
        {
            logger.LogInformation("Configuring Federated Identity Credential for Managed Identity...");
            // Federated credential names are scoped to the application and only need to be unique per app.
            // Use a readable name based on the display name, with whitespace removed and "-MSI" suffix.
            var credentialName = $"{displayName.Replace(" ", "")}-MSI";

            // Create FIC with retry logic - handles both new and existing blueprints
            // The create API returns 409 Conflict if the FIC already exists, which we treat as success
            var retryHelper = new RetryHelper(logger);
            FederatedCredentialCreateResult? ficCreateResult = null;

            await retryHelper.ExecuteWithRetryAsync(
                async ct =>
                {
                    ficCreateResult = await federatedCredentialService.CreateFederatedCredentialAsync(
                        tenantId,
                        objectId,
                        credentialName,
                        $"https://login.microsoftonline.com/{tenantId}/v2.0",
                        managedIdentityPrincipalId,
                        new List<string> { "api://AzureADTokenExchange" },
                        ct);

                    // Return true if successful or already exists
                    // Return false with ShouldRetry=true only for transient errors (e.g. HTTP 404 propagation delay)
                    return ficCreateResult.Success || ficCreateResult.AlreadyExisted;
                },
                result => !result && (ficCreateResult?.ShouldRetry ?? false), // Only retry on transient failures
                maxRetries: 10,
                baseDelaySeconds: 3,
                ct);

            ficConfigured = (ficCreateResult?.Success ?? false) || (ficCreateResult?.AlreadyExisted ?? false);

            if (ficCreateResult?.AlreadyExisted ?? false)
            {
                logger.LogInformation("Federated Identity Credential already configured");
            }
            else if (ficCreateResult?.Success ?? false)
            {
                logger.LogInformation("Federated Identity Credential created successfully");
            }
            else
            {
                ficError = ficCreateResult?.ErrorMessage
                    ?? "Federated Identity Credential creation failed";
                logger.LogWarning("[WARN] Federated Identity Credential creation failed - you may need to create it manually in Entra ID");
                logger.LogWarning("  Ensure the client app has 'AgentIdentityBlueprint.UpdateAuthProperties.All' permission consented.");
            }
        }
        else if (!useManagedIdentity)
        {
            logger.LogDebug("Skipping Federated Identity Credential creation (external hosting / no MSI configured)");
        }
        else
        {
            logger.LogDebug("Skipping Federated Identity Credential creation (no MSI Principal ID provided)");
        }

        // ========================================================================
        // Admin Consent
        // ========================================================================
        
        var (consentSuccess, consentUrlGraph, graphInheritablePermissionsConfigured, graphInheritablePermissionsError) = await EnsureAdminConsentAsync(
            logger,
            executor,
            graphApiService,
            blueprintService,
            blueprintLookupService,
            tenantId,
            appId,
            objectId,
            servicePrincipalId,
            setupConfig,
            alreadyExisted,
            ct,
            deferConsent: options?.DeferConsent ?? false);

        // Add Graph API consent to the resource consents collection
        var applicationScopes = GetApplicationScopes(setupConfig, logger);
        var resourceConsents = new JsonArray();
        resourceConsents.Add(new JsonObject
        {
            ["resourceName"] = "Microsoft Graph",
            ["resourceAppId"] = AuthenticationConstants.MicrosoftGraphResourceAppId,
            ["consentUrl"] = consentUrlGraph,
            ["consentGranted"] = consentSuccess,
            ["consentTimestamp"] = consentSuccess ? DateTime.UtcNow.ToString("O") : null,
            ["scopes"] = new JsonArray(applicationScopes.Select(s => JsonValue.Create(s)).ToArray())
        });

        generatedConfig["resourceConsents"] = resourceConsents;

        if (!consentSuccess && !string.IsNullOrEmpty(consentUrlGraph))
        {
            logger.LogWarning("");
            logger.LogWarning("Admin consent may not have been detected");
            logger.LogWarning("The setup will continue, but you may need to grant consent manually.");
            logger.LogWarning("Consent URL: {Url}", consentUrlGraph);
        }

        // Track Graph permissions status - this is critical for agent token exchange
        bool graphPermissionsFailed = !graphInheritablePermissionsConfigured;
        string? adminConsentUrl = !consentSuccess ? consentUrlGraph : null;
        return (true, appId, objectId, servicePrincipalId, alreadyExisted, consentSuccess, graphPermissionsFailed, graphInheritablePermissionsError, ficConfigured, ficError, adminConsentUrl);
    }

    /// <summary>
    /// Gets application scopes from config with fallback to defaults.
    /// </summary>
    private static List<string> GetApplicationScopes(Models.Agent365Config setupConfig, ILogger logger)
    {
        var applicationScopes = new List<string>();

        var appScopesFromConfig = setupConfig.AgentApplicationScopes;
        if (appScopesFromConfig != null && appScopesFromConfig.Count > 0)
        {
            logger.LogDebug("  Found 'agentApplicationScopes' in typed config");
            applicationScopes.AddRange(appScopesFromConfig);
        }
        else
        {
            logger.LogDebug("  'agentApplicationScopes' not found in config, using hardcoded defaults");
            applicationScopes.AddRange(ConfigConstants.DefaultAgentApplicationScopes);
        }

        // Final fallback (should not happen with proper defaults)
        if (applicationScopes.Count == 0)
        {
            logger.LogWarning("No application scopes available, falling back to User.Read");
            applicationScopes.Add("User.Read");
        }

        return applicationScopes;
    }

    /// <summary>
    /// Ensures admin consent for the blueprint application.
    /// For existing blueprints, checks if consent already exists before requesting browser interaction.
    /// For new blueprints, skips verification and directly requests consent.
    /// Returns: (consentSuccess, consentUrl, graphInheritablePermissionsConfigured, graphInheritablePermissionsError)
    /// </summary>
    private static async Task<(bool consentSuccess, string consentUrl, bool graphInheritablePermissionsConfigured, string? graphInheritablePermissionsError)> EnsureAdminConsentAsync(
        ILogger logger,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        BlueprintLookupService blueprintLookupService,
        string tenantId,
        string appId,
        string objectId,
        string? servicePrincipalId,
        Models.Agent365Config setupConfig,
        bool alreadyExisted,
        CancellationToken ct,
        bool deferConsent = false)
    {
        // When called from AllSubcommand via DeferConsent: true, skip consent and Graph
        // inheritable permissions entirely. The batch orchestrator handles both as Phase 3
        // (and Phase 2 via the Graph spec). Return a neutral result: consent not done yet
        // (false), no URL from this step (empty string), inheritable permissions not failed
        // (true so AllSubcommand does not add a spurious warning in Step 2).
        if (deferConsent)
        {
            logger.LogDebug("Admin consent deferred to batch orchestrator — skipping in blueprint step.");
            return (consentSuccess: false, consentUrl: string.Empty,
                    graphInheritablePermissionsConfigured: true, graphInheritablePermissionsError: null);
        }

        var applicationScopes = GetApplicationScopes(setupConfig, logger);
        bool consentAlreadyExists = false;

        // Resolve blueprint SP object ID once — reused by both pre-check and polling.
        // servicePrincipalId comes from generated config (persisted on previous runs).
        // If absent, look it up using MSAL scopes that include Application.Read.All.
        // Without Application.Read.All the az CLI token causes Graph to return empty results silently.
        var blueprintSpId = servicePrincipalId;
        if (string.IsNullOrWhiteSpace(blueprintSpId))
        {
            logger.LogDebug("Looking up service principal for blueprint...");
            var spLookup = await blueprintLookupService.GetServicePrincipalByAppIdAsync(
                tenantId, appId, ct,
                scopes: AuthenticationConstants.RequiredPermissionGrantScopes);
            blueprintSpId = spLookup.ObjectId;
        }

        // Only check for existing consent if blueprint already existed
        // New blueprints cannot have consent yet, so skip the verification
        if (alreadyExisted)
        {
            logger.LogInformation("Verifying admin consent for application");
            logger.LogDebug("  - Application scopes: {Scopes}", string.Join(", ", applicationScopes));

            if (!string.IsNullOrWhiteSpace(blueprintSpId))
            {
                // Get Microsoft Graph service principal ID (needs Application.Read.All)
                var graphSpId = await graphApiService.LookupServicePrincipalByAppIdAsync(
                    tenantId,
                    AuthenticationConstants.MicrosoftGraphResourceAppId,
                    ct,
                    AuthenticationConstants.RequiredPermissionGrantScopes);

                if (!string.IsNullOrWhiteSpace(graphSpId))
                {
                    // Use shared helper to check existing consent
                    consentAlreadyExists = await AdminConsentHelper.CheckConsentExistsAsync(
                        graphApiService,
                        tenantId,
                        blueprintSpId,
                        graphSpId,
                        applicationScopes,
                        logger,
                        ct,
                        scopes: AuthenticationConstants.RequiredPermissionGrantScopes);
                }
            }

            if (consentAlreadyExists)
            {
                logger.LogInformation("Admin consent already granted for all required scopes");
                logger.LogDebug("  - Scopes: {Scopes}", string.Join(", ", applicationScopes));
            }
        }

        var consentUrlGraph = SetupHelpers.BuildAdminConsentUrl(
            tenantId, appId,
            applicationScopes.Select(s => $"{AuthenticationConstants.MicrosoftGraphResourceUri}/{s}"));

        if (consentAlreadyExists)
        {
            // For existing consent, we still need to verify/configure inheritable permissions
            logger.LogInformation("Configuring inheritable permissions for Microsoft Graph...");
            bool graphInheritableConfigured = false;
            string? graphInheritableError = null;
            try
            {
                setupConfig.AgentBlueprintId = appId;

                await SetupHelpers.EnsureResourcePermissionsAsync(
                    graph: graphApiService,
                    blueprintService: blueprintService,
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
                graphInheritableConfigured = true;
            }
            catch (Exception ex)
            {
                graphInheritableError = ex.Message;
                logger.LogWarning("Failed to configure Microsoft Graph inheritable permissions: {Message}", ex.Message);
                logger.LogWarning("Agent instances may not be able to access Microsoft Graph resources");
                logger.LogWarning("You can configure these manually later with: a365 setup blueprint");
            }

            return (true, consentUrlGraph, graphInheritableConfigured, graphInheritableError);
        }

        // Check if the current user has an admin role that can grant tenant-wide consent
        var adminCheck = await graphApiService.IsCurrentUserAdminAsync(tenantId, ct);
        if (adminCheck == Models.RoleCheckResult.DoesNotHaveRole)
        {
            logger.LogWarning("Admin consent is required but the current user does not have the Global Administrator role.");
            logger.LogWarning("Ask a tenant administrator to complete the following:");
            logger.LogWarning("");
            logger.LogWarning("  1. Grant admin consent for the agent blueprint:");
            logger.LogWarning("     {ConsentUrl}", consentUrlGraph);

            return (false, consentUrlGraph, false, null);
        }

        if (adminCheck == Models.RoleCheckResult.Unknown)
        {
            logger.LogDebug("Admin role check inconclusive — attempting consent anyway; API will surface any permission error.");
        }

        // Request consent via browser
        logger.LogInformation("Requesting admin consent for application");
        logger.LogDebug("  - Application scopes: {Scopes}", string.Join(", ", applicationScopes));
        logger.LogInformation("Opening browser for Graph API admin consent...");
        logger.LogInformation("If the browser does not open automatically, navigate to this URL to grant consent: {ConsentUrl}", consentUrlGraph);
        BrowserHelper.TryOpenUrl(consentUrlGraph, logger);

        bool consentSuccess;
        if (!string.IsNullOrWhiteSpace(blueprintSpId))
        {
            consentSuccess = await AdminConsentHelper.PollAdminConsentAsync(
                graphApiService, logger, tenantId, blueprintSpId,
                "Graph API Scopes", 180, 5, ct);
        }
        else
        {
            logger.LogDebug("Could not resolve blueprint service principal. Falling back to az rest polling.");
            consentSuccess = await AdminConsentHelper.PollAdminConsentAsync(executor, logger, appId, "Graph API Scopes", 180, 5, ct);
        }

        if (consentSuccess)
        {
            logger.LogInformation("Graph API admin consent granted successfully!");
        }
        else
        {
            logger.LogWarning("Graph API admin consent may not have completed");
        }

        // Configure Graph inheritable permissions regardless of admin consent outcome.
        // Inheritable permissions define what scopes agent instances *can* inherit from the blueprint
        // and require AgentIdentityBlueprint.ReadWrite.All (already consented on the client app).
        // Admin consent is a separate gate that controls whether those inherited scopes are usable
        // at runtime — it does not block configuring the permission manifest here.
        bool graphInheritablePermissionsConfigured = false;
        string? graphInheritablePermissionsError = null;

        logger.LogInformation("Configuring inheritable permissions for Microsoft Graph...");
        try
        {
            setupConfig.AgentBlueprintId = appId;

            await SetupHelpers.EnsureResourcePermissionsAsync(
                graph: graphApiService,
                blueprintService: blueprintService,
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
            if (!consentSuccess)
            {
                logger.LogWarning("Note: Admin consent has not been granted — Graph permissions will not be usable at runtime until an admin grants consent via: {Url}", consentUrlGraph);
            }
            graphInheritablePermissionsConfigured = true;
        }
        catch (Exception ex)
        {
            graphInheritablePermissionsError = ex.Message;
            logger.LogWarning("Failed to configure Microsoft Graph inheritable permissions: {Message}", ex.Message);
            logger.LogWarning("Agent instances may not be able to access Microsoft Graph resources");
            logger.LogWarning("You can configure these manually later with: a365 setup blueprint");
        }

        return (consentSuccess, consentUrlGraph, graphInheritablePermissionsConfigured, graphInheritablePermissionsError);
    }

    /// <summary>
    /// Acquires a Microsoft Graph access token using MSAL interactive authentication
    /// (WAM on Windows, browser-based flow on other platforms).
    /// Pass a specific scope (e.g. AgentIdentityBlueprint.ReadWrite.All) to avoid bundling
    /// Application.ReadWrite.All and the Directory.AccessAsUser.All scope it carries, which is
    /// rejected by the Agent Blueprint API. Defaults to .default (all consented permissions).
    /// Pass loginHint so WAM targets the az-logged-in user rather than the OS default account.
    /// </summary>
    private static async Task<string?> AcquireMsalGraphTokenAsync(string tenantId, string clientAppId, ILogger logger, CancellationToken ct = default, string? scope = null, string? loginHint = null, string[]? additionalScopes = null)
    {
        // Guard: MSAL will fail (and block for ~30s on WAM) with empty credentials.
        if (string.IsNullOrWhiteSpace(clientAppId) || string.IsNullOrWhiteSpace(tenantId))
        {
            logger.LogDebug("Skipping MSAL token acquisition: clientAppId or tenantId is empty");
            return null;
        }

        try
        {
            var credential = new MsalBrowserCredential(
                clientAppId,
                tenantId,
                redirectUri: null,  // Let MsalBrowserCredential use WAM on Windows
                logger,
                loginHint: loginHint);

            var primaryScope = string.IsNullOrWhiteSpace(scope)
                ? $"{Constants.GraphApiConstants.BaseUrl}/.default"
                : $"{Constants.GraphApiConstants.BaseUrl}/{scope}";

            var allScopes = additionalScopes?.Length > 0
                ? new[] { primaryScope }.Concat(additionalScopes.Select(s => $"{Constants.GraphApiConstants.BaseUrl}/{s}")).ToArray()
                : new[] { primaryScope };

            var tokenRequestContext = new TokenRequestContext(allScopes);
            var token = await credential.GetTokenAsync(tokenRequestContext, ct);

            logger.LogDebug("Acquired MSAL token (requested: [{Scopes}])", string.Join(", ", allScopes));
            TryLogTokenScp(token.Token, logger);

            return token.Token;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to acquire MSAL Graph access token");
            return null;
        }
    }

    /// <summary>
    /// Decodes the JWT payload and logs the scp claim at Debug level.
    /// Used only to diagnose scope issues during blueprint creation.
    /// </summary>
    private static void TryLogTokenScp(string token, ILogger logger)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var scp = doc.RootElement.TryGetProperty("scp", out var scpEl) ? scpEl.GetString() : "(absent)";
            var upn = doc.RootElement.TryGetProperty("upn", out var upnEl) ? upnEl.GetString()
                : doc.RootElement.TryGetProperty("unique_name", out var unEl) ? unEl.GetString() : "(absent)";
            logger.LogDebug("Token scp: {Scp} | upn: {Upn}", scp, upn);
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Creates and authenticates a GraphServiceClient using InteractiveGraphAuthService.
    /// This common method consolidates the authentication logic used across multiple methods.
    /// </summary>
    private async static Task<GraphServiceClient> GetAuthenticatedGraphClientAsync(ILogger logger, Models.Agent365Config setupConfig, string tenantId, CancellationToken ct)
    {
        logger.LogInformation("Sign in to Microsoft Graph to continue...");

        // Use InteractiveGraphAuthService to get proper authentication.
        // Pass the caller's logger so messages appear in the correct indent scope.
        var interactiveAuth = new InteractiveGraphAuthService(
            logger,
            setupConfig.ClientAppId);

        try
        {
            var graphClient = await interactiveAuth.GetAuthenticatedGraphClientAsync(tenantId, ct);
            return graphClient;
        }
        catch (Exception ex)
        {
            var isCanceled = ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase);
            if (!isCanceled)
            {
                logger.LogError("Failed to authenticate to Microsoft Graph: {Message}", ex.Message);
                logger.LogError("");
                logger.LogError("TROUBLESHOOTING:");
                logger.LogError("1. Ensure you are a Global Administrator or have AgentIdentityBlueprint.ReadWrite.All permission");
                logger.LogError("2. The account must have already consented to these permissions");
                logger.LogError("");
            }
            throw new InvalidOperationException($"Microsoft Graph authentication failed: {ex.Message}", ex);
        }
    }


    /// <summary>
    /// Creates client secret for Agent Blueprint (Phase 2.5)
    /// Used by: BlueprintSubcommand and A365SetupRunner
    /// </summary>
    /// <returns>True if the secret was created successfully; false if it failed and manual action is required.</returns>
    public static async Task<bool> CreateBlueprintClientSecretAsync(
        string blueprintObjectId,
        string blueprintAppId,
        GraphApiService graphService,
        Models.Agent365Config setupConfig,
        IConfigService configService,
        ILogger logger,
        CancellationToken ct = default,
        Func<Task<string?>>? loginHintResolver = null)
    {
        logger.LogInformation("");
        logger.LogInformation("Creating blueprint client secret...");
        using var clientSecretScope = logger.Indent();
        try
        {
            // Resolve login hint so WAM targets the az-logged-in user, not the OS default account.
            // Without this, WAM may return a cached token for a different user who is not the owner.
            var loginHint = loginHintResolver != null
                ? await loginHintResolver()
                : await InteractiveGraphAuthService.ResolveAzLoginHintAsync();

            // Use a token scoped to AgentIdentityBlueprint.ReadWrite.All (already consented on the
            // client app). Using .default bundles Application.ReadWrite.All → Directory.AccessAsUser.All,
            // which the Agent Blueprint API explicitly rejects for addPassword. ReadWrite.All includes
            // all granular update permissions including AddRemoveCreds (passwordCredentials).
            var graphToken = await AcquireMsalGraphTokenAsync(
                setupConfig.TenantId ?? string.Empty,
                setupConfig.ClientAppId ?? string.Empty,
                logger, ct,
                scope: AuthenticationConstants.AgentIdentityBlueprintReadWriteAllScope,
                loginHint: loginHint);

            if (string.IsNullOrWhiteSpace(graphToken))
            {
                logger.LogError("Failed to acquire MSAL Graph access token for client secret creation");
                throw new InvalidOperationException("Cannot create client secret without Graph API token");
            }

            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(graphToken);

            var secretBody = new JsonObject
            {
                ["passwordCredential"] = new JsonObject
                {
                    ["displayName"] = "Agent 365 CLI Generated Secret",
                    ["endDateTime"] = DateTime.UtcNow.AddYears(2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                }
            };

            var addPasswordUrl = $"{Constants.GraphApiConstants.BaseUrl}/v1.0/applications/{blueprintObjectId}/addPassword";
            var secretBodyJson = secretBody.ToJsonString();

            // Retry on 404 (blueprint not yet visible on all replicas) and transient 403 (owner
            // propagation lag — the blueprint was just created with owners@odata.bind, and Entra
            // may not yet recognize the caller as owner when addPassword is called immediately after
            // creation). Do NOT retry on Authorization_RequestDenied (permanent permission failure).
            var retryHelper = new RetryHelper(logger);
            var passwordResponse = await retryHelper.ExecuteWithRetryAsync(
                async token => await httpClient.PostAsync(
                    addPasswordUrl,
                    new StringContent(secretBodyJson, System.Text.Encoding.UTF8, "application/json"),
                    token),
                async (response, token) =>
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        return true;
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        // Buffer so the body can be re-read by the caller after this predicate.
                        await response.Content.LoadIntoBufferAsync();
                        var body = await response.Content.ReadAsStringAsync(token);
                        // Authorization_RequestDenied = permanent privilege failure — no point retrying.
                        if (body.Contains("Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase))
                            return false;
                        return true; // transient 403 (owner propagation lag), retry
                    }
                    return false;
                },
                maxRetries: 5,
                baseDelaySeconds: 5,
                cancellationToken: ct);

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

            var protectedSecret = Microsoft.Agents.A365.DevTools.Cli.Helpers.SecretProtectionHelper.ProtectSecret(secretTextNode.GetValue<string>(), logger);

            var isProtected = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            setupConfig.AgentBlueprintClientSecret = protectedSecret;
            setupConfig.AgentBlueprintClientSecretProtected = isProtected;

            // Single consolidated save: persists blueprint identifiers (objectId, servicePrincipalId, appId) + client secret
            // This ensures all blueprint-related state is saved atomically
            await configService.SaveStateAsync(setupConfig);

            logger.LogInformation("Client secret created successfully!");
            logger.LogInformation($"  - Secret stored in generated config (encrypted: {isProtected})");
            logger.LogWarning("IMPORTANT: The client secret has been stored in a365.generated.config.json");
            logger.LogWarning("Keep this file secure and do not commit it to source control!");

            if (!isProtected)
            {
                logger.LogWarning("WARNING: Secret encryption is only available on Windows. The secret is stored in plaintext.");
                logger.LogWarning("Consider using environment variables or Azure Key Vault for production deployments.");
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to create blueprint client secret (detail)");
            logger.LogWarning("Insufficient privileges to create blueprint client secret automatically. You must create it manually.");
            logger.LogWarning("Create the client secret manually for blueprint app {AppId} and add it to a365.generated.config.json, then re-run: a365 setup all", blueprintAppId);
            logger.LogWarning("See: https://learn.microsoft.com/en-us/entra/identity-platform/how-to-add-credentials");
            return false;
        }
    }

    /// <summary>
    /// Validates an existing client secret by attempting to authenticate with Microsoft Graph.
    /// Returns true if the secret is valid and can successfully acquire a token.
    /// Performs automatic retry for transient network errors.
    /// </summary>
    private static async Task<bool> ValidateClientSecretAsync(
        string clientId,
        string clientSecret,
        bool isProtected,
        string tenantId,
        ILogger logger,
        CancellationToken ct = default)
    {
        // Decrypt the secret if it's protected (do this once outside the loop)
        var plaintextSecret = SecretProtectionHelper.UnprotectSecret(
            clientSecret,
            isProtected,
            logger);

        // Create HttpClient once outside the retry loop to avoid socket exhaustion
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(ClientSecretValidationTimeoutSeconds);

        var tokenUrl = string.Format(MicrosoftLoginOAuthTokenEndpoint, tenantId);

        for (int attempt = 1; attempt <= ClientSecretValidationMaxRetries; attempt++)
        {
            try
            {
                using var requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = plaintextSecret,
                    ["scope"] = $"{Constants.GraphApiConstants.BaseUrl}/.default",
                    ["grant_type"] = "client_credentials"
                });

                using var response = await httpClient.PostAsync(tokenUrl, requestContent, ct);

                if (response.IsSuccessStatusCode)
                {
                    logger.LogDebug("Client secret validation successful");
                    return true;
                }

                var errorContent = await response.Content.ReadAsStringAsync(ct);

                // Check if this is a transient error that should be retried
                bool isTransient = response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                                  response.StatusCode == System.Net.HttpStatusCode.GatewayTimeout ||
                                  response.StatusCode == System.Net.HttpStatusCode.TooManyRequests;

                if (isTransient && attempt < ClientSecretValidationMaxRetries)
                {
                    logger.LogDebug("Transient error during validation (attempt {Attempt}/{MaxRetries}), retrying...",
                        attempt, ClientSecretValidationMaxRetries);
                    await Task.Delay(ClientSecretValidationRetryDelayMs, ct);
                    continue;
                }

                // Non-transient error or final retry - log and return false
                logger.LogDebug("Client secret validation failed: {StatusCode} - {Error}",
                    response.StatusCode, errorContent);

                return false;
            }
            catch (HttpRequestException ex) when (attempt < ClientSecretValidationMaxRetries)
            {
                logger.LogDebug(ex, "Network error during validation (attempt {Attempt}/{MaxRetries}), retrying...",
                    attempt, ClientSecretValidationMaxRetries);
                await Task.Delay(ClientSecretValidationRetryDelayMs, ct);
            }
            catch (TaskCanceledException ex) when (attempt < ClientSecretValidationMaxRetries && !ct.IsCancellationRequested)
            {
                // Timeout (not user cancellation)
                logger.LogDebug(ex, "Timeout during validation (attempt {Attempt}/{MaxRetries}), retrying...",
                    attempt, ClientSecretValidationMaxRetries);
                await Task.Delay(ClientSecretValidationRetryDelayMs, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unexpected exception during client secret validation: {Message}", ex.Message);
                return false;
            }
        }

        // All retries exhausted
        logger.LogWarning("Client secret validation failed after {MaxRetries} attempts", ClientSecretValidationMaxRetries);
        return false;
    }

    /// <summary>
    /// Registers blueprint messaging endpoint and syncs project settings.
    /// Public method that can be called by AllSubcommand.
    /// Returns (success, alreadyExisted)
    /// </summary>
    public static async Task<(bool success, bool alreadyExisted)> RegisterEndpointAndSyncAsync(
        string configPath,
        ILogger logger,
        IConfigService configService,
        IBotConfigurator botConfigurator,
        PlatformDetector platformDetector,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var setupConfig = await configService.LoadAsync(configPath);

        if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
        {
            logger.LogError("Blueprint ID not found. Please confirm agent blueprint id is in config file.");
            Environment.Exit(1);
        }

        // Validate webAppName if needDeployment is true
        if (setupConfig.NeedDeployment && string.IsNullOrWhiteSpace(setupConfig.WebAppName))
        {
            logger.LogError("Web App Name not found. Run 'a365 setup infrastructure' first.");
            Environment.Exit(1);
        }

        // Location is required by the endpoint registration API for both Azure and external hosting
        if (string.IsNullOrWhiteSpace(setupConfig.Location))
        {
            logger.LogError(ErrorMessages.EndpointLocationRequiredForCreate);
            logger.LogInformation(ErrorMessages.EndpointLocationAddToConfig);
            logger.LogInformation(ErrorMessages.EndpointLocationExample);
            Environment.Exit(1);
        }

        logger.LogInformation("Registering blueprint messaging endpoint...");
        logger.LogInformation("");

        var (endpointRegistered, endpointAlreadyExisted) = await SetupHelpers.RegisterBlueprintMessagingEndpointAsync(
            setupConfig, logger, botConfigurator, correlationId: correlationId);


        setupConfig.Completed = true;
        setupConfig.CompletedAt = DateTime.UtcNow;

        await configService.SaveStateAsync(setupConfig);

        logger.LogInformation("");
        if (endpointRegistered)
        {
            if (endpointAlreadyExisted)
            {
                logger.LogInformation("Blueprint messaging endpoint already registered");
            }
            else
            {
                logger.LogInformation("Blueprint messaging endpoint registered successfully");
            }
        }
        else
        {
            logger.LogInformation("Blueprint messaging endpoint registration skipped");
        }

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
        
        return (endpointRegistered, endpointAlreadyExisted);
    }

    /// <summary>
    /// Updates the messaging endpoint by deleting the existing one and registering a new one.
    /// </summary>
    /// <param name="configPath">Path to the configuration file</param>
    /// <param name="newEndpointUrl">The new messaging endpoint URL</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="configService">Configuration service</param>
    /// <param name="botConfigurator">Bot configurator service</param>
    /// <param name="platformDetector">Platform detector service</param>
    /// <param name="correlationId">Optional correlation ID for tracing</param>
    public static async Task UpdateEndpointAsync(
        string configPath,
        string newEndpointUrl,
        ILogger logger,
        IConfigService configService,
        IBotConfigurator botConfigurator,
        PlatformDetector platformDetector,
        string? correlationId = null)
    {
        var setupConfig = await configService.LoadAsync(configPath);

        // Validate blueprint ID exists
        if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
        {
            logger.LogError("Blueprint ID not found. Please confirm agent blueprint id is in config file.");
            throw new Exceptions.SetupValidationException("Agent Blueprint ID is required for endpoint update.");
        }

        // Validate new endpoint URL
        if (!Uri.TryCreate(newEndpointUrl, UriKind.Absolute, out var newUri) ||
            newUri.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogError("New endpoint must be a valid HTTPS URL. Current value: {Endpoint}", newEndpointUrl);
            throw new Exceptions.SetupValidationException("New endpoint must be a valid HTTPS URL.");
        }

        logger.LogInformation("Updating messaging endpoint...");
        logger.LogInformation("");

        // Normalize location once; used by both Step 1 and Step 1.5.
        // Null-coalescing is intentional: Location is only validated inside the Step 1 block (not here),
        // so it may still be null at this point. The empty-string fallback is never passed to any API —
        // Step 1 throws before using it, and Step 1.5 guards on !IsNullOrWhiteSpace(Location).
        var normalizedLocation = setupConfig.Location?.Replace(" ", "").ToLowerInvariant() ?? string.Empty;

        // Step 1: Delete existing endpoint if it exists
        if (!string.IsNullOrWhiteSpace(setupConfig.MessagingEndpoint) || !string.IsNullOrWhiteSpace(setupConfig.BotName))
        {
            logger.LogInformation("Deleting existing messaging endpoint...");
            if (string.IsNullOrWhiteSpace(setupConfig.Location))
            {
                logger.LogError("Location not found. Please confirm location is in the config file.");
                throw new Exceptions.SetupValidationException("Location is required to delete the existing messaging endpoint.");
            }

            // For needsDeployment=false, derive the endpoint name from the currently registered URL.
            // BotMessagingEndpoint (generated config) is updated after every successful registration,
            // so it reflects the actual registered endpoint name after any --update-endpoint calls.
            // Fall back to MessagingEndpoint (static config) if BotMessagingEndpoint is not yet set.
            string endpointName;
            if (!setupConfig.NeedDeployment && (!string.IsNullOrWhiteSpace(setupConfig.BotMessagingEndpoint) || !string.IsNullOrWhiteSpace(setupConfig.MessagingEndpoint)))
            {
                var urlForName = !string.IsNullOrWhiteSpace(setupConfig.BotMessagingEndpoint)
                    ? setupConfig.BotMessagingEndpoint
                    : setupConfig.MessagingEndpoint;
                endpointName = Services.Helpers.EndpointHelper.GetEndpointNameFromUrl(urlForName, setupConfig.AgentBlueprintId);
            }
            else
            {
                // When NeedDeployment=true, BotName is always non-empty (derived from WebAppName),
                // so GetEndpointName(BotName) is safe here.
                endpointName = Services.Helpers.EndpointHelper.GetEndpointName(setupConfig.BotName);
            }

            var deleted = await botConfigurator.DeleteEndpointWithAgentBlueprintAsync(
                endpointName,
                normalizedLocation,
                setupConfig.AgentBlueprintId,
                correlationId: correlationId);

            if (!deleted)
            {
                logger.LogError("Failed to delete existing messaging endpoint.");
                throw new Exceptions.SetupValidationException("Failed to delete existing messaging endpoint. Cannot proceed with update.");
            }

            logger.LogInformation("Existing endpoint deleted successfully.");
        }
        else
        {
            logger.LogInformation("No existing endpoint found. Proceeding with registration.");
        }

        // Step 1.5: Pre-create cleanup of the target endpoint name.
        // If a previous --update-endpoint failed during the create step, Azure may have
        // partially provisioned the new endpoint and left it in a bad state that blocks
        // subsequent creates with InternalServerError. Delete it now to ensure a clean slate.
        if (!setupConfig.NeedDeployment && !string.IsNullOrWhiteSpace(setupConfig.Location))
        {
            var targetEndpointName = Services.Helpers.EndpointHelper.GetEndpointNameFromUrl(newEndpointUrl, setupConfig.AgentBlueprintId);
            logger.LogInformation("Removing target endpoint '{EndpointName}' (derived from {Url}) to ensure a clean state before registration.", targetEndpointName, newEndpointUrl);
            var preCleanupDeleted = await botConfigurator.DeleteEndpointWithAgentBlueprintAsync(targetEndpointName, normalizedLocation, setupConfig.AgentBlueprintId, correlationId: correlationId);
            if (!preCleanupDeleted)
            {
                // Not fatal — proceed and let Step 2 surface the error if the partially-provisioned
                // endpoint is still blocking. The warning helps diagnose production issues.
                logger.LogWarning("Pre-create cleanup for '{EndpointName}' did not confirm deletion. Proceeding anyway.", targetEndpointName);
            }
        }

        // Step 2: Register new endpoint with the provided URL
        logger.LogInformation("");
        logger.LogInformation("Registering new messaging endpoint...");

        var (endpointRegistered, _) = await SetupHelpers.RegisterBlueprintMessagingEndpointAsync(
            setupConfig, logger, botConfigurator, newEndpointUrl, correlationId: correlationId);

        if (!endpointRegistered)
        {
            throw new Exceptions.SetupValidationException("Failed to register new messaging endpoint.");
        }

        // Step 3: Save updated configuration
        setupConfig.Completed = true;
        setupConfig.CompletedAt = DateTime.UtcNow;

        await configService.SaveStateAsync(setupConfig);

        // Step 4: Sync to project settings
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

        logger.LogInformation("");
        logger.LogInformation("Endpoint update completed successfully!");
        logger.LogInformation("New endpoint: {Endpoint}", newEndpointUrl);
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
        try
        {
            var federatedCredential = new JsonObject
            {
                ["name"] = credentialName,
                ["issuer"] = $"https://login.microsoftonline.com/{tenantId}/v2.0",
                ["subject"] = msiPrincipalId,
                ["audiences"] = new JsonArray { "api://AzureADTokenExchange" }
            };

            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(graphToken);
            httpClient.DefaultRequestHeaders.Add("ConsistencyLevel", "eventual");

            var urls = new []
            {
                $"{Constants.GraphApiConstants.BaseUrl}/beta/applications/{blueprintObjectId}/federatedIdentityCredentials",
                $"{Constants.GraphApiConstants.BaseUrl}/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/federatedIdentityCredentials"
            };

            // Use RetryHelper for federated credential creation with exponential backoff
            var retryHelper = new RetryHelper(logger);
            
            foreach (var url in urls)
            {
                logger.LogDebug("Attempting federated credential creation with endpoint: {Url}", url);
                
                var result = await retryHelper.ExecuteWithRetryAsync(
                    async ct =>
                    {
                        var response = await httpClient.PostAsync(
                            url,
                            new StringContent(federatedCredential.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                            ct);

                        if (response.IsSuccessStatusCode)
                        {
                            return (success: true, error: string.Empty, shouldRetry: false);
                        }

                        var error = await response.Content.ReadAsStringAsync(ct);

                        // Check if it's a transient error that should be retried
                        if (error.Contains("Request_ResourceNotFound") || error.Contains("does not exist"))
                        {
                            return (success: false, error, shouldRetry: true);
                        }

                        // Check if credential already exists
                        if (error.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                        {
                            logger.LogInformation("Federated Identity Credential already exists (name: {Name})", credentialName);
                            return (success: true, error: string.Empty, shouldRetry: false);
                        }

                        // Check if we should try the alternative endpoint
                        if (error.Contains("Agent Blueprints are not supported on the API version"))
                        {
                            logger.LogDebug("Standard endpoint not supported, will try Agent Blueprint-specific path...");
                            return (success: false, error, shouldRetry: false);
                        }

                        // Non-retryable error
                        return (success: false, error, shouldRetry: false);
                    },
                    r => r.shouldRetry,
                    maxRetries: 10,
                    baseDelaySeconds: 3,
                    ct);

                if (result.success)
                {
                    logger.LogInformation("  - Credential Name: {Name}", credentialName);
                    logger.LogInformation("  - Issuer: https://login.microsoftonline.com/{TenantId}/v2.0", tenantId);
                    logger.LogInformation("  - Subject (MSI Principal ID): {MsiId}", msiPrincipalId);
                    return true;
                }

                // If we got a non-retryable error and it's not the endpoint issue, fail
                if (!string.IsNullOrEmpty(result.error) && 
                    !result.error.Contains("Agent Blueprints are not supported on the API version"))
                {
                    logger.LogDebug("FIC creation failed with error: {Error}", result.error);
                    return false;
                }
            }

            logger.LogDebug("Failed to create federated identity credential after trying all endpoints");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception creating federated identity credential: {Message}", ex.Message);
            return false;
        }
    }

    #endregion
}
