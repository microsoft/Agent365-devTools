// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Request model for adding a BYO (Bring Your Own) MCP server
/// </summary>
public class AddMcpServerRequest
{
    /// <summary>
    /// The remote server URL
    /// </summary>
    [JsonPropertyName("serverUrl")]
    public string? ServerUrl { get; set; }

    /// <summary>
    /// The server name
    /// </summary>
    [JsonPropertyName("serverName")]
    public string? ServerName { get; set; }

    /// <summary>
    /// The list of tool names exposed by this server
    /// </summary>
    [JsonPropertyName("toolList")]
    public List<string>? ToolList { get; set; }

    /// <summary>
    /// Tool descriptions keyed by tool name. Used for MOS package generation only.
    /// </summary>
    [JsonPropertyName("toolDescriptions")]
    public Dictionary<string, string>? ToolDescriptions { get; set; }

    /// <summary>
    /// Authentication metadata for the server
    /// </summary>
    [JsonPropertyName("authMetadata")]
    public AddMcpServerAuthMetadata? AuthMetadata { get; set; }

    /// <summary>
    /// Authentication type: "Entra" or "ExternalIDP"
    /// </summary>
    [JsonPropertyName("authType")]
    public string? AuthType { get; set; }

    /// <summary>
    /// Scopes for the remote MCP server
    /// </summary>
    [JsonPropertyName("remoteServerScopes")]
    public string? RemoteServerScopes { get; set; }

    /// <summary>
    /// External IDP details. Required when AuthType is "ExternalIDP".
    /// </summary>
    [JsonPropertyName("externalIdp")]
    public ExternalIdpDetails? ExternalIdp { get; set; }
}

/// <summary>
/// External identity provider details for BYO MCP servers using non-Entra authentication
/// </summary>
public class ExternalIdpDetails
{
    /// <summary>
    /// Authorization URL of the external IDP
    /// </summary>
    [JsonPropertyName("authorizationUrl")]
    public string? AuthorizationUrl { get; set; }

    /// <summary>
    /// Token URL of the external IDP
    /// </summary>
    [JsonPropertyName("tokenUrl")]
    public string? TokenUrl { get; set; }

    /// <summary>
    /// Scopes for the external IDP
    /// </summary>
    [JsonPropertyName("scopes")]
    public string? Scopes { get; set; }
}

/// <summary>
/// Authentication metadata for a BYO MCP server request
/// </summary>
public class AddMcpServerAuthMetadata
{
    /// <summary>
    /// First client application ID (Entra app GUID)
    /// </summary>
    [JsonPropertyName("clientApp1Id")]
    public string? ClientApp1Id { get; set; }

    /// <summary>
    /// First client application secret
    /// </summary>
    [JsonPropertyName("clientApp1Secret")]
    public string? ClientApp1Secret { get; set; }

    /// <summary>
    /// Second client application ID (Entra app GUID or external IDP client ID string)
    /// </summary>
    [JsonPropertyName("clientApp2Id")]
    public string? ClientApp2Id { get; set; }

    /// <summary>
    /// Second client application secret
    /// </summary>
    [JsonPropertyName("clientApp2Secret")]
    public string? ClientApp2Secret { get; set; }
}
