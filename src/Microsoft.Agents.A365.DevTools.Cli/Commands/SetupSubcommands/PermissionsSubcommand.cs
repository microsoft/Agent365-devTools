// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Threading;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Permissions subcommand - Configures OAuth2 permission grants and inheritable permissions
/// Required Permissions: Global Administrator (for admin consent)
/// </summary>
internal static class PermissionsSubcommand
{
    /// <summary>
    /// Validates MCP permissions prerequisites without performing any actions.
    /// </summary>
    public static Task<List<string>> ValidateMcpAsync(
        Agent365Config config,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
        {
            errors.Add("Blueprint ID not found. Run 'a365 setup blueprint' first");
        }

        if (string.IsNullOrWhiteSpace(config.DeploymentProjectPath))
        {
            errors.Add("deploymentProjectPath is required to read toolingManifest.json");
            return Task.FromResult(errors);
        }

        var manifestPath = Path.Combine(config.DeploymentProjectPath, "toolingManifest.json");
        if (!File.Exists(manifestPath))
        {
            errors.Add($"toolingManifest.json not found at {manifestPath}");
        }

        return Task.FromResult(errors);
    }

    /// <summary>
    /// Validates Bot permissions prerequisites without performing any actions.
    /// </summary>
    public static Task<List<string>> ValidateBotAsync(
        Agent365Config config,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
        {
            errors.Add("Blueprint ID not found. Run 'a365 setup blueprint' first");
        }

        return Task.FromResult(errors);
    }
    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService)
    {
        var permissionsCommand = new Command("permissions", 
            "Configure OAuth2 permission grants and inheritable permissions\n" +
            "Minimum required permissions: Global Administrator\n");

        // Add subcommands
        permissionsCommand.AddCommand(CreateMcpSubcommand(logger, configService, executor, graphApiService, blueprintService));
        permissionsCommand.AddCommand(CreateBotSubcommand(logger, configService, executor, graphApiService, blueprintService));

        return permissionsCommand;
    }

    /// <summary>
    /// MCP permissions subcommand
    /// </summary>
    private static Command CreateMcpSubcommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService)
    {
        var command = new Command("mcp", 
            "Configure MCP server OAuth2 grants and inheritable permissions\n" +
            "Minimum required permissions: Global Administrator\n\n");

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

        command.SetHandler(async (config, verbose, dryRun) =>
        {
            var setupConfig = await configService.LoadAsync(config.FullName);

            if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
            {
                logger.LogError("Blueprint ID not found. Run 'a365 setup blueprint' first.");
                Environment.Exit(1);
            }

            // Configure GraphApiService with custom client app ID if available
            if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
            {
                graphApiService.CustomClientAppId = setupConfig.ClientAppId;
            }

            if (dryRun)
            {
                // Read scopes from toolingManifest.json
                var manifestPath = Path.Combine(setupConfig.DeploymentProjectPath ?? string.Empty, "toolingManifest.json");
                var toolingScopes = await ManifestHelper.GetRequiredScopesAsync(manifestPath);

                logger.LogInformation("DRY RUN: Configure MCP Permissions");
                logger.LogInformation("Would configure OAuth2 grants and inheritable permissions:");
                logger.LogInformation("  - Blueprint: {BlueprintId}", setupConfig.AgentBlueprintId);
                logger.LogInformation("  - Resource: Agent 365 Tools ({Environment})", setupConfig.Environment);
                logger.LogInformation("  - Scopes: {Scopes}", string.Join(", ", toolingScopes));
                return;
            }

            await ConfigureMcpPermissionsAsync(
                config.FullName,
                logger,
                configService,
                executor,
                graphApiService,
                blueprintService,
                setupConfig,
                false);

        }, configOption, verboseOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Bot API permissions subcommand
    /// </summary>
    private static Command CreateBotSubcommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService)
    {
        var command = new Command("bot", 
            "Configure Messaging Bot API OAuth2 grants and inheritable permissions\n" +
            "Minimum required permissions: Global Administrator\n\n" +
            "Prerequisites: Blueprint and MCP permissions (run 'a365 setup permissions mcp' first)\n" +
            "Next step: Deploy your agent (run 'a365 deploy' if hosting on Azure)");

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

        command.SetHandler(async (config, verbose, dryRun) =>
        {
            var setupConfig = await configService.LoadAsync(config.FullName);

            if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
            {
                logger.LogError("Blueprint ID not found. Run 'a365 setup blueprint' first.");
                Environment.Exit(1);
            }

            // Configure GraphApiService with custom client app ID if available
            if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
            {
                graphApiService.CustomClientAppId = setupConfig.ClientAppId;
            }

            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Configure Bot API Permissions");
                logger.LogInformation("Would configure Bot API permissions:");
                logger.LogInformation("  - Blueprint: {BlueprintId}", setupConfig.AgentBlueprintId);
                logger.LogInformation("  - Messaging Bot API: Authorization.ReadWrite, user_impersonation");
                logger.LogInformation("  - Observability API: user_impersonation");
                logger.LogInformation("  - Power Platform API: Connectivity.Connections.Read");
                return;
            }

            await ConfigureBotPermissionsAsync(
                config.FullName,
                logger,
                configService,
                executor,
                setupConfig,
                graphApiService,
                blueprintService,
                false);

        }, configOption, verboseOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Configures MCP server permissions (OAuth2 grants and inheritable permissions).
    /// Public method that can be called by AllSubcommand.
    /// </summary>
    public static async Task<bool> ConfigureMcpPermissionsAsync(
        string configPath,
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        Models.Agent365Config setupConfig,
        bool iSetupAll,
        SetupResults? setupResults = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("");
        logger.LogInformation("Configuring MCP server permissions...");
        logger.LogInformation("");

        try
        {
            // Read scopes from toolingManifest.json
            var manifestPath = Path.Combine(setupConfig.DeploymentProjectPath ?? string.Empty, "toolingManifest.json");
            var toolingScopes = await ManifestHelper.GetRequiredScopesAsync(manifestPath);

            var resourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(setupConfig.Environment);
            
            // Configure all permissions using unified method
            await SetupHelpers.EnsureResourcePermissionsAsync(
                graphApiService,
                blueprintService,
                setupConfig,
                resourceAppId,
                "Agent 365 Tools",
                toolingScopes,
                logger,
                addToRequiredResourceAccess: false,
                setInheritablePermissions: true,
                setupResults,
                cancellationToken);

            logger.LogInformation("");
            logger.LogInformation("MCP server permissions configured successfully");
            logger.LogInformation("");
            if (!iSetupAll)
            {
                logger.LogInformation("Next step: 'a365 setup permissions bot' to configure Bot API permissions");
            }

            // write changes to generated config
            await configService.SaveStateAsync(setupConfig);
            return true;
        }
        catch (Exception mcpEx)
        {
            logger.LogError("Failed to configure MCP server permissions: {Message}", mcpEx.Message);
            logger.LogInformation("To configure MCP permissions manually:");
            logger.LogInformation("  1. Ensure the agent blueprint has the required permissions in Azure Portal");
            logger.LogInformation("  2. Grant admin consent for the MCP scopes");
            logger.LogInformation("  3. Run 'a365 setup mcp' to retry MCP permission configuration");
            if (iSetupAll)
            {
                throw;
            }
            return false;
        }
    }

    /// <summary>
    /// Configures Bot API permissions (OAuth2 grants and inheritable permissions).
    /// Public method that can be called by AllSubcommand.
    /// </summary>
    public static async Task<bool> ConfigureBotPermissionsAsync(
        string configPath,
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        Models.Agent365Config setupConfig,
        GraphApiService graphService,
        AgentBlueprintService blueprintService,
        bool iSetupAll,
        SetupResults? setupResults = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("");
        logger.LogInformation("Configuring Messaging Bot API permissions...");
        logger.LogInformation("");

        try
        {
            // Configure Messaging Bot API permissions using unified method
            // Note: Messaging Bot API is a first-party Microsoft service with custom OAuth2 scopes
            // that are not published in the standard service principal permissions.
            // We skip addToRequiredResourceAccess because the scopes won't be found there.
            // The permissions appear in the portal via OAuth2 grants and inheritable permissions.
            await SetupHelpers.EnsureResourcePermissionsAsync(
                graphService,
                blueprintService,
                setupConfig,
                ConfigConstants.MessagingBotApiAppId,
                "Messaging Bot API",
                new[] { "Authorization.ReadWrite", "user_impersonation" },
                logger,
                addToRequiredResourceAccess: false,
                setInheritablePermissions: true,
                setupResults,
                cancellationToken);

            // Configure Observability API permissions using unified method
            // Note: Observability API is also a first-party Microsoft service
            await SetupHelpers.EnsureResourcePermissionsAsync(
                graphService,
                blueprintService,
                setupConfig,
                ConfigConstants.ObservabilityApiAppId,
                "Observability API",
                new[] { "user_impersonation" },
                logger,
                addToRequiredResourceAccess: false,
                setInheritablePermissions: true,
                setupResults,
                cancellationToken);

            // Configure Power Platform API permissions using unified method
            // Note: Using the MOS Power Platform API (8578e004-a5c6-46e7-913e-12f58912df43) which is
            // the Power Platform API for agent operations. This API exposes Connectivity.Connections.Read
            // for reading Power Platform connections.
            // Similar to Messaging Bot API, we skip addToRequiredResourceAccess because the scopes
            // won't be found in the standard service principal permissions.
            // The permissions appear in the portal via OAuth2 grants and inheritable permissions.
            await SetupHelpers.EnsureResourcePermissionsAsync(
                graphService,
                blueprintService,
                setupConfig,
                ConfigConstants.PowerPlatformApiAppId,
                "Power Platform API",
                new[] { "Connectivity.Connections.Read" },
                logger,
                addToRequiredResourceAccess: false,
                setInheritablePermissions: true,
                setupResults,
                cancellationToken);

            // write changes to generated config
            await configService.SaveStateAsync(setupConfig);

            logger.LogInformation("");
            logger.LogInformation("Bot API permissions configured successfully");
            logger.LogInformation("");
            if (!iSetupAll)
            {
                logger.LogInformation("Next step: Deploy your agent (run 'a365 deploy' if hosting on Azure)");
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to configure Bot API permissions: {Message}", ex.Message);
            if (iSetupAll)
            {
                throw;
            }
            return false;
        }
    }
}
