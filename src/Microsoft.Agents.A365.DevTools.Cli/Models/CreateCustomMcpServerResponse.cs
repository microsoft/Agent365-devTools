// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Response model from the CreateCustomMCPServer MCPManagement tool call
/// </summary>
public class CreateCustomMcpServerResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("baseServer")]
    public CustomMcpBaseServer? BaseServer { get; set; }

    [JsonPropertyName("tools")]
    public CustomMcpTool[]? Tools { get; set; }

    [JsonPropertyName("totalTools")]
    public int TotalTools { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; set; }

    [JsonPropertyName("createdOn")]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("modifiedOn")]
    public string? ModifiedOn { get; set; }

    [JsonPropertyName("packageBase64")]
    public string? PackageBase64 { get; set; }

    [JsonPropertyName("mos3TitleId")]
    public string? Mos3TitleId { get; set; }

    [JsonPropertyName("mos3UploadSuccess")]
    public bool Mos3UploadSuccess { get; set; }
}

/// <summary>
/// Information about the base server a custom MCP server extends
/// </summary>
public class CustomMcpBaseServer
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("totalAvailableTools")]
    public int TotalAvailableTools { get; set; }
}

/// <summary>
/// A tool included in a custom MCP server
/// </summary>
public class CustomMcpTool
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; }

    [JsonPropertyName("displayOrder")]
    public int DisplayOrder { get; set; }
}
