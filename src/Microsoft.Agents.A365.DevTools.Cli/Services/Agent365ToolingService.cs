// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for interacting with Microsoft Agent 365 Tooling API endpoints for MCP server management in Dataverse
/// Handles authentication, HTTP communication, and response deserialization
/// </summary>
public class Agent365ToolingService : IAgent365ToolingService
{
    private readonly IConfigService _configService;
    private readonly AuthenticationService _authService;
    private readonly ILogger<Agent365ToolingService> _logger;
    private readonly string _environment;

    /// <inheritdoc />
    public string Environment => _environment;

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
    /// Handles double-serialized JSON responses from the Microsoft Agent 365 API.
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
        // Extract server-side correlation ID for troubleshooting
        string? serverCorrelationId = null;
        if (response.Headers.TryGetValues("x-ms-correlation-id", out var correlationValues))
        {
            serverCorrelationId = correlationValues.FirstOrDefault();
        }

        // Check HTTP status first
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to {Operation}. Status: {Status}", operationName, response.StatusCode);
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Error response: {Error}", errorContent);
            if (!string.IsNullOrWhiteSpace(serverCorrelationId))
            {
                _logger.LogError("Server correlation ID (x-ms-correlation-id): {CorrelationId}", serverCorrelationId);
            }
            return (false, errorContent);
        }

        // Read response content
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogDebug("Received response from {Operation} endpoint", operationName);
        _logger.LogDebug("Response content: {ResponseContent}", responseContent);

        // Check if response content indicates failure (Microsoft Agent 365 API pattern)
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
                    
                    _logger.LogDebug("{Operation} failed: {Message}", operationName, errorMessage);
                    if (!string.IsNullOrWhiteSpace(serverCorrelationId))
                    {
                        _logger.LogWarning("Server correlation ID (x-ms-correlation-id): {CorrelationId}", serverCorrelationId);
                    }
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
    /// Extracts a human-readable error message from a JSON error response body.
    /// </summary>
    internal static string? ExtractErrorMessage(string? responseContent)
    {
        if (string.IsNullOrWhiteSpace(responseContent))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;

            var details = root.TryGetProperty("details", out var d) ? d.GetString() : null;
            string? error = null;
            if (root.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.Object)
                    error = e.TryGetProperty("message", out var em) ? em.GetString() : e.ToString();
                else if (e.ValueKind == JsonValueKind.String)
                    error = e.GetString();
            }
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;

            return details ?? error ?? message;
        }
        catch
        {
            return responseContent;
        }
    }

    /// <summary>
    /// Common helper method to log HTTP request details
    /// </summary>
    /// <param name="method">HTTP method</param>
    /// <param name="url">Request URL</param>
    /// <param name="payload">Request payload (optional)</param>
    private void LogRequest(string method, string url, string? payload = null)
    {
        _logger.LogDebug("HTTP Method: {Method}", method);
        _logger.LogDebug("Request URL: {Url}", url);
        if (!string.IsNullOrEmpty(payload))
        {
            _logger.LogDebug("Request Payload: {Payload}", RedactSecretsFromPayload(payload));
        }
        _logger.LogDebug("Making {Method} request to: {Url}", method, url);
    }

    internal static string RedactSecretsFromPayload(string payload)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(payload) as System.Text.Json.Nodes.JsonObject;
            if (node == null) return "[non-JSON payload]";

            RedactSecretFields(node);
            return node.ToJsonString();
        }
        catch
        {
            return "[payload redacted]";
        }
    }

    private static void RedactSecretFields(System.Text.Json.Nodes.JsonObject obj)
    {
        var secretKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "clientApp1Secret", "clientApp2Secret", "clientSecret"
        };

        foreach (var key in obj.Select(p => p.Key).ToList())
        {
            if (secretKeys.Contains(key))
            {
                obj[key] = "***REDACTED***";
            }
            else if (obj[key] is System.Text.Json.Nodes.JsonObject child)
            {
                RedactSecretFields(child);
            }
        }
    }

    /// <summary>
    /// Builds base URL for Microsoft Agent 365 Tools API based on environment
    /// </summary>
    /// <param name="environment">Environment name (test, preprod, prod)</param>
    /// <returns>Base URL for the Microsoft Agent 365 Tools API</returns>
    private string BuildAgent365ToolsBaseUrl(string environment)
    {
        // Get from ConfigConstants to leverage existing URL construction logic
        var discoverUrl = ConfigConstants.GetDiscoverEndpointUrl(environment);
        var uri = new Uri(discoverUrl);
        return $"{uri.Scheme}://{uri.Authority}";
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
    /// Builds URL for publishing an MCP server to a Dataverse environment. Hits the platform's v2
    /// publish endpoint, which performs the full elevation orchestration (PPMI provisioning and MOS
    /// upload).
    /// </summary>
    /// <param name="environment">Environment name</param>
    /// <param name="environmentId">Dataverse environment ID</param>
    /// <param name="serverName">MCP server name</param>
    /// <returns>Full URL for publish MCP server endpoint</returns>
    private string BuildPublishMcpServerUrl(string environment, string environmentId, string serverName)
    {
        var baseUrl = BuildAgent365ToolsBaseUrl(environment);
        return $"{baseUrl}/agents/dataverse/environments/{environmentId}/mcpServers/{serverName}/publish/v2";
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
    /// Builds URL for adding a BYO MCP server
    /// </summary>
    /// <param name="environment">Environment name</param>
    /// <returns>Full URL for add MCP server endpoint</returns>
    private string BuildAddMcpServerUrl(string environment)
    {
        var baseUrl = BuildAgent365ToolsBaseUrl(environment);
        return $"{baseUrl}/agents/externalMcpServers/add";
    }

    private string BuildLogRegisterUrl(string environment)
    {
        var baseUrl = BuildAgent365ToolsBaseUrl(environment);
        return $"{baseUrl}/agents/externalMcpServers/logRegister";
    }

    private string BuildLogEvaluateUrl(string environment)
    {
        var baseUrl = BuildAgent365ToolsBaseUrl(environment);
        return $"{baseUrl}/agents/externalMcpServers/logEvaluate";
    }

    /// <summary>
    /// Builds URL for deleting a BYO MCP server
    /// </summary>
    /// <param name="environment">Environment name</param>
    /// <param name="serverName">MCP server name</param>
    /// <returns>Full URL for delete MCP server endpoint</returns>
    private string BuildDeleteMcpServerUrl(string environment, string serverName)
    {
        var baseUrl = BuildAgent365ToolsBaseUrl(environment);
        return $"{baseUrl}/agents/externalMcpServers/{Uri.EscapeDataString(serverName)}/delete";
    }

    /// <summary>
    /// Builds URL for provisioning an identity for an external MCP server
    /// </summary>
    /// <param name="environment">Environment name</param>
    /// <param name="serverName">MCP server name</param>
    /// <returns>Full URL for provision identity endpoint</returns>
    private string BuildProvisionIdentityUrl(string environment, string serverName)
    {
        var baseUrl = BuildAgent365ToolsBaseUrl(environment);
        return $"{baseUrl}/agents/mcpServers/{Uri.EscapeDataString(serverName)}/provisionIdentity";
    }

    private string BuildGetMcpServerAppIdsUrl(string environment, string serverName)
    {
        var baseUrl = BuildAgent365ToolsBaseUrl(environment);
        return $"{baseUrl}/agents/mcpServers/appIds?serverName={Uri.EscapeDataString(serverName)}";
    }

    /// <inheritdoc />
    public async Task<DataverseEnvironmentsResponse?> ListEnvironmentsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Build URL using environment from constructor
            var endpointUrl = BuildListEnvironmentsUrl(_environment);
            
            // Generate correlation ID at workflow entry point
            var correlationId = Internal.HttpClientFactory.GenerateCorrelationId();

            _logger.LogDebug("Listing Dataverse environments (CorrelationId: {CorrelationId})", correlationId);
            _logger.LogDebug("Environment: {Env}", _environment);
            _logger.LogDebug("Endpoint URL: {Url}", endpointUrl);

            // Get authentication token
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            _logger.LogDebug("Acquiring access token for audience: {Audience}", audience);
            
            var loginHint = await AzCliHelper.ResolveLoginHintAsync();
            var authToken = await _authService.GetAccessTokenAsync(audience, userId: loginHint, ct: cancellationToken);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire authentication token");
                return null;
            }

            // Create authenticated HTTP client
            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken, correlationId: correlationId);
            
            // Log request details
            LogRequest("GET", endpointUrl);
            
            // Make request
            using var response = await httpClient.GetAsync(endpointUrl, cancellationToken);

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
            
            // Generate correlation ID at workflow entry point
            var correlationId = Internal.HttpClientFactory.GenerateCorrelationId();

            _logger.LogDebug("Listing MCP servers for environment {EnvId} (CorrelationId: {CorrelationId})", environmentId, correlationId);
            _logger.LogDebug("Environment: {Env}", _environment);
            _logger.LogDebug("Endpoint URL: {Url}", endpointUrl);

            // Get authentication token
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            _logger.LogDebug("Acquiring access token for audience: {Audience}", audience);
            
            var loginHint = await AzCliHelper.ResolveLoginHintAsync();
            var authToken = await _authService.GetAccessTokenAsync(audience, userId: loginHint, ct: cancellationToken);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire authentication token");
                return null;
            }

            // Create authenticated HTTP client
            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken, correlationId: correlationId);
            
            // Log request details
            LogRequest("GET", endpointUrl);
            
            // Make request
            using var response = await httpClient.GetAsync(endpointUrl, cancellationToken);

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
            
            // Generate correlation ID at workflow entry point
            var correlationId = Internal.HttpClientFactory.GenerateCorrelationId();

            _logger.LogDebug("Publishing MCP server {ServerName} to environment {EnvId} (CorrelationId: {CorrelationId})", serverName, environmentId, correlationId);
            _logger.LogDebug("Environment: {Env}", _environment);
            _logger.LogDebug("Endpoint URL: {Url}", endpointUrl);

            // Get authentication token
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            _logger.LogDebug("Acquiring access token for audience: {Audience}", audience);
            
            var loginHint = await AzCliHelper.ResolveLoginHintAsync();
            var authToken = await _authService.GetAccessTokenAsync(audience, userId: loginHint, ct: cancellationToken);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire authentication token");
                return null;
            }

            // Create authenticated HTTP client
            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken, correlationId: correlationId);
            
            // Serialize request body
            var requestPayload = JsonSerializer.Serialize(request);
            var jsonContent = new StringContent(
                requestPayload,
                System.Text.Encoding.UTF8,
                "application/json");

            // Log request details
            LogRequest("POST", endpointUrl, requestPayload);

            // Make request
            using var response = await httpClient.PostAsync(endpointUrl, jsonContent, cancellationToken);

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
            
            // Generate correlation ID at workflow entry point
            var correlationId = Internal.HttpClientFactory.GenerateCorrelationId();

            _logger.LogDebug("Unpublishing MCP server {ServerName} from environment {EnvId} (CorrelationId: {CorrelationId})", serverName, environmentId, correlationId);
            _logger.LogDebug("Environment: {Env}", _environment);
            _logger.LogDebug("Endpoint URL: {Url}", endpointUrl);

            // Get authentication token
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            _logger.LogDebug("Acquiring access token for audience: {Audience}", audience);
            
            var loginHint = await AzCliHelper.ResolveLoginHintAsync();
            var authToken = await _authService.GetAccessTokenAsync(audience, userId: loginHint, ct: cancellationToken);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire authentication token");
                return false;
            }

            // Create authenticated HTTP client
            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken, correlationId: correlationId);
            
            // Log request details
            LogRequest("DELETE", endpointUrl);
            
            // Make request
            using var response = await httpClient.DeleteAsync(endpointUrl, cancellationToken);

            // Validate response using common helper
            var (isSuccess, _) = await ValidateResponseAsync(response, "unpublish MCP server", cancellationToken);
            if (!isSuccess)
            {
                return false;
            }

            _logger.LogDebug("Successfully unpublished MCP server");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unpublish MCP server {ServerName} from environment {EnvId}", serverName, environmentId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task LogRegisterUsageAsync(
        string serverName,
        string authType,
        int toolCount,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointUrl = BuildLogRegisterUrl(_environment);
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            var loginHint = await AzCliHelper.ResolveLoginHintAsync();
            var authToken = await _authService.GetAccessTokenAsync(audience, userId: loginHint);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogDebug("Skipping telemetry: failed to acquire token");
                return;
            }

            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken);
            var payload = JsonSerializer.Serialize(new { serverName, authType, toolCount });
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

            _logger.LogDebug("Logging register usage telemetry...");
            using var response = await httpClient.PostAsync(endpointUrl, content, cancellationToken);
            _logger.LogDebug("Telemetry logged: {StatusCode}", response.StatusCode);
            if (!response.IsSuccessStatusCode &&
                response.Headers.TryGetValues("x-ms-correlation-id", out var telemetryCorrelationValues))
            {
                var telemetryCorrelationId = telemetryCorrelationValues.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(telemetryCorrelationId))
                {
                    _logger.LogDebug("Telemetry server correlation ID: {CorrelationId}", telemetryCorrelationId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Telemetry logging failed (non-blocking): {Error}", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task LogEvaluateUsageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointUrl = BuildLogEvaluateUrl(_environment);
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            var loginHint = await AzCliHelper.ResolveLoginHintAsync();
            var authToken = await _authService.GetAccessTokenAsync(audience, userId: loginHint);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogDebug("Skipping telemetry: failed to acquire token");
                return;
            }

            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken);
            // Empty body: server identifies the caller from the bearer token and pulls any
            // operation context from ServiceContext. CLI does not ship customer-private content.
            var content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json");

            _logger.LogDebug("Logging evaluate usage telemetry...");
            using var response = await httpClient.PostAsync(endpointUrl, content, cancellationToken);
            _logger.LogDebug("Telemetry logged: {StatusCode}", response.StatusCode);
            if (!response.IsSuccessStatusCode &&
                response.Headers.TryGetValues("x-ms-correlation-id", out var telemetryCorrelationValues))
            {
                var telemetryCorrelationId = telemetryCorrelationValues.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(telemetryCorrelationId))
                {
                    _logger.LogDebug("Telemetry server correlation ID: {CorrelationId}", telemetryCorrelationId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Telemetry logging failed (non-blocking): {Error}", ex.Message);
        }
    }

    public async Task<AddMcpServerResponse?> AddMcpServerAsync(
        AddMcpServerRequest request,
        string? environmentId = null,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.ServerName))
            throw new ArgumentException("Server name cannot be null or empty", nameof(request.ServerName));

        try
        {
            var endpointUrl = BuildAddMcpServerUrl(_environment);

            var correlationId = Internal.HttpClientFactory.GenerateCorrelationId();

            _logger.LogDebug("Adding MCP server {ServerName} (CorrelationId: {CorrelationId})", request.ServerName, correlationId);
            _logger.LogDebug("Environment: {Env}", _environment);
            _logger.LogDebug("Endpoint URL: {Url}", endpointUrl);

            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            _logger.LogDebug("Acquiring access token for audience: {Audience}", audience);

            var loginHint = await AzCliHelper.ResolveLoginHintAsync();
            var authToken = await _authService.GetAccessTokenAsync(audience, userId: loginHint);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire authentication token");
                return null;
            }

            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken, correlationId: correlationId);

            if (!string.IsNullOrWhiteSpace(environmentId))
            {
                httpClient.DefaultRequestHeaders.Add("x-ms-environment-id", environmentId);
                _logger.LogDebug("Setting x-ms-environment-id header: {EnvironmentId}", environmentId);
            }

            var requestPayload = JsonSerializer.Serialize(request);
            var jsonContent = new StringContent(
                requestPayload,
                System.Text.Encoding.UTF8,
                "application/json");

            LogRequest("POST", endpointUrl, requestPayload);

            using var response = await httpClient.PostAsync(endpointUrl, jsonContent, cancellationToken);

            var (isSuccess, responseContent) = await ValidateResponseAsync(response, "add MCP server", cancellationToken);
            if (!isSuccess)
            {
                // Try to extract error details from server response
                var errorMessage = ExtractErrorMessage(responseContent) ?? $"Server returned {response.StatusCode}";
                return new AddMcpServerResponse { Status = "Failed", Message = errorMessage };
            }

            if (string.IsNullOrWhiteSpace(responseContent))
            {
                _logger.LogError("Add MCP server returned empty response");
                return null;
            }

            var addResponse = JsonDeserializationHelper.DeserializeWithDoubleSerialization<AddMcpServerResponse>(
                responseContent, _logger);

            if (addResponse == null)
            {
                _logger.LogError("Failed to deserialize add MCP server response");
                return null;
            }

            _logger.LogDebug("Successfully added MCP server {ServerName}", request.ServerName);
            return addResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add MCP server {ServerName}", request.ServerName);
            return new AddMcpServerResponse { Status = "Failed", Message = "Failed to add MCP server. See logs for details." };
        }
    }

    /// <inheritdoc />
    public async Task<ProvisionIdentityResponse?> ProvisionIdentityAsync(
        string serverName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverName))
            throw new ArgumentException("Server name cannot be null or empty", nameof(serverName));

        try
        {
            var endpointUrl = BuildProvisionIdentityUrl(_environment, serverName);

            var correlationId = Internal.HttpClientFactory.GenerateCorrelationId();

            _logger.LogDebug("Provisioning identity for MCP server {ServerName} (CorrelationId: {CorrelationId})", serverName, correlationId);
            _logger.LogDebug("Environment: {Env}", _environment);
            _logger.LogDebug("Endpoint URL: {Url}", endpointUrl);

            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            _logger.LogDebug("Acquiring access token for audience: {Audience}", audience);

            var loginHint = await AzCliHelper.ResolveLoginHintAsync();
            var authToken = await _authService.GetAccessTokenAsync(audience, userId: loginHint);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire authentication token");
                return null;
            }

            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken, correlationId: correlationId);

            LogRequest("POST", endpointUrl);

            var content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync(endpointUrl, content, cancellationToken);

            var (isSuccess, responseContent) = await ValidateResponseAsync(response, "provision identity", cancellationToken);
            if (!isSuccess)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(responseContent))
            {
                _logger.LogError("Provision identity returned empty response");
                return null;
            }

            var provisionResponse = JsonDeserializationHelper.DeserializeWithDoubleSerialization<ProvisionIdentityResponse>(
                responseContent, _logger);

            if (provisionResponse == null || string.IsNullOrWhiteSpace(provisionResponse.ApplicationId))
            {
                _logger.LogError("Provision identity response is missing applicationId");
                return null;
            }

            _logger.LogDebug("Successfully provisioned identity with application ID: {ApplicationId}", provisionResponse.ApplicationId);
            return provisionResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision identity for MCP server {ServerName}", serverName);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<McpServerAppIdResponse?> GetMcpServerAppIdByNameAsync(
        string serverName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverName))
            throw new ArgumentException("Server name cannot be null or empty", nameof(serverName));

        try
        {
            var endpointUrl = BuildGetMcpServerAppIdsUrl(_environment, serverName);

            var correlationId = Internal.HttpClientFactory.GenerateCorrelationId();

            _logger.LogDebug("Getting app ID for MCP server {ServerName} (CorrelationId: {CorrelationId})", serverName, correlationId);
            _logger.LogDebug("Endpoint URL: {Url}", endpointUrl);

            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            _logger.LogDebug("Acquiring access token for audience: {Audience}", audience);

            var loginHint = await AzCliHelper.ResolveLoginHintAsync();
            var authToken = await _authService.GetAccessTokenAsync(audience, userId: loginHint);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire authentication token");
                return null;
            }

            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken, correlationId: correlationId);

            LogRequest("GET", endpointUrl);

            using var response = await httpClient.GetAsync(endpointUrl, cancellationToken);

            var (isSuccess, responseContent) = await ValidateResponseAsync(response, "get MCP server app ID", cancellationToken);
            if (!isSuccess)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(responseContent))
            {
                _logger.LogError("Get MCP server app ID returned empty response");
                return null;
            }

            var appIdResponse = JsonDeserializationHelper.DeserializeWithDoubleSerialization<McpServerAppIdResponse>(
                responseContent, _logger);

            if (appIdResponse == null || string.IsNullOrWhiteSpace(appIdResponse.McpServerAppId))
            {
                _logger.LogError("Get MCP server app ID response is missing mcpServerAppId");
                return null;
            }

            _logger.LogDebug("Successfully retrieved app ID {AppId} for MCP server {ServerName}", appIdResponse.McpServerAppId, serverName);
            return appIdResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get app ID for MCP server {ServerName}", serverName);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<CleanupMcpServerResponse?> DeleteMcpServerAsync(
        string serverName,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverName))
            throw new ArgumentException("Server name cannot be null or empty", nameof(serverName));

        try
        {
            var endpointUrl = BuildDeleteMcpServerUrl(_environment, serverName);
            if (force)
            {
                endpointUrl += "?force=true";
            }

            var correlationId = Internal.HttpClientFactory.GenerateCorrelationId();

            _logger.LogDebug("Deleting MCP server {ServerName} (CorrelationId: {CorrelationId}, Force: {Force})", serverName, correlationId, force);
            _logger.LogDebug("Environment: {Env}", _environment);
            _logger.LogDebug("Endpoint URL: {Url}", endpointUrl);

            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            _logger.LogDebug("Acquiring access token for audience: {Audience}", audience);

            var loginHint = await AzCliHelper.ResolveLoginHintAsync();
            var authToken = await _authService.GetAccessTokenAsync(audience, userId: loginHint);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire authentication token");
                return null;
            }

            using var httpClient = Internal.HttpClientFactory.CreateAuthenticatedClient(authToken, correlationId: correlationId);

            LogRequest("DELETE", endpointUrl);

            using var response = await httpClient.DeleteAsync(endpointUrl, cancellationToken);

            var (isSuccess, responseContent) = await ValidateResponseAsync(response, "delete MCP server", cancellationToken);
            if (!isSuccess)
            {
                return null;
            }

            var deleteResponse = JsonDeserializationHelper.DeserializeWithDoubleSerialization<CleanupMcpServerResponse>(
                responseContent!, _logger);

            _logger.LogDebug("Successfully deleted MCP server {ServerName}", serverName);
            return deleteResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete MCP server {ServerName}", serverName);
            return null;
        }
    }
}

