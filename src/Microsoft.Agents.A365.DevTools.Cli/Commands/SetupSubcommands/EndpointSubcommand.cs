// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Endpoint subcommand - Registers blueprint messaging endpoint (Azure Bot Service)
/// Required Permissions: Azure Subscription Contributor
/// </summary>
internal static class EndpointSubcommand
{
    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        IBotConfigurator botConfigurator,
        PlatformDetector platformDetector)
    {
        var command = new Command("endpoint", 
            "Register blueprint messaging endpoint (Azure Bot Service)\n" +
            "Minimum required permissions: Azure Subscription Contributor\n");

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

            if (string.IsNullOrWhiteSpace(setupConfig.WebAppName))
            {
                logger.LogError("Web App Name not found. Run 'a365 setup infrastructure' first.");
                Environment.Exit(1);
            }

            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Register Messaging Endpoint");
                logger.LogInformation("Would register Bot Service endpoint:");
                logger.LogInformation("  - Endpoint Name: {Name}-endpoint", setupConfig.WebAppName);
                logger.LogInformation("  - Messaging URL: https://{Name}.azurewebsites.net/api/messages", setupConfig.WebAppName);
                logger.LogInformation("  - Blueprint ID: {Id}", setupConfig.AgentBlueprintId);
                logger.LogInformation("Would sync generated configuration to project settings");
                return;
            }

            await RegisterEndpointAndSyncAsync(
                configPath: config.FullName,
                logger: logger,
                configService: configService,
                botConfigurator: botConfigurator,
                platformDetector: platformDetector);

            // Display verification info and summary
            await SetupHelpers.DisplayVerificationInfoAsync(config, logger);
            
        }, configOption, verboseOption, dryRunOption);

        return command;
    }

    #region Public Static Implementation Method (for AllSubcommand)

    /// <summary>
    /// Registers blueprint messaging endpoint and syncs project settings.
    /// Public method that can be called by AllSubcommand.
    /// </summary>
    public static async Task RegisterEndpointAndSyncAsync(
        string configPath,
        ILogger logger,
        IConfigService configService,
        IBotConfigurator botConfigurator,
        PlatformDetector platformDetector,
        CancellationToken cancellationToken = default)
    {
        var setupConfig = await configService.LoadAsync(configPath);

        logger.LogInformation("Registering blueprint messaging endpoint...");
        logger.LogInformation("");

        await SetupHelpers.RegisterBlueprintMessagingEndpointAsync(
            setupConfig, logger, botConfigurator);


        setupConfig.Completed = true;
        setupConfig.CompletedAt = DateTime.UtcNow;

        await configService.SaveStateAsync(setupConfig);

        logger.LogInformation("");
        logger.LogInformation("Blueprint messaging endpoint registered successfully");

        // Sync generated config to project settings (appsettings.json or .env)
        logger.LogInformation("");
        logger.LogInformation("Syncing configuration to project settings...");
            
        var configFileInfo = new FileInfo(configPath);
        var generatedConfigPath = Path.Combine(
            configFileInfo.DirectoryName ?? Environment.CurrentDirectory,
            "a365.generated.config.json");

        try
        {
            await ProjectSettingsSyncHelper.ExecuteAsync(
                a365ConfigPath: configPath,
                a365GeneratedPath: generatedConfigPath,
                configService: configService,
                platformDetector: platformDetector,
                logger: logger);

            logger.LogInformation("Configuration synced to project settings successfully");
        }
        catch (Exception syncEx)
        {
            logger.LogWarning(syncEx, "Project settings sync failed (non-blocking). Please sync settings manually if needed.");
        }
    }

    #endregion
}
