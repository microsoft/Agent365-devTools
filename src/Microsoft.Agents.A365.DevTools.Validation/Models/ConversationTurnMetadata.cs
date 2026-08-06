// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// Metadata for a single conversation turn.
/// </summary>
public sealed class ConversationTurnMetadata
{
    /// <summary>The message sent to the agent.</summary>
    public string Input { get; init; } = string.Empty;

    /// <summary>HTTP status code returned by /api/messages.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Truncated response body snippet.</summary>
    public string? ResponseSnippet { get; init; }

    /// <summary>Round-trip latency in milliseconds.</summary>
    public long? LatencyMs { get; init; }

    /// <summary>Whether this turn succeeded.</summary>
    public bool Ok { get; init; }

    /// <summary>Error description if the turn failed.</summary>
    public string? Error { get; init; }

    /// <summary>Whether the agent sent a response via the serviceUrl callback. Null if tracking was unavailable.</summary>
    public bool? AgentResponded { get; init; }

    /// <summary>The text content of the agent's callback response, if any.</summary>
    public string? AgentResponseText { get; init; }
}
