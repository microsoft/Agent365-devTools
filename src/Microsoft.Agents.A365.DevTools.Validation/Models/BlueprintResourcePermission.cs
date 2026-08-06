// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// Permission status for a single resource API in the blueprint registration check.
/// </summary>
public sealed class BlueprintResourcePermission
{
    /// <summary>Display name of the resource (e.g., "Microsoft Graph").</summary>
    public string ResourceName { get; init; } = string.Empty;

    /// <summary>Application ID of the resource.</summary>
    public string ResourceAppId { get; init; } = string.Empty;

    /// <summary>Scopes expected from config.</summary>
    public List<string> ExpectedScopes { get; init; } = new();

    /// <summary>Scopes actually found in Entra inheritable permissions.</summary>
    public List<string> ActualScopes { get; init; } = new();

    /// <summary>Scopes in config but missing from Entra.</summary>
    public List<string> MissingScopes { get; init; } = new();

    /// <summary>Whether admin consent has been granted (from config).</summary>
    public bool? ConsentGranted { get; init; }

    /// <summary>Whether inheritable permissions are configured in Entra for this resource.</summary>
    public bool InheritablePermissionsConfigured { get; init; }

    /// <summary>Whether kind=allAllowed is set for delegated scopes on this resource.</summary>
    public bool ScopesAllAllowed { get; init; }

    /// <summary>Whether kind=allAllowed is set for app roles on this resource.</summary>
    public bool RolesAllAllowed { get; init; }

    /// <summary>App roles actually granted on the blueprint SP for this resource.</summary>
    public List<string> ActualAppRoles { get; init; } = new();

    /// <summary>
    /// Effective inheritance status: true when kind=allAllowed on both sides AND at least one
    /// permission is granted on the blueprint SP for this resource.
    /// </summary>
    public bool EffectiveInheritance { get; init; }
}
