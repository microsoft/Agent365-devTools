// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Response model for the MCP server provision identity operation
/// </summary>
public class ProvisionIdentityResponse
{
    /// <summary>
    /// The application ID returned by the provision identity endpoint
    /// </summary>
    [JsonPropertyName("applicationId")]
    public string? ApplicationId { get; set; }
}
