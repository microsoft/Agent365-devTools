// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Represents the result of a directory role membership check.
/// </summary>
public enum RoleCheckResult
{
    /// <summary>Role is confirmed active — proceed with confidence or skip redundant work.</summary>
    HasRole,

    /// <summary>Role is confirmed absent — fail fast with a clear message.</summary>
    DoesNotHaveRole,

    /// <summary>
    /// Check failed (e.g. network error, throttling, auth failure) — attempt the operation
    /// anyway and let the API surface the real error rather than blocking on a false negative.
    /// </summary>
    Unknown
}
