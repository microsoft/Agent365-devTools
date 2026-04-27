// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands
{
    /// <summary>
    /// Setup command - Agent 365 environment setup with granular subcommands
    /// Supports permission-based workflow: blueprint -> permissions -> endpoint
    /// </summary>
    public class SetupCommand
    {
        /// <summary>
        /// Returns the base requirement checks shared by all setup subcommands:
        /// Azure authentication, Frontier Preview enrollment, and PowerShell modules.
        /// </summary>
        public static List<IRequirementCheck> GetBaseChecks(AzureAuthValidator auth)
            => [
                new AzureAuthRequirementCheck(auth),
                new FrontierPreviewRequirementCheck(),
                new PowerShellModulesRequirementCheck()
            ];

        public static Command CreateCommand(
            ILogger<SetupCommand> logger,
            IConfigService configService,
            CommandExecutor executor,
            ITeamsGraphBackendConfigurator backendConfigurator,
            AzureAuthValidator authValidator,
            PlatformDetector platformDetector,
            GraphApiService graphApiService,
            AgentBlueprintService blueprintService,
            BlueprintLookupService blueprintLookupService,
            FederatedCredentialService federatedCredentialService,
            IClientAppValidator clientAppValidator,
            IConfirmationProvider confirmationProvider,
            ArmApiService? armApiService = null,
            IEnumerable<IRequirementCheck>? requirementChecksOverride = null)
        {
            var command = new Command("setup",
                "Set up your Agent 365 environment with granular control over each step\n\n" +
                "Recommended execution order:\n" +
                "  0. a365 setup requirements           # Check prerequisites (optional)\n" +
                "  1. a365 setup blueprint\n" +
                "  2. a365 setup permissions mcp\n" +
                "  3. a365 setup permissions bot\n" +
                "Or run all steps at once:\n" +
                "  a365 setup all                      # Full setup\n\n" +
                "For non-admin users — complete GA-only grants after setup all:\n" +
                "  a365 setup admin --config-dir \"<path>\"  # Run as Global Administrator");

            // Add subcommands
            command.AddCommand(RequirementsSubcommand.CreateCommand(
                logger, configService, authValidator, clientAppValidator, requirementChecksOverride));

            command.AddCommand(BlueprintSubcommand.CreateCommand(
                logger, configService, executor, authValidator, platformDetector, backendConfigurator, graphApiService, blueprintService, clientAppValidator, blueprintLookupService, federatedCredentialService));

            command.AddCommand(PermissionsSubcommand.CreateCommand(
                logger, authValidator, configService, executor, graphApiService, blueprintService, confirmationProvider));

            command.AddCommand(AllSubcommand.CreateCommand(
                logger, configService, executor, backendConfigurator, authValidator, platformDetector, graphApiService, blueprintService, clientAppValidator, blueprintLookupService, federatedCredentialService, armApiService, confirmationProvider));

            command.AddCommand(AdminSubcommand.CreateCommand(
                logger, configService, authValidator, graphApiService, confirmationProvider, blueprintService));

            return command;
        }
    }
}
