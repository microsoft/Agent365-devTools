// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Represents admin consent information for a resource API
/// </summary>
public class ResourceConsent
{
    /// <summary>
    /// Display name of the resource (e.g., "Microsoft Graph", "Agent 365 Tools", "Messaging Bot API")
    /// </summary>
    [JsonPropertyName("resourceName")]
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>
    /// Application ID of the resource
    /// </summary>
    [JsonPropertyName("resourceAppId")]
    public string ResourceAppId { get; set; } = string.Empty;

    /// <summary>
    /// Admin consent URL for granting permissions via browser.
    /// Only populated for resources requiring interactive consent (e.g., Microsoft Graph).
    /// API-based grants (Bot API, Observability API) do not require consent URLs.
    /// </summary>
    [JsonPropertyName("consentUrl")]
    public string? ConsentUrl { get; set; }

    /// <summary>
    /// Whether admin consent has been granted
    /// </summary>
    [JsonPropertyName("consentGranted")]
    public bool ConsentGranted { get; set; }

    /// <summary>
    /// Timestamp when consent was granted
    /// </summary>
    [JsonPropertyName("consentTimestamp")]
    public DateTime? ConsentTimestamp { get; set; }

    /// <summary>
    /// Scopes/permissions requested
    /// </summary>
    [JsonPropertyName("scopes")]
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// Whether inheritable permissions are configured for this resource (for agent blueprints).
    /// Inheritable permissions allow agent instances to inherit OAuth2 grants from the blueprint.
    /// Null if not requested, true if successfully configured, false if configuration failed.
    /// Set via Graph API beta endpoint: /applications/microsoft.graph.agentIdentityBlueprint/{id}/inheritablePermissions
    /// </summary>
    [JsonPropertyName("inheritablePermissionsConfigured")]
    public bool? InheritablePermissionsConfigured { get; set; }

    /// <summary>
    /// Whether inheritable permissions already existed before this configuration attempt.
    /// Null if inheritable permissions were not requested for this resource.
    /// </summary>
    [JsonPropertyName("inheritablePermissionsAlreadyExist")]
    public bool? InheritablePermissionsAlreadyExist { get; set; }

    /// <summary>
    /// Error message if inheritable permissions configuration failed.
    /// Null if no error occurred or inheritable permissions were not requested.
    /// </summary>
    [JsonPropertyName("inheritablePermissionsError")]
    public string? InheritablePermissionsError { get; set; }
}
