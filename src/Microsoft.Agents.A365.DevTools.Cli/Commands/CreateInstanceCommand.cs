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
/// CreateInstance command - Create agent instances with identities, M365 licenses and tooling gateway
/// </summary>
public class CreateInstanceCommand
{
    public static Command CreateCommand(ILogger<CreateInstanceCommand> logger, IConfigService configService, CommandExecutor executor,
        BotConfigurator botConfigurator, GraphApiService graphApiService, IAzureValidator azureValidator)
    {
        var command = new Command("create-instance", "Create and configure agent user identities with appropriate\nlicenses and notification settings for your deployed agent");

        // Options for the main create-instance command
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

        // Add subcommands
        command.AddCommand(CreateIdentitySubcommand(logger, configService, executor));
        command.AddCommand(CreateLicensesSubcommand(logger, configService, executor));

        // Default handler runs all 4 steps
        command.SetHandler(async (config, verbose, dryRun) =>
        {
            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Agent 365 Instance Creation - All Steps");
                logger.LogInformation("This would execute the following operations:");
                logger.LogInformation("  1. Create Agent Identity and Agent User");
                logger.LogInformation("  2. Add licenses to Agent User");
                logger.LogInformation("  3. Configure Bot Service");
                logger.LogInformation("No actual changes will be made.");
                return;
            }

            logger.LogInformation("Agent 365 Instance Creation - All Steps");
            logger.LogInformation("Creating agent instance with full configuration...\n");
            
            try
            {
                // Load configuration from specified config file
                var instanceConfig = await LoadConfigAsync(logger, configService, config.FullName);
                if (instanceConfig == null) Environment.Exit(1);

                // Validate Azure CLI authentication, subscription, and environment
                if (!await azureValidator.ValidateAllAsync(instanceConfig.SubscriptionId))
                {
                    logger.LogError("Instance creation cannot proceed without proper Azure CLI authentication and subscription");
                    Environment.Exit(1);
                }
                logger.LogInformation("");

                // Step 1-3: Identity, Licenses, and MCP Registration
                logger.LogInformation("Step 1-3: Creating Agent Identity, adding licenses, and registering MCP servers...");
                logger.LogInformation("");

                // Use C# runner with AuthenticationService and GraphApiService
                var authService = new AuthenticationService(
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<AuthenticationService>());

                var graphService = new GraphApiService(
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<GraphApiService>(),
                    executor);

                var instanceRunner = new A365CreateInstanceRunner(
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<A365CreateInstanceRunner>(),
                    executor,
                    graphService);

                var generatedConfigPath = Path.Combine(
                    config.DirectoryName ?? Environment.CurrentDirectory,
                    "a365.generated.config.json");

                var success = await instanceRunner.RunAsync(config.FullName, generatedConfigPath, step: "all");

                if (!success)
                {
                    logger.LogError("A365CreateInstanceRunner failed");
                    throw new InvalidOperationException("Instance runner execution failed");
                }

                logger.LogInformation("Identity, licenses, and MCP registration configured successfully");

                // Reload configuration to pick up IDs
                logger.LogInformation("Reloading configuration to pick up agent identity and user IDs...");
                instanceConfig = await LoadConfigAsync(logger, configService, config.FullName) 
                    ?? throw new InvalidOperationException("Failed to reload configuration after identity creation");
                logger.LogInformation("     Agent Identity ID: {AgenticAppId}", instanceConfig.AgenticAppId ?? "(not set)");
                logger.LogInformation("     Agent User ID: {AgenticUserId}", instanceConfig.AgenticUserId ?? "(not set)");
                logger.LogInformation("     Agent User Principal Name: {AgentUserPrincipalName}", instanceConfig.AgentUserPrincipalName ?? "(not set)");

                // Admin consent for MCP scopes (oauth2PermissionGrants)
                logger.LogInformation("Granting MCP scopes to Agent Identity via oauth2PermissionGrants");

                var manifestPath = Path.Combine(instanceConfig.DeploymentProjectPath ?? string.Empty, "ToolingManifest.json");
                var scopesForAgent = await ManifestHelper.GetRequiredScopesAsync(manifestPath);

                // clientId must be the *service principal objectId* of the agentic app
                var agenticAppSpObjectId = await graphApiService.LookupServicePrincipalByAppIdAsync(
                    instanceConfig.TenantId,
                    instanceConfig.AgenticAppId ?? string.Empty
                ) ?? throw new InvalidOperationException($"Service Principal not found for agentic app Id {instanceConfig.AgenticAppId}");

                var resourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(instanceConfig.Environment);
                var Agent365ToolsResourceSpObjectId = await graphApiService.LookupServicePrincipalByAppIdAsync(instanceConfig.TenantId, resourceAppId)
                    ?? throw new InvalidOperationException("Agent 365 Tools Service Principal not found for appId " + resourceAppId);

                var response = await graphApiService.CreateOrUpdateOauth2PermissionGrantAsync(
                    instanceConfig.TenantId,
                    agenticAppSpObjectId,
                    Agent365ToolsResourceSpObjectId,
                    scopesForAgent
                );

                if (!response)
                {
                    logger.LogWarning("Failed to create/update oauth2PermissionGrant for agent identity.");
                }

                logger.LogInformation("     OAuth2 admin consent completed for Agent Identity (scopes: {Scopes})",
                    string.Join(' ', scopesForAgent));

                logger.LogInformation("");
                logger.LogInformation("Granting Bot Framework API scopes to Agent Identity");

                var botApiResourceSpObjectId = await graphApiService.EnsureServicePrincipalForAppIdAsync(
                    instanceConfig.TenantId,
                    ConfigConstants.MessagingBotApiAppId);

                // Grant oauth2PermissionGrants: *agent identity SP* -> Messaging Bot API SP
                var botApiGrantOk = await graphApiService.CreateOrUpdateOauth2PermissionGrantAsync(
                    instanceConfig.TenantId,
                    agenticAppSpObjectId,
                    botApiResourceSpObjectId,
                    new[] { "Authorization.ReadWrite", "user_impersonation" });

                if (!botApiGrantOk)
                    logger.LogWarning("Failed to create/update oauth2PermissionGrant for agent identity to Messaging Bot API.");

                logger.LogInformation("Admin consent granted for Agent Identity completed successfully");

                // Register agent with Microsoft Graph API
                logger.LogInformation("     Registering agent with Microsoft Graph API");
                logger.LogInformation("     - Configuring Graph API permissions");
                logger.LogInformation("     - Setting up agent identity integration");
                
                logger.LogInformation("     - Agent Blueprint ID: {AgentBlueprintId}", instanceConfig.AgentBlueprintId);
                logger.LogInformation("     - Required Graph scopes: {Scopes}", string.Join(", ", instanceConfig.AgentIdentityScopes));
                
                // Attempt to read agent identity information from agenticuser.config.json
                var agentUserConfigPath = Path.Combine(Environment.CurrentDirectory, "agenticuser.config.json");
                string? agenticAppId = instanceConfig.AgenticAppId;
                string? agenticUserId = instanceConfig.AgenticUserId;
                var endpointName = $"{instanceConfig.WebAppName}-endpoint";
                
                if (File.Exists(agentUserConfigPath))
                {
                    logger.LogInformation("     - Reading agent identity from agenticuser.config.json");
                    try
                    {
                        var agentUserConfigText = await File.ReadAllTextAsync(agentUserConfigPath);
                        var agentUserConfigJson = JsonSerializer.Deserialize<JsonElement>(agentUserConfigText);
                        
                        if (agentUserConfigJson.TryGetProperty("AgenticAppId", out var identityIdElement))
                        {
                            var extractedIdentityId = identityIdElement.GetString();
                            if (!string.IsNullOrEmpty(extractedIdentityId))
                            {
                                agenticAppId = extractedIdentityId;
                                logger.LogInformation("     - Loaded Agent Identity ID: {AgenticAppId}", agenticAppId);
                            }
                        }
                        
                        if (agentUserConfigJson.TryGetProperty("AgenticUserId", out var userIdElement))
                        {
                            var extractedUserId = userIdElement.GetString();
                            if (!string.IsNullOrEmpty(extractedUserId))
                            {
                                agenticUserId = extractedUserId;
                                logger.LogInformation("     - Loaded Agent User ID: {AgenticUserId}", agenticUserId);
                            }
                        }
                        
                        logger.LogInformation("     - Agent user config loaded for identity lookup");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning("Could not read agent user config: {Message}", ex.Message);
                    }
                }
                

                // Update configuration with the populated values
                logger.LogInformation("Updating configuration with generated values...");
                
                // Get the actual Bot ID (Microsoft App ID) from Azure
                logger.LogInformation("     Querying Bot ID from Azure portal...");
                var botConfig = await botConfigurator.GetBotConfigurationAsync(instanceConfig.ResourceGroup, endpointName);
                var actualBotId = botConfig?.Properties?.MsaAppId ?? endpointName;
                
                if (!string.IsNullOrEmpty(botConfig?.Properties?.MsaAppId))
                {
                    logger.LogInformation("     Retrieved Microsoft App ID: {AppId}", botConfig.Properties.MsaAppId);
                }
                else
                {
                    logger.LogWarning("     Could not retrieve Microsoft App ID from Azure, using bot name as fallback");
                }
                
                // Update Agent365Config state properties
                instanceConfig.BotId = actualBotId;
                instanceConfig.BotMsaAppId = botConfig?.Properties?.MsaAppId;
                instanceConfig.BotMessagingEndpoint = botConfig?.Properties?.Endpoint;
                
                logger.LogInformation("     Agent Blueprint ID: {AgentBlueprintId}", instanceConfig.AgentBlueprintId);
                logger.LogInformation("     Agent Instance ID: {AgenticAppId}", instanceConfig.AgenticAppId);
                logger.LogInformation("     Agent User ID: {AgenticUserId}", instanceConfig.AgenticUserId);
                logger.LogInformation("     Bot ID: {BotId}", instanceConfig.BotId);
                
                // Save the updated configuration using ConfigService
                await configService.SaveStateAsync(instanceConfig);
                logger.LogInformation("Configuration updated and saved successfully");

                logger.LogInformation("Agent 365 instance creation completed successfully!");

                // Sync generated config in project settings from deployment project
                try
                {
                    generatedConfigPath = Path.Combine(
                        config.DirectoryName ?? Environment.CurrentDirectory,
                        "a365.generated.config.json");
                    var platformDetector = new PlatformDetector(LoggerFactory.Create(b => b.AddConsole()).CreateLogger<PlatformDetector>());

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
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Instance creation failed: {Message}", ex.Message);
                throw;
            }
        }, configOption, verboseOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Create identity subcommand
    /// </summary>
    private static Command CreateIdentitySubcommand(
        ILogger<CreateInstanceCommand> logger,
        IConfigService configService,
        CommandExecutor executor)
    {
        var command = new Command("identity", "Create Agent Identity and Agent User");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Configuration file path");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Enable verbose logging");

        var dryRunOption = new Option<bool>(
            ["--dry-run"],
            description: "Show what would be done without executing");

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);

        command.SetHandler(async (config, verbose, dryRun) =>
        {
            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Creating Agent Identity and Agent User");
                logger.LogInformation("This would create Entra ID application and agent user identity");
                return;
            }

            logger.LogInformation("Creating Agent Identity and Agent User...");
            logger.LogInformation(""); // Empty line for readability
            
            try
            {
                // Load configuration from specified file
                var instanceConfig = await LoadConfigAsync(logger, configService, config.FullName);
                if (instanceConfig == null) Environment.Exit(1);

                // Use C# runner with AuthenticationService and GraphApiService
                var authService = new AuthenticationService(
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<AuthenticationService>());

                var graphService = new GraphApiService(
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<GraphApiService>(),
                    executor);

                var instanceRunner = new A365CreateInstanceRunner(
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<A365CreateInstanceRunner>(),
                    executor,
                    graphService);

                var generatedConfigPath = Path.Combine(
                    config.DirectoryName ?? Environment.CurrentDirectory,
                    "a365.generated.config.json");

                var success = await instanceRunner.RunAsync(config.FullName, generatedConfigPath, step: "identity");

                if (!success)
                {
                    logger.LogError("A365CreateInstanceRunner failed");
                    throw new InvalidOperationException("Instance runner execution failed");
                }

                logger.LogInformation("Agent Identity and Agent User created successfully.");
                
                // Sync generated config in project settings from deployment project
                try
                {
                    generatedConfigPath = Path.Combine(
                        config.DirectoryName ?? Environment.CurrentDirectory,
                        "a365.generated.config.json");
                    var platformDetector = new PlatformDetector(LoggerFactory.Create(b => b.AddConsole()).CreateLogger<PlatformDetector>());

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
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Identity creation failed: {Message}", ex.Message);
                throw;
            }
        }, configOption, verboseOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Create licenses subcommand
    /// </summary>
    private static Command CreateLicensesSubcommand(
        ILogger<CreateInstanceCommand> logger,
        IConfigService configService,
        CommandExecutor executor)
    {
        var command = new Command("licenses", "Add licenses to Agent User");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Configuration file path");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Enable verbose logging");

        var dryRunOption = new Option<bool>(
            ["--dry-run"],
            description: "Show what would be done without executing");

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);

        command.SetHandler(async (config, verbose, dryRun) =>
        {
            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Adding licenses to Agent User");
                logger.LogInformation("This would assign M365 and Power Platform licenses to the agent user");
                return;
            }

            logger.LogInformation("Adding licenses to Agent User...");
            logger.LogInformation("");

            try
            {
                var instanceConfig = await LoadConfigAsync(logger, configService, config.FullName);
                if (instanceConfig == null) Environment.Exit(1);

                // Use C# runner with AuthenticationService and GraphApiService
                var authService = new AuthenticationService(
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<AuthenticationService>());

                var graphService = new GraphApiService(
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<GraphApiService>(),
                    executor);

                var instanceRunner = new A365CreateInstanceRunner(
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<A365CreateInstanceRunner>(),
                    executor,
                    graphService);

                var generatedConfigPath = Path.Combine(
                    config.DirectoryName ?? Environment.CurrentDirectory,
                    "a365.generated.config.json");

                var success = await instanceRunner.RunAsync(config.FullName, generatedConfigPath, step: "licenses");

                if (!success)
                {
                    logger.LogError("A365CreateInstanceRunner failed");
                    throw new InvalidOperationException("Instance runner execution failed");
                }

                logger.LogInformation("Licenses assigned successfully.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "License assignment failed: {Message}", ex.Message);
                throw;
            }
        }, configOption, verboseOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Load configuration using unified Agent365Config system
    /// </summary>
    private static async Task<Agent365Config?> LoadConfigAsync(
        ILogger<CreateInstanceCommand> logger,
        IConfigService configService,
        string? configPath = null)
    {
        try
        {
            // Use new unified config system (a365.config.json + a365.generated.config.json)
            var config = configPath != null 
                ? await configService.LoadAsync(configPath) 
                : await configService.LoadAsync();
            return config;
        }
        catch (FileNotFoundException ex)
        {
            logger.LogError("Configuration file not found: {Message}", ex.Message);
            logger.LogInformation("");
            logger.LogInformation("To get started:");
            logger.LogInformation("  1. Copy a365.config.example.json to a365.config.json");
            logger.LogInformation("  2. Edit a365.config.json with your Azure tenant and subscription details");
            logger.LogInformation("  3. Run 'a365 setup' to initialize your environment first");
            logger.LogInformation("  4. Then run 'a365 createinstance' to create agent instances");
            logger.LogInformation("");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading configuration: {Message}", ex.Message);
            return null;
        }
    }
}
