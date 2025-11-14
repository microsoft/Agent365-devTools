// Copyright (c) Microsoft Corporation.  
// Licensed under the MIT License. 
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for interacting with Agent365 Tooling API endpoints for MCP server management in Dataverse
/// Handles authentication, HTTP communication, and response deserialization
/// </summary>
public class Agent365ToolingService : IAgent365ToolingService
{
    private readonly IConfigService _configService;
    private readonly AuthenticationService _authService;
    private readonly ILogger<Agent365ToolingService> _logger;
    private readonly string _environment;

    public Agent365ToolingService(
        IConfigService configService,
        AuthenticationService authService,
        ILogger<Agent365ToolingService> logger,
        string environment = "prod")
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _environment = environment ?? "prod";
    }

    /// <summary>
    /// Common helper method to handle HTTP response validation and logging.
    /// Handles double-serialized JSON responses from the Agent365 API.
    /// </summary>
    /// <param name="response">The HTTP response message</param>
    /// <param name="operationName">Name of the operation for logging purposes</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple of (isSuccess, responseContent)</returns>
    private async Task<(bool IsSuccess, string ResponseContent)> ValidateResponseAsync(
        HttpResponseMessage response, 
        string operationName, 
        CancellationToken cancellationToken)
    {
        // Check HTTP status first
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to {Operation}. Status: {Status}", operationName, response.StatusCode);
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Error response: {Error}", errorContent);
            return (false, errorContent);
        }

        // Read response content
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("Received response from {Operation} endpoint", operationName);
        _logger.LogDebug("Response content: {ResponseContent}", responseContent);

        // Check if response content indicates failure (Agent365 API pattern)
        // The API may return double-serialized JSON, so we use JsonDeserializationHelper
        if (!string.IsNullOrWhiteSpace(responseContent))
        {
            try
            {
                // Use JsonDeserializationHelper to handle both normal and double-serialized JSON
                var statusResponse = JsonDeserializationHelper.DeserializeWithDoubleSerialization<ApiStatusResponse>(
                    responseContent, _logger);

                if (statusResponse != null && !string.IsNullOrEmpty(statusResponse.Status) && statusResponse.Status != "Success")
                {
                    // Extract error message
                    string errorMessage = statusResponse.Message ?? $"{operationName} failed";
                    
                    // Also check for Error property which might contain additional details
                    if (!string.IsNullOrEmpty(statusResponse.Error))
                    {
                        errorMessage += $" - {statusResponse.Error}";
                    }
                    
                    _logger.LogError("{Operation} failed: {Message}", operationName, errorMessage);
                    return (false, responseContent);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Response content is not valid JSON for {Operation}, treating as success", operationName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing response content for {Operation}", operationName);
                return (false, responseContent);
            }
        }

        return (true, responseContent);
    }

    /// <summary>
    /// Internal model for API status responses (used for validation)
    /// </summary>
    private class ApiStatusResponse
    {
        public string? Status { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Common helper method to log HTTP request details
    /// </summary>
    /// <param name="method">HTTP method</param>
    /// <param name="url">Request URL</param>
    /// <param name="payload">Request payload (optional)</param>
    private void LogRequest(string method, string url, string? payload = null)
    {
        _logger.LogInformation("HTTP Method: {Method}", method);
        _logger.LogInformation("Request URL: {Url}", url);
        if (!string.IsNullOrEmpty(payload))
        {
            _logger.LogInformation("Request Payload: {Payload}", payload);
        }
        _logger.LogInformation("Making {Method} request to: {Url}", method, url);
    }

    /// <summary>
    /// Builds base URL for Agent365 Tools API based on environment
    /// </summary>
    /// <param name="environment">Environment name (test, preprod, prod)</param>
    /// <returns>Base URL for the Agent365 Tools API</returns>
    private string BuildAgent365ToolsBaseUrl(string environment)
    {
        // Get from ConfigConstants to leverage existing URL construction logic
        var discoverUrl = ConfigConstants.GetDiscoverEndpointUrl(environment);
        var uri = new Uri(discoverUrl);
        return $"{uri.Scheme}://{uri.Host}";
    }

    /// <summary>
    /// Builds URL for listing Dataverse environments
    /// </summary>
    /// <param name="environment">Environment name</param>
    /// <returns>Full URL for list environments endpoint</returns>
    private string BuildListEnvironmentsUrl(string environment)
    {
        var baseUrl = BuildAgent365ToolsBaseUrl(environment);
        return $"{baseUrl}/agents/dataverse/environments";
    }

    /// <summary>
    /// Builds URL for listing MCP servers in a Dataverse environment
    /// </summary>
    /// <param name="environment">Environment name</param>
    /// <param name="environmentId">Dataverse environment ID</param>
    /// <returns>Full URL for list MCP servers endpoint</returns>
    private string BuildListMcpServersUrl(string environment, string environmentId)
    {
        var baseUrl = BuildAgent365ToolsBaseUrl(environment);
        return $"{baseUrl}/agents/dataverse/environments/{environmentId}/mcpServers";
    }

    /// <summary>
    /// Builds URL for publishing an MCP server to a Dataverse environment
    /// </summary>
    /// <param name="environment">Environment name</param>
    /// <param name="environmentId">Dataverse environment ID</param>
    /// <param name="serverName">MCP server name</param>
    /// <returns>Full URL for publish MCP server endpoint</returns>
    private string BuildPublishMcpServerUrl(string environment, string environmentId, string serverName)
    {
        var baseUrl = BuildAgent365ToolsBaseUrl(environment);
        return $"{baseUrl}/agents/dataverse/environments/{environmentId}/mcpServers/{serverName}/publish";
    }

    /// <summary>
    /// Builds URL for unpublishing an MCP server from a Dataverse environment
    /// </summary>
    /// <param name="environment">Environment name</param>
    /// <param name="environmentId">Dataverse environment ID</param>
    /// <param name="serverName">MCP server name</param>
    /// <returns>Full URL for unpublish endpoint</returns>
    private string BuildUnpublishMcpServerUrl(string environment, string environmentId, string serverName)
    {
        var baseUrl = BuildAgent365ToolsBaseUrl(environment);
        return $"{baseUrl}/agents/dataverse/environments/{environmentId}/mcpServers/{serverName}/unpublish";
    }

    /// <summary>
    /// Builds URL for approving an MCP server
    /// </summary>
    /// <param name="environment">Environment name</param>
    /// <param name="serverName">MCP server name</param>
    /// <returns>Full URL for approve endpoint</returns>
    private string BuildApproveMcpServerUrl(string environment, string serverName)
    {
        var baseUrl = BuildAgent365ToolsBaseUrl(environment);
        return $"{baseUrl}/agents/mcpServers/{serverName}/approve";
    }

    /// <summary>
    /// Builds URL for blocking an MCP server
    /// </summary>
    /// <param name="environment">Environment name</param>
    /// <param name="serverName">MCP server name</param>
    /// <returns>Full URL for block endpoint</returns>
    private string BuildBlockMcpServerUrl(string environment, string serverName)
    {
        var baseUrl = BuildAgent365ToolsBaseUrl(environment);
        return $"{baseUrl}/agents/mcpServers/{serverName}/block";
    }

    /// <inheritdoc />
    public async Task<DataverseEnvironmentsResponse?> ListEnvironmentsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Build URL using environment from constructor
            var endpointUrl = BuildListEnvironmentsUrl(_environment);
            
            _logger.LogInformation("Listing Dataverse environments");
            _logger.LogInformation("Environment: {Env}", _environment);
            _logger.LogInformation("Endpoint URL: {Url}", endpointUrl);

            // Get authentication token
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            _logger.LogInformation("Acquiring access token for audience: {Audience}", audience);
            
            var authToken = await _authService.GetAccessTokenAsync(audience);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire authentication token");
                return null;
            }

            // Create authenticated HTTP client
            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken);
            
            // Log request details
            LogRequest("GET", endpointUrl);
            
            // Make request
            var response = await httpClient.GetAsync(endpointUrl, cancellationToken);

            // Validate response using common helper
            var (isSuccess, responseContent) = await ValidateResponseAsync(response, "list environments", cancellationToken);
            if (!isSuccess)
            {
                return null;
            }

            var environmentsResponse = JsonDeserializationHelper.DeserializeWithDoubleSerialization<DataverseEnvironmentsResponse>(
                responseContent, _logger);

            // Fallback: try to parse as raw array if primary deserialization fails
            if (environmentsResponse == null)
            {
                _logger.LogDebug("Attempting to parse response as raw array...");
                try
                {
                    var rawArray = JsonSerializer.Deserialize<DataverseEnvironment[]>(responseContent);
                    if (rawArray != null && rawArray.Length > 0)
                    {
                        _logger.LogDebug("Successfully parsed as raw array with {Count} items", rawArray.Length);
                        environmentsResponse = new DataverseEnvironmentsResponse { Environments = rawArray };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse as raw array");
                }
            }

            return environmentsResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list Dataverse environments");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<DataverseMcpServersResponse?> ListServersAsync(
        string environmentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
            throw new ArgumentException("Environment ID cannot be null or empty", nameof(environmentId));

        try
        {
            // Build URL using environment from constructor
            var endpointUrl = BuildListMcpServersUrl(_environment, environmentId);
            
            _logger.LogInformation("Listing MCP servers for environment {EnvId}", environmentId);
            _logger.LogInformation("Environment: {Env}", _environment);
            _logger.LogInformation("Endpoint URL: {Url}", endpointUrl);

            // Get authentication token
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            _logger.LogInformation("Acquiring access token for audience: {Audience}", audience);
            
            var authToken = await _authService.GetAccessTokenAsync(audience);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire authentication token");
                return null;
            }

            // Create authenticated HTTP client
            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken);
            
            // Log request details
            LogRequest("GET", endpointUrl);
            
            // Make request
            var response = await httpClient.GetAsync(endpointUrl, cancellationToken);

            // Validate response using common helper
            var (isSuccess, responseContent) = await ValidateResponseAsync(response, "list MCP servers", cancellationToken);
            if (!isSuccess)
            {
                return null;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var serversResponse = JsonDeserializationHelper.DeserializeWithDoubleSerialization<DataverseMcpServersResponse>(
                responseContent, _logger, options);

            return serversResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list MCP servers for environment {EnvId}", environmentId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<PublishMcpServerResponse?> PublishServerAsync(
        string environmentId,
        string serverName,
        PublishMcpServerRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
            throw new ArgumentException("Environment ID cannot be null or empty", nameof(environmentId));
        if (string.IsNullOrWhiteSpace(serverName))
            throw new ArgumentException("Server name cannot be null or empty", nameof(serverName));
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        try
        {
            // Load configuration
            // Use environment from constructor
            
            // Build URL using private helper method
            var endpointUrl = BuildPublishMcpServerUrl(_environment, environmentId, serverName);
            
            _logger.LogInformation("Publishing MCP server {ServerName} to environment {EnvId}", serverName, environmentId);
            _logger.LogInformation("Environment: {Env}", _environment);
            _logger.LogInformation("Endpoint URL: {Url}", endpointUrl);

            // Get authentication token
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            _logger.LogInformation("Acquiring access token for audience: {Audience}", audience);
            
            var authToken = await _authService.GetAccessTokenAsync(audience);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire authentication token");
                return null;
            }

            // Create authenticated HTTP client
            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken);
            
            // Serialize request body
            var requestPayload = JsonSerializer.Serialize(request);
            var jsonContent = new StringContent(
                requestPayload,
                System.Text.Encoding.UTF8,
                "application/json");

            // Log request details
            LogRequest("POST", endpointUrl, requestPayload);

            // Make request
            var response = await httpClient.PostAsync(endpointUrl, jsonContent, cancellationToken);

            // Validate response using common helper
            var (isSuccess, responseContent) = await ValidateResponseAsync(response, "publish MCP server", cancellationToken);
            if (!isSuccess)
            {
                return null;
            }

            // Try to deserialize response, but allow for empty/null response
            if (string.IsNullOrWhiteSpace(responseContent))
            {
                return new PublishMcpServerResponse
                {
                    Status = "Success",
                    Message = $"Successfully published {serverName}"
                };
            }

            var publishResponse = JsonDeserializationHelper.DeserializeWithDoubleSerialization<PublishMcpServerResponse>(
                responseContent, _logger);

            return publishResponse ?? new PublishMcpServerResponse
            {
                Status = "Success",
                Message = $"Successfully published {serverName}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish MCP server {ServerName} to environment {EnvId}", serverName, environmentId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> UnpublishServerAsync(
        string environmentId,
        string serverName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
            throw new ArgumentException("Environment ID cannot be null or empty", nameof(environmentId));
        if (string.IsNullOrWhiteSpace(serverName))
            throw new ArgumentException("Server name cannot be null or empty", nameof(serverName));

        try
        {
            // Load configuration
            // Use environment from constructor
            
            // Build URL using private helper method
            var endpointUrl = BuildUnpublishMcpServerUrl(_environment, environmentId, serverName);
            
            _logger.LogInformation("Unpublishing MCP server {ServerName} from environment {EnvId}", serverName, environmentId);
            _logger.LogInformation("Environment: {Env}", _environment);
            _logger.LogInformation("Endpoint URL: {Url}", endpointUrl);

            // Get authentication token
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            _logger.LogInformation("Acquiring access token for audience: {Audience}", audience);
            
            var authToken = await _authService.GetAccessTokenAsync(audience);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire authentication token");
                return false;
            }

            // Create authenticated HTTP client
            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken);
            
            // Log request details
            LogRequest("DELETE", endpointUrl);
            
            // Make request
            var response = await httpClient.DeleteAsync(endpointUrl, cancellationToken);

            // Validate response using common helper
            var (isSuccess, _) = await ValidateResponseAsync(response, "unpublish MCP server", cancellationToken);
            if (!isSuccess)
            {
                return false;
            }

            _logger.LogInformation("Successfully unpublished MCP server");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unpublish MCP server {ServerName} from environment {EnvId}", serverName, environmentId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ApproveServerAsync(
        string serverName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverName))
            throw new ArgumentException("Server name cannot be null or empty", nameof(serverName));

        try
        {
            // Load configuration
            // Use environment from constructor
            
            // Build URL using private helper method
            var endpointUrl = BuildApproveMcpServerUrl(_environment, serverName);
            
            _logger.LogInformation("Approving MCP server {ServerName}", serverName);
            _logger.LogInformation("Environment: {Env}", _environment);
            _logger.LogInformation("Endpoint URL: {Url}", endpointUrl);

            // Get authentication token
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            _logger.LogInformation("Acquiring access token for audience: {Audience}", audience);
            
            var authToken = await _authService.GetAccessTokenAsync(audience);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire authentication token");
                return false;
            }

            // Create authenticated HTTP client
            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken);
            
            // Log request details
            LogRequest("POST", endpointUrl);
            
            // Make request with empty content
            var content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(endpointUrl, content, cancellationToken);

            // Validate response using common helper
            var (isSuccess, responseContent) = await ValidateResponseAsync(response, "approve MCP server", cancellationToken);
            if (!isSuccess)
            {
                return false;
            }

            _logger.LogInformation("Successfully approved MCP server");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to approve MCP server {ServerName}", serverName);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> BlockServerAsync(
        string serverName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverName))
            throw new ArgumentException("Server name cannot be null or empty", nameof(serverName));

        try
        {
            // Load configuration
            // Use environment from constructor
            
            // Build URL using private helper method
            var endpointUrl = BuildBlockMcpServerUrl(_environment, serverName);
            
            _logger.LogInformation("Blocking MCP server {ServerName}", serverName);
            _logger.LogInformation("Environment: {Env}", _environment);
            _logger.LogInformation("Endpoint URL: {Url}", endpointUrl);

            // Get authentication token
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            _logger.LogInformation("Acquiring access token for audience: {Audience}", audience);
            
            var authToken = await _authService.GetAccessTokenAsync(audience);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire authentication token");
                return false;
            }

            // Create authenticated HTTP client
            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken);
            
            // Log request details
            LogRequest("POST", endpointUrl);
            
            // Make request with empty content
            var content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(endpointUrl, content, cancellationToken);

            // Validate response using common helper
            var (isSuccess, responseContent) = await ValidateResponseAsync(response, "block MCP server", cancellationToken);
            if (!isSuccess)
            {
                return false;
            }

            _logger.LogInformation("Successfully blocked MCP server");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to block MCP server {ServerName}", serverName);
            return false;
        }
    }
}
