// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// Run command - Unified deployment workflow that performs:
/// - Setup (if agent does not exist)
/// - Deploy (application binaries to Azure App Service)
/// - Publish (manifest to MOS)
/// 
/// This command is designed for quick, one-command deployments.
/// </summary>
public class RunCommand
{
    // MOS Titles service URLs
    private const string MosTitlesUrlProd = "https://titles.prod.mos.microsoft.com";

    /// <summary>
    /// Gets the appropriate MOS Titles URL based on environment variable override or defaults to production.
    /// </summary>
    private static string GetMosTitlesUrl(string? tenantId)
    {
        var envUrl = Environment.GetEnvironmentVariable("MOS_TITLES_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
        {
            return envUrl;
        }

        return MosTitlesUrlProd;
    }

    /// <summary>
    /// Gets the project directory from config, with fallback to current directory.
    /// </summary>
    private static string GetProjectDirectory(Agent365Config config, ILogger logger)
    {
        var projectPath = config.DeploymentProjectPath;

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            logger.LogWarning("deploymentProjectPath not configured, using current directory.");
            return Environment.CurrentDirectory;
        }

        try
        {
            var absolutePath = Path.IsPathRooted(projectPath)
                ? projectPath
                : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, projectPath));

            if (!Directory.Exists(absolutePath))
            {
                logger.LogWarning("Configured deploymentProjectPath does not exist: {Path}. Using current directory.", absolutePath);
                return Environment.CurrentDirectory;
            }

            return absolutePath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve deploymentProjectPath: {Path}. Using current directory.", projectPath);
            return Environment.CurrentDirectory;
        }
    }

    public static Command CreateCommand(
        ILogger<RunCommand> logger,
        IConfigService configService,
        CommandExecutor executor,
        IBotConfigurator botConfigurator,
        IAzureValidator azureValidator,
        AzureWebAppCreator webAppCreator,
        PlatformDetector platformDetector,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        BlueprintLookupService blueprintLookupService,
        FederatedCredentialService federatedCredentialService,
        IClientAppValidator clientAppValidator,
        DeploymentService deploymentService,
        AgentPublishService agentPublishService,
        ManifestTemplateService manifestTemplateService)
    {
        var command = new Command("run",
            "Run complete agent deployment workflow (setup + deploy + publish)\n\n" +
            "This command automatically:\n" +
            "  1. Sets up infrastructure and agent blueprint (if not already present)\n" +
            "  2. Deploys application binaries to Azure App Service\n" +
            "  3. Publishes the agent manifest to Microsoft 365\n\n" +
            "Perfect for CI/CD pipelines and quick deployments.\n\n" +
            "Examples:\n" +
            "  a365 run                        # Run with default config\n" +
            "  a365 run --config my-agent.json # Use custom config file\n" +
            "  a365 run --dry-run              # Preview what will be executed\n" +
            "  a365 run --force-setup          # Force setup even if agent exists");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Path to the configuration file (default: a365.config.json)");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Enable verbose logging");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            description: "Show what would be executed without running");

        var forceSetupOption = new Option<bool>(
            "--force-setup",
            description: "Force setup execution even if agent blueprint exists");

        var skipInfrastructureOption = new Option<bool>(
            "--skip-infrastructure",
            description: "Skip Azure infrastructure creation during setup\n" +
                        "(use if infrastructure already exists)");

        var skipRequirementsOption = new Option<bool>(
            "--skip-requirements",
            description: "Skip requirements validation check during setup");

        var skipPublishOption = new Option<bool>(
            "--skip-publish",
            description: "Skip the publish step (only run setup and deploy)");

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(forceSetupOption);
        command.AddOption(skipInfrastructureOption);
        command.AddOption(skipRequirementsOption);
        command.AddOption(skipPublishOption);

        command.SetHandler(async (FileInfo config, bool verbose, bool dryRun, bool forceSetup, bool skipInfrastructure, bool skipRequirements, bool skipPublish) =>
        {
            try
            {
                await ExecuteRunWorkflowAsync(
                    logger,
                    configService,
                    executor,
                    botConfigurator,
                    azureValidator,
                    webAppCreator,
                    platformDetector,
                    graphApiService,
                    blueprintService,
                    blueprintLookupService,
                    federatedCredentialService,
                    clientAppValidator,
                    deploymentService,
                    agentPublishService,
                    manifestTemplateService,
                    config,
                    verbose,
                    dryRun,
                    forceSetup,
                    skipInfrastructure,
                    skipRequirements,
                    skipPublish);
            }
            catch (Agent365Exception ex)
            {
                var logFilePath = ConfigService.GetCommandLogPath(CommandNames.Run);
                ExceptionHandler.HandleAgent365Exception(ex, logFilePath: logFilePath);
                Environment.Exit(ex.ExitCode);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Run command failed: {Message}", ex.Message);
                throw;
            }
        }, configOption, verboseOption, dryRunOption, forceSetupOption, skipInfrastructureOption, skipRequirementsOption, skipPublishOption);

        return command;
    }

    private static async Task ExecuteRunWorkflowAsync(
        ILogger<RunCommand> logger,
        IConfigService configService,
        CommandExecutor executor,
        IBotConfigurator botConfigurator,
        IAzureValidator azureValidator,
        AzureWebAppCreator webAppCreator,
        PlatformDetector platformDetector,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        BlueprintLookupService blueprintLookupService,
        FederatedCredentialService federatedCredentialService,
        IClientAppValidator clientAppValidator,
        DeploymentService deploymentService,
        AgentPublishService agentPublishService,
        ManifestTemplateService manifestTemplateService,
        FileInfo configFile,
        bool verbose,
        bool dryRun,
        bool forceSetup,
        bool skipInfrastructure,
        bool skipRequirements,
        bool skipPublish)
    {
        logger.LogInformation("Agent 365 Run - Unified Deployment Workflow");
        logger.LogInformation("============================================");
        logger.LogInformation("");

        // Step 1: Load and validate configuration
        logger.LogInformation("Loading configuration from {ConfigFile}...", configFile.FullName);
        var config = await configService.LoadAsync(configFile.FullName);
        if (config == null)
        {
            throw new SetupValidationException("Failed to load configuration file.");
        }

        // Configure GraphApiService with custom client app ID if available
        if (!string.IsNullOrWhiteSpace(config.ClientAppId))
        {
            graphApiService.CustomClientAppId = config.ClientAppId;
            blueprintLookupService.CustomClientAppId = config.ClientAppId;
        }

        // Step 2: Check if agent blueprint already exists
        bool needsSetup = forceSetup;
        if (!forceSetup)
        {
            logger.LogInformation("Checking if agent blueprint already exists...");
            needsSetup = !await CheckAgentExistsAsync(config, blueprintLookupService, logger);
        }
        else
        {
            logger.LogInformation("Force setup enabled - will run setup regardless of existing agent");
        }

        // Handle dry-run mode
        if (dryRun)
        {
            await ShowDryRunInfoAsync(logger, config, needsSetup, skipInfrastructure, skipPublish);
            return;
        }

        logger.LogInformation("");

        // Step 3: Execute setup if needed
        if (needsSetup)
        {
            logger.LogInformation("STEP 1/3: Running Setup...");
            logger.LogInformation("─────────────────────────────────────────");
            await ExecuteSetupAllAsync(
                config,
                configFile,
                logger,
                configService,
                executor,
                botConfigurator,
                azureValidator,
                webAppCreator,
                platformDetector,
                graphApiService,
                blueprintService,
                blueprintLookupService,
                federatedCredentialService,
                clientAppValidator,
                skipInfrastructure,
                skipRequirements);

            // Reload config to get updated values (e.g., blueprintId)
            config = await configService.LoadAsync(configFile.FullName);
            if (config == null)
            {
                throw new SetupValidationException("Failed to reload configuration after setup.");
            }

            logger.LogInformation("");
        }
        else
        {
            logger.LogInformation("STEP 1/3: Setup - SKIPPED (agent blueprint already exists)");
            logger.LogInformation("─────────────────────────────────────────");
            logger.LogInformation("Blueprint: {BlueprintName}", config.AgentBlueprintDisplayName);
            if (!string.IsNullOrWhiteSpace(config.AgentBlueprintId))
            {
                logger.LogInformation("Blueprint ID: {BlueprintId}", config.AgentBlueprintId);
            }
            logger.LogInformation("");
        }

        // Step 4: Execute deployment (continue on failure)
        bool deploymentSucceeded = true;
        if (config.NeedDeployment)
        {
            logger.LogInformation("STEP 2/3: Deploying Application...");
            logger.LogInformation("─────────────────────────────────────────");
            try
            {
                await ExecuteDeployAsync(config, configFile.FullName, configService, azureValidator, executor, deploymentService, logger);
                logger.LogInformation("");
            }
            catch (Exception ex)
            {
                deploymentSucceeded = false;
                logger.LogWarning("⚠ Deployment failed: {Message}", ex.Message);
                logger.LogWarning("Continuing to publish step. You can retry deployment later with 'a365 deploy'.");
                logger.LogInformation("");
            }
        }
        else
        {
            logger.LogInformation("STEP 2/3: Deploy - SKIPPED (external messaging endpoint)");
            logger.LogInformation("─────────────────────────────────────────");
            logger.LogInformation("NeedDeployment is set to false in configuration.");
            logger.LogInformation("");
        }

        // Step 5: Execute publish
        if (!skipPublish)
        {
            logger.LogInformation("STEP 3/3: Publishing Agent Manifest...");
            logger.LogInformation("─────────────────────────────────────────");
            await ExecutePublishAsync(config, logger, configService, graphApiService, blueprintService, manifestTemplateService);
            logger.LogInformation("");
        }
        else
        {
            logger.LogInformation("STEP 3/3: Publish - SKIPPED (--skip-publish flag)");
            logger.LogInformation("─────────────────────────────────────────");
            logger.LogInformation("");
        }

        // Final summary
        logger.LogInformation("============================================");
        if (deploymentSucceeded)
        {
            logger.LogInformation("✓ Agent deployment completed successfully!");
        }
        else
        {
            logger.LogWarning("⚠ Agent deployment completed with warnings");
            logger.LogWarning("  - Application deployment failed (you can retry with 'a365 deploy')");
        }
        logger.LogInformation("============================================");
        logger.LogInformation("");
        logger.LogInformation("Your agent is now available in Microsoft 365.");
        logger.LogInformation("Agent Name: {AgentName}", config.AgentBlueprintDisplayName);
        if (!string.IsNullOrWhiteSpace(config.WebAppName) && config.NeedDeployment)
        {
            logger.LogInformation("Web App: {WebAppName}", config.WebAppName);
            if (!deploymentSucceeded)
            {
                logger.LogWarning("Note: Web App deployment failed. Run 'a365 deploy' to retry.");
            }
        }
    }

    /// <summary>
    /// Checks if the agent blueprint already exists in the tenant.
    /// </summary>
    private static async Task<bool> CheckAgentExistsAsync(
        Agent365Config config,
        BlueprintLookupService blueprintLookupService,
        ILogger logger)
    {
        try
        {
            // First check if we have a blueprintId in config
            if (!string.IsNullOrWhiteSpace(config.AgentBlueprintId))
            {
                logger.LogInformation("Found blueprint ID in config: {BlueprintId}", config.AgentBlueprintId);
                
                // Verify it still exists by looking it up
                var lookupResult = await blueprintLookupService.GetServicePrincipalByAppIdAsync(
                    config.TenantId,
                    config.AgentBlueprintId);

                if (lookupResult.Found)
                {
                    logger.LogInformation("✓ Agent blueprint exists: {DisplayName}", lookupResult.DisplayName);
                    return true;
                }
                else
                {
                    logger.LogWarning("Blueprint ID in config no longer exists. Will run setup.");
                    return false;
                }
            }

            // Fallback: check by display name
            if (!string.IsNullOrWhiteSpace(config.AgentBlueprintDisplayName))
            {
                logger.LogDebug("Checking for blueprint by display name: {DisplayName}", config.AgentBlueprintDisplayName);
                
                var lookupResult = await blueprintLookupService.GetApplicationByDisplayNameAsync(
                    config.TenantId,
                    config.AgentBlueprintDisplayName);

                if (lookupResult.Found)
                {
                    logger.LogInformation("✓ Found existing agent blueprint: {DisplayName} (AppId: {AppId})",
                        lookupResult.DisplayName, lookupResult.AppId);
                    return true;
                }
            }

            logger.LogInformation("Agent blueprint not found. Setup is required.");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error checking for existing blueprint. Will assume setup is needed.");
            return false;
        }
    }

    /// <summary>
    /// Displays dry-run information showing what would be executed.
    /// </summary>
    private static Task ShowDryRunInfoAsync(
        ILogger logger,
        Agent365Config config,
        bool needsSetup,
        bool skipInfrastructure,
        bool skipPublish)
    {
        logger.LogInformation("DRY RUN: Agent 365 Run Command");
        logger.LogInformation("===============================");
        logger.LogInformation("");
        logger.LogInformation("Configuration:");
        logger.LogInformation("  Tenant ID: {TenantId}", config.TenantId);
        logger.LogInformation("  Subscription: {SubscriptionId}", config.SubscriptionId);
        logger.LogInformation("  Resource Group: {ResourceGroup}", config.ResourceGroup);
        logger.LogInformation("  Web App: {WebAppName}", config.WebAppName);
        logger.LogInformation("  Agent Blueprint: {BlueprintName}", config.AgentBlueprintDisplayName);
        logger.LogInformation("");
        logger.LogInformation("Planned Operations:");

        if (needsSetup)
        {
            logger.LogInformation("  STEP 1: Setup - WILL RUN");
            if (!skipInfrastructure && config.NeedDeployment)
            {
                logger.LogInformation("    - Create Azure infrastructure (App Service Plan, Web App)");
            }
            else if (skipInfrastructure)
            {
                logger.LogInformation("    - Azure infrastructure: SKIPPED (--skip-infrastructure)");
            }
            else
            {
                logger.LogInformation("    - Azure infrastructure: SKIPPED (external endpoint)");
            }
            logger.LogInformation("    - Create agent blueprint (Entra ID application)");
            logger.LogInformation("    - Configure MCP server permissions");
            logger.LogInformation("    - Configure Bot API permissions");
            logger.LogInformation("    - Register messaging endpoint");
        }
        else
        {
            logger.LogInformation("  STEP 1: Setup - SKIPPED (agent already exists)");
        }

        if (config.NeedDeployment)
        {
            logger.LogInformation("  STEP 2: Deploy - WILL RUN");
            logger.LogInformation("    - Build application");
            logger.LogInformation("    - Deploy to Azure Web App: {WebAppName}", config.WebAppName);
        }
        else
        {
            logger.LogInformation("  STEP 2: Deploy - SKIPPED (external messaging endpoint)");
        }

        if (!skipPublish)
        {
            logger.LogInformation("  STEP 3: Publish - WILL RUN");
            logger.LogInformation("    - Update manifest.json with blueprint ID");
            logger.LogInformation("    - Upload package to MOS Titles service");
            logger.LogInformation("    - Configure title access");
        }
        else
        {
            logger.LogInformation("  STEP 3: Publish - SKIPPED (--skip-publish)");
        }

        logger.LogInformation("");
        logger.LogInformation("No actual changes will be made (dry run mode).");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Executes the full setup workflow (equivalent to 'a365 setup all').
    /// </summary>
    private static async Task ExecuteSetupAllAsync(
        Agent365Config setupConfig,
        FileInfo configFile,
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        IBotConfigurator botConfigurator,
        IAzureValidator azureValidator,
        AzureWebAppCreator webAppCreator,
        PlatformDetector platformDetector,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        BlueprintLookupService blueprintLookupService,
        FederatedCredentialService federatedCredentialService,
        IClientAppValidator clientAppValidator,
        bool skipInfrastructure,
        bool skipRequirements)
    {
        var setupResults = new SetupResults();

        try
        {
            // PHASE 0: CHECK REQUIREMENTS (if not skipped)
            if (!skipRequirements)
            {
                logger.LogDebug("Validating system prerequisites...");

                try
                {
                    var result = await RequirementsSubcommand.RunRequirementChecksAsync(
                        RequirementsSubcommand.GetRequirementChecks(),
                        setupConfig,
                        logger,
                        category: null,
                        CancellationToken.None);

                    if (!result)
                    {
                        logger.LogError("");
                        logger.LogError("Setup cannot proceed due to the failed requirement checks above. Please fix the issues above and then try again.");
                        throw new SetupValidationException("Requirements check failed. Fix the issues above and try again.");
                    }
                }
                catch (SetupValidationException)
                {
                    throw;
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
                throw new SetupValidationException("Validation failures detected. Fix the errors and try again.");
            }

            logger.LogDebug("All validations passed. Starting setup execution...");

            var generatedConfigPath = Path.Combine(
                configFile.DirectoryName ?? Environment.CurrentDirectory,
                "a365.generated.config.json");

            // Step 1: Infrastructure (optional)
            try
            {
                var (setupInfra, infraAlreadyExisted) = await InfrastructureSubcommand.CreateInfrastructureImplementationAsync(
                    logger,
                    configFile.FullName,
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
                    configFile,
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
                    federatedCredentialService);

                setupResults.BlueprintCreated = result.BlueprintCreated;
                setupResults.BlueprintAlreadyExisted = result.BlueprintAlreadyExisted;
                setupResults.MessagingEndpointRegistered = result.EndpointRegistered;
                setupResults.EndpointAlreadyExisted = result.EndpointAlreadyExisted;

                if (result.EndpointAlreadyExisted)
                {
                    setupResults.Warnings.Add("Messaging endpoint already exists (not newly created)");
                }

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
                logger.LogInformation("Ensuring configuration file is synchronized...");
                await Task.Delay(2000);

                // Reload config to get blueprint ID
                var fullConfigPath = Path.GetFullPath(configFile.FullName);
                setupConfig = await configService.LoadAsync(fullConfigPath);
                setupResults.BlueprintId = setupConfig.AgentBlueprintId;

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
                    configFile.FullName,
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
                    configFile.FullName,
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
        catch (Agent365Exception)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Setup failed: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Executes application deployment to Azure Web App.
    /// </summary>
    private static async Task ExecuteDeployAsync(
        Agent365Config config,
        string configPath,
        IConfigService configService,
        IAzureValidator azureValidator,
        CommandExecutor executor,
        DeploymentService deploymentService,
        ILogger logger)
    {
        // Validate Azure CLI authentication and Web App existence
        logger.LogInformation("Validating deployment prerequisites...");

        if (!await azureValidator.ValidateAllAsync(config.SubscriptionId))
        {
            throw new DeployAppException("Azure CLI authentication failed or subscription not set correctly");
        }

        // Validate Azure Web App exists
        logger.LogInformation("Validating Azure Web App exists...");
        var checkResult = await executor.ExecuteAsync("az",
            $"webapp show --resource-group {config.ResourceGroup} --name {config.WebAppName} --subscription {config.SubscriptionId}",
            captureOutput: true,
            suppressErrorLogging: true);

        if (!checkResult.Success)
        {
            logger.LogError("Azure Web App '{WebAppName}' does not exist in resource group '{ResourceGroup}'",
                config.WebAppName, config.ResourceGroup);
            throw new DeployAppException($"Azure Web App '{config.WebAppName}' does not exist. Run setup first or verify your configuration.");
        }

        logger.LogInformation("Confirmed Azure Web App '{WebAppName}' exists", config.WebAppName);

        // Perform deployment
        var deployConfig = new DeploymentConfiguration
        {
            ResourceGroup = config.ResourceGroup,
            AppName = config.WebAppName,
            ProjectPath = config.DeploymentProjectPath,
            DeploymentZip = "app.zip",
            PublishOutputPath = "publish",
            Platform = null // Auto-detect
        };

        var success = await deploymentService.DeployAsync(deployConfig, verbose: false, inspect: false, restart: false);

        if (!success)
        {
            throw new DeployAppException("Application deployment failed. Check the logs for details.");
        }

        logger.LogInformation("✓ Application deployed successfully");
    }

    /// <summary>
    /// Executes agent manifest publishing to MOS.
    /// </summary>
    private static async Task ExecutePublishAsync(
        Agent365Config config,
        ILogger logger,
        IConfigService configService,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        ManifestTemplateService manifestTemplateService)
    {
        var blueprintId = config.AgentBlueprintId;
        var tenantId = config.TenantId;
        var agentBlueprintDisplayName = config.AgentBlueprintDisplayName ?? "Agent365 Agent";

        if (string.IsNullOrWhiteSpace(blueprintId))
        {
            throw new SetupValidationException("agentBlueprintId missing in configuration. Run setup first.");
        }

        // Use deploymentProjectPath from config for portability
        var baseDir = GetProjectDirectory(config, logger);
        var manifestDir = Path.Combine(baseDir, "manifest");
        var manifestPath = Path.Combine(manifestDir, "manifest.json");
        var agenticUserManifestTemplatePath = Path.Combine(manifestDir, "agenticUserTemplateManifest.json");

        logger.LogDebug("Using project directory: {BaseDir}", baseDir);
        logger.LogDebug("Using manifest directory: {ManifestDir}", manifestDir);

        // If manifest directory doesn't exist, extract templates from embedded resources
        if (!Directory.Exists(manifestDir))
        {
            logger.LogInformation("Manifest directory not found. Extracting templates from embedded resources...");
            Directory.CreateDirectory(manifestDir);

            if (!manifestTemplateService.ExtractTemplates(manifestDir))
            {
                throw new SetupValidationException("Failed to extract manifest templates from embedded resources");
            }

            logger.LogInformation("Successfully extracted manifest templates to {ManifestDir}", manifestDir);
        }

        if (!File.Exists(manifestPath))
        {
            throw new SetupValidationException($"Manifest file not found at {manifestPath}. Expected location based on deploymentProjectPath: {baseDir}");
        }

        // Determine MOS Titles URL based on tenant
        var mosTitlesBaseUrl = GetMosTitlesUrl(tenantId);
        logger.LogInformation("Using MOS Titles URL: {Url}", mosTitlesBaseUrl);

        // Update manifest files with blueprint ID
        string updatedManifest = await UpdateManifestFileAsync(logger, agentBlueprintDisplayName, blueprintId, manifestPath);
        string updatedAgenticUserManifestTemplate = await UpdateAgenticUserManifestTemplateFileAsync(logger, agentBlueprintDisplayName, blueprintId, agenticUserManifestTemplatePath);

        await File.WriteAllTextAsync(manifestPath, updatedManifest);
        logger.LogInformation("Manifest updated with blueprint ID: {BlueprintId}", blueprintId);

        await File.WriteAllTextAsync(agenticUserManifestTemplatePath, updatedAgenticUserManifestTemplate);

        // Create manifest.zip
        var zipPath = Path.Combine(manifestDir, "manifest.zip");
        if (File.Exists(zipPath))
        {
            try { File.Delete(zipPath); } catch { /* ignore */ }
        }

        var expectedFiles = new List<string>();
        string[] candidateNames = ["manifest.json", "color.png", "outline.png", "logo.png", "icon.png"];
        foreach (var name in candidateNames)
        {
            var p = Path.Combine(manifestDir, name);
            if (File.Exists(p)) expectedFiles.Add(p);
            if (expectedFiles.Count == 4) break;
        }

        if (expectedFiles.Count < 4)
        {
            foreach (var f in Directory.EnumerateFiles(manifestDir).Where(f => !expectedFiles.Contains(f)))
            {
                expectedFiles.Add(f);
                if (expectedFiles.Count == 4) break;
            }
        }

        if (expectedFiles.Count == 0)
        {
            throw new SetupValidationException($"No manifest files found to zip in {manifestDir}");
        }

        using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            foreach (var file in expectedFiles)
            {
                var entryName = Path.GetFileName(file);
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var src = File.OpenRead(file);
                await src.CopyToAsync(entryStream);
                logger.LogDebug("Added {File} to manifest.zip", entryName);
            }
        }
        logger.LogInformation("Created manifest package: {ZipPath}", zipPath);

        // Ensure MOS prerequisites are configured
        try
        {
            logger.LogDebug("Checking MOS prerequisites (service principals and permissions)...");
            var mosPrereqsConfigured = await PublishHelpers.EnsureMosPrerequisitesAsync(
                graphApiService, blueprintService, config, logger);

            if (!mosPrereqsConfigured)
            {
                throw new SetupValidationException("Failed to configure MOS prerequisites.");
            }
        }
        catch (SetupValidationException)
        {
            throw;
        }

        // Acquire MOS token
        logger.LogDebug("Acquiring MOS authentication token...");
        var cleanLoggerFactory = LoggerFactoryHelper.CreateCleanLoggerFactory();
        var mosTokenService = new MosTokenService(
            cleanLoggerFactory.CreateLogger<MosTokenService>(),
            configService);

        string? mosToken;
        try
        {
            mosToken = await mosTokenService.AcquireTokenAsync("prod", null);
            logger.LogDebug("MOS token acquired successfully");
        }
        catch (Exception ex)
        {
            throw new SetupValidationException($"Failed to acquire MOS token: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(mosToken))
        {
            throw new SetupValidationException("Unable to acquire MOS token.");
        }

        using var http = HttpClientFactory.CreateAuthenticatedClient(mosToken);

        // Upload package to Titles service
        logger.LogInformation("Uploading package to Titles service...");
        var packagesUrl = $"{mosTitlesBaseUrl}/admin/v1/tenants/packages";

        using var form = new MultipartFormDataContent();
        await using (var zipFs = File.OpenRead(zipPath))
        {
            var fileContent = new StreamContent(zipFs);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(fileContent, "package", Path.GetFileName(zipPath));

            var uploadResp = await http.PostAsync(packagesUrl, form);
            var uploadBody = await uploadResp.Content.ReadAsStringAsync();

            if (!uploadResp.IsSuccessStatusCode)
            {
                throw new SetupValidationException($"Package upload failed ({uploadResp.StatusCode}). Response: {uploadBody}");
            }

            JsonDocument? uploadJson;
            try
            {
                uploadJson = JsonDocument.Parse(uploadBody);
            }
            catch (Exception jex)
            {
                throw new SetupValidationException($"Failed to parse upload response JSON: {jex.Message}");
            }

            if (!uploadJson.RootElement.TryGetProperty("operationId", out var opIdEl))
            {
                throw new SetupValidationException($"operationId missing in upload response. Body: {uploadBody}");
            }
            var operationId = opIdEl.GetString();

            string? titleId = null;
            if (uploadJson.RootElement.TryGetProperty("titlePreview", out var previewEl) &&
                previewEl.ValueKind == JsonValueKind.Object &&
                previewEl.TryGetProperty("titleId", out var previewTitleIdEl))
            {
                titleId = previewTitleIdEl.GetString();
            }
            if (string.IsNullOrWhiteSpace(titleId))
            {
                throw new SetupValidationException($"titleId not found in upload response. Body: {uploadBody}");
            }

            logger.LogInformation("Upload succeeded. operationId={Op} titleId={Title}", operationId, titleId);

            // POST titles with operationId
            var titlesUrl = $"{mosTitlesBaseUrl}/admin/v1/tenants/packages/titles";
            var titlePayload = JsonSerializer.Serialize(new { operationId });

            using (var content = new StringContent(titlePayload, System.Text.Encoding.UTF8, "application/json"))
            {
                var titlesResp = await http.PostAsync(titlesUrl, content);
                var titlesBody = await titlesResp.Content.ReadAsStringAsync();

                if (!titlesResp.IsSuccessStatusCode)
                {
                    throw new SetupValidationException($"Titles creation failed ({titlesResp.StatusCode}). Body: {titlesBody}");
                }
                logger.LogInformation("Title creation initiated.");
            }

            // Configure title access for all users
            logger.LogInformation("Configuring title access for all users...");
            var allowUrl = $"{mosTitlesBaseUrl}/admin/v1/tenants/titles/{titleId}/allowed";
            var allowedPayload = JsonSerializer.Serialize(new
            {
                EntityCollection = new
                {
                    ForAllUsers = true,
                    Entities = Array.Empty<object>()
                }
            });

            var retryHelper = new RetryHelper(logger);
            var allowResult = await retryHelper.ExecuteWithRetryAsync(
                async ct =>
                {
                    using var allowContent = new StringContent(allowedPayload, System.Text.Encoding.UTF8, "application/json");
                    var allowResp = await http.PutAsync(allowUrl, allowContent);
                    return (allowResp.IsSuccessStatusCode, await allowResp.Content.ReadAsStringAsync());
                },
                shouldRetry: result => !result.Item1, // Retry if not successful
                maxRetries: 5,
                baseDelaySeconds: 2,
                CancellationToken.None);

            if (!allowResult.Item1)
            {
                logger.LogWarning("Failed to configure title access for all users. You may need to do this manually.");
            }
            else
            {
                logger.LogInformation("✓ Title access configured for all users");
            }
        }

        logger.LogInformation("✓ Agent manifest published successfully");
    }

    /// <summary>
    /// Updates manifest.json with the blueprint ID and display name.
    /// </summary>
    private static async Task<string> UpdateManifestFileAsync(
        ILogger logger,
        string agentBlueprintDisplayName,
        string blueprintId,
        string manifestPath)
    {
        var manifestContent = await File.ReadAllTextAsync(manifestPath);
        using var doc = JsonDocument.Parse(manifestContent);
        var root = doc.RootElement;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "id")
                {
                    writer.WriteString("id", blueprintId);
                }
                else if (prop.Name == "name" && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("name");
                    writer.WriteStartObject();
                    foreach (var nameProp in prop.Value.EnumerateObject())
                    {
                        if (nameProp.Name == "short")
                        {
                            var shortName = agentBlueprintDisplayName.Length > 30
                                ? agentBlueprintDisplayName[..30]
                                : agentBlueprintDisplayName;
                            writer.WriteString("short", shortName);
                        }
                        else if (nameProp.Name == "full")
                        {
                            writer.WriteString("full", agentBlueprintDisplayName);
                        }
                        else
                        {
                            nameProp.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Updates agenticUserTemplateManifest.json with the blueprint ID.
    /// </summary>
    private static async Task<string> UpdateAgenticUserManifestTemplateFileAsync(
        ILogger logger,
        string agentBlueprintDisplayName,
        string blueprintId,
        string templatePath)
    {
        if (!File.Exists(templatePath))
        {
            logger.LogDebug("Agentic user manifest template not found at {Path}, skipping update", templatePath);
            return string.Empty;
        }

        var templateContent = await File.ReadAllTextAsync(templatePath);
        using var doc = JsonDocument.Parse(templateContent);
        var root = doc.RootElement;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "agentBlueprintId")
                {
                    writer.WriteString("agentBlueprintId", blueprintId);
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
