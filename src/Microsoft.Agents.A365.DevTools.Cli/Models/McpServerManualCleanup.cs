// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Describes resources the unpublish operation could not delete on the caller's behalf and that the
/// caller must remove manually. Mirrors the platform's response contract so any client (not just this
/// CLI) can discover the cleanup responsibility from the response body.
/// </summary>
public class McpServerManualCleanup
{
    /// <summary>
    /// Human-readable explanation of why the listed resources were not deleted automatically and how
    /// to remove them.
    /// </summary>
    [JsonPropertyName("Reason")]
    public string? Reason { get; set; }

    /// <summary>
    /// The Entra app registrations the caller must delete manually in their own tenant.
    /// </summary>
    [JsonPropertyName("Apps")]
    public List<McpServerAppEntry>? Apps { get; set; }
}
