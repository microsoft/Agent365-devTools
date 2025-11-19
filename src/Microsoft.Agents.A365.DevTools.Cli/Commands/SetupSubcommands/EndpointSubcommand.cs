// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;

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
        IBotConfigurator botConfigurator)
    {
        var command = new Command("endpoint", 
            "Register blueprint messaging endpoint (Azure Bot Service)\n" +
            "Minimum required permissions: Azure Subscription Contributor\n" +
            "Prerequisites: Blueprint and permissions must be configured (run permissions commands first)\n" +
            "Next step: a365 create-instance identity (instance creation)\n");

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
                return;
            }

            logger.LogInformation("Registering blueprint messaging endpoint...");
            logger.LogInformation("");

            try
            {
                await SetupHelpers.RegisterBlueprintMessagingEndpointAsync(
                    setupConfig, logger, botConfigurator);

                logger.LogInformation("");
                logger.LogInformation("Blueprint messaging endpoint registered successfully");
                logger.LogInformation("");
                logger.LogInformation("Setup complete! Next steps:");
                logger.LogInformation("  1. Review Azure resources: a365 config display");
                logger.LogInformation("  2. Create agent instance: a365 create-instance identity");
                logger.LogInformation("  3. Deploy application: a365 deploy app");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register messaging endpoint: {Message}", ex.Message);
                Environment.Exit(1);
            }
        }, configOption, verboseOption, dryRunOption);

        return command;
    }
}
