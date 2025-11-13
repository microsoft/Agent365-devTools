// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Configuration model for MCP servers with Entra scopes and audience information
/// </summary>
public class McpServerConfig
{
    /// <summary>
    /// The display name of the MCP server
    /// </summary>
    [JsonPropertyName("mcpServerName")]
    public string McpServerName { get; set; } = string.Empty;
    
    /// <summary>
    /// The unique identifier for the MCP server (optional)
    /// </summary>
    [JsonPropertyName("mcpServerUniqueName")]  
    public string? McpServerUniqueName { get; set; }
    
    /// <summary>
    /// Optional URL for the MCP server endpoint
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }
    
    /// <summary>
    /// The Entra scope required to access this MCP server
    /// </summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
    
    /// <summary>
    /// The audience (resource identifier) for token requests to this MCP server
    /// </summary>
    [JsonPropertyName("audience")]
    public string? Audience { get; set; }
    
    /// <summary>
    /// Optional description of the MCP server's capabilities
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    
    /// <summary>
    /// Optional array of capability identifiers supported by this server
    /// </summary>
    [JsonPropertyName("capabilities")]
    public string[]? Capabilities { get; set; }

    /// <summary>
    /// Validates that the MCP server configuration has required fields
    /// </summary>
    /// <returns>True if valid, false otherwise</returns>
    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(McpServerName) && 
               !string.IsNullOrWhiteSpace(Url);
    }

    /// <summary>
    /// Gets a display-friendly string representation of the server
    /// </summary>
    /// <returns>Formatted string with server name and scope</returns>
    public override string ToString()
    {
        var scopeInfo = !string.IsNullOrWhiteSpace(Scope) 
            ? $" (Scope: {Scope})"
            : " (No scope required)";
        return $"{McpServerName}{scopeInfo}";
    }
}