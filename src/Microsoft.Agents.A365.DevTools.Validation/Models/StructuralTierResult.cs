// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// Structural tier: config and manifest validation checks.
/// </summary>
public sealed class StructuralTierResult : TierResult
{
    [JsonPropertyName("checks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StructuralCheck>? Checks { get; set; }
}
