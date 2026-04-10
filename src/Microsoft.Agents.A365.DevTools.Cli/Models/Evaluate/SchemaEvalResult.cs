// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

/// <summary>
/// Top-level evaluation result container, used to generate eval_report.json.
/// </summary>
public class SchemaEvalResult
{
    [JsonPropertyName("server_name")]
    public string ServerName { get; init; } = string.Empty;

    [JsonPropertyName("server_url")]
    public string ServerUrl { get; init; } = string.Empty;

    [JsonPropertyName("evaluated_at")]
    public DateTime EvaluatedAt { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("overall_score")]
    public float OverallScore { get; init; }

    [JsonPropertyName("maturity")]
    public MaturityLevel Maturity { get; init; } = new();

    [JsonPropertyName("tool_count")]
    public int ToolCount { get; init; }

    [JsonPropertyName("tool_results")]
    public List<ToolEvalResult> ToolResults { get; init; } = [];

    [JsonPropertyName("toolset_result")]
    public ToolsetEvalResult ToolsetResult { get; init; } = new();

    [JsonPropertyName("all_action_items")]
    public List<ActionItem> AllActionItems { get; init; } = [];

    [JsonPropertyName("category_averages")]
    public Dictionary<string, float> CategoryAverages { get; init; } = [];

    [JsonPropertyName("action_items_by_priority")]
    public Dictionary<string, int> ActionItemsByPriority { get; init; } = [];

    [JsonPropertyName("smell_summary")]
    public Dictionary<string, int> SmellSummary { get; init; } = [];

    [JsonPropertyName("eval_engine")]
    public string EvalEngine { get; init; } = string.Empty;
}
