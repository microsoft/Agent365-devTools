// Copyright (c) Microsoft Corporation.  
// Licensed under the MIT License.  
using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Request model for publishing an MCP server to a Dataverse environment
/// </summary>
public class PublishMcpServerRequest
{
    /// <summary>
    /// Alias for the MCP server
    /// </summary>
    [JsonPropertyName("alias")]
    public required string Alias { get; set; }

    /// <summary>
    /// Display name for the MCP server
    /// </summary>
    [JsonPropertyName("DisplayName")]
    public required string DisplayName { get; set; }
}
