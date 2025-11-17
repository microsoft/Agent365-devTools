// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for configuring Azure Bot resources
/// </summary>
public class BotConfigurator : IBotConfigurator
{
    private readonly ILogger<IBotConfigurator> _logger;
    private readonly CommandExecutor _executor;

    private readonly IConfigService _configService;
    private readonly AuthenticationService _authService;

    public BotConfigurator(ILogger<IBotConfigurator> logger, CommandExecutor executor, IConfigService configService, AuthenticationService authService)
    {
        _logger = logger;
        _executor = executor;
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    /// <summary>
    /// Create endpoint with Agent Blueprint Identity
    /// </summary>
    public async Task<bool> CreateEndpointWithAgentBlueprintAsync(
        string endpointName,
        string location,
        string messagingEndpoint,
        string agentDescription,
        string agentBlueprintId)
    {
        _logger.LogInformation("Creating endpoint with Agent Blueprint Identity...");
        _logger.LogDebug("   Endpoint Name: {EndpointName}", endpointName);
        _logger.LogDebug("   Messaging Endpoint: {Endpoint}", messagingEndpoint);
        _logger.LogDebug("   Agent Blueprint ID: {AgentBlueprintId}", agentBlueprintId);

        try
        {
            // Get subscription info for tenant ID
            var subscriptionResult = await _executor.ExecuteAsync("az", "account show", captureOutput: true);
            if (subscriptionResult == null)
            {
                _logger.LogError("Failed to execute account show command - null result");
                return false;
            }

            if (!subscriptionResult.Success)
            {
                _logger.LogError("Failed to get subscription information for endpoint creation");
                return false;
            }

            var subscriptionInfo = JsonSerializer.Deserialize<JsonElement>(subscriptionResult.StandardOutput);
            var tenantId = subscriptionInfo.GetProperty("tenantId").GetString();

            if (string.IsNullOrEmpty(tenantId))
            {
                _logger.LogError("Could not determine tenant ID for endpoint creation");
                return false;
            }

            // Create new endpoint with agent blueprint identity
            _logger.LogInformation("Creating new endpoint with Agent Blueprint Identity...");

            try
            {
                var config = await _configService.LoadAsync();
                var createEndpointUrl = EndpointHelper.GetCreateEndpointUrl(config.Environment);

                _logger.LogInformation("Calling create endpoint directly...");

                // Get authentication token interactively (unless skip-auth is specified)
                string? authToken = null;
                _logger.LogInformation("Getting authentication token...");

                // Determine the audience (App ID) based on the environment
                var audience = ConfigConstants.GetAgent365ToolsResourceAppId(config.Environment);
                authToken = await _authService.GetAccessTokenAsync(audience);

                if (string.IsNullOrWhiteSpace(authToken))
                {
                    _logger.LogError("Failed to acquire authentication token");
                    return false;
                }
                _logger.LogInformation("Successfully acquired access token");

                var createEndpointBody = new JsonObject
                {
                    ["AzureBotServiceInstanceName"] = endpointName,
                    ["AppId"] = agentBlueprintId,
                    ["TenantId"] = tenantId,
                    ["MessagingEndpoint"] = messagingEndpoint,
                    ["Description"] = agentDescription,
                    ["Location"] = location,
                    ["Environment"] = EndpointHelper.GetDeploymentEnvironment(config.Environment),
                    ["ClusterCategory"] = EndpointHelper.GetClusterCategory(config.Environment)
                };
                // Use helper to create authenticated HTTP client
                using var httpClient = Services.Internal.HttpClientFactory.CreateAuthenticatedClient(authToken);

                // Call the endpoint
                _logger.LogInformation("Making request to create endpoint.");

                var response = await httpClient.PostAsync(createEndpointUrl,
                 new StringContent(createEndpointBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to call create endpoint. Status: {Status}", response.StatusCode);
                    var errorContent = await response.Content.ReadAsStringAsync();
                    if (errorContent.Contains("Failed to provision bot resource via Azure Management API. Status: BadRequest", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError("Please ensure that the Agent 365 CLI is supported in the selected region ('{Location}') and that your web app name ('{EndpointName}') is globally unique.", location, endpointName);
                        return false;
                    }
                    _logger.LogError("Error response: {Error}", errorContent);
                    return false;
                }

                _logger.LogInformation("Successfully received response from create endpoint");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to call create endpoint directly");
                return false;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError("Failed to parse tenant information: {Message}", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating endpoint with agent blueprint: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Delete endpoint with Agent Blueprint Identity
    /// </summary>
    public async Task<bool> DeleteEndpointWithAgentBlueprintAsync(
        string endpointName,
        string location,
        string agentBlueprintId)
    {
        _logger.LogInformation("Deleting endpoint with Agent Blueprint Identity...");
        _logger.LogDebug("   Endpoint Name: {EndpointName}", endpointName);
        _logger.LogDebug("   Agent Blueprint ID: {AgentBlueprintId}", agentBlueprintId);

        try
        {
            // Get subscription info for tenant ID
            var subscriptionResult = await _executor.ExecuteAsync("az", "account show", captureOutput: true);
            if (subscriptionResult == null)
            {
                _logger.LogError("Failed to execute account show command - null result");
                return false;
            }

            if (!subscriptionResult.Success)
            {
                _logger.LogError("Failed to get subscription information for endpoint deletion");
                return false;
            }

            var subscriptionInfo = JsonSerializer.Deserialize<JsonElement>(subscriptionResult.StandardOutput);
            var tenantId = subscriptionInfo.GetProperty("tenantId").GetString();

            if (string.IsNullOrEmpty(tenantId))
            {
                _logger.LogError("Could not determine tenant ID for endpoint deletion");
                return false;
            }

            // Delete endpoint with agent blueprint identity
            _logger.LogInformation("Deleting endpoint with Agent Blueprint Identity...");

            try
            {
                var config = await _configService.LoadAsync();
                var deleteEndpointUrl = EndpointHelper.GetDeleteEndpointUrl(config.Environment);

                _logger.LogInformation("Calling delete endpoint directly...");
                _logger.LogInformation("Environment: {Env}", config.Environment);
                _logger.LogInformation("Endpoint URL: {Url}", deleteEndpointUrl);

                // Get authentication token interactively (unless skip-auth is specified)
                string? authToken = null;
                _logger.LogInformation("Getting authentication token...");

                // Determine the audience (App ID) based on the environment
                var audience = ConfigConstants.GetAgent365ToolsResourceAppId(config.Environment);

                _logger.LogInformation("Environment: {Environment}, Audience: {Audience}", config.Environment, audience);

                authToken = await _authService.GetAccessTokenAsync(audience);

                if (string.IsNullOrWhiteSpace(authToken))
                {
                    _logger.LogError("Failed to acquire authentication token");
                    return false;
                }
                _logger.LogInformation("Successfully acquired access token");

                var createEndpointBody = new JsonObject
                {
                    ["AzureBotServiceInstanceName"] = endpointName,
                    ["AppId"] = agentBlueprintId,
                    ["TenantId"] = tenantId,
                    ["Location"] = location,
                    ["Environment"] = EndpointHelper.GetDeploymentEnvironment(config.Environment),
                    ["ClusterCategory"] = EndpointHelper.GetClusterCategory(config.Environment)
                };
                // Use helper to create authenticated HTTP client
                using var httpClient = Services.Internal.HttpClientFactory.CreateAuthenticatedClient(authToken);

                // Call the endpoint
                _logger.LogInformation("Making request to delete endpoint.");

                using var request = new HttpRequestMessage(HttpMethod.Delete, deleteEndpointUrl);
                request.Content = new StringContent(createEndpointBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                var response = await httpClient.SendAsync(request);


                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to call delete endpoint. Status: {Status}", response.StatusCode);
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Error response: {Error}", errorContent);
                    return false;
                }

                _logger.LogInformation("Successfully received response from delete endpoint");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to call delete endpoint directly");
                return false;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError("Failed to parse tenant information: {Message}", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting endpoint with agent blueprint: {Message}", ex.Message);
            return false;
        }
    }
}
