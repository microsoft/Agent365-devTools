// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Request model for creating a custom MCP server via the MCPManagement server
/// </summary>
public class CreateCustomMcpServerRequest
{
    /// <summary>
    /// Unique logical name for the custom MCP server (no whitespace)
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>
    /// User-friendly display name
    /// </summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Description of the custom MCP server
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// AI agent instructions for how to use this server
    /// </summary>
    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    /// <summary>
    /// Base server ID to extend (e.g. "mcp_MailServer")
    /// </summary>
    [JsonPropertyName("baseServerId")]
    public required string BaseServerId { get; set; }

    /// <summary>
    /// Optional subset of tools to select from the base server.
    /// Comma-separated string matching the MCPManagement server's expected format.
    /// </summary>
    [JsonPropertyName("selectedBaseTools")]
    public string? SelectedBaseTools { get; set; }

    /// <summary>
    /// Optional additional tools from other sources (Graph, Connector, etc.)
    /// </summary>
    [JsonPropertyName("additionalTools")]
    public AdditionalToolRequest[]? AdditionalTools { get; set; }

    /// <summary>
    /// null = tenant-level, value = environment-level
    /// </summary>
    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; set; }
}

/// <summary>
/// Represents an additional tool to include from a non-base-server source
/// </summary>
public class AdditionalToolRequest
{
    /// <summary>
    /// The type of backend tool (see BackendToolType enum values)
    /// </summary>
    [JsonPropertyName("backendToolType")]
    public int BackendToolType { get; set; }

    /// <summary>
    /// Microsoft Graph operation ID (used when backendToolType = 2)
    /// </summary>
    [JsonPropertyName("graphOperationId")]
    public string? GraphOperationId { get; set; }

    /// <summary>
    /// Power Platform connector ID (used when backendToolType = 1)
    /// </summary>
    [JsonPropertyName("connectorId")]
    public string? ConnectorId { get; set; }

    /// <summary>
    /// Power Platform connector operation ID (used when backendToolType = 1)
    /// </summary>
    [JsonPropertyName("connectorOperationId")]
    public string? ConnectorOperationId { get; set; }
}
