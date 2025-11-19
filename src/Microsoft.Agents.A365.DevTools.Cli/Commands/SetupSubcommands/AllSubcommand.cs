// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// All subcommand - Runs complete setup (all steps in sequence)
/// This is the equivalent of the original monolithic 'a365 setup' command
/// Required permissions:
///   - Azure Subscription Contributor/Owner (for infrastructure and endpoint)
///   - Agent ID Developer role (for blueprint creation)
///   - Global Administrator (for permission grants and admin consent)
/// </summary>
internal static class AllSubcommand
{
    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        IBotConfigurator botConfigurator,
        IAzureValidator azureValidator,
        AzureWebAppCreator webAppCreator,
        PlatformDetector platformDetector)
    {
        var command = new Command("all", 
            "Run complete Agent 365 setup (all steps in sequence)\n" +
            "Includes: Blueprint creation + Permission configuration + Endpoint registration\n\n" +
            "Minimum required permissions (Global Administrator has all of these):\n" +
            "  - Azure Subscription Contributor (for infrastructure and endpoint)\n" +
            "  - Agent ID Developer role (for blueprint creation)\n" +
            "  - Global Administrator (for permission grants and admin consent)\n\n" +
            "Note: If you have Global Administrator, you don't need the other roles.\n");

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

        var skipInfrastructureOption = new Option<bool>(
            "--skip-infrastructure",
            description: "Skip Azure infrastructure creation (use if infrastructure already exists)\n" +
                        "This will still create: Blueprint + Permissions + Endpoint");

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(skipInfrastructureOption);

        command.SetHandler(async (config, verbose, dryRun, skipInfrastructure) =>
        {
            if (dryRun)
            {
                var dryRunConfig = await configService.LoadAsync(config.FullName);
                
                logger.LogInformation("DRY RUN: Complete Agent 365 Setup");
                logger.LogInformation("This would execute the following operations:");
                
                if (!skipInfrastructure)
                {
                    logger.LogInformation("  1. Create Azure infrastructure (Resource Group, App Service Plan, Web App, MSI)");
                }
                else
                {
                    logger.LogInformation("  1. [SKIPPED] Azure infrastructure (--skip-infrastructure flag used)");
                }
                
                logger.LogInformation("  2. Create agent blueprint (Entra ID application)");
                logger.LogInformation("  3. Configure MCP server permissions");
                logger.LogInformation("  4. Configure Bot API permissions");
                logger.LogInformation("  5. Register blueprint messaging endpoint");
                logger.LogInformation("No actual changes will be made.");
                return;
            }

            logger.LogInformation("Agent 365 Setup - Complete");
            logger.LogInformation("Running all setup steps...");
            
            if (skipInfrastructure)
            {
                logger.LogInformation("NOTE: Skipping infrastructure creation (--skip-infrastructure flag used)");
            }
            
            logger.LogInformation("");

            var setupResults = new SetupResults();

            try
            {
                // Load configuration
                var setupConfig = await configService.LoadAsync(config.FullName);

                // Validate Azure authentication
                if (!await azureValidator.ValidateAllAsync(setupConfig.SubscriptionId))
                {
                    Environment.Exit(1);
                }

                logger.LogInformation("");

                // Step 1: Create blueprint (and optionally infrastructure)
                logger.LogInformation("Step 1: Creating agent blueprint...");
                logger.LogInformation("");

                var generatedConfigPath = Path.Combine(
                    config.DirectoryName ?? Environment.CurrentDirectory,
                    "a365.generated.config.json");

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

                // Pass skipInfrastructure to setup runner
                var success = await setupRunner.RunAsync(config.FullName, generatedConfigPath, skipInfrastructure);

                if (!success)
                {
                    setupResults.BlueprintCreated = false;
                    setupResults.Errors.Add("Agent blueprint creation failed");
                    throw new InvalidOperationException("Setup runner execution failed");
                }

                setupResults.BlueprintCreated = true;

                // Reload config to get blueprint ID
                var tempConfig = await configService.LoadAsync(config.FullName);
                setupResults.BlueprintId = tempConfig.AgentBlueprintId;

                logger.LogInformation("Agent blueprint created successfully");

                // Step 2a: MCP Permissions
                logger.LogInformation("");
                logger.LogInformation("Step 2a: Configuring MCP server permissions...");
                logger.LogInformation("");

                setupConfig = await configService.LoadAsync(config.FullName);

                var manifestPath = Path.Combine(setupConfig.DeploymentProjectPath ?? string.Empty, "toolingManifest.json");
                var toolingScopes = await ManifestHelper.GetRequiredScopesAsync(manifestPath);

                try
                {
                    await SetupHelpers.EnsureMcpOauth2PermissionGrantsAsync(
                        graphService, setupConfig, toolingScopes, logger);

                    await SetupHelpers.EnsureMcpInheritablePermissionsAsync(
                        graphService, setupConfig, toolingScopes, logger);

                    setupResults.McpPermissionsConfigured = true;
                    setupResults.InheritablePermissionsConfigured = tempConfig.InheritanceConfigured;

                    logger.LogInformation("MCP server permissions configured successfully");
                }
                catch (Exception mcpEx)
                {
                    setupResults.McpPermissionsConfigured = false;
                    setupResults.InheritablePermissionsConfigured = false;
                    setupResults.Errors.Add($"MCP permissions: {mcpEx.Message}");
                    logger.LogError("Failed to configure MCP server permissions: {Message}", mcpEx.Message);
                    logger.LogWarning("Setup will continue, but MCP server permissions must be configured manually");
                }

                // Step 2b: Bot API Permissions
                logger.LogInformation("");
                logger.LogInformation("Step 2b: Configuring Messaging Bot API permissions...");
                logger.LogInformation("");

                try
                {
                    if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
                        throw new InvalidOperationException("AgentBlueprintId is required.");

                    var blueprintSpObjectId = await graphService.LookupServicePrincipalByAppIdAsync(
                        setupConfig.TenantId, setupConfig.AgentBlueprintId)
                        ?? throw new InvalidOperationException($"Blueprint Service Principal not found");

                    var botApiResourceSpObjectId = await graphService.EnsureServicePrincipalForAppIdAsync(
                        setupConfig.TenantId, ConfigConstants.MessagingBotApiAppId);

                    var botApiGrantOk = await graphService.CreateOrUpdateOauth2PermissionGrantAsync(
                        setupConfig.TenantId,
                        blueprintSpObjectId,
                        botApiResourceSpObjectId,
                        new[] { "Authorization.ReadWrite", "user_impersonation" });

                    if (!botApiGrantOk)
                    {
                        setupResults.Warnings.Add("Failed to create/update oauth2PermissionGrant for Messaging Bot API");
                    }

                    var (ok, already, err) = await graphService.SetInheritablePermissionsAsync(
                        setupConfig.TenantId,
                        setupConfig.AgentBlueprintId,
                        ConfigConstants.MessagingBotApiAppId,
                        new[] { "Authorization.ReadWrite", "user_impersonation" });

                    if (!ok && !already)
                    {
                        setupResults.Warnings.Add($"Failed to set inheritable permissions for Messaging Bot API: {err}");
                    }

                    setupResults.BotApiPermissionsConfigured = true;
                    logger.LogInformation("Messaging Bot API permissions configured successfully");
                }
                catch (Exception botEx)
                {
                    setupResults.BotApiPermissionsConfigured = false;
                    setupResults.Errors.Add($"Bot API permissions: {botEx.Message}");
                    logger.LogError("Failed to configure Bot API permissions: {Message}", botEx.Message);
                }

                // Step 3: Register endpoint
                logger.LogInformation("");
                logger.LogInformation("Step 3: Registering blueprint messaging endpoint...");
                logger.LogInformation("");

                try
                {
                    setupConfig = await configService.LoadAsync(config.FullName);

                    await SetupHelpers.RegisterBlueprintMessagingEndpointAsync(
                        setupConfig, logger, botConfigurator);

                    setupResults.MessagingEndpointRegistered = true;
                    logger.LogInformation("Blueprint messaging endpoint registered successfully");
                }
                catch (Exception endpointEx)
                {
                    setupResults.MessagingEndpointRegistered = false;
                    setupResults.Errors.Add($"Messaging endpoint: {endpointEx.Message}");
                    logger.LogError("Failed to register messaging endpoint: {Message}", endpointEx.Message);
                }

                // Sync generated config to project settings
                try
                {
                    await ProjectSettingsSyncHelper.ExecuteAsync(
                        a365ConfigPath: config.FullName,
                        a365GeneratedPath: generatedConfigPath,
                        configService: configService,
                        platformDetector: platformDetector,
                        logger: logger);

                    logger.LogDebug("Generated config synced to project settings");
                }
                catch (Exception syncEx)
                {
                    logger.LogWarning(syncEx, "Project settings sync failed (non-blocking)");
                }

                // Display verification info and summary
                await SetupHelpers.DisplayVerificationInfoAsync(config, logger);
                SetupHelpers.DisplaySetupSummary(setupResults, logger);
            }
            catch (Agent365Exception ex)
            {
                ExceptionHandler.HandleAgent365Exception(ex);
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Setup failed: {Message}", ex.Message);
                throw;
            }
        }, configOption, verboseOption, dryRunOption, skipInfrastructureOption);

        return command;
    }
}
