// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Exception thrown when the a365.config.json configuration file cannot be found.
/// This is a USER ERROR - the file is missing or the command was run from the wrong directory.
/// </summary>
public class ConfigFileNotFoundException : Agent365Exception
{
    public ConfigFileNotFoundException(string configFilePath)
        : base(
            errorCode: "CONFIG_NOT_FOUND",
            issueDescription: $"Configuration file not found: {configFilePath}",
            mitigationSteps:
            [
                "Make sure you are running this command from your agent project directory.",
                "Pass --agent-name <name> to run without a config file.",
                "Or run 'a365 setup all --agent-name <name>' to perform a full setup."
            ])
    {
    }

    public override int ExitCode => 2; // Configuration error
}
