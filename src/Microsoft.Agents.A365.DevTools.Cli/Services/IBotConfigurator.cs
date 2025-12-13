// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.


namespace Microsoft.Agents.A365.DevTools.Cli.Services
{
    /// <summary>
    /// Service for configuring messaging endpoints.
    /// </summary>
    public interface IBotConfigurator
    {
        Task<Models.EndpointRegistrationResult> CreateEndpointWithAgentBlueprintAsync(string endpointName, string location, string messagingEndpoint, string agentDescription, string agentBlueprintId);
        Task<bool> DeleteEndpointWithAgentBlueprintAsync(string endpointName, string location, string agentBlueprintId);
    }
}