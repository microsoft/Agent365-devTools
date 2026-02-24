// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.


namespace Microsoft.Agents.A365.DevTools.Cli.Services
{
    /// <summary>
    /// Service for configuring messaging endpoints.
    /// </summary>
    public interface IBotConfigurator
    {
        /// <summary>
        /// Registers a messaging endpoint with the Agent Blueprint identity.
        /// </summary>
        /// <param name="endpointName">Azure Bot Service instance name (4-42 characters).</param>
        /// <param name="location">
        /// Required. Azure region for the endpoint registration (e.g., "eastus").
        /// Must not be null or whitespace — an empty value returns <see cref="Models.EndpointRegistrationResult.Failed"/>
        /// without making any API call.
        /// </param>
        /// <param name="messagingEndpoint">HTTPS URL the Bot Framework will call.</param>
        /// <param name="agentDescription">Human-readable description of the agent.</param>
        /// <param name="agentBlueprintId">Entra ID application ID of the agent blueprint.</param>
        /// <param name="correlationId">Optional correlation ID for request tracing.</param>
        Task<Models.EndpointRegistrationResult> CreateEndpointWithAgentBlueprintAsync(string endpointName, string location, string messagingEndpoint, string agentDescription, string agentBlueprintId, string? correlationId = null);

        /// <summary>
        /// Deletes a messaging endpoint registration associated with the Agent Blueprint identity.
        /// </summary>
        /// <param name="endpointName">Azure Bot Service instance name to delete.</param>
        /// <param name="location">
        /// Required. Azure region the endpoint was registered in (e.g., "eastus").
        /// Must not be null or whitespace — an empty value returns <c>false</c>
        /// without making any API call.
        /// </param>
        /// <param name="agentBlueprintId">Entra ID application ID of the agent blueprint.</param>
        /// <param name="correlationId">Optional correlation ID for request tracing.</param>
        Task<bool> DeleteEndpointWithAgentBlueprintAsync(string endpointName, string location, string agentBlueprintId, string? correlationId = null);
    }
}
