// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

public class DeployCommand
{
    public static Command CreateCommand(
        ILogger<DeployCommand> logger, 
        IConfigService configService, 
        CommandExecutor executor,
        DeploymentService deploymentService,
        IAzureValidator azureValidator)
    {
        // Top-level command name set to 'deploy' so it appears in CLI help as 'deploy'
        var command = new Command("deploy", "Deploy Agent 365 application binaries to the configured Azure App Service");

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

        // Single handler for the deploy command 
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

                // Validate Azure CLI authentication, subscription, and environment
                if (!await azureValidator.ValidateAllAsync(configData.SubscriptionId))
                {
                    logger.LogError("Deployment cannot proceed without proper Azure CLI authentication and the correct subscription context");
                    return;
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
                    return;
                }
                
                logger.LogInformation("Confirmed Azure Web App '{WebAppName}' exists", configData.WebAppName);

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
            }
            catch (FileNotFoundException ex)
            {
                logger.LogError("Configuration file not found: {Message}", ex.Message);
                logger.LogInformation("");
                logger.LogInformation("To get started:");
                logger.LogInformation("  1. Copy a365.config.example.json to a365.config.json");
                logger.LogInformation("  2. Edit a365.config.json with your Azure tenant and subscription details");
                logger.LogInformation("  3. Run 'a365 deploy' to perform a deployment");
                logger.LogInformation("");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Deployment failed: {Message}", ex.Message);
            }
        }, configOption, verboseOption, dryRunOption, inspectOption, restartOption);

        return command;
    }

    /// <summary>
    /// Convert Agent 365Config to DeploymentConfiguration
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
}
