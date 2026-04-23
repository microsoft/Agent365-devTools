// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Result of endpoint registration operation
/// </summary>
public enum EndpointRegistrationResult
{
    /// <summary>
    /// Endpoint registration failed
    /// </summary>
    Failed,

    /// <summary>
    /// Endpoint was successfully created
    /// </summary>
    Created,

    /// <summary>
    /// Endpoint already exists (HTTP 409 Conflict)
    /// </summary>
    AlreadyExists,

    /// <summary>
    /// The server rejected the request with a contract-mismatch signature, indicating the
    /// Teams Graph rollout is still in progress on that environment. The caller should treat
    /// this as non-fatal, surface it in the summary as "Teams registration not done", and
    /// point the user at the Teams Developer Portal for manual configuration.
    /// TEMPORARY: remove once rollout completes and v1/v2 contract versioning is in place.
    /// </summary>
    SkippedDueToRollout
}
