// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;

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
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        IClientAppValidator clientAppValidator,
        BlueprintLookupService blueprintLookupService,
        FederatedCredentialService federatedCredentialService)
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

        var skipRequirementsOption = new Option<bool>(
            "--skip-requirements",
            description: "Skip requirements validation check\n" +
                        "Use with caution: setup may fail if prerequisites are not met");

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(skipInfrastructureOption);
        command.AddOption(skipRequirementsOption);

        command.SetHandler(async (config, verbose, dryRun, skipInfrastructure, skipRequirements) =>
        {
            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Complete Agent 365 Setup");
                logger.LogInformation("This would execute the following operations:");
                logger.LogInformation("");
                
                if (!skipRequirements)
                {
                    logger.LogInformation("  0. Validate prerequisites (PowerShell modules, etc.)");
                }
                else
                {
                    logger.LogInformation("  0. [SKIPPED] Requirements validation (--skip-requirements flag used)");
                }
                
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
            
            if (skipRequirements)
            {
                logger.LogInformation("NOTE: Skipping requirements validation (--skip-requirements flag used)");
            }
            
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
                
                // Configure GraphApiService with custom client app ID if available
                // This ensures inheritable permissions operations use the validated custom app
                if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
                {
                    graphApiService.CustomClientAppId = setupConfig.ClientAppId;
                }

                // PHASE 0: CHECK REQUIREMENTS (if not skipped)
                if (!skipRequirements)
                {
                    logger.LogDebug("Validating system prerequisites...");

                    try
                    {
                        var result = await RequirementsSubcommand.RunRequirementChecksAsync(
                            RequirementsSubcommand.GetRequirementChecks(clientAppValidator),
                            setupConfig,
                            logger,
                            category: null,
                            CancellationToken.None);

                        if (!result)
                        {
                            logger.LogError("");
                            logger.LogError("Setup cannot proceed due to the failed requirement checks above. Please fix the issues above and then try again.");
                            ExceptionHandler.ExitWithCleanup(1);
                            return;
                        }
                    }
                    catch (Exception reqEx)
                    {
                        logger.LogWarning(reqEx, "Requirements check encountered an error: {Message}", reqEx.Message);
                        logger.LogWarning("Continuing with setup, but some prerequisites may be missing.");
                        logger.LogWarning("");
                    }
                }
                else
                {
                    logger.LogDebug("Skipping requirements validation (--skip-requirements flag used)");
                }

                // PHASE 1: VALIDATE ALL PREREQUISITES UPFRONT
                logger.LogDebug("Validating all prerequisites...");

                var allErrors = new List<string>();

                // Validate Azure CLI authentication first
                logger.LogDebug("Validating Azure CLI authentication...");
                if (!await azureValidator.ValidateAllAsync(setupConfig.SubscriptionId))
                {
                    allErrors.Add("Azure CLI authentication failed or subscription not set correctly");
                    logger.LogError("Azure CLI authentication validation failed");
                }
                else
                {
                    logger.LogDebug("Azure CLI authentication: OK");
                }

                // Validate Infrastructure prerequisites
                if (!skipInfrastructure && setupConfig.NeedDeployment)
                {
                    logger.LogDebug("Validating Infrastructure prerequisites...");
                    var infraErrors = await InfrastructureSubcommand.ValidateAsync(setupConfig, azureValidator, CancellationToken.None);
                    if (infraErrors.Count > 0)
                    {
                        allErrors.AddRange(infraErrors.Select(e => $"Infrastructure: {e}"));
                    }
                    else
                    {
                        logger.LogDebug("Infrastructure prerequisites: OK");
                    }
                }

                // Validate Blueprint prerequisites
                logger.LogDebug("Validating Blueprint prerequisites...");
                var blueprintErrors = await BlueprintSubcommand.ValidateAsync(setupConfig, azureValidator, clientAppValidator, CancellationToken.None);
                if (blueprintErrors.Count > 0)
                {
                    allErrors.AddRange(blueprintErrors.Select(e => $"Blueprint: {e}"));
                }
                else
                {
                    logger.LogDebug("Blueprint prerequisites: OK");
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

                logger.LogDebug("All validations passed. Starting setup execution...");

                var generatedConfigPath = Path.Combine(
                    config.DirectoryName ?? Environment.CurrentDirectory,
                    "a365.generated.config.json");

                // Step 1: Infrastructure (optional)
                try
                {

                    var (setupInfra, infraAlreadyExisted) = await InfrastructureSubcommand.CreateInfrastructureImplementationAsync(
                        logger,
                        config.FullName,
                        generatedConfigPath,
                        executor,
                        platformDetector,
                        setupConfig.NeedDeployment,
                        skipInfrastructure,
                        CancellationToken.None);

                    setupResults.InfrastructureCreated = skipInfrastructure ? false : setupInfra;
                    setupResults.InfrastructureAlreadyExisted = infraAlreadyExisted;
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
                        graphApiService,
                        blueprintService,
                        blueprintLookupService,
                        federatedCredentialService
                        );

                    setupResults.BlueprintCreated = result.BlueprintCreated;
                    setupResults.BlueprintAlreadyExisted = result.BlueprintAlreadyExisted;
                    setupResults.MessagingEndpointRegistered = result.EndpointRegistered;
                    setupResults.EndpointAlreadyExisted = result.EndpointAlreadyExisted;
                    
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
                try
                {
                    bool mcpPermissionSetup = await PermissionsSubcommand.ConfigureMcpPermissionsAsync(
                        config.FullName,
                        logger,
                        configService,
                        executor,
                        graphApiService,
                        blueprintService,
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
                try
                {
                    bool botPermissionSetup = await PermissionsSubcommand.ConfigureBotPermissionsAsync(
                        config.FullName,
                        logger,
                        configService,
                        executor,
                        setupConfig,
                        graphApiService,
                        blueprintService,
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
                var logFilePath = ConfigService.GetCommandLogPath(CommandNames.Setup);
                ExceptionHandler.HandleAgent365Exception(ex, logFilePath: logFilePath);
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Setup failed: {Message}", ex.Message);
                throw;
            }
        }, configOption, verboseOption, dryRunOption, skipInfrastructureOption, skipRequirementsOption);

        return command;
    }
}
