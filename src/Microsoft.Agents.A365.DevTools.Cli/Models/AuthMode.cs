// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Authentication mode the user selected for the non-DW blueprint setup flow.
/// Determines which grant paths run for the agent identity. DW (AI Teammate) setup does
/// not use this — the DW path is identified by <see cref="Commands.SetupSubcommands.SetupResults.IsNonDwBlueprintFlow"/>
/// being false; the auth mode field stays null for that flow.
/// </summary>
public enum AuthMode
{
    /// <summary>On-Behalf-Of: principal-scoped delegated grants for the developer. No admin required.</summary>
    Obo,

    /// <summary>Service-to-Service: app role assignments on the agent identity. Requires admin role or PowerShell fallback.</summary>
    S2s,

    /// <summary>Both OBO (delegated) and S2S (app role) grants are attempted.</summary>
    Both,
}
