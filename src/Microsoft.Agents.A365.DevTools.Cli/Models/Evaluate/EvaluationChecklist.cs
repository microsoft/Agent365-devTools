// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

/// <summary>
/// Root of the evaluation checklist JSON. Intermediate artifact that is auditable
/// and can be evaluated by a coding agent or manually.
/// </summary>
public class EvaluationChecklist
{
    [JsonPropertyName("metadata")]
    public ChecklistMetadata Metadata { get; init; } = new();

    [JsonPropertyName("tools")]
    public List<ToolChecklist> Tools { get; init; } = [];

    [JsonPropertyName("server_checks")]
    public List<ChecklistItem> ServerChecks { get; init; } = [];
}

public class ChecklistMetadata
{
    [JsonPropertyName("server_name")]
    public string ServerName { get; init; } = string.Empty;

    [JsonPropertyName("server_url")]
    public string ServerUrl { get; init; } = string.Empty;

    [JsonPropertyName("tool_count")]
    public int ToolCount { get; init; }

    [JsonPropertyName("generated_at")]
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("generator_version")]
    public string GeneratorVersion { get; init; } = string.Empty;
}
