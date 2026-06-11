// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

/// <summary>
/// Represents an MCP tool schema discovered from a server or file.
/// </summary>
public class ToolSchema
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("inputSchema")]
    public JsonElement? InputSchema { get; init; }
}
