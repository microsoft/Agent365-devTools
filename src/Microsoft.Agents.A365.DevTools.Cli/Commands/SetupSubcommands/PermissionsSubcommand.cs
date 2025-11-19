// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;

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
        CommandExecutor executor)
    {
        var permissionsCommand = new Command("permissions", 
            "Configure OAuth2 permission grants and inheritable permissions\n" +
            "Minimum required permissions: Global Administrator\n" +
            "Prerequisites: Blueprint must exist (run 'a365 setup blueprint' first)\n" +
            "Subcommands should be run in order: mcp, then bot\n");

        // Add subcommands
        permissionsCommand.AddCommand(CreateMcpSubcommand(logger, configService, executor));
        permissionsCommand.AddCommand(CreateBotSubcommand(logger, configService, executor));

        return permissionsCommand;
    }

    /// <summary>
    /// MCP permissions subcommand
    /// </summary>
    private static Command CreateMcpSubcommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor)
    {
        var command = new Command("mcp", 
            "Configure MCP server OAuth2 grants and inheritable permissions\n" +
            "Minimum required permissions: Global Administrator\n\n" +
            "Prerequisites: Blueprint must exist (run 'a365 setup blueprint' first)\n" +
            "Next step: a365 setup permissions bot");

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

            // Read scopes from toolingManifest.json
            var manifestPath = Path.Combine(setupConfig.DeploymentProjectPath ?? string.Empty, "toolingManifest.json");
            var toolingScopes = await ManifestHelper.GetRequiredScopesAsync(manifestPath);

            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Configure MCP Permissions");
                logger.LogInformation("Would configure OAuth2 grants and inheritable permissions:");
                logger.LogInformation("  - Blueprint: {BlueprintId}", setupConfig.AgentBlueprintId);
                logger.LogInformation("  - Resource: Agent 365 Tools ({Environment})", setupConfig.Environment);
                logger.LogInformation("  - Scopes: {Scopes}", string.Join(", ", toolingScopes));
                return;
            }

            logger.LogInformation("Configuring MCP server permissions...");
            logger.LogInformation("");

            var graphService = new GraphApiService(
                LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<GraphApiService>(),
                executor);

            try
            {
                // OAuth2 permission grants
                await SetupHelpers.EnsureMcpOauth2PermissionGrantsAsync(
                    graphService, setupConfig, toolingScopes, logger);

                // Inheritable permissions
                await SetupHelpers.EnsureMcpInheritablePermissionsAsync(
                    graphService, setupConfig, toolingScopes, logger);

                logger.LogInformation("");
                logger.LogInformation("MCP server permissions configured successfully");
                logger.LogInformation("");
                logger.LogInformation("Next step: Run 'a365 setup permissions bot' to configure Bot API permissions");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to configure MCP permissions: {Message}", ex.Message);
                Environment.Exit(1);
            }
        }, configOption, verboseOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Bot API permissions subcommand
    /// </summary>
    private static Command CreateBotSubcommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor)
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

            logger.LogInformation("Configuring Messaging Bot API permissions...");
            logger.LogInformation("");

            var graphService = new GraphApiService(
                LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<GraphApiService>(),
                executor);

            try
            {
                var blueprintSpObjectId = await graphService.LookupServicePrincipalByAppIdAsync(setupConfig.TenantId, setupConfig.AgentBlueprintId)
                    ?? throw new InvalidOperationException($"Blueprint Service Principal not found for appId {setupConfig.AgentBlueprintId}");

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
                var (ok, already, err) = await graphService.SetInheritablePermissionsAsync(
                    setupConfig.TenantId,
                    setupConfig.AgentBlueprintId,
                    ConfigConstants.MessagingBotApiAppId,
                    new[] { "Authorization.ReadWrite", "user_impersonation" });

                if (!ok && !already)
                {
                    throw new InvalidOperationException($"Failed to set inheritable permissions for Messaging Bot API: {err}");
                }

                logger.LogInformation("");
                logger.LogInformation("Messaging Bot API permissions configured successfully");
                logger.LogInformation("");
                logger.LogInformation("Next step: Run 'a365 setup endpoint' to register messaging endpoint");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to configure Bot API permissions: {Message}", ex.Message);
                Environment.Exit(1);
            }
        }, configOption, verboseOption, dryRunOption);

        return command;
    }
}
