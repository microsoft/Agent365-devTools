// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// Subcommand that lists well-known first-party agent blueprint IDs, so callers can discover the
/// ID to pass to the commands that require one.
/// </summary>
public static class AgentBlueprintCatalogSubcommand
{
    private const string CommandName = "list-agent-blueprints";

    /// <summary>
    /// Creates the list-agent-blueprints subcommand.
    /// </summary>
    public static Command CreateCommand(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var command = new Command(CommandName,
            "List well-known Microsoft first-party agent blueprint IDs and their names.");

        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "Show what would be done without executing");
        command.AddOption(dryRunOption);

        command.AddOption(new Option<bool>(["--verbose", "-v"], description: "Enable verbose logging"));

        command.SetHandler((InvocationContext context) =>
        {
            if (context.ParseResult.GetValueForOption(dryRunOption))
            {
                logger.LogInformation("[DRY RUN] Would list first-party agent blueprint IDs and names");
                context.ExitCode = 0;
                return;
            }

            var blueprints = AgentBlueprintCatalog.FirstPartyBlueprints;

            if (blueprints.Count == 0)
            {
                logger.LogWarning("No first-party agent blueprints are registered in this CLI version.");
                context.ExitCode = 1;
                return;
            }

            // Pad to the longest name so IDs line up and stay easy to copy.
            var nameWidth = blueprints.Max(b => b.DisplayName.Length);

            logger.LogInformation("First-party agent blueprints:");
            logger.LogInformation("");

            foreach (var blueprint in blueprints)
            {
                logger.LogInformation("  {Name}  {Id}", blueprint.DisplayName.PadRight(nameWidth), blueprint.BlueprintId);
            }

            logger.LogInformation("");
            logger.LogInformation("Pass an ID with --agent-blueprint-id, for example:");
            logger.LogInformation(
                "  a365 develop-mcp list-agent-instances --agent-blueprint-id {Id} --mcp-server-name <name>",
                blueprints[0].BlueprintId);

            context.ExitCode = 0;
        });

        return command;
    }
}
