// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System;
using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Response model for MCP server publish operation.
/// </summary>
public class PublishMcpServerResponse
{
    /// <summary>
    /// Status of the publish operation.
    /// </summary>
    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    /// <summary>
    /// Message from the API response.
    /// </summary>
    [JsonPropertyName("Message")]
    public string? Message { get; set; }

    /// <summary>
    /// Resolved underlying MCP server's Entra app client id. The platform picks the right source per
    /// server type — Custom servers from <c>managedidentityid</c> on the <c>mcpserver</c> row, app-based
    /// / Dataverse MCP servers from 1p server-to-app mappings, fallback to the platform's own app id.
    /// The CLI uses this together with <see cref="McpServerScope"/> to look up the scope id and grant
    /// required-resource-access on the A365 Proxy + Public Clients Entra apps.
    /// </summary>
    [JsonPropertyName("McpServerAppId")]
    public string? McpServerAppId { get; set; }

    /// <summary>
    /// Resolved OAuth scope name on the underlying MCP server's app. Paired with <see cref="McpServerAppId"/>;
    /// the CLI calls Graph's GetOAuth2PermissionScopeId on (McpServerAppId, McpServerScope) to get the
    /// scope guid for required-resource-access grants.
    /// </summary>
    [JsonPropertyName("McpServerScope")]
    public string? McpServerScope { get; set; }

    /// <summary>
    /// CMS connector id created at publish time for the A365 Proxy connector, or null when the CLI
    /// didn't pass Entra app credentials (older CLI flow) and no connector was created.
    /// </summary>
    [JsonPropertyName("A365ProxyConnectorId")]
    public string? A365ProxyConnectorId { get; set; }

    /// <summary>
    /// OAuth redirect URI for the A365 Proxy connector. The CLI writes this onto the just-created
    /// A365 Proxy Entra app's redirect URI list (with tc / non-tc variants) so OAuth flows complete.
    /// </summary>
    [JsonPropertyName("A365ProxyRedirectUri")]
    public string? A365ProxyRedirectUri { get; set; }

    /// <summary>
    /// Public Clients Entra app client id, echoed back from the request so post-response
    /// orchestration can grant the PPMI scope onto it.
    /// </summary>
    [JsonPropertyName("PublicClientsAppId")]
    public string? PublicClientsAppId { get; set; }

    /// <summary>
    /// Whether the operation was successful.
    /// </summary>
    [JsonIgnore]
    public bool IsSuccess => Status?.Equals("Success", StringComparison.OrdinalIgnoreCase) ?? false;
}
