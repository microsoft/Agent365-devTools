// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
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
    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService)
    {
        var permissionsCommand = new Command("permissions", 
            "Configure OAuth2 permission grants and inheritable permissions\n" +
            "Minimum required permissions: Global Administrator\n");

        // Add subcommands
        permissionsCommand.AddCommand(CreateMcpSubcommand(logger, configService, executor, graphApiService));
        permissionsCommand.AddCommand(CreateBotSubcommand(logger, configService, executor, graphApiService));

        return permissionsCommand;
    }

    /// <summary>
    /// MCP permissions subcommand
    /// </summary>
    private static Command CreateMcpSubcommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService)
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
        GraphApiService graphApiService)
    {
        var command = new Command("bot", 
            "Configure Messaging Bot API OAuth2 grants and inheritable permissions\n" +
            "Minimum required permissions: Global Administrator\n\n" +
            "Prerequisites: Blueprint and MCP permissions (run 'a365 setup permissions mcp' first)\n" +
            "Next step: a365 setup endpoint");

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

            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Configure Bot API Permissions");
                logger.LogInformation("Would configure Messaging Bot API permissions:");
                logger.LogInformation("  - Blueprint: {BlueprintId}", setupConfig.AgentBlueprintId);
                logger.LogInformation("  - Scopes: Authorization.ReadWrite, user_impersonation");
                return;
            }

            await ConfigureBotPermissionsAsync(
                config.FullName,
                logger,
                configService,
                executor,
                setupConfig,
                graphApiService,
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
        Models.Agent365Config setupConfig,
        bool iSetupAll,
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

            // OAuth2 permission grants
            await SetupHelpers.EnsureMcpOauth2PermissionGrantsAsync(
                graphApiService, setupConfig, toolingScopes, logger);

            // Inheritable permissions
            await SetupHelpers.EnsureMcpInheritablePermissionsAsync(
                graphApiService, setupConfig, toolingScopes, logger);

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
        bool iSetupAll,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("");
        logger.LogInformation("Configuring Messaging Bot API permissions...");
        logger.LogInformation("");

        try
        {
            if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
            {
                throw new SetupValidationException("AgentBlueprintId is required.");
            }

            var blueprintSpObjectId = await graphService.LookupServicePrincipalByAppIdAsync(setupConfig.TenantId, setupConfig.AgentBlueprintId)
                ?? throw new SetupValidationException($"Blueprint Service Principal not found for appId {setupConfig.AgentBlueprintId}");

            // Ensure Messaging Bot API SP exists
            var botApiResourceSpObjectId = await graphService.EnsureServicePrincipalForAppIdAsync(
                setupConfig.TenantId,
                ConfigConstants.MessagingBotApiAppId);

            // Grant OAuth2 permissions
            var botApiGrantOk = await graphService.CreateOrUpdateOauth2PermissionGrantAsync(
                setupConfig.TenantId,
                blueprintSpObjectId,
                botApiResourceSpObjectId,
                new[] { "Authorization.ReadWrite", "user_impersonation" });

            if (!botApiGrantOk)
            {
                throw new InvalidOperationException("Failed to create/update oauth2PermissionGrant for Messaging Bot API");
            }

            // Set inheritable permissions
            var (botApiOk, botApiAlready, botApiErr) = await graphService.SetInheritablePermissionsAsync(
                setupConfig.TenantId,
                setupConfig.AgentBlueprintId,
                ConfigConstants.MessagingBotApiAppId,
                new[] { "Authorization.ReadWrite", "user_impersonation" });

            if (!botApiOk && !botApiAlready)
            {
                throw new InvalidOperationException($"Failed to set inheritable permissions for Messaging Bot API: {botApiErr}");
            }

            // Ensure Messaging Bot API SP exists
            var observabilityApiResourceSpObjectId = await graphService.EnsureServicePrincipalForAppIdAsync(
                setupConfig.TenantId,
                ConfigConstants.ObservabilityApiAppId);

            // Grant OAuth2 permissions
            var observabilityApiGrantOk = await graphService.CreateOrUpdateOauth2PermissionGrantAsync(
                setupConfig.TenantId,
                blueprintSpObjectId,
                observabilityApiResourceSpObjectId,
                new[] { "user_impersonation" });

            if (!observabilityApiGrantOk)
            {
                throw new InvalidOperationException("Failed to create/update oauth2PermissionGrant for Observability API");
            }

            // Set inheritable permissions
            var (observabilityApiOk, observabilityApiAlready, observabilityApiErr) = await graphService.SetInheritablePermissionsAsync(
                setupConfig.TenantId,
                setupConfig.AgentBlueprintId,
                ConfigConstants.ObservabilityApiAppId,
                new[] { "user_impersonation" });

            if (!observabilityApiOk && !observabilityApiAlready)
            {
                throw new InvalidOperationException($"Failed to set inheritable permissions for Observability API: {observabilityApiErr}");
            }

            // write changes to generated config
            await configService.SaveStateAsync(setupConfig);

            logger.LogInformation("");
            logger.LogInformation("Messaging Bot API permissions configured successfully");
            logger.LogInformation("");
            if (!iSetupAll)
            {
                logger.LogInformation("Next step: Run 'a365 setup endpoint' to register messaging endpoint");
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
