// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Represents an agent instance linked to a blueprint, consisting of an agent identity
/// service principal and an optional agentic user.
/// </summary>
public sealed record AgentInstanceInfo
{
    /// <summary>Graph object ID of the agent identity service principal.</summary>
    public required string IdentitySpId { get; init; }

    /// <summary>Display name of the identity service principal, shown in cleanup preview.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Graph object ID of the linked agentic user, if one exists.</summary>
    public string? AgentUserId { get; init; }
}
