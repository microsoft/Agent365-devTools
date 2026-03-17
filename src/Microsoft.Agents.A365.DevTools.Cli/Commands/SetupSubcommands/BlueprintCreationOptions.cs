// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Options that control blueprint creation behavior in the setup orchestration.
/// </summary>
/// <param name="DeferConsent">
/// When true, the blueprint step skips admin consent and the Graph inheritable permissions
/// call that follows it. The caller (e.g. AllSubcommand) is responsible for running consent
/// as a separate phase via BatchPermissionsOrchestrator.
/// This is an orchestration flag — it is NOT tied to whether the current user is an admin.
/// Standalone 'setup blueprint' uses the default value of false so consent runs normally.
/// </param>
internal record BlueprintCreationOptions(bool DeferConsent = false);
