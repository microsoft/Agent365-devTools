// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System;
using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Request model for publishing an MCP server to a Dataverse environment.
/// </summary>
public class PublishMcpServerRequest
{
    /// <summary>
    /// Alias for the MCP server.
    /// </summary>
    [JsonPropertyName("alias")]
    public required string Alias { get; set; }

    /// <summary>
    /// Display name for the MCP server.
    /// </summary>
    [JsonPropertyName("DisplayName")]
    public required string DisplayName { get; set; }

    /// <summary>
    /// A365 Proxy Entra app client id created CLI-side at publish time. Paired with
    /// <see cref="A365ProxyClientSecret"/>. When provided, the platform creates an A365 Proxy CMS
    /// connector keyed by server name so the published server is reachable through Power Platform / Copilot.
    /// </summary>
    [JsonPropertyName("a365ProxyClientId")]
    public Guid? A365ProxyClientId { get; set; }

    /// <summary>
    /// A365 Proxy Entra app client secret. Paired with <see cref="A365ProxyClientId"/>.
    /// </summary>
    [JsonPropertyName("a365ProxyClientSecret")]
    public string? A365ProxyClientSecret { get; set; }

    /// <summary>
    /// Public Clients (VS Code / Copilot CLI) Entra app client id created CLI-side. Carried in the
    /// request so the platform echoes it back in the response and the CLI can wire the PPMI
    /// <c>Tools.ListInvoke.All</c> scope onto it after publish completes.
    /// </summary>
    [JsonPropertyName("publicClientsAppId")]
    public string? PublicClientsAppId { get; set; }

    /// <summary>
    /// Publisher / developer name written into the MOS package manifest. Required for Custom
    /// (user-created) servers — the platform's v2 publish validator rejects empty values for those.
    /// Ignored for 1p Microsoft-owned app-based servers (e.g. <c>msdyn_DataverseMCPServer</c>),
    /// which always publish under "Microsoft" regardless.
    /// </summary>
    [JsonPropertyName("publisherName")]
    public string? PublisherName { get; set; }
}
