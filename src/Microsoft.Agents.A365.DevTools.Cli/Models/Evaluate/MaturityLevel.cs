// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

/// <summary>
/// Maturity level (0-4) determined from overall score with category caps.
/// </summary>
public class MaturityLevel
{
    [JsonPropertyName("level")]
    public int Level { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("next_level_requirements")]
    public List<string> NextLevelRequirements { get; init; } = [];
}
