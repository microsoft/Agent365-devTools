// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// Container for all validation tiers.
/// </summary>
public sealed class ValidationTiers
{
    [JsonPropertyName("structural")]
    public StructuralTierResult Structural { get; set; } = TierResult.CreateSkipped<StructuralTierResult>();

    [JsonPropertyName("build")]
    public BuildTierResult Build { get; set; } = TierResult.CreateSkipped<BuildTierResult>();

    [JsonPropertyName("boot")]
    public BootTierResult Boot { get; set; } = TierResult.CreateSkipped<BootTierResult>();

    [JsonPropertyName("conversation")]
    public ConversationTierResult Conversation { get; set; } = TierResult.CreateSkipped<ConversationTierResult>("not yet implemented");

    [JsonPropertyName("telemetry")]
    public TelemetryTierResult Telemetry { get; set; } = TierResult.CreateSkipped<TelemetryTierResult>("not yet run");

    [JsonPropertyName("blueprint")]
    public BlueprintTierResult Blueprint { get; set; } = TierResult.CreateSkipped<BlueprintTierResult>("not yet implemented");
}
