// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.DevelopSubcommands;

/// <summary>
/// AddPermissions subcommand - Adds MCP server API permissions to a custom application
/// </summary>
internal static class AddPermissionsSubcommand
{
    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService)
    {
        var command = new Command(
            "add-permissions",
            "Add MCP server API permissions to a custom application");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Configuration file path");

        var manifestOption = new Option<FileInfo?>(
            ["--manifest", "-m"],
            description: "Path to ToolingManifest.json (defaults to current directory)");

        var appIdOption = new Option<string>(
            ["--app-id"],
            description: "Application (client) ID to add permissions to. If not specified, uses the clientAppId from config")
        {
            IsRequired = false
        };

        var scopesOption = new Option<string[]?>(
            ["--scopes"],
            description: "Specific scopes to add (e.g., McpServers.Mail.All McpServers.Calendar.All). If not specified, uses all scopes from ToolingManifest.json")
        {
            AllowMultipleArgumentsPerToken = true
        };

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Show detailed output");

        var dryRunOption = new Option<bool>(
            ["--dry-run"],
            description: "Show what would be done without executing");

        command.AddOption(configOption);
        command.AddOption(manifestOption);
        command.AddOption(appIdOption);
        command.AddOption(scopesOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);

        command.SetHandler(async (config, manifest, appId, scopes, verbose, dryRun) =>
        {
            try
            {
                logger.LogInformation("Adding MCP server permissions to application...");
                logger.LogInformation("");

                // Check if config file exists or if --app-id was provided
                var setupConfig = File.Exists(config.FullName) 
                    ? await configService.LoadAsync(config.FullName) 
                    : null;

                if (setupConfig == null && string.IsNullOrWhiteSpace(appId))
                {
                    logger.LogError("Configuration file not found: {ConfigPath}", config.FullName);
                    logger.LogInformation("");
                    logger.LogInformation("To add MCP server permissions, you must either:");
                    logger.LogInformation("  1. Run 'a365 setup all --agent-name <name>' to create a config file.");
                    logger.LogInformation("  2. Specify the application ID using: a365 develop addpermissions --app-id <your-app-id>");
                    logger.LogInformation("");
                    logger.LogInformation("Example: a365 develop addpermissions --app-id 12345678-1234-1234-1234-123456789abc --scopes McpServers.Mail.All");
                    Environment.Exit(1);
                    return;
                }

                // Determine target application ID
                string targetAppId;
                if (!string.IsNullOrWhiteSpace(appId))
                {
                    targetAppId = appId;
                    logger.LogInformation("Target Application ID (from --app-id): {AppId}", targetAppId);
                }
                else if (setupConfig != null && !string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
                {
                    targetAppId = setupConfig.ClientAppId;
                    logger.LogInformation("Target Application ID (from config): {AppId}", targetAppId);
                }
                else
                {
                    logger.LogError("No application ID specified. Use --app-id or ensure ClientAppId is set in config.");
                    logger.LogInformation("");
                    logger.LogInformation("Example: a365 develop addpermissions --app-id <your-app-id>");
                    Environment.Exit(1);
                    return;
                }

                // Determine manifest path
                var manifestPath = manifest?.FullName 
                    ?? Path.Combine(setupConfig?.DeploymentProjectPath ?? Environment.CurrentDirectory, McpConstants.ToolingManifestFileName);

                var environment = setupConfig?.Environment ?? "prod";
                var atgResourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(environment);

                // Determine which scopes to add.
                // Explicit --scopes: single ATG call (no audience info available).
                // Manifest: per-audience calls via GetScopesByAudienceAsync (V1 + V2 support).
                string[]? requestedScopes = null;
                Dictionary<string, string[]>? scopesByAudience = null;

                if (scopes != null && scopes.Length > 0)
                {
                    requestedScopes = scopes;
                    logger.LogInformation("Using user-specified scopes: {Scopes}", string.Join(", ", requestedScopes));
                    logger.LogInformation("");
                }
                else
                {
                    if (!File.Exists(manifestPath))
                    {
                        logger.LogError("ToolingManifest.json not found at: {Path}", manifestPath);
                        logger.LogInformation("");
                        logger.LogInformation("Please ensure ToolingManifest.json exists in your project directory");
                        logger.LogInformation("or specify scopes explicitly with --scopes option.");
                        logger.LogInformation("");
                        logger.LogInformation("Example: a365 develop addpermissions --scopes McpServers.Mail.All McpServers.Calendar.All");
                        Environment.Exit(1);
                        return;
                    }

                    logger.LogInformation("Reading MCP server configuration from: {Path}", manifestPath);

                    scopesByAudience = await ManifestHelper.GetScopesByAudienceAsync(manifestPath, resolvedAtgAppId: atgResourceAppId);

                    if (scopesByAudience.Count == 0)
                    {
                        logger.LogError("No scopes found in ToolingManifest.json");
                        logger.LogInformation("You can specify scopes explicitly with --scopes option.");
                        Environment.Exit(1);
                        return;
                    }

                    var totalScopes = scopesByAudience.Values.SelectMany(s => s).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    logger.LogInformation("Found {AudienceCount} audience(s) with {ScopeCount} unique scope(s) from manifest",
                        scopesByAudience.Count, totalScopes);
                }

                logger.LogInformation("");

                // Dry run mode
                if (dryRun)
                {
                    logger.LogInformation("DRY RUN: Add MCP Server Permissions");
                    logger.LogInformation("Would add the following permissions to application {AppId}:", targetAppId);
                    logger.LogInformation("");
                    if (scopesByAudience != null)
                    {
                        foreach (var kvp in scopesByAudience)
                            logger.LogInformation("  {ResourceAppId} — {Scopes}", kvp.Key, string.Join(", ", kvp.Value));
                    }
                    else
                    {
                        logger.LogInformation("  {ResourceAppId} — {Scopes}", atgResourceAppId, string.Join(", ", requestedScopes!));
                    }
                    logger.LogInformation("");
                    logger.LogInformation("No changes made (dry run mode)");
                    return;
                }

                // Add permissions to the application
                logger.LogInformation("Adding permissions to application...");
                logger.LogInformation("");

                string tenantId = await TenantDetectionHelper.DetectTenantIdAsync(setupConfig, logger) ?? string.Empty;

                bool success = true;
                if (scopesByAudience != null)
                {
                    // Per-audience calls — one entry per resource app ID (V1 + V2)
                    foreach (var kvp in scopesByAudience)
                    {
                        logger.LogInformation("Processing resource: {ResourceAppId}", kvp.Key);
                        try
                        {
                            var ok = await blueprintService.AddRequiredResourceAccessAsync(
                                tenantId, targetAppId, kvp.Key, kvp.Value, isDelegated: true);
                            if (ok)
                                logger.LogInformation("  Added permissions for {ResourceAppId}", kvp.Key);
                            else
                            {
                                logger.LogError("  Failed to add permissions for {ResourceAppId}", kvp.Key);
                                success = false;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError("  {ResourceAppId}: {Message}", kvp.Key, ex.Message);
                            logger.LogDebug("    {StackTrace}", ex.StackTrace);
                            success = false;
                        }
                    }
                }
                else
                {
                    // Explicit --scopes: single ATG call
                    logger.LogInformation("Processing resource: {ResourceAppId}", atgResourceAppId);
                    try
                    {
                        success = await blueprintService.AddRequiredResourceAccessAsync(
                            tenantId, targetAppId, atgResourceAppId, requestedScopes!, isDelegated: true);
                        if (success)
                            logger.LogInformation("  Added permissions for {ResourceAppId}", atgResourceAppId);
                        else
                            logger.LogError("  Failed to add permissions for {ResourceAppId}", atgResourceAppId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("  Exception adding permissions for {ResourceAppId}: {Message}", atgResourceAppId, ex.Message);
                        logger.LogDebug("    {StackTrace}", ex.StackTrace);
                        success = false;
                    }
                }
                
                logger.LogInformation("");

                // Summary
                logger.LogInformation("=== Summary ===");

                if (success)
                {
                    logger.LogInformation("All permissions added successfully");
                    logger.LogInformation("");
                    logger.LogInformation("  Review permissions in Azure Portal: https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/{AppId}", targetAppId);
                }
                else
                {
                    logger.LogWarning("Permission addition failed. Review the errors above.");
                    Environment.Exit(1);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to add MCP server permissions: {Message}", ex.Message);
                Environment.Exit(1);
            }
        }, configOption, manifestOption, appIdOption, scopesOption, verboseOption, dryRunOption);

        return command;
    }
}
