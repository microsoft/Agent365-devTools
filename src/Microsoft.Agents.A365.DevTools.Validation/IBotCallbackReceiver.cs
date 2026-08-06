// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// Receives Bot Framework callback activities sent by the agent to its serviceUrl.
/// Used during conversation validation to detect whether the agent actually responded.
/// </summary>
public interface IBotCallbackReceiver : IAsyncDisposable
{
    /// <summary>
    /// The service URL that should be set in outgoing activities so the bot sends responses here.
    /// </summary>
    string ServiceUrl { get; }

    /// <summary>
    /// Starts listening for callback activities.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for a response activity from the bot. Returns null if no response arrives within the timeout.
    /// </summary>
    Task<BotCallbackResponse?> WaitForResponseAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears any previously received responses. Call before each conversation turn.
    /// </summary>
    void ClearResponses();
}

/// <summary>
/// A response activity received from the bot via the serviceUrl callback.
/// </summary>
public sealed record BotCallbackResponse(string? Text, string? Type);
