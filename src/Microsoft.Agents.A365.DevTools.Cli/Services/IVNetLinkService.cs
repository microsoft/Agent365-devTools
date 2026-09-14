// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Links an Azure virtual network enterprise policy to the tenant's Agent 365 Power Platform
/// environment through the Agent 365 platform, which resolves that environment itself.
/// </summary>
public interface IVNetLinkService
{
    /// <summary>
    /// Links a policy. Resolves the policy's systemId from ARM using the caller's Azure session,
    /// then asks the platform to perform the link.
    /// </summary>
    /// <param name="policyArmId">ARM resource id of the NetworkInjection enterprise policy.</param>
    /// <param name="swap">Whether an existing link to a different policy may be replaced.</param>
    /// <param name="tenantId">Tenant to authenticate against for the ARM read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resulting status, or null when the operation could not be started.</returns>
    Task<VNetStatusResponse?> LinkAsync(
        string policyArmId,
        bool swap,
        string tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the current link. The platform supplies the policy identifier it stored at link time.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resulting status, or null when the operation could not be started.</returns>
    Task<VNetStatusResponse?> UnlinkAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the current link status, optionally resuming a specific operation handle.
    /// </summary>
    /// <param name="operationId">Handle returned by a link or unlink that was still running.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current status, or null when it could not be read.</returns>
    Task<VNetStatusResponse?> GetStatusAsync(
        string? operationId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls status until the operation reaches a terminal state or the timeout elapses.
    /// </summary>
    /// <param name="operationId">Handle of the running operation.</param>
    /// <param name="timeout">How long to keep polling.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The last status read, which may still be Running if the timeout elapsed.</returns>
    Task<VNetStatusResponse?> WaitForCompletionAsync(
        string operationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
