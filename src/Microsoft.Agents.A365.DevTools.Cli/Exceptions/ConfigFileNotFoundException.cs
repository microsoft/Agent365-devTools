// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Exception thrown when the a365.config.json configuration file cannot be found.
/// This is a USER ERROR - the file is missing or the command was run from the wrong directory.
/// </summary>
public class ConfigFileNotFoundException : Agent365Exception
{
    public ConfigFileNotFoundException()
        : base(
            errorCode: "CONFIG_NOT_FOUND",
            issueDescription: "Configuration file not found.",
            mitigationSteps:
            [
                "Run this command from your agent project directory, or use --agent-name <name> to specify the agent."
            ])
    {
    }

    public override int ExitCode => 2; // Configuration error
}
