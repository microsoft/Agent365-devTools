// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers;

/// <summary>
/// Helper methods for safely constructing command strings and preventing injection attacks.
/// </summary>
public static class CommandStringHelper
{
    /// <summary>
    /// Escapes a string for safe use in PowerShell single-quoted strings.
    /// In PowerShell single-quoted strings, only single quotes need escaping (doubled).
    /// </summary>
    /// <param name="input">The string to escape</param>
    /// <returns>The escaped string safe for PowerShell single-quoted context</returns>
    public static string EscapePowerShellString(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        // In PowerShell single-quoted strings, only single quotes need escaping
        return input.Replace("'", "''");
    }

    /// <summary>
    /// Checks if a string contains characters that could be dangerous in command contexts.
    /// This is useful for validation and logging purposes.
    /// </summary>
    /// <param name="input">The string to check</param>
    /// <returns>True if the string contains potentially dangerous characters</returns>
    public static bool ContainsDangerousCharacters(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        var dangerous = new[] { '\'', '"', ';', '`', '$', '&', '|', '<', '>', '\n', '\r', '\t' };
        return input.IndexOfAny(dangerous) >= 0;
    }
}
