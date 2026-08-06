// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// Permission and consent status for a single resource API in the blueprint.
/// </summary>
public sealed class BlueprintResourceResult
{
    [JsonPropertyName("resourceName")]
    public string ResourceName { get; set; } = string.Empty;

    [JsonPropertyName("resourceAppId")]
    public string ResourceAppId { get; set; } = string.Empty;

    [JsonPropertyName("expectedScopes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ExpectedScopes { get; set; }

    [JsonPropertyName("actualScopes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ActualScopes { get; set; }

    [JsonPropertyName("missingScopes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? MissingScopes { get; set; }

    [JsonPropertyName("consentGranted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ConsentGranted { get; set; }

    [JsonPropertyName("inheritablePermissionsConfigured")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? InheritablePermissionsConfigured { get; set; }

    [JsonPropertyName("scopesAllAllowed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ScopesAllAllowed { get; set; }

    [JsonPropertyName("rolesAllAllowed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RolesAllAllowed { get; set; }

    [JsonPropertyName("actualAppRoles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ActualAppRoles { get; set; }

    [JsonPropertyName("effectiveInheritance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EffectiveInheritance { get; set; }
}
