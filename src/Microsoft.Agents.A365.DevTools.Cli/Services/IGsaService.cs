// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Turns Global Secure Access on and off for the tenant's Agent 365 Power Platform environment
/// through the Agent 365 platform, which resolves that environment itself.
/// </summary>
/// <remarks>
/// Every method is told which Azure account to act as rather than reading the Azure CLI itself.
/// <c>az account show</c> reflects mutable local state, so resolving it once for the confirmation
/// prompt and again for authentication could confirm one tenant and change another.
/// </remarks>
public interface IGsaService
{
    /// <summary>
    /// Sets Global Secure Access to the requested value.
    /// </summary>
    /// <param name="account">The Azure account to authenticate as. Its tenant is the tenant changed.</param>
    /// <param name="enabled">The value to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resulting status, or null when the change could not be requested.</returns>
    Task<GsaStatusResponse?> SetAsync(
        AzureAccountInfo account,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the current Global Secure Access setting.
    /// </summary>
    /// <param name="account">The Azure account to authenticate as. Its tenant is the tenant read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current status, or null when it could not be read.</returns>
    Task<GsaStatusResponse?> GetStatusAsync(
        AzureAccountInfo account,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls status until the environment reports the requested value or the timeout elapses.
    /// </summary>
    /// <param name="account">The Azure account to authenticate as. Its tenant is the tenant polled.</param>
    /// <param name="expectedStatus">The status being waited for, Enabled or Disabled.</param>
    /// <param name="timeout">How long to keep polling.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The last status read, which may still differ if the timeout elapsed.</returns>
    Task<GsaStatusResponse?> WaitForStatusAsync(
        AzureAccountInfo account,
        string expectedStatus,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
