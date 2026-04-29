// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Confirmation provider that automatically approves all prompts.
/// Used when <c>--yes</c> is passed to skip interactive confirmation.
/// </summary>
internal sealed class NonInteractiveConfirmationProvider : IConfirmationProvider
{
    public Task<bool> ConfirmAsync(string prompt) => Task.FromResult(true);

    public Task<bool> ConfirmWithTypedResponseAsync(string prompt, string expectedResponse) => Task.FromResult(true);
}
