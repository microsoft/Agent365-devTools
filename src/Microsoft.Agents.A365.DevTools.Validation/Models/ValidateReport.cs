// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// Root model for the structured validation report written to a365.validate.json.
/// </summary>
public sealed class ValidateReport
{
    [JsonPropertyName("agent")]
    public AgentInfo Agent { get; set; } = new();

    [JsonPropertyName("tiers")]
    public ValidationTiers Tiers { get; set; } = new();

    [JsonPropertyName("summary")]
    public SummaryResult Summary { get; set; } = new();

    [JsonPropertyName("agentConsoleLogFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentConsoleLogFile { get; set; }
}
