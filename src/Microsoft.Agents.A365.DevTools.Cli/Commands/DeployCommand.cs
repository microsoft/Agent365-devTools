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
        GraphApiService graphApiService)
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
        command.AddCommand(CreateMcpSubcommand(logger, configService, executor, graphApiService));
        command.AddCommand(CreateScopesSubcommand(logger, configService, executor, graphApiService));

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
                if (ShouldSkipWebAppDeployment(configData, logger))
                {
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
                if (ShouldSkipWebAppDeployment(configData, logger))
                {
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
        GraphApiService graphApiService)
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

                await DeployMcpToolPermissionsAsync(updateConfig, executor, logger, graphApiService);
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

    private static Command CreateScopesSubcommand(
        ILogger<DeployCommand> logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService)
    {
        var command = new Command("scopes", "Grant or update OAuth2 scopes for a specific resource app ID on the agent blueprint");

        var configOption = new Option<FileInfo>(
            new[] { "--config", "-c" },
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Path to the configuration file (default: a365.config.json)");

        var resourceAppIdOption = new Option<string>(
            new[] { "--resource-app-id", "-r" },
            description: "Resource App ID (required)")
        { IsRequired = true };

        var scopesOption = new Option<string[]>(
            new[] { "--scopes", "-s" },
            description: "Comma-separated list of scopes (required)")
        {
            IsRequired = true,
            AllowMultipleArgumentsPerToken = true
        };

        var tenantIdOption = new Option<string?>(
            new[] { "--tenant-id", "-t" },
            description: "Tenant ID (overrides config)");

        var blueprintAppIdOption = new Option<string?>(
            new[] { "--blueprint-app-id", "-b" },
            description: "Agent Blueprint App ID (overrides config)");

        var verboseOption = new Option<bool>(
            new[] { "--verbose", "-v" },
            description: "Enable verbose logging");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            description: "Show what would be done without executing");

        command.AddOption(configOption);
        command.AddOption(resourceAppIdOption);
        command.AddOption(scopesOption);
        command.AddOption(tenantIdOption);
        command.AddOption(blueprintAppIdOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);

        command.SetHandler(async (FileInfo configFile, string resourceAppId, string[] scopes, string? tenantId, string? blueprintAppId, bool verbose, bool dryRun) =>
        {
            try
            {
                // Load config for defaults
                var config = await configService.LoadAsync(configFile.FullName);
                if (config == null)
                {
                    logger.LogError("Failed to load configuration from {ConfigFile}", configFile.FullName);
                    return;
                }

                var effectiveTenantId = !string.IsNullOrWhiteSpace(tenantId) ? tenantId : config.TenantId;
                var effectiveBlueprintAppId = !string.IsNullOrWhiteSpace(blueprintAppId) ? blueprintAppId : config.AgentBlueprintId;

                if (string.IsNullOrWhiteSpace(effectiveTenantId))
                {
                    throw new DeployScopesException("Tenant ID is required (not found in config or command line)");
                }
                if (string.IsNullOrWhiteSpace(effectiveBlueprintAppId))
                {
                    throw new DeployScopesException("Agent Blueprint App ID is required (not found in config or command line)");
                }
                if (string.IsNullOrWhiteSpace(resourceAppId))
                {
                    throw new DeployScopesException("Resource App ID is required");
                }
                if (scopes == null || scopes.Length == 0)
                {
                    throw new DeployScopesException("At least one scope is required");
                }

                if (dryRun)
                {
                    logger.LogInformation("DRY RUN: Would grant scopes [{Scopes}] to resource app {ResourceAppId} for blueprint {BlueprintAppId} in tenant {TenantId}",
                        string.Join(", ", scopes), resourceAppId, effectiveBlueprintAppId, effectiveTenantId);
                    return;
                }

                await DeployScopesPermissionsAsync(config, effectiveBlueprintAppId, effectiveTenantId, resourceAppId, scopes, executor, logger, graphApiService);
                logger.LogInformation("   - Inheritable permissions completed: blueprint {Blueprint} to resourceAppId {ResourceAppId} scopes [{Scopes}]",
                    effectiveBlueprintAppId, resourceAppId, string.Join(' ', scopes));

                logger.LogInformation("Successfully granted scopes [{Scopes}] to resource app {ResourceAppId}", string.Join(", ", scopes), resourceAppId);
            }
            catch (DeployScopesException)
            {
                // Re-throw known structured exceptions so global handlers can format them
                throw;
            }
            catch (Exception ex)
            {
                // Wrap unexpected exceptions to avoid leaking stack traces to users
                throw new DeployScopesException(ex.Message, ex);
            }
        },
        configOption, resourceAppIdOption, scopesOption, tenantIdOption, blueprintAppIdOption, verboseOption, dryRunOption);

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
        ILogger logger,
        GraphApiService graphApiService)
    {
        // Read scopes from toolingManifest.json (at deploymentProjectPath)
        var manifestPath = Path.Combine(config.DeploymentProjectPath ?? string.Empty, "toolingManifest.json");
        var toolingScopes = await ManifestHelper.GetRequiredScopesAsync(manifestPath);

        // 1. Apply MCP OAuth2 permission grants
        logger.LogInformation("1. Applying MCP OAuth2 permission grants...");
        await EnsureMcpOauth2PermissionGrantsAsync(
            graphApiService,
            config,
            toolingScopes,
            logger
        );

        // 2. Consent to required scopes for the agent identity
        logger.LogInformation("2. Consenting to required MCP scopes for the agent identity...");
        await EnsureMcpAdminConsentForAgenticAppAsync(
            graphApiService,
            config,
            toolingScopes,
            logger
        );

        // 3. Apply inheritable permissions on the agent identity blueprint
        logger.LogInformation("3. Applying MCP inheritable permissions...");
        await EnsureMcpInheritablePermissionsAsync(
            graphApiService,
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
            config.TenantId, config.AgentBlueprintId, resourceAppId, scopes, new List<string>() { "AgentIdentityBlueprint.ReadWrite.All" }, ct);

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

    private static async Task EnsureMcpAdminConsentForAgenticAppAsync(
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
    /// Performs MCP tool permissions deployment
    /// </summary>
    private static async Task DeployScopesPermissionsAsync(
        Agent365Config config,
        string blueprintAppId,
        string tenantId,
        string resourceAppId,
        string[] scopes,
        CommandExecutor executor,
        ILogger logger,
        GraphApiService graphApiService)
    {
        // 1. Ensure admin consent (programmatic preferred, fallback to interactive)
        logger.LogInformation("1. Ensuring admin consent for blueprint (programmatic preferred, interactive fallback)...");
        await EnsureNewScopesBlueprintAsync(
            graphApiService,
            tenantId,
            blueprintAppId,
            resourceAppId,
            scopes,
            executor,
            logger
        );

        // 2. Apply inheritable permissions on the agent identity blueprint
        logger.LogInformation("2. Applying inheritable permissions...");
        await EnsureInheritablePermissionsAsync(
            graphApiService,
            tenantId,
            blueprintAppId,
            resourceAppId,
            scopes,
            logger
        );

        logger.LogInformation("Deploy Microsoft Agent 365 Tool Permissions completed successfully!");
    }

    private static async Task EnsureInheritablePermissionsAsync(
        GraphApiService graphService,
        string tenantId,
        string blueprintAppId,
        string resourceAppId,
        string[] scopes,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blueprintAppId))
            throw new InvalidOperationException("AgentBlueprintId (appId) is required.");

        var (ok, alreadyExists, err) = await graphService.SetInheritablePermissionsAsyncV2(
           tenantId, blueprintAppId, resourceAppId, scopes, new List<string>() { "AgentIdentityBlueprint.ReadWrite.All" }, ct);

        logger.LogInformation("   - Inheritable permissions completed: blueprint {Blueprint} to resourceAppId {ResourceAppId} scopes [{Scopes}]",
            blueprintAppId, resourceAppId, string.Join(' ', scopes));
    }

    private static async Task EnsureNewScopesBlueprintAsync(
        GraphApiService graphService,
        string tenantId,
        string blueprintAppId,
        string resourceAppId,
        string[] scopes,
        CommandExecutor executor,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blueprintAppId))
            throw new InvalidOperationException("BlueprintAppId is required.");

        // clientId must be the *service principal objectId* of the blueprint app
        var blueprintAppSpObjectId = await graphService.LookupServicePrincipalByAppIdAsync(
            tenantId,
            blueprintAppId ?? string.Empty
        ) ?? throw new InvalidOperationException($"Service Principal not found for agentic appId {blueprintAppId}");

        var objectId = await graphService.LookupServicePrincipalByAppIdAsync(tenantId, resourceAppId)
            ?? throw new InvalidOperationException("Object id not found for appId " + resourceAppId);

        // First, attempt programmatic admin consent by creating/updating oauth2PermissionGrant
        try
        {
            var ok = await graphService.ReplaceOauth2PermissionGrantAsync(tenantId, blueprintAppSpObjectId, objectId, scopes, ct);
            if (ok)
            {
                logger.LogInformation("   - Programmatic admin consent applied for blueprint {Blueprint}", blueprintAppId);
                return;
            }
            logger.LogWarning("Programmatic admin consent not applied for blueprint {Blueprint}; falling back to interactive consent", blueprintAppId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Programmatic admin consent attempt threw an exception: {Message}", ex.Message);
        }

        // Programmatic consent failed - fall back to interactive admin consent URL
        var scopesJoined = string.Join(' ', scopes);
        var consentUrl = $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent?client_id={blueprintAppId}&scope={Uri.EscapeDataString(scopesJoined)}&redirect_uri=https://entra.microsoft.com/TokenAuthorize&state=xyz123";

        logger.LogInformation("Opening browser for admin consent: {Url}", consentUrl);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = consentUrl,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Best-effort; continue to poll even if browser cannot be opened
            logger.LogWarning("Failed to open browser for admin consent. Please open the following URL manually:\n{Url}", consentUrl);
        }

        var granted = await AdminConsentHelper.PollAdminConsentAsync(executor, logger, blueprintAppId ?? string.Empty, "OAuth2 Scopes", 180, 5, ct);
        if (!granted)
        {
            throw new InvalidOperationException("Failed to detect admin consent for blueprint after interactive flow.");
        }

        logger.LogInformation("   - Admin consent detected for blueprint {Blueprint} to resourceAppId {ResourceAppId} scopes [{Scopes}]",
            blueprintAppId, resourceAppId, string.Join(' ', scopes));
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

    /// <summary>
    /// Determines if web app deployment should be skipped because an external messaging endpoint is configured
    /// </summary>
    /// <param name="config">Configuration to check</param>
    /// <param name="logger">Logger for informational messages</param>
    /// <returns>True if deployment should be skipped, false otherwise</returns>
    private static bool ShouldSkipWebAppDeployment(Agent365Config config, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(config.WebAppName) && !string.IsNullOrWhiteSpace(config.MessagingEndpoint))
        {
            logger.LogInformation($"MessagingEndpoint {config.MessagingEndpoint} is already provided and hence WebApp deployment is skipped.");
            return true;
        }

        return false;
    }
}

