// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// Setup command - Complete initial agent deployment (blueprint, messaging endpoint registration) in one step
/// </summary>
public class SetupCommand
{
    // Test hook: if set, this delegate will be invoked instead of creating/running the real A365SetupRunner.
    // Signature: (setupConfigPath, generatedConfigPath, executor, webAppCreator) => Task<bool>
    public static Func<string, string, CommandExecutor, AzureWebAppCreator, Task<bool>>? SetupRunnerInvoker { get; set; }

    public static Command CreateCommand(
        ILogger<SetupCommand> logger,
        IConfigService configService,
        CommandExecutor executor,
        DeploymentService deploymentService, // still injected for future use, not used here
        IBotConfigurator botConfigurator,
        IAzureValidator azureValidator,
        AzureWebAppCreator webAppCreator,
        PlatformDetector platformDetector,
        GraphApiService graphApiService)
    {
        var command = new Command("setup", "Set up your Agent 365 environment by creating Azure resources, configuring\npermissions, and registering your agent blueprint for deployment");

        // Options for the main setup command
        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Setup configuration file path");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Show detailed output");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            description: "Show what would be done without executing");

        var blueprintOnlyOption = new Option<bool>(
            "--blueprint",
            description: "Skip Azure infrastructure setup and create blueprint only. ");

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(blueprintOnlyOption);

        // No subcommands - all logic is in the main handler
        command.SetHandler(async (config, verbose, dryRun, blueprintOnly) =>
        {
            if (dryRun)
            {
                // Validate configuration even in dry-run mode
                var dryRunConfig = await configService.LoadAsync(config.FullName);

                // Validate non-Azure messaging endpoint
                ValidateMessagingEndpointForNonAzure(dryRunConfig, logger);
                
                logger.LogInformation("DRY RUN: Agent 365 Setup - Blueprint + Messaging Endpoint Registration");
                logger.LogInformation("This would execute the following operations:");
                logger.LogInformation("  1. Create agent blueprint and Azure resources");
                logger.LogInformation("  2. Register blueprint messaging endpoint");
                logger.LogInformation("No actual changes will be made.");
                logger.LogInformation("Configuration file validated successfully: {ConfigFile}", config.FullName);
                return;
            }

            logger.LogInformation("Agent 365 Setup - Blueprint + Messaging Endpoint Registration");
            logger.LogInformation("Creating blueprint and registering messaging endpoint...");
            logger.LogInformation("");
            
            // Track setup results for summary
            var setupResults = new SetupResults();
            
            try
            {
                // Load configuration - ConfigService automatically finds generated config in same directory
                var setupConfig = await configService.LoadAsync(config.FullName);
                
                // Early fail for non-Azure if no MessagingEndpoint
                ValidateMessagingEndpointForNonAzure(setupConfig, logger);

                // Validate Azure CLI authentication, subscription, and environment
                if (!await azureValidator.ValidateAllAsync(setupConfig.SubscriptionId))
                {
                    Environment.Exit(1);
                }
                
                logger.LogInformation("");

                // Step 1: Create blueprint
                logger.LogInformation("Step 1: Creating agent blueprint...");
                logger.LogInformation("");

                var generatedConfigPath = Path.Combine(
                    config.DirectoryName ?? Environment.CurrentDirectory,
                    "a365.generated.config.json");

                bool success;

                var delegatedConsentService = new DelegatedConsentService(
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<DelegatedConsentService>(),
                    graphApiService);

                var setupRunner = new A365SetupRunner(
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<A365SetupRunner>(),
                    executor,
                    graphApiService,
                    webAppCreator,
                    delegatedConsentService,
                    platformDetector);

                // Use test invoker if set (for testing), otherwise use real runner
                if (SetupRunnerInvoker != null)
                {
                    success = await SetupRunnerInvoker(config.FullName, generatedConfigPath, executor, webAppCreator);
                    
                    // If using test invoker, stop here - tests don't mock all the downstream services
                    if (success)
                    {
                        logger.LogDebug("Generated config saved at: {Path}", generatedConfigPath);
                        logger.LogInformation("Setup completed successfully (test mode)");
                        return;
                    }
                }
                else
                {
                    // Pass blueprintOnly option to setup runner
                    success = await setupRunner.RunAsync(config.FullName, generatedConfigPath, blueprintOnly);
                }

                if (!success)
                {
                    logger.LogError("Agent blueprint creation failed");
                    setupResults.BlueprintCreated = false;
                    setupResults.Errors.Add("Agent blueprint creation failed");
                    throw new InvalidOperationException("Setup runner execution failed");
                }

                setupResults.BlueprintCreated = true;
                
                // Reload config to get blueprint ID and check for inheritable permissions status
                var tempConfig = await configService.LoadAsync(config.FullName);
                setupResults.BlueprintId = tempConfig.AgentBlueprintId;
                
                logger.LogInformation("Agent blueprint created successfully");
                logger.LogDebug("Generated config saved at: {Path}", generatedConfigPath);

                logger.LogInformation("");
                logger.LogInformation("Step 2a: Applying MCP server permissions (OAuth2 permission grants + inheritable permissions)");
                logger.LogInformation("");

                // Reload configuration to pick up blueprint ID from generated config
                // ConfigService automatically resolves generated config in same directory
                setupConfig = await configService.LoadAsync(config.FullName);

                // Read scopes from toolingManifest.json (at deploymentProjectPath)
                var manifestPath = Path.Combine(setupConfig.DeploymentProjectPath ?? string.Empty, "toolingManifest.json");
                var toolingScopes = await ManifestHelper.GetRequiredScopesAsync(manifestPath);

                // Apply OAuth2 permission grant (admin consent) and inheritable permissions
                // Wrap in try-catch to prevent unhandled exceptions
                try
                {
                    await EnsureMcpOauth2PermissionGrantsAsync(
                        graphApiService,
                        setupConfig,
                        toolingScopes,
                        logger
                    );

                    // Apply inheritable permissions on the agent identity blueprint
                    await EnsureMcpInheritablePermissionsAsync(
                        graphApiService,
                        setupConfig,
                        toolingScopes,
                        logger
                    );

                    setupResults.McpPermissionsConfigured = true;
                    logger.LogInformation("MCP server permissions configured successfully");
                    // Check if inheritable permissions were configured successfully
                    // The A365SetupRunner sets this flag in generated config
                    setupResults.InheritablePermissionsConfigured = tempConfig.InheritanceConfigured;

                    if (!tempConfig.InheritanceConfigured)
                    {
                        setupResults.Warnings.Add("Inheritable permissions configuration incomplete");

                        if (!string.IsNullOrEmpty(tempConfig.InheritanceConfigError))
                        {
                            setupResults.Warnings.Add($"Inheritable permissions error: {tempConfig.InheritanceConfigError}");
                        }
                    }
                }
                catch (Exception mcpEx)
                {
                    setupResults.McpPermissionsConfigured = false;
                    setupResults.InheritablePermissionsConfigured = false;  // ADD THIS LINE
                    setupResults.Errors.Add($"MCP permissions: {mcpEx.Message}");
                    logger.LogError("Failed to configure MCP server permissions: {Message}", mcpEx.Message);
                    logger.LogWarning("Setup will continue, but MCP server permissions must be configured manually");
                    logger.LogInformation("To configure MCP permissions manually:");
                    logger.LogInformation("  1. Ensure the agent blueprint has the required permissions in Azure Portal");
                    logger.LogInformation("  2. Grant admin consent for the MCP scopes");
                    logger.LogInformation("  3. Run 'a365 deploy mcp' to retry MCP permission configuration");
                }

                logger.LogInformation("");
                logger.LogInformation("Step 2b: add Messaging Bot API permission + inheritable permissions");
                logger.LogInformation("");

                if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
                    throw new InvalidOperationException("AgentBlueprintId is required.");

                var blueprintSpObjectId = await graphApiService.LookupServicePrincipalByAppIdAsync(setupConfig.TenantId, setupConfig.AgentBlueprintId)
                    ?? throw new InvalidOperationException($"Blueprint Service Principal not found for appId {setupConfig.AgentBlueprintId}");

                // Ensure Messaging Bot API SP exists
                var botApiResourceSpObjectId = await graphApiService.EnsureServicePrincipalForAppIdAsync(
                    setupConfig.TenantId,
                    ConfigConstants.MessagingBotApiAppId);

                try
                {
                    // Grant oauth2PermissionGrants: blueprint SP -> Messaging Bot API SP
                    var botApiGrantOk = await graphApiService.CreateOrUpdateOauth2PermissionGrantAsync(
                        setupConfig.TenantId,
                        blueprintSpObjectId,
                        botApiResourceSpObjectId,
                        new[] { "Authorization.ReadWrite", "user_impersonation" });

                    if (!botApiGrantOk)
                    {
                        setupResults.Warnings.Add("Failed to create/update oauth2PermissionGrant for Messaging Bot API");
                        logger.LogWarning("Failed to create/update oauth2PermissionGrant for Messaging Bot API.");
                    }
                    
                    // Add inheritable permissions on blueprint for Messaging Bot API
                    var (ok, already, err) = await graphApiService.SetInheritablePermissionsAsync(
                        setupConfig.TenantId,
                        setupConfig.AgentBlueprintId,
                        ConfigConstants.MessagingBotApiAppId,
                        new[] { "Authorization.ReadWrite", "user_impersonation" });

                    if (!ok && !already)
                    {
                        setupResults.Warnings.Add($"Failed to set inheritable permissions for Messaging Bot API: {err}");
                        logger.LogWarning("Failed to set inheritable permissions for Messaging Bot API: " + err);
                    }

                    setupResults.BotApiPermissionsConfigured = true;
                    logger.LogInformation("Messaging Bot API permissions configured (grant + inheritable) successfully.");
                }
                catch (Exception botEx)
                {
                    setupResults.BotApiPermissionsConfigured = false;
                    setupResults.Errors.Add($"Bot API permissions: {botEx.Message}");
                    logger.LogError("Failed to configure Messaging Bot API permissions: {Message}", botEx.Message);
                }

                logger.LogInformation("");
                logger.LogInformation("Step 3: Registering blueprint messaging endpoint...");
                
                try
                {
                    // Reload config to get any updated values from blueprint creation
                    setupConfig = await configService.LoadAsync(config.FullName);
                    
                    await RegisterBlueprintMessagingEndpointAsync(setupConfig, logger, botConfigurator);
                    setupResults.MessagingEndpointRegistered = true;
                    logger.LogInformation("Blueprint messaging endpoint registered successfully");
                }
                catch (Exception endpointEx)
                {
                    setupResults.MessagingEndpointRegistered = false;
                    setupResults.Errors.Add($"Messaging endpoint: {endpointEx.Message}");
                    logger.LogError("Failed to register messaging endpoint: {Message}", endpointEx.Message);
                }

                // Sync generated config in project settings from deployment project
                try
                {
                    generatedConfigPath = Path.Combine(
                        config.DirectoryName ?? Environment.CurrentDirectory,
                        "a365.generated.config.json");
                    await ProjectSettingsSyncHelper.ExecuteAsync(
                        a365ConfigPath: config.FullName,
                        a365GeneratedPath: generatedConfigPath,
                        configService: configService,
                        platformDetector: platformDetector,
                        logger: logger
                    );

                    logger.LogDebug("Generated config synced to project settings");
                }
                catch (Exception syncEx)
                {
                    logger.LogWarning(syncEx, "Project settings sync failed (non-blocking). Please sync settings manually.");
                }
                
                // Display verification URLs and next steps
                await DisplayVerificationInfoAsync(config, logger);
                
                // Display comprehensive setup summary
                DisplaySetupSummary(setupResults, logger);
            }
            catch (Agent365Exception ex)
            {
                ExceptionHandler.HandleAgent365Exception(ex);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Setup failed: {Message}", ex.Message);
                throw;
            }
        }, configOption, verboseOption, dryRunOption, blueprintOnlyOption);

        return command;
    }


    /// <summary>
    /// Display verification URLs and next steps after successful setup
    /// </summary>
    private static async Task DisplayVerificationInfoAsync(FileInfo setupConfigFile, ILogger logger)
    {
        try
        {
            logger.LogInformation("Generating verification information...");
            var baseDir = setupConfigFile.DirectoryName ?? Environment.CurrentDirectory;
            var generatedConfigPath = Path.Combine(baseDir, "a365.generated.config.json");
            
            if (!File.Exists(generatedConfigPath))
            {
                logger.LogWarning("Generated config not found - skipping verification info");
                return;
            }

            using var stream = File.OpenRead(generatedConfigPath);
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            logger.LogInformation("");
            logger.LogInformation("Verification URLs and Next Steps:");
            logger.LogInformation("==========================================");

            // Azure Web App URL - construct from AppServiceName
            if (root.TryGetProperty("AppServiceName", out var appServiceProp) && !string.IsNullOrWhiteSpace(appServiceProp.GetString()))
            {
                var webAppUrl = $"https://{appServiceProp.GetString()}.azurewebsites.net";
                logger.LogInformation("Agent Web App: {Url}", webAppUrl);
            }

            // Azure Resource Group
            if (root.TryGetProperty("ResourceGroup", out var rgProp) && !string.IsNullOrWhiteSpace(rgProp.GetString()))
            {
                var resourceGroup = rgProp.GetString();
                logger.LogInformation("Azure Resource Group: https://portal.azure.com/#@/resource/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}",
                    root.TryGetProperty("SubscriptionId", out var subProp) ? subProp.GetString() : "{subscription}", 
                    resourceGroup);
            }

            // Entra ID Application
            if (root.TryGetProperty("AgentBlueprintId", out var blueprintProp) && !string.IsNullOrWhiteSpace(blueprintProp.GetString()))
            {
                logger.LogInformation("Entra ID Application: https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/{AppId}",
                    blueprintProp.GetString());
            }

            logger.LogInformation("");
            logger.LogInformation("Next Steps:");
            logger.LogInformation("   1. Review Azure resources in the portal");
            logger.LogInformation("   2. View configuration: a365 config display");
            logger.LogInformation("   3. Create agent instance: a365 create-instance identity");
            logger.LogInformation("   4. Deploy application: a365 deploy app");
            logger.LogInformation("");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not display verification info: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Register blueprint messaging endpoint using deployed web app URL
    /// </summary>
    private static async Task RegisterBlueprintMessagingEndpointAsync(
        Agent365Config setupConfig,
        ILogger<SetupCommand> logger,
        IBotConfigurator botConfigurator)
    {
        // Validate required configuration
        if (string.IsNullOrEmpty(setupConfig.AgentBlueprintId))
        {
            logger.LogError("Agent Blueprint ID not found. Blueprint creation may have failed.");
            throw new InvalidOperationException("Agent Blueprint ID is required for messaging endpoint registration");
        }

        string messagingEndpoint;
        string endpointName;
        if (string.Equals(setupConfig.HostingMode, "AzureAppService", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(setupConfig.WebAppName))
            {
                logger.LogError("Web App Name not configured in a365.config.json");
                throw new InvalidOperationException("Web App Name is required for messaging endpoint registration");
            }

            // Generate endpoint name with Azure Bot Service constraints (4-42 chars)
            var baseEndpointName = $"{setupConfig.WebAppName}-endpoint";
            endpointName = EndpointHelper.GetEndpointName(baseEndpointName);
            
            // Construct messaging endpoint URL from web app name
            messagingEndpoint = $"https://{setupConfig.WebAppName}.azurewebsites.net/api/messages";
        }
        else // External hosting (non-Azure)
        {
            if (string.IsNullOrWhiteSpace(setupConfig.MessagingEndpoint))
            {
                logger.LogError("MessagingEndpoint must be provided in a365.config.json for External hosting mode");
                throw new InvalidOperationException("MessagingEndpoint is required for messaging endpoint registration");
            }

            if (!Uri.TryCreate(setupConfig.MessagingEndpoint, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                logger.LogError("MessagingEndpoint must be a valid HTTPS URL. Current value: {Endpoint}",
                    setupConfig.MessagingEndpoint);
                throw new InvalidOperationException("MessagingEndpoint must be a valid HTTPS URL.");
            }

            messagingEndpoint = setupConfig.MessagingEndpoint;

            // Derive endpoint name from host when there's no WebAppName
            var hostPart = uri.Host.Replace('.', '-');
            var baseEndpointName = $"{hostPart}-endpoint";
            endpointName = EndpointHelper.GetEndpointName(baseEndpointName );
        }

        if (endpointName.Length < 4)
        {
            logger.LogError("Bot endpoint name '{EndpointName}' is too short (must be at least 4 characters)", endpointName);
            throw new InvalidOperationException($"Bot endpoint name '{endpointName}' is too short (must be at least 4 characters)");
        }
        
        logger.LogInformation("   - Registering blueprint messaging endpoint");
        logger.LogInformation("     * Endpoint Name: {EndpointName}", endpointName);
        logger.LogInformation("     * Messaging Endpoint: {Endpoint}", messagingEndpoint);
        logger.LogInformation("     * Using Agent Blueprint ID: {AgentBlueprintId}", setupConfig.AgentBlueprintId);
        
        var endpointRegistered = await botConfigurator.CreateEndpointWithAgentBlueprintAsync(
            endpointName: endpointName,
            location: setupConfig.Location,
            messagingEndpoint: messagingEndpoint,
            agentDescription: "Agent 365 messaging endpoint for automated interactions",
            agentBlueprintId: setupConfig.AgentBlueprintId);
        
        if (!endpointRegistered)
        {
            logger.LogError("Failed to register blueprint messaging endpoint");
            throw new InvalidOperationException("Blueprint messaging endpoint registration failed");
        }
    }

    /// <summary>
    /// Ensure OAuth2 permission grants are set from blueprint to MCP server
    /// </summary>
    private static async Task EnsureMcpOauth2PermissionGrantsAsync(
        GraphApiService graph,
        Agent365Config config,
        string[] scopes,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
            throw new InvalidOperationException("AgentBlueprintId (appId) is required.");

        var blueprintSpObjectId = await graph.LookupServicePrincipalByAppIdAsync(config.TenantId, config.AgentBlueprintId, ct);
        if (string.IsNullOrWhiteSpace(blueprintSpObjectId))
        {
            throw new InvalidOperationException($"Blueprint Service Principal not found for appId {config.AgentBlueprintId}. " +
                "The service principal may not have propagated yet. Wait a few minutes and retry.");
        }

        var resourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(config.Environment);
        var Agent365ToolsSpObjectId = await graph.LookupServicePrincipalByAppIdAsync(config.TenantId, resourceAppId, ct);
        if (string.IsNullOrWhiteSpace(Agent365ToolsSpObjectId))
        {
            throw new InvalidOperationException($"Agent 365 Tools Service Principal not found for appId {resourceAppId}. " +
                $"Ensure the Agent 365 Tools application is available in your tenant for environment: {config.Environment}");
        }

        logger.LogInformation("   - OAuth2 grant: client {ClientId} to resource {ResourceId} scopes [{Scopes}]",
            blueprintSpObjectId, Agent365ToolsSpObjectId, string.Join(' ', scopes));

        var response = await graph.CreateOrUpdateOauth2PermissionGrantAsync(
            config.TenantId, blueprintSpObjectId, Agent365ToolsSpObjectId, scopes, ct);

        if (!response)
        {
            throw new InvalidOperationException(
                $"Failed to create/update OAuth2 permission grant from blueprint {config.AgentBlueprintId} to Agent 365 Tools {resourceAppId}. " +
                "This may be due to insufficient permissions. Ensure you have DelegatedPermissionGrant.ReadWrite.All or Application.ReadWrite.All permissions.");
        }
    }

    /// <summary>
    /// Ensure inheritable permissions are set from blueprint to MCP server
    /// </summary>
    private static async Task EnsureMcpInheritablePermissionsAsync(
        GraphApiService graph,
        Agent365Config config,
        string[] scopes,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
            throw new InvalidOperationException("AgentBlueprintId (appId) is required.");

        var resourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(config.Environment);

        logger.LogInformation("   - Inheritable permissions: blueprint {Blueprint} to resourceAppId {ResourceAppId} scopes [{Scopes}]",
            config.AgentBlueprintId, resourceAppId, string.Join(' ', scopes));

        var (ok, alreadyExists, err) = await graph.SetInheritablePermissionsAsync(
            config.TenantId, config.AgentBlueprintId, resourceAppId, scopes, new List<string>() { "AgentIdentityBlueprint.ReadWrite.All" }, ct);

        if (!ok && !alreadyExists)
        {
            config.InheritanceConfigured = false;
            config.InheritanceConfigError = err;
            throw new InvalidOperationException($"Failed to set inheritable permissions: {err}. " +
                "Ensure you have Application.ReadWrite.All permissions and the blueprint supports inheritable permissions.");
        }

        config.InheritanceConfigured = true;
        config.InheritablePermissionsAlreadyExist = alreadyExists;
        config.InheritanceConfigError = null;
    }

    /// <summary>
    /// Validate messaging endpoint configuration for non-Azure deployments
    /// </summary>
    private static void ValidateMessagingEndpointForNonAzure(Agent365Config config, ILogger logger)
    {
        if (!string.Equals(config.HostingMode, "External", StringComparison.OrdinalIgnoreCase))
            return;

        if (string.IsNullOrWhiteSpace(config.MessagingEndpoint))
        {
            logger.LogError(
                "For non-Azure deployments (hostingMode=External), 'messagingEndpoint' is required in a365.config.json. " +
                "This is the HTTPS URL that Bot Framework will call (e.g. https://your-host.com/api/messages).");

            throw new InvalidOperationException("Missing MessagingEndpoint for non-Azure deployment.");
        }

        if (!Uri.TryCreate(config.MessagingEndpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogError(
                "MessagingEndpoint must be a valid HTTPS URL for non-Azure deployments. Current value: {Endpoint}",
                config.MessagingEndpoint);

            throw new InvalidOperationException("Invalid MessagingEndpoint URL for non-Azure deployment.");
        }

        logger.LogInformation("Non-Azure hosting detected. Using external messaging endpoint: {Endpoint}",
            config.MessagingEndpoint);
    }

    /// <summary>
    /// Display comprehensive setup summary showing what succeeded and what failed
    /// </summary>
    private static void DisplaySetupSummary(SetupResults results, ILogger logger)
    {
        logger.LogInformation("");
        logger.LogInformation("==========================================");
        logger.LogInformation("Setup Summary");
        logger.LogInformation("==========================================");
        
        // Show what succeeded
        logger.LogInformation("Completed Steps:");
        if (results.BlueprintCreated)
        {
            logger.LogInformation("  [OK] Agent blueprint created (Blueprint ID: {BlueprintId})", results.BlueprintId ?? "unknown");
        }
        if (results.McpPermissionsConfigured)
            logger.LogInformation("  [OK] MCP server permissions configured");
        if (results.InheritablePermissionsConfigured)
            logger.LogInformation("  [OK] Inheritable permissions configured");
        if (results.BotApiPermissionsConfigured)
            logger.LogInformation("  [OK] Messaging Bot API permissions configured");
        if (results.MessagingEndpointRegistered)
            logger.LogInformation("  [OK] Messaging endpoint registered");
        
        // Show what failed
        if (results.Errors.Count > 0)
        {
            logger.LogInformation("");
            logger.LogInformation("Failed Steps:");
            foreach (var error in results.Errors)
            {
                logger.LogInformation("  [FAILED] {Error}", error);
            }
        }
        
        // Show warnings
        if (results.Warnings.Count > 0)
        {
            logger.LogInformation("");
            logger.LogInformation("Warnings:");
            foreach (var warning in results.Warnings)
            {
                logger.LogInformation("  [WARN] {Warning}", warning);
            }
        }
        
        logger.LogInformation("");
        
        // Overall status
        if (results.HasErrors)
        {
            logger.LogWarning("Setup completed with errors");
            logger.LogInformation("");
            logger.LogInformation("Recovery Actions:");
            
            if (!results.InheritablePermissionsConfigured)
            {
                logger.LogInformation("  - Inheritable Permissions: Refer to Agent 365 CLI documentation for manual configuration");
            }
            
            if (!results.McpPermissionsConfigured)
            {
                logger.LogInformation("  - MCP Permissions: Refer to Agent 365 CLI documentation for manual configuration");
            }
            
            if (!results.BotApiPermissionsConfigured)
            {
                logger.LogInformation("  - Bot API Permissions: Refer to Agent 365 CLI documentation for manual configuration");
            }
            
            if (!results.MessagingEndpointRegistered)
            {
                logger.LogInformation("  - Messaging Endpoint: Refer to Agent 365 CLI documentation for manual configuration");
            }
        }
        else if (results.HasWarnings)
        {
            logger.LogInformation("Setup completed successfully with warnings");
            logger.LogInformation("Review warnings above and take action if needed");
        }
        else
        {
            logger.LogInformation("Setup completed successfully");
            logger.LogInformation("All components configured correctly");
        }
        
        logger.LogInformation("==========================================");
    }
}

/// <summary>
/// Tracks the results of each setup step for summary reporting
/// </summary>
internal class SetupResults
{
    public bool BlueprintCreated { get; set; }
    public string? BlueprintId { get; set; }
    public bool McpPermissionsConfigured { get; set; }
    public bool BotApiPermissionsConfigured { get; set; }
    public bool MessagingEndpointRegistered { get; set; }
    public bool InheritablePermissionsConfigured { get; set; }
    
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    
    public bool HasErrors => Errors.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;
}
