// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Outcome of a single permission grant attempt during setup. Used by <see cref="Commands.SetupSubcommands.SetupResults"/>
/// to track each grant type independently so the setup summary can decide what action items
/// to print without re-deriving intent from result fields.
/// </summary>
public enum GrantOutcome
{
    /// <summary>The grant was not part of the user's auth-mode intent or was not reached.</summary>
    NotApplicable,

    /// <summary>The grant was attempted and succeeded (including idempotent "already granted").</summary>
    Granted,

    /// <summary>
    /// The grant status could not be confirmed. The admin opened the consent browser but the poll
    /// timed out without observing a grant. Setup proceeds, but the operator should run
    /// <c>a365 query-entra inheritance</c> to confirm permissions are actually in place.
    /// </summary>
    Unverified,

    /// <summary>The grant was attempted (or skipped because the user lacks the role) and is not in place.</summary>
    Failed,
}
