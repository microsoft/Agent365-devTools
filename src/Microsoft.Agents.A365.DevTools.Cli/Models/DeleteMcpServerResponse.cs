// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Response model for deleting a BYO MCP server
/// </summary>
public class DeleteMcpServerResponse
{
    /// <summary>
    /// Status of the operation
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// Human-readable status message
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    /// App IDs that were associated with the server and should be cleaned up
    /// </summary>
    [JsonPropertyName("appIds")]
    public List<DeleteMcpServerAppEntry>? AppIds { get; set; }

    /// <summary>
    /// MOS title ID that was targeted for deletion
    /// </summary>
    [JsonPropertyName("mosTitleId")]
    public string? MosTitleId { get; set; }

    /// <summary>
    /// Whether the MOS title was successfully deleted
    /// </summary>
    [JsonPropertyName("mosTitleDeleted")]
    public bool MosTitleDeleted { get; set; }

    /// <summary>
    /// UTC timestamp of the operation
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    [JsonIgnore]
    public bool IsSuccess => Status?.Equals("Success", StringComparison.OrdinalIgnoreCase) ?? false;
}

/// <summary>
/// App entry returned from the delete MCP server operation
/// </summary>
public class DeleteMcpServerAppEntry
{
    /// <summary>
    /// Display name of the app (e.g., "serverName-A365Proxy")
    /// </summary>
    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>
    /// Entra application (client) ID
    /// </summary>
    [JsonPropertyName("appId")]
    public string? AppId { get; set; }
}
