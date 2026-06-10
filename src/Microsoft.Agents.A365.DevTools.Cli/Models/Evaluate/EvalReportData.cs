// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

/// <summary>
/// Final JSON blob fed to the HTML template. Contains everything the template needs
/// to render the report. All evaluation logic, descriptions, and assertions are
/// pre-computed in C# code -- the HTML template is a pure display layer.
/// </summary>
public class EvalReportData
{
    [JsonPropertyName("result")]
    public SchemaEvalResult Result { get; init; } = new();

    [JsonPropertyName("impact_map")]
    public Dictionary<string, IssueImpactInfo> ImpactMap { get; init; } = [];

    [JsonPropertyName("maturity_ladder")]
    public List<MaturityLadderEntry> MaturityLadder { get; init; } = [];
}

public class IssueImpactInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("impact")]
    public string Impact { get; init; } = string.Empty;

    [JsonPropertyName("areas")]
    public List<string> Areas { get; init; } = [];
}

public class MaturityLadderEntry
{
    [JsonPropertyName("level")]
    public int Level { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("is_current")]
    public bool IsCurrent { get; init; }
}
