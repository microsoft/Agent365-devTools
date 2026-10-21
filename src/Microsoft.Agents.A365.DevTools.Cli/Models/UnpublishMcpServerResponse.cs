// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System;
using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Response model for an MCP server unpublish operation.
/// </summary>
public class UnpublishMcpServerResponse
{
    /// <summary>
    /// Status of the unpublish operation.
    /// </summary>
    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    /// <summary>
    /// Message from the API response.
    /// </summary>
    [JsonPropertyName("Message")]
    public string? Message { get; set; }

    /// <summary>
    /// Resources the platform could not delete in the customer tenant and that the caller must remove
    /// manually (with a reason and the affected app registrations). Null when there is nothing for the
    /// caller to clean up (for example OOB Dataverse servers or legacy records).
    /// </summary>
    [JsonPropertyName("ManualCleanupRequired")]
    public McpServerManualCleanup? ManualCleanupRequired { get; set; }

    /// <summary>
    /// Whether the operation was successful.
    /// </summary>
    [JsonIgnore]
    public bool IsSuccess => Status?.Equals("Success", StringComparison.OrdinalIgnoreCase) ?? false;
}
