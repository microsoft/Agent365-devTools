// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Text.Json.Nodes;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// All subcommand - Runs complete setup (all steps in sequence)
/// Orchestrates individual subcommand implementations
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
        PlatformDetector platformDetector,
        GraphApiService graphApiService)
    {
        var command = new Command("all", 
            "Run complete Agent 365 setup (all steps in sequence)\n" +
            "Includes: Infrastructure + Blueprint + Permissions + Endpoint\n\n" +
            "Minimum required permissions (Global Administrator has all of these):\n" +
            "  - Azure Subscription Contributor (for infrastructure and endpoint)\n" +
            "  - Agent ID Developer role (for blueprint creation)\n" +
            "  - Global Administrator (for permission grants and admin consent)\n\n");

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
                    logger.LogInformation("  1. Create Azure infrastructure");
                }
                else
                {
                    logger.LogInformation("  1. [SKIPPED] Azure infrastructure (--skip-infrastructure flag used)");
                }
                
                logger.LogInformation("  2. Create agent blueprint (Entra ID application)");
                logger.LogInformation("  3. Configure MCP server permissions");
                logger.LogInformation("  4. Configure Bot API permissions");
                logger.LogInformation("  5. Register blueprint messaging endpoint and sync project settings");
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

                var generatedConfigPath = Path.Combine(
                    config.DirectoryName ?? Environment.CurrentDirectory,
                    "a365.generated.config.json");

                // Step 1: Infrastructure (optional)
                if (!skipInfrastructure)
                {
                    logger.LogInformation("Step 1: Creating Azure infrastructure...");
                    logger.LogInformation("");

                    try
                    {
                        await InfrastructureSubcommand.CreateInfrastructureImplementationAsync(
                            logger,
                            config.FullName,
                            generatedConfigPath,
                            executor,
                            platformDetector,
                            CancellationToken.None);

                        setupResults.InfrastructureCreated = true;
                        logger.LogInformation("Azure infrastructure created successfully");
                    }
                    catch (Exception infraEx)
                    {
                        setupResults.InfrastructureCreated = false;
                        setupResults.Errors.Add($"Infrastructure: {infraEx.Message}");
                        logger.LogError(infraEx, "Failed to create infrastructure: {Message}", infraEx.Message);
                        throw;
                    }
                }
                else
                {
                    logger.LogInformation("Step 1: [SKIPPED] Infrastructure creation (--skip-infrastructure)");
                }

                // Step 2: Blueprint
                logger.LogInformation("");
                logger.LogInformation("Step 2: Creating agent blueprint...");
                logger.LogInformation("");

                try
                {
                    await BlueprintSubcommand.CreateBlueprintImplementationAsync(
                        setupConfig,
                        config,
                        executor,
                        azureValidator,
                        logger);

                    setupResults.BlueprintCreated = true;

                    // Reload config to get blueprint ID
                    var tempConfig = await configService.LoadAsync(config.FullName);
                    setupResults.BlueprintId = tempConfig.AgentBlueprintId;

                    logger.LogInformation("Agent blueprint created successfully");
                }
                catch (Exception blueprintEx)
                {
                    setupResults.BlueprintCreated = false;
                    setupResults.Errors.Add($"Blueprint: {blueprintEx.Message}");
                    logger.LogError(blueprintEx, "Failed to create blueprint: {Message}", blueprintEx.Message);
                    throw;
                }

                // Step 3: MCP Permissions
                logger.LogInformation("");
                logger.LogInformation("Step 3: Configuring MCP server permissions...");
                logger.LogInformation("");

                try
                {
                    await PermissionsSubcommand.ConfigureMcpPermissionsAsync(
                        config.FullName,
                        logger,
                        configService,
                        executor,
                        graphApiService,
                        setupConfig);

                    setupResults.McpPermissionsConfigured = true;
                    
                    var tempConfig = await configService.LoadAsync(config.FullName);
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

                // Step 4: Bot API Permissions
                logger.LogInformation("");
                logger.LogInformation("Step 4: Configuring Messaging Bot API permissions...");
                logger.LogInformation("");

                try
                {
                    await PermissionsSubcommand.ConfigureBotPermissionsAsync(
                        config.FullName,
                        logger,
                        configService,
                        executor,
                        setupConfig,
                        graphApiService);

                    setupResults.BotApiPermissionsConfigured = true;
                    logger.LogInformation("Messaging Bot API permissions configured successfully");
                }
                catch (Exception botEx)
                {
                    setupResults.BotApiPermissionsConfigured = false;
                    setupResults.Errors.Add($"Bot API permissions: {botEx.Message}");
                    logger.LogError("Failed to configure Bot API permissions: {Message}", botEx.Message);
                }

                // Step 5: Register endpoint and sync
                logger.LogInformation("");
                logger.LogInformation("Step 5: Registering blueprint messaging endpoint...");
                logger.LogInformation("");

                try
                {
                    await EndpointSubcommand.RegisterEndpointAndSyncAsync(
                        config.FullName,
                        logger,
                        configService,
                        botConfigurator,
                        platformDetector);

                    setupResults.MessagingEndpointRegistered = true;
                    logger.LogInformation("Blueprint messaging endpoint registered successfully");
                }
                catch (Exception endpointEx)
                {
                    setupResults.MessagingEndpointRegistered = false;
                    setupResults.Errors.Add($"Messaging endpoint: {endpointEx.Message}");
                    logger.LogError("Failed to register messaging endpoint: {Message}", endpointEx.Message);
                }

                // Display verification info and summary
                logger.LogInformation("");
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

    private static bool CheckNeedDeployment(JsonObject setupConfig)
    {
        bool needDeployment = true; // default
        if (setupConfig.TryGetPropertyValue("needDeployment", out var needDeploymentNode) &&
            needDeploymentNode is JsonValue nv)
        {
            // Prefer native bool
            if (nv.TryGetValue<bool>(out var boolVal))
            {
                needDeployment = boolVal;
            }
            else if (nv.TryGetValue<string?>(out var strVal) && !string.IsNullOrWhiteSpace(strVal))
            {
                // Backward compatibility with "yes"/"no" / "true"/"false"
                needDeployment =
                    !string.Equals(strVal, "no", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(strVal, "false", StringComparison.OrdinalIgnoreCase);
            }
        }

        return needDeployment;
    }
}
