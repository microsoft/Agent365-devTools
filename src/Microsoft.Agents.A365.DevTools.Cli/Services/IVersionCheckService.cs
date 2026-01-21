// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for checking if newer versions of the CLI are available.
/// </summary>
public interface IVersionCheckService
{
    /// <summary>
    /// Checks if a newer version of the CLI is available on NuGet.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort the check.</param>
    /// <returns>Version check result indicating if an update is available.</returns>
    Task<VersionCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
}
