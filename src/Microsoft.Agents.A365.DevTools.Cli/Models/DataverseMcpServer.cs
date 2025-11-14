using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Model representing an MCP server in a Dataverse environment
/// </summary>
public class DataverseMcpServer
{
    /// <summary>
    /// The unique ID of the MCP server
    /// </summary>
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    /// <summary>
    /// The name of the MCP server
    /// </summary>
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    /// <summary>
    /// The display name of the MCP server
    /// </summary>
    [JsonPropertyName("DisplayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// The description of the MCP server
    /// </summary>
    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    /// <summary>
    /// The environment ID where this server is published
    /// </summary>
    [JsonPropertyName("EnvironmentId")]
    public string? EnvironmentId { get; set; }

    // Legacy/Compatibility properties for existing code
    
    /// <summary>
    /// Gets the MCP server name (compatibility property, maps to Name)
    /// </summary>
    [JsonIgnore]
    public string? McpServerName => Name ?? DisplayName;

    /// <summary>
    /// The URL endpoint for the MCP server (may be computed/derived)
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// The publication status of the server (may be derived)
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// The version of the MCP server
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>
    /// When the server was published
    /// </summary>
    [JsonPropertyName("publishedDate")]
    public DateTime? PublishedDate { get; set; }

    /// <summary>
    /// Validates that the MCP server has required fields
    /// </summary>
    /// <returns>True if valid, false otherwise</returns>
    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(Name) || !string.IsNullOrWhiteSpace(DisplayName);
    }

    /// <summary>
    /// Gets a display-friendly string representation of the server
    /// </summary>
    /// <returns>Formatted string with server name and status</returns>
    public override string ToString()
    {
        var name = DisplayName ?? Name ?? "Unknown";
        var status = Status ?? "Unknown";
        return $"{name} ({status})";
    }
}

/// <summary>
/// Response model for the list-servers endpoint
/// Matches the actual API wrapper response structure
/// </summary>
public class DataverseMcpServersResponse
{
    /// <summary>
    /// Status of the API response
    /// </summary>
    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    /// <summary>
    /// Message from the API response
    /// </summary>
    [JsonPropertyName("Message")]
    public string? Message { get; set; }

    /// <summary>
    /// Environment ID from the response
    /// </summary>
    [JsonPropertyName("EnvironmentId")]
    public string? EnvironmentId { get; set; }

    /// <summary>
    /// Array of MCP servers in the environment
    /// Primary property for deserialization - supports both "MCPServers", "mcpServers", and "servers"
    /// </summary>
    [JsonPropertyName("MCPServers")]
    public DataverseMcpServer[] MCPServers { get; set; } = Array.Empty<DataverseMcpServer>();

    /// <summary>
    /// Timestamp from the response
    /// </summary>
    [JsonPropertyName("Timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Warning message from the response
    /// </summary>
    [JsonPropertyName("Warning")]
    public string? Warning { get; set; }

    /// <summary>
    /// Optional: total count of servers
    /// </summary>
    [JsonPropertyName("count")]
    public int? Count { get; set; }

    /// <summary>
    /// Gets the servers array, with fallback logic for different API response formats
    /// </summary>
    public DataverseMcpServer[] GetServers()
    {
        // Return the primary MCPServers array
        return MCPServers ?? Array.Empty<DataverseMcpServer>();
    }
}
