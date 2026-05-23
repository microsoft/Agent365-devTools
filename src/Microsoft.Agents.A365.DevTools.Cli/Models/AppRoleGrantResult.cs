// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Result of an S2S app role assignment grant operation on a service principal.
/// Distinguishes "all newly granted or partially granted" from "all already in place"
/// so the setup summary can render "granted" vs "already granted" wording without
/// re-querying state.
/// </summary>
/// <param name="AllSucceeded">
/// True when every requested role is either successfully assigned (newly POSTed) or
/// was already present before the call. False when any assignment POST failed or any
/// requested role could not be resolved on the resource.
/// </param>
/// <param name="AllAlreadyAssigned">
/// True when no POST was issued during the call — every requested role was already
/// present in the existing assignments. Only meaningful when <see cref="AllSucceeded"/>
/// is also true. False on failure or when at least one role was newly created.
/// </param>
public record AppRoleGrantResult(bool AllSucceeded, bool AllAlreadyAssigned);
