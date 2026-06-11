// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

/// <summary>
/// Evaluation result for toolset-level (cross-tool) checks.
/// </summary>
public class ToolsetEvalResult
{
    [JsonPropertyName("score")]
    public float Score { get; init; }

    [JsonPropertyName("checks")]
    public List<ChecklistItem> Checks { get; init; } = [];

    [JsonPropertyName("action_items")]
    public List<ActionItem> ActionItems { get; init; } = [];
}
