// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
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
        BotConfigurator botConfigurator,
        IAzureValidator azureValidator,
        AzureWebAppCreator webAppCreator,
        PlatformDetector platformDetector)
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
            
            try
            {
                // Load configuration - ConfigService automatically finds generated config in same directory
                var setupConfig = await configService.LoadAsync(config.FullName);

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

                // Use C# runner with GraphApiService
                var graphService = new GraphApiService(
                        LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<GraphApiService>(),
                        executor);

                var delegatedConsentService = new DelegatedConsentService(
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<DelegatedConsentService>(),
                    graphService);

                var setupRunner = new A365SetupRunner(
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<A365SetupRunner>(),
                    executor,
                    graphService,
                    webAppCreator,
                    delegatedConsentService,
                    platformDetector);

                // Pass blueprintOnly option to setup runner
                success = await setupRunner.RunAsync(config.FullName, generatedConfigPath, blueprintOnly);

                if (!success)
                {
                    logger.LogError("A365SetupRunner failed");
                    throw new InvalidOperationException("Setup runner execution failed");
                }

                logger.LogInformation("Blueprint created successfully");

                logger.LogInformation("");
                logger.LogInformation("Step 2a: Applying MCP server permissions (OAuth2 permission grants + inheritable permissions)");
                logger.LogInformation("");

                // Reload configuration to pick up blueprint ID from generated config
                // ConfigService automatically resolves generated config in same directory
                setupConfig = await configService.LoadAsync(config.FullName);

                // Read scopes from toolingManifest.json (at deploymentProjectPath)
                var manifestPath = Path.Combine(setupConfig.DeploymentProjectPath ?? string.Empty, "toolingManifest.json");
                var toolingScopes = await ManifestHelper.GetRequiredScopesAsync(manifestPath);

                // Apply OAuth2 permission grant (admin consent)
                await EnsureMcpOauth2PermissionGrantsAsync(
                    graphService,
                    setupConfig,
                    toolingScopes,
                    logger
                );

                // Apply inheritable permissions on the agent identity blueprint
                await EnsureMcpInheritablePermissionsAsync(
                    graphService,
                    setupConfig,
                    toolingScopes,
                    logger
                );

                logger.LogInformation("MCP server permissions configured");

                logger.LogInformation("");
                logger.LogInformation("Step 2b: add Messaging Bot API permission + inheritable permissions");
                logger.LogInformation("");

                if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
                    throw new InvalidOperationException("AgentBlueprintId is required.");

                var blueprintSpObjectId = await graphService.LookupServicePrincipalByAppIdAsync(setupConfig.TenantId, setupConfig.AgentBlueprintId)
                    ?? throw new InvalidOperationException($"Blueprint Service Principal not found for appId {setupConfig.AgentBlueprintId}");

                // Ensure Messaging Bot API SP exists
                var botApiResourceSpObjectId = await graphService.EnsureServicePrincipalForAppIdAsync(
                    setupConfig.TenantId,
                    ConfigConstants.MessagingBotApiAppId);

                // Grant oauth2PermissionGrants: blueprint SP -> Messaging Bot API SP
                var botApiGrantOk = await graphService.CreateOrUpdateOauth2PermissionGrantAsync(
                    setupConfig.TenantId,
                    blueprintSpObjectId,
                    botApiResourceSpObjectId,
                    new[] { "Authorization.ReadWrite", "user_impersonation" });

                if (!botApiGrantOk)
                    logger.LogWarning("Failed to create/update oauth2PermissionGrant for Messaging Bot API.");
                
                // Add inheritable permissions on blueprint for Messaging Bot API
                var (ok, already, err) = await graphService.SetInheritablePermissionsAsync(
                    setupConfig.TenantId,
                    setupConfig.AgentBlueprintId,
                    ConfigConstants.MessagingBotApiAppId,
                    new[] { "Authorization.ReadWrite", "user_impersonation" });

                if (!ok && !already)
                    logger.LogWarning("Failed to set inheritable permissions for Messaging Bot API: " + err);

                logger.LogInformation("Messaging Bot API permissions configured (grant + inheritable) successfully.");

                logger.LogInformation("");
                logger.LogInformation("Step 3: Registering blueprint messaging endpoint...");
                
                // Reload config to get any updated values from blueprint creation
                setupConfig = await configService.LoadAsync(config.FullName);
                
                await RegisterBlueprintMessagingEndpointAsync(setupConfig, logger, botConfigurator);
                logger.LogInformation("Blueprint messaging endpoint registered successfully");

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

                    logger.LogInformation("Generated config in project settings successfully!");
                }
                catch (Exception syncEx)
                {
                    logger.LogWarning(syncEx, "Project settings sync failed (non-blocking). Please sync settings manually.");
                }
                
                // Display verification URLs and next steps
                await DisplayVerificationInfoAsync(config, logger);
                
                logger.LogInformation("Agent 365 setup completed successfully");
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
    /// Convert Agent365Config to DeploymentConfiguration
    /// </summary>
    private static DeploymentConfiguration ConvertToDeploymentConfig(Agent365Config config)
    {
        return new DeploymentConfiguration
        {
            ResourceGroup = config.ResourceGroup,
            AppName = config.WebAppName,
            ProjectPath = config.DeploymentProjectPath,
            DeploymentZip = "app.zip",
            BuildConfiguration = "Release",
            PublishOptions = new PublishOptions
            {
                SelfContained = false,
                OutputPath = "publish"
            }
        };
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

            // Configuration files
            logger.LogInformation("Configuration Files:");
            logger.LogInformation("   • Setup Config: {SetupConfig}", setupConfigFile.FullName);
            logger.LogInformation("   • Generated Config: {GeneratedConfig}", generatedConfigPath);

            logger.LogInformation("");
            logger.LogInformation("Next Steps:");
            logger.LogInformation("   1. Review Azure resources in the portal");
            logger.LogInformation("   2. Create agent instance using CLI for testing purposes");
            logger.LogInformation("   3. Use 'a365 deploy' to deploy the application to Azure");
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
        BotConfigurator botConfigurator)
    {
        // Validate required configuration
        if (string.IsNullOrEmpty(setupConfig.AgentBlueprintId))
        {
            logger.LogError("Agent Blueprint ID not found. Blueprint creation may have failed.");
            throw new InvalidOperationException("Agent Blueprint ID is required for messaging endpoint registration");
        }

        if (string.IsNullOrEmpty(setupConfig.WebAppName))
        {
            logger.LogError("Web App Name not configured in a365.config.json");
            throw new InvalidOperationException("Web App Name is required for messaging endpoint registration");
        }

        // Register Bot Service provider (hidden as messaging endpoint provider)
        logger.LogInformation("   • Ensuring messaging endpoint provider is registered");
        var providerRegistered = await botConfigurator.EnsureBotServiceProviderAsync(
            setupConfig.SubscriptionId, 
            setupConfig.ResourceGroup);
        
        if (!providerRegistered)
        {
            logger.LogError("Failed to register messaging endpoint provider");
            throw new InvalidOperationException("Messaging endpoint provider registration failed");
        }

        // Register messaging endpoint using agent blueprint identity and deployed web app URL
        var endpointName = $"{setupConfig.WebAppName}-endpoint";
        var messagingEndpoint = $"https://{setupConfig.WebAppName}.azurewebsites.net/api/messages";
        
        logger.LogInformation("   • Registering blueprint messaging endpoint");
        logger.LogInformation("     - Endpoint Name: {EndpointName}", endpointName);
        logger.LogInformation("     - Messaging Endpoint: {Endpoint}", messagingEndpoint);
        logger.LogInformation("     - Using Agent Blueprint ID: {AgentBlueprintId}", setupConfig.AgentBlueprintId);
        
        var endpointRegistered = await botConfigurator.CreateOrUpdateBotWithAgentBlueprintAsync(
            appServiceName: setupConfig.WebAppName,
            botName: endpointName,
            resourceGroupName: setupConfig.ResourceGroup,
            subscriptionId: setupConfig.SubscriptionId,
            location: "global",
            messagingEndpoint: messagingEndpoint,
            agentDescription: "Agent 365 messaging endpoint for automated interactions",
            sku: "F0",
            agentBlueprintId: setupConfig.AgentBlueprintId);
        
        if (!endpointRegistered)
        {
            logger.LogError("Failed to register blueprint messaging endpoint");
            throw new InvalidOperationException("Blueprint messaging endpoint registration failed");
        }

        // Configure channels (Teams, Email) as messaging integrations
        logger.LogInformation("   • Configuring messaging integrations");
        var integrationsConfigured = await botConfigurator.ConfigureChannelsAsync(
            endpointName,
            setupConfig.ResourceGroup,
            enableTeams: true,
            enableEmail: !string.IsNullOrEmpty(setupConfig.AgentUserPrincipalName),
            agentUserPrincipalName: setupConfig.AgentUserPrincipalName);

        if (integrationsConfigured)
        {
            logger.LogInformation("     - Messaging integrations configured successfully");
        }
        else
        {
            logger.LogWarning("     - Some messaging integrations failed to configure");
        }
    }

    /// <summary>
    /// Get well-known resource names for common Microsoft services
    /// </summary>
    private static string GetWellKnownResourceName(string? resourceAppId)
    {
        return resourceAppId switch
        {
            "00000003-0000-0000-c000-000000000000" => "Microsoft Graph",
            "00000002-0000-0000-c000-000000000000" => "Azure Active Directory Graph",
            "797f4846-ba00-4fd7-ba43-dac1f8f63013" => "Azure Service Management",
            "00000001-0000-0000-c000-000000000000" => "Azure ESTS Service",
            _ => $"Unknown Resource ({resourceAppId})"
        };
    }

    private static async Task EnsureMcpOauth2PermissionGrantsAsync(
        GraphApiService graph,
        Agent365Config cfg,
        string[] scopes,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.AgentBlueprintId))
            throw new InvalidOperationException("AgentBlueprintId (appId) is required.");

        var blueprintSpObjectId = await graph.LookupServicePrincipalByAppIdAsync(cfg.TenantId, cfg.AgentBlueprintId, ct)
            ?? throw new InvalidOperationException("Blueprint Service Principal not found for appId " + cfg.AgentBlueprintId);

        var resourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(cfg.Environment);
        var Agent365ToolsSpObjectId = await graph.LookupServicePrincipalByAppIdAsync(cfg.TenantId, resourceAppId, ct)
            ?? throw new InvalidOperationException("Agent 365 Tools Service Principal not found for appId " + resourceAppId);

        logger.LogInformation("   • OAuth2 grant: client {ClientId} to resource {ResourceId} scopes [{Scopes}]",
            blueprintSpObjectId, Agent365ToolsSpObjectId, string.Join(' ', scopes));

        var response = await graph.CreateOrUpdateOauth2PermissionGrantAsync(
            cfg.TenantId, blueprintSpObjectId, Agent365ToolsSpObjectId, scopes, ct);

        if (!response) throw new InvalidOperationException("Failed to create/update oauth2PermissionGrant.");
    }

    private static async Task EnsureMcpInheritablePermissionsAsync(
        GraphApiService graph,
        Agent365Config cfg,
        string[] scopes,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.AgentBlueprintId))
            throw new InvalidOperationException("AgentBlueprintId (appId) is required.");

        var resourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(cfg.Environment);

        logger.LogInformation("   • Inheritable permissions: blueprint {Blueprint} to resourceAppId {ResourceAppId} scopes [{Scopes}]",
            cfg.AgentBlueprintId, resourceAppId, string.Join(' ', scopes));

        var (ok, alreadyExists, err) = await graph.SetInheritablePermissionsAsync(
            cfg.TenantId, cfg.AgentBlueprintId, resourceAppId, scopes, ct);

        if (!ok && !alreadyExists)
        {
            cfg.InheritanceConfigured = false;
            cfg.InheritanceConfigError = err;
            throw new InvalidOperationException("Failed to set inheritable permissions: " + err);
        }

        cfg.InheritanceConfigured = true;
        cfg.InheritablePermissionsAlreadyExist = alreadyExists;
        cfg.InheritanceConfigError = null;
    }
}
