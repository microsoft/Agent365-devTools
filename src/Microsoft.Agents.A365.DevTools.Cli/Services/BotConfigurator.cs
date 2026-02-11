// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
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
    private const string AlreadyExistsErrorMessage = "already exists";

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
    public async Task<EndpointRegistrationResult> CreateEndpointWithAgentBlueprintAsync(
        string endpointName,
        string location,
        string messagingEndpoint,
        string agentDescription,
        string agentBlueprintId,
        string? correlationId = null)
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
                return EndpointRegistrationResult.Failed;
            }

            if (!subscriptionResult.Success)
            {
                _logger.LogError("Failed to get subscription information for endpoint creation");
                return EndpointRegistrationResult.Failed;
            }

            var cleanedOutput = JsonDeserializationHelper.CleanAzureCliJsonOutput(subscriptionResult.StandardOutput);
            var subscriptionInfo = JsonSerializer.Deserialize<JsonElement>(cleanedOutput);
            var tenantId = subscriptionInfo.GetProperty("tenantId").GetString();

            if (string.IsNullOrEmpty(tenantId))
            {
                _logger.LogError("Could not determine tenant ID for endpoint creation");
                return EndpointRegistrationResult.Failed;
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
                authToken = await _authService.GetAccessTokenAsync(audience, tenantId);

                if (string.IsNullOrWhiteSpace(authToken))
                {
                    _logger.LogError("Failed to acquire authentication token");
                    return EndpointRegistrationResult.Failed;
                }
                _logger.LogInformation("Successfully acquired access token");

                var normalizedLocation = NormalizeLocation(location);
                var createEndpointBody = new JsonObject
                {
                    ["AzureBotServiceInstanceName"] = endpointName,
                    ["AppId"] = agentBlueprintId,
                    ["TenantId"] = tenantId,
                    ["MessagingEndpoint"] = messagingEndpoint,
                    ["Description"] = agentDescription,
                    ["Location"] = normalizedLocation,
                    ["Environment"] = EndpointHelper.GetDeploymentEnvironment(config.Environment),
                    ["ClusterCategory"] = EndpointHelper.GetClusterCategory(config.Environment)
                };
                // Use helper to create authenticated HTTP client
                using var httpClient = Services.Internal.HttpClientFactory.CreateAuthenticatedClient(authToken, correlationId: correlationId);

                // Call the endpoint
                _logger.LogInformation("Making request to create endpoint (Location: {Location}).", normalizedLocation);

                var response = await httpClient.PostAsync(createEndpointUrl,
                 new StringContent(createEndpointBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    
                    // Check for "already exists" condition - must be bot/endpoint-specific to avoid false positives
                    // Valid patterns:
                    // 1. HTTP 409 Conflict (standard REST pattern for resource conflicts)
                    // 2. HTTP 500 with bot-specific "already exists" message (Azure Bot Service pattern)
                    //    - Must contain "already exists" AND at least one bot-specific keyword
                    bool isBotAlreadyExists = response.StatusCode == System.Net.HttpStatusCode.Conflict ||
                        (errorContent.Contains(AlreadyExistsErrorMessage, StringComparison.OrdinalIgnoreCase) &&
                         (errorContent.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
                          errorContent.Contains("endpoint", StringComparison.OrdinalIgnoreCase) ||
                          errorContent.Contains(endpointName, StringComparison.OrdinalIgnoreCase)));
                    
                    if (isBotAlreadyExists)
                    {
                        _logger.LogWarning("Endpoint '{EndpointName}' {AlreadyExistsMessage} in the resource group", endpointName, AlreadyExistsErrorMessage);
                        _logger.LogInformation("Endpoint registration completed ({AlreadyExistsMessage})", AlreadyExistsErrorMessage);
                        _logger.LogInformation("");
                        _logger.LogInformation("If you need to update the endpoint:");
                        _logger.LogInformation("  1. Delete existing endpoint: a365 cleanup blueprint --endpoint-only");
                        _logger.LogInformation("  2. Register new endpoint: a365 setup blueprint --endpoint-only");
                        return EndpointRegistrationResult.AlreadyExists;
                    }
                    
                    // Log error only for actual failures (not idempotent "already exists" scenarios)
                    _logger.LogError("Failed to call create endpoint. Status: {Status}", response.StatusCode);
                    
                    if (errorContent.Contains("Failed to provision bot resource via Azure Management API. Status: BadRequest", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError("Please ensure that the Agent 365 CLI is supported in the selected region ('{Location}') and that your web app name ('{EndpointName}') is globally unique.", location, endpointName);
                        return EndpointRegistrationResult.Failed;
                    }

                    _logger.LogError("Error response: {Error}", errorContent);
                    _logger.LogError("");
                    _logger.LogError("To resolve this issue:");
                    _logger.LogError("  1. Check if endpoint exists: Review error details above");
                    _logger.LogError("  2. Delete conflicting endpoint: a365 cleanup blueprint --endpoint-only");
                    _logger.LogError("  3. Try registration again: a365 setup blueprint --endpoint-only");
                    return EndpointRegistrationResult.Failed;
                }

                _logger.LogInformation("Successfully received response from create endpoint");
                return EndpointRegistrationResult.Created;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to call create endpoint directly");
                return EndpointRegistrationResult.Failed;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError("Failed to parse tenant information: {Message}", ex.Message);
            return EndpointRegistrationResult.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating endpoint with agent blueprint: {Message}", ex.Message);
            return EndpointRegistrationResult.Failed;
        }
    }

    /// <summary>
    /// Delete endpoint with Agent Blueprint Identity
    /// </summary>
    public async Task<bool> DeleteEndpointWithAgentBlueprintAsync(
        string endpointName,
        string location,
        string agentBlueprintId,
        string? correlationId = null)
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

            var cleanedOutput = JsonDeserializationHelper.CleanAzureCliJsonOutput(subscriptionResult.StandardOutput);
            var subscriptionInfo = JsonSerializer.Deserialize<JsonElement>(cleanedOutput);
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

                authToken = await _authService.GetAccessTokenAsync(audience, tenantId);

                if (string.IsNullOrWhiteSpace(authToken))
                {
                    _logger.LogError("Failed to acquire authentication token");
                    return false;
                }
                _logger.LogInformation("Successfully acquired access token");

                var normalizedLocation = NormalizeLocation(location);
                var deleteEndpointBody = new JsonObject
                {
                    ["AzureBotServiceInstanceName"] = endpointName,
                    ["AppId"] = agentBlueprintId,
                    ["TenantId"] = tenantId,
                    ["Location"] = normalizedLocation,
                    ["Environment"] = EndpointHelper.GetDeploymentEnvironment(config.Environment),
                    ["ClusterCategory"] = EndpointHelper.GetClusterCategory(config.Environment)
                };
                // Use helper to create authenticated HTTP client
                using var httpClient = Services.Internal.HttpClientFactory.CreateAuthenticatedClient(authToken, correlationId: correlationId);

                // Call the endpoint
                _logger.LogInformation("Making request to delete endpoint (Location: {Location}).", normalizedLocation);

                using var request = new HttpRequestMessage(HttpMethod.Delete, deleteEndpointUrl);
                request.Content = new StringContent(deleteEndpointBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                var response = await httpClient.SendAsync(request);


                if (!response.IsSuccessStatusCode)
                {
                    // Read error content ONCE for all error handling
                    var errorContent = await response.Content.ReadAsStringAsync();
                    // Check if resource was not found - this is success for deletion (idempotent)
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound || 
                        response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        // For BadRequest, verify it's actually "not found" scenario
                        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                        {
                            try
                            {
                                var errorJson = JsonSerializer.Deserialize<JsonElement>(errorContent);
                                if (errorJson.TryGetProperty("details", out var detailsElement))
                                {
                                    var details = detailsElement.GetString();
                                    if (details?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
                                    {
                                        _logger.LogInformation("Bot endpoint not found - already deleted or does not exist");
                                        return true; // Not found is success for deletion
                                    }
                                }
                            }
                            catch
                            {
                                // If we can't parse, fall through to error handling
                            }
                        }
                        else // NotFound status
                        {
                            _logger.LogInformation("Bot endpoint not found - already deleted or does not exist");
                            return true; // Not found is success for deletion
                        }
                    }
                    // Real error - log and return false
                    try
                    {
                        var errorJson = JsonSerializer.Deserialize<JsonElement>(errorContent);
                        if (errorJson.TryGetProperty("error", out var errorMessage))
                        {
                            var error = errorMessage.GetString();
                            _logger.LogError("Failed to delete bot endpoint. Status: {Status}", response.StatusCode);
                            _logger.LogError("{Error}", error);
                        }
                        else
                        {
                            _logger.LogError("Failed to delete bot endpoint. Status: {Status}", response.StatusCode);
                            _logger.LogError("Error response: {Error}", errorContent);
                        }
                    }
                    catch
                    {
                        _logger.LogError("Failed to delete bot endpoint. Status: {Status}", response.StatusCode);
                        _logger.LogError("Error response: {Error}", errorContent);
                    }
                    return false;
                }

                _logger.LogInformation("Successfully received response from delete endpoint");
                return true;
            }
            catch (AzureAuthenticationException ex)
            {
                _logger.LogError("Authentication failed: {Message}", ex.IssueDescription);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to call delete endpoint: {Message}", ex.Message);
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

    private string NormalizeLocation(string location)
    {
        // Normalize location: Remove spaces and convert to lowercase (e.g., "Canada Central" -> "canadacentral")
        // Azure APIs require the API-friendly location name format
        // TODO: Consider using `az account list-locations` for robust display name → programmatic name mapping
        // See: https://learn.microsoft.com/en-us/cli/azure/account?view=azure-cli-latest#az-account-list-locations
        // Current approach works for existing regions but may need updates for new region naming patterns
        return location.Replace(" ", "").ToLowerInvariant();
    }
}
