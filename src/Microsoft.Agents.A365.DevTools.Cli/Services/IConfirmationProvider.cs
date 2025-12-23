// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Provides user confirmation prompts for destructive operations.
/// This interface abstracts Console I/O to enable unit testing.
/// </summary>
public interface IConfirmationProvider
{
    /// <summary>
    /// Prompts the user for a yes/no confirmation.
    /// </summary>
    /// <param name="prompt">The prompt message to display to the user</param>
    /// <returns>True if user confirms (y/yes), false otherwise</returns>
    Task<bool> ConfirmAsync(string prompt);

    /// <summary>
    /// Prompts the user to type a specific confirmation string.
    /// </summary>
    /// <param name="prompt">The prompt message to display to the user</param>
    /// <param name="expectedResponse">The exact string the user must type to confirm</param>
    /// <returns>True if user types the expected response exactly, false otherwise</returns>
    Task<bool> ConfirmWithTypedResponseAsync(string prompt, string expectedResponse);
}
