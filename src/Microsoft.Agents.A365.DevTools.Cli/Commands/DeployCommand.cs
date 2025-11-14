// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

public class DeployCommand
{
    public static Command CreateCommand(
        ILogger<DeployCommand> logger, 
        IConfigService configService, 
        CommandExecutor executor,
        DeploymentService deploymentService,
        IAzureValidator azureValidator)
    {
        // Top-level command name set to 'deploy' so it appears in CLI help as 'deploy'
        var command = new Command("deploy", "Deploy Agent 365 application binaries to the configured Azure App Service and update Agent 365 Tool permissions");

        var configOption = new Option<FileInfo>(
            new[] { "--config", "-c" },
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Path to the configuration file (default: a365.config.json)");

        var verboseOption = new Option<bool>(
            new[] { "--verbose", "-v" },
            description: "Enable verbose logging");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            description: "Show what would be done without executing");

        var inspectOption = new Option<bool>(
            "--inspect",
            description: "Pause before deployment to inspect publish folder and ZIP contents");

        var restartOption = new Option<bool>(
            "--restart",
            description: "Skip build and start from compressing existing publish folder (for quick iteration after manual changes)");

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(inspectOption);
        command.AddOption(restartOption);

        // Add subcommands
        command.AddCommand(CreateAppSubcommand(logger, configService, executor, deploymentService, azureValidator));
        command.AddCommand(CreateMcpSubcommand(logger, configService, executor));

        // Single handler for the deploy command 
        command.SetHandler(async (config, verbose, dryRun, inspect, restart) =>
        {
            try
            {
                // Suppress stale warning since deploy is a legitimate read-only operation
                var configData = await configService.LoadAsync(config.FullName);
                if (configData == null) return;

                if (dryRun)
                {
                    logger.LogInformation("DRY RUN: Step 1 - Deploy application binaries");
                    logger.LogInformation("Target resource group: {ResourceGroup}", configData.ResourceGroup);
                    logger.LogInformation("Target web app: {WebAppName}", configData.WebAppName);
                    logger.LogInformation("Configuration file validated: {ConfigFile}", config.FullName);
                    logger.LogInformation("");
                    logger.LogInformation("DRY RUN: Step 2 - Deploy/update Agent 365 Tool permissions");
                    logger.LogInformation("Update MCP OAuth2 permission grants and inheritable permissions");
                    logger.LogInformation("Consent to required scopes for the agent identity");
                    return;
                }

                // Step 1: Deploy application binaries
                logger.LogInformation("Step 1: Start deploying application binaries...");
                
                var validatedConfig = await ValidateDeploymentPrerequisitesAsync(
                    config.FullName, configService, azureValidator, executor, logger);
                if (validatedConfig == null) return;

                var appDeploySuccess = await DeployApplicationAsync(
                    validatedConfig, deploymentService, verbose, inspect, restart, logger);
                if (!appDeploySuccess) return;

                // Step 2: Deploy MCP Tool Permissions
                logger.LogInformation("Step 2: Start deploying Agent 365 Tool Permissions...");
                await DeployMcpToolPermissionsAsync(validatedConfig, executor, logger);
            }
            catch (Exception ex)
            {
                HandleDeploymentException(ex, logger);
                if (ex is not FileNotFoundException)
                    throw;
            }
        }, configOption, verboseOption, dryRunOption, inspectOption, restartOption);

        return command;
    }

    private static Command CreateAppSubcommand(
        ILogger<DeployCommand> logger,
        IConfigService configService,
        CommandExecutor executor,
        DeploymentService deploymentService,
        IAzureValidator azureValidator)
    {
        var command = new Command("app", "Deploy Microsoft Agent 365 application binaries to the configured Azure App Service");

        var configOption = new Option<FileInfo>(
            new[] { "--config", "-c" },
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Path to the configuration file (default: a365.config.json)");

        var verboseOption = new Option<bool>(
            new[] { "--verbose", "-v" },
            description: "Enable verbose logging");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            description: "Show what would be done without executing");

        var inspectOption = new Option<bool>(
            "--inspect",
            description: "Pause before deployment to inspect publish folder and ZIP contents");

        var restartOption = new Option<bool>(
            "--restart",
            description: "Skip build and start from compressing existing publish folder (for quick iteration after manual changes)");

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(inspectOption);
        command.AddOption(restartOption);

        command.SetHandler(async (config, verbose, dryRun, inspect, restart) =>
        {
            try
            {
                // Suppress stale warning since deploy is a legitimate read-only operation
                var configData = await configService.LoadAsync(config.FullName);
                if (configData == null) return;
                
                if (dryRun)
                {
                    logger.LogInformation("DRY RUN: Deploy application binaries");
                    logger.LogInformation("Target resource group: {ResourceGroup}", configData.ResourceGroup);
                    logger.LogInformation("Target web app: {WebAppName}", configData.WebAppName);
                    logger.LogInformation("Configuration file validated: {ConfigFile}", config.FullName);
                    return;
                }

                var validatedConfig = await ValidateDeploymentPrerequisitesAsync(
                    config.FullName, configService, azureValidator, executor, logger);
                if (validatedConfig == null) return;

                await DeployApplicationAsync(validatedConfig, deploymentService, verbose, inspect, restart, logger);
            }
            catch (Exception ex)
            {
                HandleDeploymentException(ex, logger);
            }
        }, configOption, verboseOption, dryRunOption, inspectOption, restartOption);
        
        return command;
    }

    private static Command CreateMcpSubcommand(
        ILogger<DeployCommand> logger,
        IConfigService configService,
        CommandExecutor executor)
    {
        var command = new Command("mcp", "Update mcp servers scopes and permissions on existing agent blueprint");

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
                logger.LogInformation("DRY RUN: Deploy/update Agent 365 Tool Permissions");
                logger.LogInformation("This would execute the following operations:");
                logger.LogInformation("  1. Update MCP OAuth2 permission grants and inheritable permissions");
                logger.LogInformation("  2. Consent to required scopes for the agent identity");
                logger.LogInformation("No actual changes will be made.");
                return;
            }

            logger.LogInformation("Starting deploy Microsoft Agent 365 Tool Permissions...");
            logger.LogInformation(""); // Empty line for readability

            try
            {
                // Load configuration from specified file
                var updateConfig = await configService.LoadAsync(config.FullName);
                if (updateConfig == null) Environment.Exit(1);

                await DeployMcpToolPermissionsAsync(updateConfig, executor, logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Microsoft Agent 365 Tool Permissions deploy/update failed: {Message}", ex.Message);
                throw;
            }
        }, configOption, verboseOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Validates configuration, Azure authentication, and Web App existence
    /// </summary>
    private static async Task<Agent365Config?> ValidateDeploymentPrerequisitesAsync(
        string configPath,
        IConfigService configService,
        IAzureValidator azureValidator,
        CommandExecutor executor,
        ILogger logger)
    {
        // Load configuration
        var configData = await configService.LoadAsync(configPath);
        if (configData == null) return null;

        // Validate Azure CLI authentication, subscription, and environment
        if (!await azureValidator.ValidateAllAsync(configData.SubscriptionId))
        {
            logger.LogError("Deployment cannot proceed without proper Azure CLI authentication and the correct subscription context");
            return null;
        }

        // Validate Azure Web App exists before starting deployment
        logger.LogInformation("Validating Azure Web App exists...");
        var checkResult = await executor.ExecuteAsync("az",
            $"webapp show --resource-group {configData.ResourceGroup} --name {configData.WebAppName} --subscription {configData.SubscriptionId}",
            captureOutput: true,
            suppressErrorLogging: true);

        if (!checkResult.Success)
        {
            logger.LogError("Azure Web App '{WebAppName}' does not exist in resource group '{ResourceGroup}'",
                configData.WebAppName, configData.ResourceGroup);
            logger.LogInformation("");
            logger.LogInformation("Please ensure the Web App exists before deploying:");
            logger.LogInformation("  1. Run 'a365 setup' to create all required Azure resources");
            logger.LogInformation("  2. Or verify your a365.config.json has the correct WebAppName and ResourceGroup");
            logger.LogInformation("");
            logger.LogError("Deployment cannot proceed without a valid Azure Web App target");
            return null;
        }

        logger.LogInformation("Confirmed Azure Web App '{WebAppName}' exists", configData.WebAppName);
        return configData;
    }

    /// <summary>
    /// Performs application deployment using DeploymentService
    /// </summary>
    private static async Task<bool> DeployApplicationAsync(
        Agent365Config configData,
        DeploymentService deploymentService,
        bool verbose,
        bool inspect,
        bool restart,
        ILogger logger)
    {
        var deployConfig = ConvertToDeploymentConfig(configData);
        var success = await deploymentService.DeployAsync(deployConfig, verbose, inspect, restart);
        
        if (!success)
        {
            logger.LogError("Deployment failed");
        }
        else
        {
            logger.LogInformation("Deployment completed successfully");
        }
        
        return success;
    }

    /// <summary>
    /// Convert Microsoft Agent 365 Config to DeploymentConfiguration
    /// </summary>
    private static DeploymentConfiguration ConvertToDeploymentConfig(Agent365Config config)
    {
        return new DeploymentConfiguration
        {
            ResourceGroup = config.ResourceGroup,
            AppName = config.WebAppName,
            ProjectPath = config.DeploymentProjectPath,
            DeploymentZip = "app.zip",
            PublishOutputPath = "publish",
            Platform = null // Auto-detect platform
        };
    }

    /// <summary>
    /// Performs MCP tool permissions deployment
    /// </summary>
    private static async Task DeployMcpToolPermissionsAsync(
        Agent365Config config,
        CommandExecutor executor,
        ILogger logger)
    {
        // Read scopes from toolingManifest.json (at deploymentProjectPath)
        var manifestPath = Path.Combine(config.DeploymentProjectPath ?? string.Empty, "toolingManifest.json");
        var toolingScopes = await ManifestHelper.GetRequiredScopesAsync(manifestPath);

        var graphService = new GraphApiService(
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<GraphApiService>(),
            executor);

        // 1. Apply MCP OAuth2 permission grants
        logger.LogInformation("1. Applying MCP OAuth2 permission grants...");
        await EnsureMcpOauth2PermissionGrantsAsync(
            graphService,
            config,
            toolingScopes,
            logger
        );

        // 2. Apply inheritable permissions on the agent identity blueprint
        logger.LogInformation("2. Applying MCP inheritable permissions...");
        await EnsureMcpInheritablePermissionsAsync(
            graphService,
            config,
            toolingScopes,
            logger
        );

        // 3. Consent to required scopes for the agent identity
        logger.LogInformation("3. Consenting to required MCP scopes for the agent identity...");
        await EnsureAdminConsentForAgenticAppAsync(
            graphService,
            config,
            toolingScopes,
            logger
        );

        logger.LogInformation("Deploy Microsoft Agent 365 Tool Permissions completed successfully!");
    }

    private static async Task EnsureMcpOauth2PermissionGrantsAsync(
        GraphApiService graphService,
        Agent365Config config,
        string[] scopes,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
            throw new InvalidOperationException("AgentBlueprintId (appId) is required.");

        var blueprintSpObjectId = await graphService.LookupServicePrincipalByAppIdAsync(config.TenantId, config.AgentBlueprintId, ct)
            ?? throw new InvalidOperationException("Blueprint Service Principal not found for appId " + config.AgentBlueprintId);

        var resourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(config.Environment);
        var mcpPlatformSpObjectId = await graphService.LookupServicePrincipalByAppIdAsync(config.TenantId, resourceAppId, ct)
            ?? throw new InvalidOperationException("MCP Platform Service Principal not found for appId " + resourceAppId);

        var ok = await graphService.ReplaceOauth2PermissionGrantAsync(
            config.TenantId, blueprintSpObjectId, mcpPlatformSpObjectId, scopes, ct);

        if (!ok) throw new InvalidOperationException("Failed to update oauth2PermissionGrant.");

        logger.LogInformation("   - OAuth2 granted: client {ClientId} to resource {ResourceId} scopes [{Scopes}]",
            blueprintSpObjectId, mcpPlatformSpObjectId, string.Join(' ', scopes));
    }

    private static async Task EnsureMcpInheritablePermissionsAsync(
        GraphApiService graphService,
        Agent365Config config,
        string[] scopes,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
            throw new InvalidOperationException("AgentBlueprintId (appId) is required.");

        var resourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(config.Environment);

        var (ok, alreadyExists, err) = await graphService.SetInheritablePermissionsAsync(
            config.TenantId, config.AgentBlueprintId, resourceAppId, scopes, ct);

        if (!ok && !alreadyExists)
        {
            config.InheritanceConfigured = false;
            config.InheritanceConfigError = err;
            throw new InvalidOperationException("Failed to set inheritable permissions: " + err);
        }

        config.InheritanceConfigured = true;
        config.InheritablePermissionsAlreadyExist = alreadyExists;
        config.InheritanceConfigError = null;

        logger.LogInformation("   - Inheritable permissions completed: blueprint {Blueprint} to resourceAppId {ResourceAppId} scopes [{Scopes}]",
            config.AgentBlueprintId, resourceAppId, string.Join(' ', scopes));
    }

    private static async Task EnsureAdminConsentForAgenticAppAsync(
        GraphApiService graphService,
        Agent365Config config,
        string[] scopes,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.AgenticAppId))
            throw new InvalidOperationException("AgenticAppId is required.");

        // clientId must be the *service principal objectId* of the agentic app
        var agenticAppSpObjectId = await graphService.LookupServicePrincipalByAppIdAsync(
            config.TenantId,
            config.AgenticAppId ?? string.Empty
        ) ?? throw new InvalidOperationException($"Service Principal not found for agentic appId {config.AgenticAppId}");

        var resourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(config.Environment);
        var mcpPlatformResourceSpObjectId = await graphService.LookupServicePrincipalByAppIdAsync(config.TenantId, resourceAppId)
            ?? throw new InvalidOperationException("MCP Platform Service Principal not found for appId " + resourceAppId);

        var ok = await graphService.ReplaceOauth2PermissionGrantAsync(
            config.TenantId,
            agenticAppSpObjectId,
            mcpPlatformResourceSpObjectId,
            scopes,
            ct
        );

        if (!ok) throw new InvalidOperationException("Failed to ensure admin consent for agent identity.");

        logger.LogInformation("   - Admin consented: agent identity {AgenticAppId} to resourceAppId {ResourceAppId} scopes [{Scopes}]",
            config.AgenticAppId, resourceAppId, string.Join(' ', scopes));
    }

    /// <summary>
    /// Handles common deployment exceptions and provides user guidance
    /// </summary>
    private static void HandleDeploymentException(Exception ex, ILogger logger)
    {
        switch (ex)
        {
            case FileNotFoundException fileNotFound:
                logger.LogError("Configuration file not found: {Message}", fileNotFound.Message);
                logger.LogInformation("");
                logger.LogInformation("To get started:");
                logger.LogInformation("  1. Copy a365.config.example.json to a365.config.json");
                logger.LogInformation("  2. Edit a365.config.json with your Azure tenant and subscription details");
                logger.LogInformation("  3. Run 'a365 deploy' to perform a deployment");
                logger.LogInformation("");
                break;
            default:
                logger.LogError(ex, "Deployment failed: {Message}", ex.Message);
                break;
        }
    }
}

