// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// An Entra app registration associated with a published MCP server that the CLI is responsible for
/// deleting on unpublish. The platform cannot delete app registrations in the customer tenant, so it
/// returns these entries and the CLI performs the deletion using the caller's Graph permissions.
/// </summary>
public class McpServerAppEntry
{
    /// <summary>
    /// Friendly name of the app registration (for logging / manual cleanup guidance).
    /// </summary>
    [JsonPropertyName("AppName")]
    public string? AppName { get; set; }

    /// <summary>
    /// The app (client) id of the Entra app registration. The CLI resolves the underlying application
    /// object id from this before calling Graph's application delete.
    /// </summary>
    [JsonPropertyName("AppId")]
    public string? AppId { get; set; }
}
