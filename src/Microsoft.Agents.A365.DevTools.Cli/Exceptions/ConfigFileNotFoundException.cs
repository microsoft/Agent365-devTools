// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Exception thrown when the a365.config.json configuration file cannot be found.
/// This is a USER ERROR - the file is missing or the command was run from the wrong directory.
/// Derives from FileNotFoundException so existing callers that catch FileNotFoundException
/// continue to work without changes.
/// </summary>
public class ConfigFileNotFoundException : FileNotFoundException
{
    public ConfigFileNotFoundException(string configFilePath)
        : base(
            message: $"Configuration file not found: {configFilePath}. " +
                     "Make sure you are running this command from your agent project directory. " +
                     "If you have not created a configuration file yet, run: a365 config init",
            fileName: configFilePath)
    {
    }

    public int ExitCode => 2; // Configuration error
}
