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
            IEnumerable<IRequirementCheck>? requirementChecksOverride = null,
            IBootstrapConfigResolver? resolver = null)
        {
            var command = new Command("setup",
                "Set up your Agent 365 environment.\n\n" +
                "Recommended:\n" +
                "  a365 setup all                       Full setup in one command (preferred)\n\n" +
                "Granular subcommands (for re-runs or partial setup):\n" +
                "  a365 setup requirements              Validate prerequisites\n" +
                "  a365 setup blueprint                 Create the agent blueprint (Entra app)\n" +
                "  a365 setup blueprint list            List existing blueprints in the tenant (read-only)\n" +
                "  a365 setup permissions mcp           MCP server permissions (when using MCP tools)\n" +
                "  a365 setup permissions bot           Bot API + Observability + Power Platform\n" +
                "  a365 setup permissions custom        Inline custom resource permission (--resource-app-id, --scopes)\n" +
                "  a365 setup permissions copilotstudio Copilot Studio agent permissions\n\n" +
                "Add a new agent identity to an existing blueprint:\n" +
                "  a365 setup all --agent-name \"Support Europe\" --blueprint-id <guid>\n" +
                "  a365 setup all --agent-name \"Support Europe\" --select-blueprint    (interactive picker)\n\n" +
                "Roles required:\n" +
                "  - Agent ID Developer                  Blueprint + inheritable permissions\n" +
                "  - Global Administrator                Tenant-wide OAuth2 consent grants\n\n" +
                "Non-admin flow: any step that needs Global Administrator action prints\n" +
                "an admin-consent URL (and, when needed, a PowerShell snippet)\n" +
                "that an admin can run out-of-band.");

            // Add subcommands
            command.AddCommand(RequirementsSubcommand.CreateCommand(
                logger, configService, authValidator, clientAppValidator, executor, graphApiService, requirementChecksOverride));

            command.AddCommand(BlueprintSubcommand.CreateCommand(
                logger, configService, executor, authValidator, platformDetector, backendConfigurator, graphApiService, blueprintService, clientAppValidator, blueprintLookupService, federatedCredentialService, confirmationProvider, resolver: resolver));

            command.AddCommand(PermissionsSubcommand.CreateCommand(
                logger, authValidator, configService, executor, graphApiService, blueprintService, confirmationProvider, resolver: resolver));

            command.AddCommand(AllSubcommand.CreateCommand(
                logger, configService, executor, backendConfigurator, authValidator, platformDetector, graphApiService, blueprintService, clientAppValidator, blueprintLookupService, federatedCredentialService, armApiService, confirmationProvider, resolver));

            return command;
        }
    }
}
