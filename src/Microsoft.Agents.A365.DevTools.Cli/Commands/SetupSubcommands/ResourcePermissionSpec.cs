// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Distinguishes between delegated OAuth2 permission scopes (oauth2PermissionGrants) and
/// application role assignments (appRoleAssignments). Delegated scopes require a user context
/// at runtime; application roles are used for autonomous/S2S flows with no user present.
/// </summary>
internal enum PermissionType
{
    /// <summary>Delegated permission scope — granted via oauth2PermissionGrants (AllPrincipals or Principal).</summary>
    Delegated,

    /// <summary>Application role — granted via appRoleAssignments. Requires the resource to publish an appRole.</summary>
    Application,
}

/// <summary>
/// Describes a single resource whose permissions should be configured on the agent blueprint.
/// Used as input to <see cref="BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync"/>.
/// </summary>
/// <param name="ResourceAppId">The application ID of the resource (e.g. Microsoft Graph, MCP Tools).</param>
/// <param name="ResourceName">Human-readable display name used in log messages.</param>
/// <param name="Scopes">Delegated permission scopes to grant and (if SetInheritable is true) make inheritable.</param>
/// <param name="SetInheritable">
/// When true, the orchestrator configures inheritable permissions on the blueprint so that
/// agent instances automatically receive these scopes at creation time.
/// </param>
/// <param name="Type">
/// Whether the scopes are delegated (default) or application roles.
/// Currently all specs use <see cref="PermissionType.Delegated"/>; switch to
/// <see cref="PermissionType.Application"/> once resource APIs publish app roles.
/// </param>
internal record ResourcePermissionSpec(
    string ResourceAppId,
    string ResourceName,
    string[] Scopes,
    bool SetInheritable,
    PermissionType Type = PermissionType.Delegated);
