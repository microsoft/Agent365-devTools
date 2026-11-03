// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers;

/// <summary>
/// Validates values that are passed to external command lines.
/// </summary>
public static class CommandArgumentGuard
{
    /// <summary>
    /// Returns the value as a canonical GUID string, or throws if it is not a GUID.
    /// </summary>
    public static string RequireGuid(string? value, string fieldName)
    {
        if (!Guid.TryParse(value, out var guid))
        {
            throw new ArgumentException($"{fieldName} must be a valid GUID.", fieldName);
        }

        return guid.ToString("D");
    }

    /// <summary>
    /// Throws if a command line passed to 'cmd.exe /c' could chain or redirect commands.
    /// Metacharacters are allowed only inside balanced double quotes, where cmd.exe treats them literally.
    /// </summary>
    public static void EnsureSafeForCmd(string commandLine)
    {
        var inQuotes = false;
        foreach (var c in commandLine)
        {
            if (c is '\r' or '\n' or '\0')
            {
                throw new ArgumentException("Command line contains a line break or null character.", nameof(commandLine));
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && (c is '&' or '|' or '<' or '>' or '^'))
            {
                throw new ArgumentException($"Command line contains the shell metacharacter '{c}' outside quotes.", nameof(commandLine));
            }
        }

        if (inQuotes)
        {
            throw new ArgumentException("Command line contains unbalanced double quotes.", nameof(commandLine));
        }
    }
}
