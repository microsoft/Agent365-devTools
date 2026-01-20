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

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

public class DeployCommand
{
    public static Command CreateCommand(
        ILogger<DeployCommand> logger,
        IConfigService configService,
        CommandExecutor executor,
        DeploymentService deploymentService,
        IAzureValidator azureValidator,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService)
    {
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
        command.AddCommand(CreateMcpSubcommand(logger, configService, executor, graphApiService, blueprintService));

        // Single handler for the deploy command - runs only the application deployment flow
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

                // Check if web app deployment should be skipped (external messaging endpoint)
                if (!configData.NeedDeployment)
                {
                    logger.LogInformation("Web App deployment is skipped as per configuration.");
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

                // Check if web app deployment should be skipped (external messaging endpoint)
                if (!configData.NeedDeployment)
                {
                    logger.LogInformation("Web App deployment is skipped as per configuration.");
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
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService)
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
            try
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

                // Load configuration from specified file
                var updateConfig = await configService.LoadAsync(config.FullName);
                if (updateConfig == null) Environment.Exit(1);

                // Configure GraphApiService with custom client app ID if available
                if (!string.IsNullOrWhiteSpace(updateConfig.ClientAppId))
                {
                    graphApiService.CustomClientAppId = updateConfig.ClientAppId;
                }

                await DeployMcpToolPermissionsAsync(updateConfig, executor, logger, graphApiService, blueprintService);
            }
            catch (DeployMcpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Microsoft Agent 365 Tool Permissions deploy/update failed: {Message}", ex.Message);
                throw new DeployMcpException(ex.Message, ex);
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
            logger.LogInformation("  1. Run 'a365 setup all' to create all required Azure resources");
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
        ILogger logger,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService)
    {
        // Read scopes from toolingManifest.json (at deploymentProjectPath)
        var manifestPath = Path.Combine(config.DeploymentProjectPath ?? string.Empty, "toolingManifest.json");
        var toolingScopes = await ManifestHelper.GetRequiredScopesAsync(manifestPath);

        // 1. Apply MCP OAuth2 permission grants
        logger.LogInformation("1. Applying MCP OAuth2 permission grants...");
        await EnsureMcpOauth2PermissionGrantsAsync(
            graphApiService,
            blueprintService,
            config,
            toolingScopes,
            logger
        );

        // 2. Consent to required scopes for the agent identity
        logger.LogInformation("2. Consenting to required MCP scopes for the agent identity...");
        await EnsureMcpAdminConsentForAgenticAppAsync(
            graphApiService,
            blueprintService,
            config,
            toolingScopes,
            logger
        );

        // 3. Apply inheritable permissions on the agent identity blueprint
        logger.LogInformation("3. Applying MCP inheritable permissions...");
        await EnsureMcpInheritablePermissionsAsync(
            graphApiService,
            blueprintService,
            config,
            toolingScopes,
            logger
        );

        logger.LogInformation("Deploy Microsoft Agent 365 Tool Permissions completed successfully!");
    }

    private static async Task EnsureMcpOauth2PermissionGrantsAsync(
        GraphApiService graphService,
        AgentBlueprintService blueprintService,
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

        var ok = await blueprintService.ReplaceOauth2PermissionGrantAsync(
            config.TenantId, blueprintSpObjectId, mcpPlatformSpObjectId, scopes, ct);

        if (!ok) throw new InvalidOperationException("Failed to update oauth2PermissionGrant.");

        logger.LogInformation("   - OAuth2 granted: client {ClientId} to resource {ResourceId} scopes [{Scopes}]",
            blueprintSpObjectId, mcpPlatformSpObjectId, string.Join(' ', scopes));
    }

    private static async Task EnsureMcpInheritablePermissionsAsync(
        GraphApiService graphService,
        AgentBlueprintService blueprintService,
        Agent365Config config,
        string[] scopes,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
            throw new InvalidOperationException("AgentBlueprintId (appId) is required.");

        var resourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(config.Environment);

        // Use custom client app auth for inheritable permissions - Azure CLI doesn't support this operation
        var requiredPermissions = new[] { "AgentIdentityBlueprint.UpdateAuthProperties.All", "Application.ReadWrite.All" };

        var (ok, alreadyExists, err) = await blueprintService.SetInheritablePermissionsAsync(
            config.TenantId, config.AgentBlueprintId, resourceAppId, scopes, requiredScopes: requiredPermissions, ct);

        if (!ok && !alreadyExists)
        {
            throw new InvalidOperationException("Failed to set inheritable permissions: " + err +
                ". Ensure you have AgentIdentityBlueprint.UpdateAuthProperties.All and Application.ReadWrite.All permissions in your custom client app.");
        }

        logger.LogInformation("   - Inheritable permissions completed: blueprint {Blueprint} to resourceAppId {ResourceAppId} scopes [{Scopes}]",
            config.AgentBlueprintId, resourceAppId, string.Join(' ', scopes));
    }

    private static async Task EnsureMcpAdminConsentForAgenticAppAsync(
        GraphApiService graphService,
        AgentBlueprintService blueprintService,
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

        var ok = await blueprintService.ReplaceOauth2PermissionGrantAsync(
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
                logger.LogError("Deployment failed: {Message}", ex.Message);

                throw new DeployAppException($"Deployment failed: {ex.Message}", ex);
        }
    }
}

