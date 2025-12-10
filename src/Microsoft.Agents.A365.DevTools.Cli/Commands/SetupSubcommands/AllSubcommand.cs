// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

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

            logger.LogInformation("Agent 365 Setup");
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

                // PHASE 1: VALIDATE ALL PREREQUISITES UPFRONT
                logger.LogInformation("Validating all prerequisites...");
                logger.LogInformation("");

                var allErrors = new List<string>();

                // Validate Azure CLI authentication first
                logger.LogInformation("Validating Azure CLI authentication...");
                if (!await azureValidator.ValidateAllAsync(setupConfig.SubscriptionId))
                {
                    allErrors.Add("Azure CLI authentication failed or subscription not set correctly");
                    logger.LogError("Azure CLI authentication validation failed");
                }
                else
                {
                    logger.LogInformation("Azure CLI authentication: OK");
                }

                // Validate Infrastructure prerequisites
                if (!skipInfrastructure && setupConfig.NeedDeployment)
                {
                    logger.LogInformation("Validating Infrastructure prerequisites...");
                    var infraErrors = await InfrastructureSubcommand.ValidateAsync(setupConfig, azureValidator, CancellationToken.None);
                    if (infraErrors.Count > 0)
                    {
                        allErrors.AddRange(infraErrors.Select(e => $"Infrastructure: {e}"));
                        foreach (var error in infraErrors)
                        {
                            logger.LogError("  - {Error}", error);
                        }
                    }
                    else
                    {
                        logger.LogInformation("Infrastructure prerequisites: OK");
                    }
                }

                // Validate Blueprint prerequisites
                logger.LogInformation("Validating Blueprint prerequisites...");
                var blueprintErrors = await BlueprintSubcommand.ValidateAsync(setupConfig, azureValidator, CancellationToken.None);
                if (blueprintErrors.Count > 0)
                {
                    allErrors.AddRange(blueprintErrors.Select(e => $"Blueprint: {e}"));
                    foreach (var error in blueprintErrors)
                    {
                        logger.LogError("  - {Error}", error);
                    }
                }
                else
                {
                    logger.LogInformation("Blueprint prerequisites: OK");
                }

                // Stop if any validation failed
                if (allErrors.Count > 0)
                {
                    logger.LogError("");
                    logger.LogError("Setup cannot proceed due to validation failures:");
                    foreach (var error in allErrors)
                    {
                        logger.LogError("  - {Error}", error);
                    }
                    logger.LogError("");
                    logger.LogError("Please fix the errors above and try again");
                    setupResults.Errors.AddRange(allErrors);
                    ExceptionHandler.ExitWithCleanup(1);
                    return;
                }

                logger.LogInformation("");
                logger.LogInformation("All validations passed. Starting setup execution...");
                logger.LogInformation("");

                var generatedConfigPath = Path.Combine(
                    config.DirectoryName ?? Environment.CurrentDirectory,
                    "a365.generated.config.json");

                // Step 1: Infrastructure (optional)
                try
                {
                    logger.LogInformation("Step 1:");
                    logger.LogInformation("");

                    bool setupInfra = await InfrastructureSubcommand.CreateInfrastructureImplementationAsync(
                        logger,
                        config.FullName,
                        generatedConfigPath,
                        executor,
                        platformDetector,
                        setupConfig.NeedDeployment,
                        skipInfrastructure,
                        CancellationToken.None);

                    setupResults.InfrastructureCreated = skipInfrastructure ? false : setupInfra;
                }
                catch (Agent365Exception infraEx)
                {
                    setupResults.InfrastructureCreated = false;
                    setupResults.Errors.Add($"Infrastructure: {infraEx.Message}");
                    throw;
                }
                catch (Exception infraEx)
                {
                    setupResults.InfrastructureCreated = false;
                    setupResults.Errors.Add($"Infrastructure: {infraEx.Message}");
                    logger.LogError("Failed to create infrastructure: {Message}", infraEx.Message);
                    throw;
                }

                // Step 2: Blueprint
                logger.LogInformation("");
                logger.LogInformation("Step 2:");
                logger.LogInformation("");

                try
                {
                    var result = await BlueprintSubcommand.CreateBlueprintImplementationAsync(
                        setupConfig,
                        config,
                        executor,
                        azureValidator,
                        logger,
                        skipInfrastructure,
                        true,
                        configService,
                        botConfigurator,
                        platformDetector,
                        graphApiService
                        );

                    setupResults.BlueprintCreated = result.BlueprintCreated;
                    setupResults.MessagingEndpointRegistered = result.EndpointRegistered;
                    
                    if (result.EndpointAlreadyExisted)
                    {
                        setupResults.Warnings.Add("Messaging endpoint already exists (not newly created)");
                    }

                    // If endpoint registration was attempted but failed, add to errors
                    // Do NOT add error if registration was skipped (--no-endpoint or missing config)
                    if (result.EndpointRegistrationAttempted && !result.EndpointRegistered)
                    {
                        setupResults.Errors.Add("Messaging endpoint registration failed");
                    }

                    if (!result.BlueprintCreated)
                    {
                        throw new GraphApiException(
                            operation: "Create Agent Blueprint",
                            reason: "Blueprint creation failed. This typically indicates missing permissions or insufficient privileges.",
                            isPermissionIssue: true);
                    }

                    // CRITICAL: Wait for file system to ensure config file is fully written
                    // Blueprint creation writes directly to disk and may not be immediately readable
                    logger.LogInformation("Ensuring configuration file is synchronized...");
                    await Task.Delay(2000); // 2 second delay to ensure file write is complete

                    // Reload config to get blueprint ID
                    // Use full path to ensure we're reading from the correct location
                    var fullConfigPath = Path.GetFullPath(config.FullName);
                    setupConfig = await configService.LoadAsync(fullConfigPath);
                    setupResults.BlueprintId = setupConfig.AgentBlueprintId;

                    // Validate blueprint ID was properly saved
                    if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
                    {
                        throw new SetupValidationException(
                            "Blueprint creation completed but AgentBlueprintId was not saved to configuration. " +
                            "This is required for the next steps (MCP permissions and Bot permissions).");
                    }
                }
                catch (Agent365Exception blueprintEx)
                {
                    setupResults.BlueprintCreated = false;
                    setupResults.MessagingEndpointRegistered = false;
                    setupResults.Errors.Add($"Blueprint: {blueprintEx.Message}");
                    throw;
                }
                catch (Exception blueprintEx)
                {
                    setupResults.BlueprintCreated = false;
                    setupResults.MessagingEndpointRegistered = false;
                    setupResults.Errors.Add($"Blueprint: {blueprintEx.Message}");
                    logger.LogError("Failed to create blueprint: {Message}", blueprintEx.Message);
                    throw;
                }

                // Step 3: MCP Permissions
                logger.LogInformation("");
                logger.LogInformation("Step 3:");
                logger.LogInformation("");

                try
                {
                    bool mcpPermissionSetup = await PermissionsSubcommand.ConfigureMcpPermissionsAsync(
                        config.FullName,
                        logger,
                        configService,
                        executor,
                        graphApiService,
                        setupConfig,
                        true,
                        setupResults);

                    setupResults.McpPermissionsConfigured = mcpPermissionSetup;
                    if (mcpPermissionSetup)
                    {
                        setupResults.InheritablePermissionsConfigured = setupConfig.IsInheritanceConfigured();
                    }
                }
                catch (Exception mcpPermEx)
                {
                    setupResults.McpPermissionsConfigured = false;
                    setupResults.Errors.Add($"MCP Permissions: {mcpPermEx.Message}");
                    logger.LogWarning("MCP permissions failed: {Message}. Setup will continue, but MCP server permissions must be configured manually", mcpPermEx.Message);
                }

                // Step 4: Bot API Permissions

                logger.LogInformation("");
                logger.LogInformation("Step 4:");
                logger.LogInformation("");

                try
                {
                    bool botPermissionSetup = await PermissionsSubcommand.ConfigureBotPermissionsAsync(
                        config.FullName,
                        logger,
                        configService,
                        executor,
                        setupConfig,
                        graphApiService,
                        true,
                        setupResults);

                    setupResults.BotApiPermissionsConfigured = botPermissionSetup;
                }
                catch (Exception botPermEx)
                {
                    setupResults.BotApiPermissionsConfigured = false;
                    setupResults.Errors.Add($"Bot API Permissions: {botPermEx.Message}");
                    logger.LogWarning("Bot permissions failed: {Message}. Setup will continue, but Bot API permissions must be configured manually", botPermEx.Message);
                }

                // Display setup summary
                logger.LogInformation("");
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
