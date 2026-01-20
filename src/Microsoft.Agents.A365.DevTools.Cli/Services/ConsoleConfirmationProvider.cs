// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Console-based implementation of user confirmation prompts.
/// </summary>
public class ConsoleConfirmationProvider : IConfirmationProvider
{
    /// <summary>
    /// Prompts the user for a yes/no confirmation via Console.
    /// </summary>
    /// <param name="prompt">The prompt message to display to the user</param>
    /// <returns>True if user confirms (y/yes), false otherwise</returns>
    public Task<bool> ConfirmAsync(string prompt)
    {
        Console.Write(prompt);
        var response = Console.ReadLine()?.Trim().ToLowerInvariant();
        return Task.FromResult(response == "y" || response == "yes");
    }

    /// <summary>
    /// Prompts the user to type a specific confirmation string via Console.
    /// </summary>
    /// <param name="prompt">The prompt message to display to the user</param>
    /// <param name="expectedResponse">The exact string the user must type to confirm</param>
    /// <returns>True if user types the expected response exactly, false otherwise</returns>
    public Task<bool> ConfirmWithTypedResponseAsync(string prompt, string expectedResponse)
    {
        Console.Write(prompt);
        var response = Console.ReadLine()?.Trim();
        return Task.FromResult(response == expectedResponse);
    }
}
