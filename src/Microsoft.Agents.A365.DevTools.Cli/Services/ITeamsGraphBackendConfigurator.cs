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
        /// <returns>
        /// <see cref="Models.EndpointRegistrationResult.Created"/> on success,
        /// <see cref="Models.EndpointRegistrationResult.AlreadyExists"/> if the server reports a duplicate,
        /// <see cref="Models.EndpointRegistrationResult.SkippedDueToRollout"/> if the server rejects the
        /// new Teams Graph contract (rollout still in progress — non-fatal),
        /// <see cref="Models.EndpointRegistrationResult.Failed"/> otherwise.
        /// </returns>
        Task<Models.EndpointRegistrationResult> SetBackendConfigurationAsync(
            string agentBlueprintId,
            string messagingEndpoint,
            string? correlationId = null);

        /// <summary>
        /// Clears the backend configuration (messaging endpoint) for an Agent Blueprint via the
        /// Teams Graph API. Body sent to MCP Platform:
        /// <c>{ agentIdentityBlueprintId, tenantId }</c>.
        /// </summary>
        /// <param name="agentBlueprintId">The Agent Blueprint ID (GUID).</param>
        /// <param name="correlationId">Optional correlation ID for request tracing.</param>
        /// <returns><c>true</c> on success or idempotent no-op; <c>false</c> on failure.</returns>
        Task<bool> ClearBackendConfigurationAsync(
            string agentBlueprintId,
            string? correlationId = null);
    }
}
