// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Status of Global Secure Access on the tenant's Agent 365 Power Platform environment, and the
/// shape returned by enable and disable.
/// </summary>
public class GsaStatusResponse
{
    /// <summary>
    /// Enabled, Disabled, or NotConfigured.
    ///
    /// NotConfigured is not the same as Disabled: it means the tenant has never set the value.
    /// The platform keeps the two apart, so the CLI does too.
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// True when a change was accepted but has not yet appeared on the environment. The
    /// accompanying <see cref="Status"/> is then the value it has not yet displaced.
    /// </summary>
    [JsonPropertyName("pending")]
    public bool Pending { get; set; }

    /// <summary>
    /// Explanation the platform has to offer, when there is one.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// Error body returned by the platform's Global Secure Access endpoints.
/// </summary>
public class GsaErrorResponse
{
    /// <summary>
    /// Human-readable error message.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
