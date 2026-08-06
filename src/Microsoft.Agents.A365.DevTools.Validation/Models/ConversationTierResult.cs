// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// Conversation tier: multi-turn conversation validation result.
/// </summary>
public sealed class ConversationTierResult : TierResult
{
    [JsonPropertyName("turns")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ConversationTurnResult>? Turns { get; set; }

    [JsonPropertyName("playgroundLaunched")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PlaygroundLaunched { get; set; }

    [JsonPropertyName("conversationLogFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConversationLogFile { get; set; }
}
