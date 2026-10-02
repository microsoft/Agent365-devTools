// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Model representing an MCP server published to tenant scope from a Dataverse environment
/// </summary>
public class PublishedMcpServer
{
    /// <summary>
    /// The tenant-level server name (alias)
    /// </summary>
    [JsonPropertyName("mcpServerName")]
    public string? McpServerName { get; set; }

    /// <summary>
    /// The display name of the MCP server
    /// </summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// The description of the MCP server
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// The tenant URL for the MCP server
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// The approval status (PendingApproval, Approved, or Blocked)
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// The ID of the Dataverse environment the server was published from
    /// </summary>
    [JsonPropertyName("sourceEnvironmentId")]
    public string? SourceEnvironmentId { get; set; }

    /// <summary>
    /// The display name of the Dataverse environment the server was published from
    /// </summary>
    [JsonPropertyName("sourceEnvironmentName")]
    public string? SourceEnvironmentName { get; set; }

    /// <summary>
    /// The server name in the source Dataverse environment
    /// </summary>
    [JsonPropertyName("sourceServerName")]
    public string? SourceServerName { get; set; }
}

/// <summary>
/// Response model for the list published MCP servers endpoint
/// </summary>
public class PublishedMcpServersResponse
{
    /// <summary>
    /// Total count of published MCP servers
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>
    /// MCP servers published to tenant scope across all accessible Dataverse environments
    /// </summary>
    [JsonPropertyName("servers")]
    public PublishedMcpServer[] Servers { get; set; } = Array.Empty<PublishedMcpServer>();

    /// <summary>
    /// Gets the servers array, or an empty array when the response carries none
    /// </summary>
    public PublishedMcpServer[] GetServers()
    {
        return Servers ?? Array.Empty<PublishedMcpServer>();
    }
}
