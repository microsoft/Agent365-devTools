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
    /// PPMI app client id for the published server. Used by the CLI to look up the
    /// <c>Tools.ListInvoke.All</c> scope id and grant it to the A365 Proxy + Public Clients Entra
    /// apps after publish completes.
    /// </summary>
    [JsonPropertyName("PpmiAppClientId")]
    public string? PpmiAppClientId { get; set; }

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
