// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

/// <summary>
/// Checklist for a single tool, organized by check category.
/// </summary>
public class ToolChecklist
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; init; }

    [JsonPropertyName("checks")]
    public ToolCheckGroups Checks { get; init; } = new();
}

/// <summary>
/// Groups of checks organized by category for a single tool.
/// </summary>
public class ToolCheckGroups
{
    [JsonPropertyName("tool_name")]
    public List<ChecklistItem> ToolName { get; init; } = [];

    [JsonPropertyName("tool_description")]
    public List<ChecklistItem> ToolDescription { get; init; } = [];

    [JsonPropertyName("schema_structure")]
    public List<ChecklistItem> SchemaStructure { get; init; } = [];

    [JsonPropertyName("parameters")]
    public Dictionary<string, ParamCheckGroups> Parameters { get; init; } = [];
}

/// <summary>
/// Groups of checks for a single parameter.
/// </summary>
public class ParamCheckGroups
{
    [JsonPropertyName("param_name")]
    public List<ChecklistItem> ParamName { get; init; } = [];

    [JsonPropertyName("param_description")]
    public List<ChecklistItem> ParamDescription { get; init; } = [];
}
