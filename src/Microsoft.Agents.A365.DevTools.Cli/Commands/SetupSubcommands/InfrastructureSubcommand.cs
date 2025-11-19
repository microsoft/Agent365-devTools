// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Infrastructure subcommand - Creates Azure infrastructure (Resource Group, App Service Plan, Web App, MSI)
/// Required Permissions: Azure Subscription Contributor/Owner
/// </summary>
internal static class InfrastructureSubcommand
{
    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        IAzureValidator azureValidator,
        AzureWebAppCreator webAppCreator)
    {
        var command = new Command("infrastructure", 
            "Create Azure infrastructure (Resource Group, App Service Plan, Web App, Managed Identity)\n" +
            "Minimum required permissions: Azure Subscription Contributor\n" +
            "Prerequisites: None (this is step 1)\n" +
            "Next step: a365 setup blueprint");

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

            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Create Azure Infrastructure");
                logger.LogInformation("Would create the following resources:");
                logger.LogInformation("  - Resource Group: {ResourceGroup}", setupConfig.ResourceGroup);
                logger.LogInformation("  - Location: {Location}", setupConfig.Location);
                logger.LogInformation("  - App Service Plan: {PlanName} (SKU: {Sku})", 
                    setupConfig.AppServicePlanName, setupConfig.AppServicePlanSku);
                logger.LogInformation("  - Web App: {WebAppName}", setupConfig.WebAppName);
                logger.LogInformation("  - Managed Service Identity: Enabled");
                return;
            }

            logger.LogInformation("Creating Azure infrastructure...");
            logger.LogInformation("");

            // Validate Azure authentication
            if (!await azureValidator.ValidateAllAsync(setupConfig.SubscriptionId))
            {
                Environment.Exit(1);
            }

            logger.LogInformation("Creating Azure resources:");
            logger.LogInformation("  - Resource Group: {ResourceGroup}", setupConfig.ResourceGroup);
            logger.LogInformation("  - App Service Plan: {PlanName}", setupConfig.AppServicePlanName);
            logger.LogInformation("  - Web App: {WebAppName}", setupConfig.WebAppName);
            logger.LogInformation("");

            var success = await webAppCreator.CreateWebAppAsync(
                setupConfig.SubscriptionId,
                setupConfig.ResourceGroup,
                setupConfig.AppServicePlanName,
                setupConfig.WebAppName,
                setupConfig.Location,
                setupConfig.TenantId);

            if (success)
            {
                logger.LogInformation("Azure infrastructure created successfully");
                logger.LogInformation("");
                logger.LogInformation("Next step: Run 'a365 setup blueprint' to create the agent blueprint");
            }
            else
            {
                logger.LogError("Failed to create Azure infrastructure");
                Environment.Exit(1);
            }
        }, configOption, verboseOption, dryRunOption);

        return command;
    }
}
