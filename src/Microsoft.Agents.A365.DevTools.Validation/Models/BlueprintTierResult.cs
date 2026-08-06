// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// Blueprint tier: Entra registration, permissions, and consent validation.
/// </summary>
public sealed class BlueprintTierResult : TierResult
{
    [JsonPropertyName("appExists")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AppExists { get; set; }

    [JsonPropertyName("servicePrincipalExists")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ServicePrincipalExists { get; set; }

    [JsonPropertyName("registrationExists")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RegistrationExists { get; set; }

    [JsonPropertyName("resources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BlueprintResourceResult>? Resources { get; set; }
}
