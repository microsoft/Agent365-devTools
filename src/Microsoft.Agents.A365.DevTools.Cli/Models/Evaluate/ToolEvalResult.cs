// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

/// <summary>
/// Evaluation result for a single tool.
/// </summary>
public class ToolEvalResult
{
    [JsonPropertyName("tool_name")]
    public string ToolName { get; init; } = string.Empty;

    [JsonPropertyName("tool_description")]
    public string ToolDescription { get; init; } = string.Empty;

    [JsonPropertyName("param_count")]
    public int ParamCount { get; init; }

    [JsonPropertyName("score")]
    public float Score { get; init; }

    [JsonPropertyName("category_scores")]
    public Dictionary<string, float> CategoryScores { get; init; } = [];

    [JsonPropertyName("checks")]
    public List<ChecklistItem> Checks { get; init; } = [];

    [JsonPropertyName("action_items")]
    public List<ActionItem> ActionItems { get; init; } = [];

    [JsonPropertyName("smells_detected")]
    public List<int> SmellsDetected { get; init; } = [];

    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; init; }
}
