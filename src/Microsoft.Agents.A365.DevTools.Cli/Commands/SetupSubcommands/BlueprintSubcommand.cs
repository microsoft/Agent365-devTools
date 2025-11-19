// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Blueprint subcommand - Creates agent blueprint (Entra ID application)
/// Required Permissions: Agent ID Developer role
/// </summary>
internal static class BlueprintSubcommand
{
    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        IAzureValidator azureValidator,
        AzureWebAppCreator webAppCreator,
        PlatformDetector platformDetector)
    {
        var command = new Command("blueprint", 
            "Create agent blueprint (Entra ID application registration)\n" +
            "Minimum required permissions: Agent ID Developer role\n" +
            "Prerequisites: Run 'a365 setup infrastructure' first if infrastructure doesn't exist\n" +
            "Next step: a365 setup permissions mcp\n");

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
                logger.LogInformation("DRY RUN: Create Agent Blueprint");
                logger.LogInformation("Would create Entra ID application:");
                logger.LogInformation("  - Display Name: {DisplayName}", setupConfig.AgentIdentityDisplayName);
                logger.LogInformation("  - Tenant: {TenantId}", setupConfig.TenantId);
                logger.LogInformation("  - Blueprint will be created without infrastructure");
                return;
            }

            logger.LogInformation("Creating agent blueprint...");
            logger.LogInformation("" );

            // Validate Azure authentication
            if (!await azureValidator.ValidateAllAsync(setupConfig.SubscriptionId))
            {
                Environment.Exit(1);
            }

            var generatedConfigPath = Path.Combine(
                config.DirectoryName ?? Environment.CurrentDirectory,
                "a365.generated.config.json");

            // Create services
            var graphService = new GraphApiService(
                LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<GraphApiService>(),
                executor);

            var delegatedConsentService = new DelegatedConsentService(
                LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<DelegatedConsentService>(),
                graphService);

            var setupRunner = new A365SetupRunner(
                LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<A365SetupRunner>(),
                executor,
                graphService,
                webAppCreator,
                delegatedConsentService,
                platformDetector);

            // Always skip infrastructure - this command only creates blueprint
            var success = await setupRunner.RunAsync(config.FullName, generatedConfigPath, blueprintOnly: true);

            if (success)
            {
                logger.LogInformation("Agent blueprint created successfully");
                logger.LogInformation("Generated config saved: {Path}", generatedConfigPath);
                logger.LogInformation("");
                logger.LogInformation("Next steps:");
                logger.LogInformation("  1. Run 'a365 setup permissions mcp' to configure MCP permissions");
                logger.LogInformation("  2. Run 'a365 setup permissions bot' to configure Bot API permissions");
                logger.LogInformation("  3. Run 'a365 setup endpoint' to register messaging endpoint");
            }
            else
            {
                logger.LogError("Failed to create agent blueprint");
                Environment.Exit(1);
            }
        }, configOption, verboseOption, dryRunOption);

        return command;
    }
}
