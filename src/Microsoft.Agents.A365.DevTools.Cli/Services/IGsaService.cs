// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Turns Global Secure Access on and off for the tenant's Agent 365 Power Platform environment
/// through the Agent 365 platform, which resolves that environment itself.
/// </summary>
public interface IGsaService
{
    /// <summary>
    /// Sets Global Secure Access to the requested value.
    /// </summary>
    /// <param name="enabled">The value to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resulting status, or null when the change could not be requested.</returns>
    Task<GsaStatusResponse?> SetAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the current Global Secure Access setting.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current status, or null when it could not be read.</returns>
    Task<GsaStatusResponse?> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls status until the environment reports the requested value or the timeout elapses.
    /// </summary>
    /// <param name="expectedStatus">The status being waited for, Enabled or Disabled.</param>
    /// <param name="timeout">How long to keep polling.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The last status read, which may still differ if the timeout elapsed.</returns>
    Task<GsaStatusResponse?> WaitForStatusAsync(
        string expectedStatus,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
