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
        GraphApiService graphApiService)
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
                    logger.LogInformation("  1. Create a config file using: a365 config init");
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
                    ?? Path.Combine(setupConfig?.DeploymentProjectPath ?? Environment.CurrentDirectory, "ToolingManifest.json");

                // Determine which scopes to add
                string[] requestedScopes;
                
                if (scopes != null && scopes.Length > 0)
                {
                    // User provided explicit scopes
                    requestedScopes = scopes;
                    logger.LogInformation("Using user-specified scopes: {Scopes}", string.Join(", ", requestedScopes));
                    logger.LogInformation("");
                }
                else
                {
                    // Read scopes from ToolingManifest.json
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

                    // Use ManifestHelper to extract scopes (includes fallback to mappings and McpServersMetadata.Read.All)
                    requestedScopes = await ManifestHelper.GetRequiredScopesAsync(manifestPath);

                    if (requestedScopes.Length == 0)
                    {
                        logger.LogError("No scopes found in ToolingManifest.json");
                        logger.LogInformation("You can specify scopes explicitly with --scopes option.");
                        Environment.Exit(1);
                        return;
                    }

                    logger.LogInformation("Collected {Count} unique scope(s) from manifest: {Scopes}", 
                        requestedScopes.Length, string.Join(", ", requestedScopes));
                }

                var environment = setupConfig?.Environment ?? "prod";
                var resourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(environment);
                
                logger.LogInformation("Target resource: Agent 365 Tools ({ResourceAppId})", resourceAppId);
                logger.LogInformation("");

                // Dry run mode
                if (dryRun)
                {
                    logger.LogInformation("DRY RUN: Add MCP Server Permissions");
                    logger.LogInformation("Would add the following permissions to application {AppId}:", targetAppId);
                    logger.LogInformation("");
                    logger.LogInformation("Resource: {ResourceAppId}", resourceAppId);
                    logger.LogInformation("  Scopes: {Scopes}", string.Join(", ", requestedScopes));
                    logger.LogInformation("");
                    logger.LogInformation("No changes made (dry run mode)");
                    return;
                }

                // Add permissions to the application
                logger.LogInformation("Adding permissions to application...");
                logger.LogInformation("");

                // Determine tenant ID (from config or detect from Azure CLI)
                string tenantId = await TenantDetectionHelper.DetectTenantIdAsync(setupConfig, logger) ?? string.Empty;

                logger.LogInformation("Processing resource: {ResourceAppId}", resourceAppId);
                
                bool success;
                try
                {
                    success = await graphApiService.AddRequiredResourceAccessAsync(
                        tenantId,
                        targetAppId,
                        resourceAppId,
                        requestedScopes,
                        isDelegated: true);

                    if (success)
                    {
                        logger.LogInformation("  [SUCCESS] Successfully added permissions for {ResourceAppId}", resourceAppId);
                    }
                    else
                    {
                        logger.LogError("  [FAILED] Failed to add permissions for {ResourceAppId}", resourceAppId);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError("  [ERROR] Exception adding permissions for {ResourceAppId}: {Message}", resourceAppId, ex.Message);
                    logger.LogDebug("    {StackTrace}", ex.StackTrace);
                    success = false;
                }
                
                logger.LogInformation("");

                // Summary
                logger.LogInformation("=== Summary ===");

                if (success)
                {
                    logger.LogInformation("[SUCCESS] All permissions added successfully!");
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
