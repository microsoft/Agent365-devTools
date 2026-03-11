// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for checking whether an active notice should be shown to the user.
/// </summary>
public interface INoticeService
{
    /// <summary>
    /// Checks for an active notice from the server-side notices endpoint.
    /// Results are cached locally with a TTL to avoid a network call on every invocation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort the check.</param>
    /// <returns>Result indicating whether there is an active notice to display.</returns>
    Task<NoticeResult> CheckForNoticeAsync(CancellationToken cancellationToken = default);
}
