// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Constants;

/// <summary>
/// String literals assigned to <c>SetupResults.MessagingEndpointFailureReason</c> when the
/// messaging endpoint step does not complete successfully. The values are persisted in setup
/// summary output, so the exact strings must not change without considering downstream
/// consumers that grep for them.
/// </summary>
public static class MessagingEndpointFailureReasons
{
    /// <summary>Signed-in user is not a blueprint owner; Teams Graph returned a 403-wrapped-as-400.</summary>
    public const string NotOwner = "NotOwner";

    /// <summary>Blueprint creation itself failed, so endpoint registration was never attempted.</summary>
    public const string BlueprintMissing = "BlueprintMissing";

    /// <summary>Messaging endpoint URL was absent from config; we direct the user to the Teams Developer Portal.</summary>
    public const string NotConfigured = "NotConfigured";

    /// <summary>Any other failure (validation exception, contract mismatch, unexpected error).</summary>
    public const string Other = "Other";
}
