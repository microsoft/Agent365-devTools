// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands
{
    /// <summary>
    /// Setup command - Agent 365 environment setup with granular subcommands
    /// Supports permission-based workflow: infrastructure -> blueprint -> permissions -> endpoint
    /// </summary>
    public class SetupCommand
    {
        public static Command CreateCommand(
            ILogger<SetupCommand> logger,
            IConfigService configService,
            CommandExecutor executor,
            DeploymentService deploymentService,
            IBotConfigurator botConfigurator,
            IAzureValidator azureValidator,
            AzureWebAppCreator webAppCreator,
            PlatformDetector platformDetector,
            GraphApiService graphApiService)
        {
            var command = new Command("setup", 
                "Set up your Agent 365 environment with granular control over each step\n\n" +
                "Recommended execution order:\n" +
                "  1. a365 setup infrastructure  (or skip if infrastructure exists)\n" +
                "  2. a365 setup blueprint\n" +
                "  3. a365 setup permissions mcp\n" +
                "  4. a365 setup permissions bot\n" +
                "Or run all steps at once:\n" +
                "  a365 setup all                      # Full setup (includes infrastructure)\n" +
                "  a365 setup all --skip-infrastructure # Skip infrastructure if it already exists");

            // Add subcommands
            command.AddCommand(InfrastructureSubcommand.CreateCommand(
                logger, configService, azureValidator, webAppCreator, platformDetector, executor));

            command.AddCommand(BlueprintSubcommand.CreateCommand(
                logger, configService, executor, azureValidator, webAppCreator, platformDetector, botConfigurator));

            command.AddCommand(PermissionsSubcommand.CreateCommand(
                logger, configService, executor, graphApiService));

            command.AddCommand(AllSubcommand.CreateCommand(
                logger, configService, executor, botConfigurator, azureValidator, webAppCreator, platformDetector, graphApiService));

            return command;
        }
    }
}
