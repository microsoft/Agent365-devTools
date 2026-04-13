// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Response model for adding a BYO MCP server
/// </summary>
public class AddMcpServerResponse
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
    /// Server details returned from the add operation
    /// </summary>
    [JsonPropertyName("server")]
    public AddMcpServerDetails? Server { get; set; }

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
/// Server details returned from the add MCP server operation
/// </summary>
public class AddMcpServerDetails
{
    /// <summary>
    /// The Dataverse record ID
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Server name
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Remote server URL
    /// </summary>
    [JsonPropertyName("serverUrl")]
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Count of tools
    /// </summary>
    [JsonPropertyName("toolCount")]
    public int ToolCount { get; set; }

    /// <summary>
    /// List of tool names
    /// </summary>
    [JsonPropertyName("tools")]
    public List<string>? Tools { get; set; }

    /// <summary>
    /// The A365 proxy connector redirect URI
    /// </summary>
    [JsonPropertyName("a365ProxyRedirectUri")]
    public string? A365ProxyRedirectUri { get; set; }

    /// <summary>
    /// The remote MCP server proxy connector redirect URI
    /// </summary>
    [JsonPropertyName("remoteMCPServerProxyRedirectUri")]
    public string? RemoteMCPServerProxyRedirectUri { get; set; }

    /// <summary>
    /// The PPMI app client ID provisioned for this server
    /// </summary>
    [JsonPropertyName("ppmiAppClientId")]
    public string? PpmiAppClientId { get; set; }

    /// <summary>
    /// The MOS title ID returned from publishing to MOS3
    /// </summary>
    [JsonPropertyName("mosTitleId")]
    public string? MOSTitleId { get; set; }
}
