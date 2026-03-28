// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

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
internal record ResourcePermissionSpec(
    string ResourceAppId,
    string ResourceName,
    string[] Scopes,
    bool SetInheritable);
