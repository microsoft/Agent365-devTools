// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services
{
    /// <summary>
    /// Service for managing the Teams Graph backend configuration of an Agent Blueprint
    /// (replaces the legacy Azure Bot Service "bot endpoint" model).
    /// </summary>
    public interface ITeamsGraphBackendConfigurator
    {
        /// <summary>
        /// Sets the backend configuration (messaging endpoint) for an Agent Blueprint via the
        /// Teams Graph API. Body sent to MCP Platform:
        /// <c>{ agentIdentityBlueprintId, callbackUri, tenantId }</c>.
        /// </summary>
        /// <param name="agentBlueprintId">The Agent Blueprint ID (GUID).</param>
        /// <param name="messagingEndpoint">The HTTPS callback URL.</param>
        /// <param name="correlationId">Optional correlation ID for request tracing.</param>
        /// <param name="ct">Cancellation token. Honored across the auth call, HTTP send, and retry backoff so Ctrl+C aborts promptly.</param>
        /// <returns>
        /// A tuple of (Result, FailureReason).
        /// Result is:
        /// <see cref="Models.EndpointRegistrationResult.Created"/> on success,
        /// <see cref="Models.EndpointRegistrationResult.AlreadyExists"/> if the server reports a duplicate,
        /// <see cref="Models.EndpointRegistrationResult.SkippedContractMismatch"/> if the server rejects the
        /// new Teams Graph contract (rollout still in progress — non-fatal),
        /// <see cref="Models.EndpointRegistrationResult.Failed"/> otherwise.
        /// FailureReason is "NotOwner" when the server rejected with a "not the owner" 403-wrapped-as-400,
        /// "Other" for other Failed outcomes, null otherwise.
        /// </returns>
        Task<(Models.EndpointRegistrationResult Result, string? FailureReason)> SetBackendConfigurationAsync(
            string agentBlueprintId,
            string messagingEndpoint,
            string? correlationId = null,
            CancellationToken ct = default);

        /// <summary>
        /// Clears the backend configuration (messaging endpoint) for an Agent Blueprint via the
        /// Teams Graph API. Body sent to MCP Platform:
        /// <c>{ agentIdentityBlueprintId, tenantId }</c>.
        /// </summary>
        /// <param name="agentBlueprintId">The Agent Blueprint ID (GUID).</param>
        /// <param name="correlationId">Optional correlation ID for request tracing.</param>
        /// <param name="ct">Cancellation token. Honored across the auth call, HTTP send, and retry backoff so Ctrl+C aborts promptly.</param>
        /// <returns><c>true</c> on success or idempotent no-op; <c>false</c> on failure.</returns>
        Task<bool> ClearBackendConfigurationAsync(
            string agentBlueprintId,
            string? correlationId = null,
            CancellationToken ct = default);
    }
}
