// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// Telemetry tier: trace export validation result.
/// </summary>
public sealed class TelemetryTierResult : TierResult
{
    [JsonPropertyName("consoleExporterActive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ConsoleExporterActive { get; set; }

    [JsonPropertyName("foundOperations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FoundOperations { get; set; }

    [JsonPropertyName("missingOperations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? MissingOperations { get; set; }

    [JsonPropertyName("scopeVersionPresent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ScopeVersionPresent { get; set; }

    [JsonPropertyName("parentLinksValid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ParentLinksValid { get; set; }

    [JsonPropertyName("childSpansMissingParent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ChildSpansMissingParent { get; set; }

    [JsonPropertyName("resourceAttributesPresent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ResourceAttributesPresent { get; set; }

    [JsonPropertyName("missingResourceAttributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? MissingResourceAttributes { get; set; }
}
