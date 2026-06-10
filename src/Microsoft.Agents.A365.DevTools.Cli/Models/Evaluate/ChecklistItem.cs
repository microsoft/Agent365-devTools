// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

/// <summary>
/// A single check item in the evaluation checklist.
/// Score is null until evaluated (deterministic checks are pre-filled, semantic checks start null).
/// </summary>
public class ChecklistItem
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public CheckType Type { get; init; }

    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    [JsonPropertyName("score")]
    public bool? Score { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("severity")]
    public Priority Severity { get; init; }

    [JsonPropertyName("category")]
    public CheckCategory Category { get; init; }

    [JsonPropertyName("issue_ids")]
    public List<int> IssueIds { get; init; } = [];

    [JsonPropertyName("impact_areas")]
    public List<ImpactArea> ImpactAreas { get; init; } = [];

    [JsonPropertyName("remediation")]
    public string Remediation { get; init; } = string.Empty;
}
