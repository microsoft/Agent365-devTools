// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

/// <summary>
/// A prioritized remediation action generated from a failed check.
/// </summary>
public class ActionItem
{
    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("param_name")]
    public string? ParamName { get; init; }

    [JsonPropertyName("priority")]
    public Priority Priority { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("smell_ids")]
    public List<int> SmellIds { get; init; } = [];

    [JsonPropertyName("impact_areas")]
    public List<ImpactArea> ImpactAreas { get; init; } = [];

    [JsonPropertyName("remediation")]
    public string Remediation { get; init; } = string.Empty;

    [JsonPropertyName("score_impact")]
    public float ScoreImpact { get; set; }

    [JsonPropertyName("issue_leads_to")]
    public List<string> IssueLeadsTo { get; init; } = [];
}
