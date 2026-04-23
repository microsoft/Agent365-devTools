// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Manages the Teams Graph backend configuration (messaging endpoint) for an Agent Blueprint.
/// Talks to the MCP Platform endpoints <c>/agents/botManagement/createAgentBlueprint</c> and
/// <c>/agents/botManagement/deleteAgentBlueprint</c>, which proxy to Teams Graph.
/// </summary>
public class TeamsGraphBackendConfigurator : ITeamsGraphBackendConfigurator
{
    private readonly ILogger<ITeamsGraphBackendConfigurator> _logger;
    private readonly IConfigService _configService;
    private readonly AuthenticationService _authService;

    public TeamsGraphBackendConfigurator(
        ILogger<ITeamsGraphBackendConfigurator> logger,
        IConfigService configService,
        AuthenticationService authService)
    {
        _logger = logger;
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    /// <inheritdoc />
    public async Task<EndpointRegistrationResult> SetBackendConfigurationAsync(
        string agentBlueprintId,
        string messagingEndpoint,
        string? correlationId = null)
    {
        _logger.LogInformation("Setting backend configuration for Agent Blueprint...");
        _logger.LogDebug("   Agent Blueprint ID: {AgentBlueprintId}", agentBlueprintId);
        _logger.LogDebug("   Callback URI: {Endpoint}", messagingEndpoint);

        try
        {
            var config = await _configService.LoadAsync();
            var tenantId = config.TenantId;

            if (string.IsNullOrEmpty(tenantId))
            {
                _logger.LogError("Could not determine tenant ID for backend configuration");
                return EndpointRegistrationResult.Failed;
            }

            var currentUser = await AzCliHelper.ResolveLoginHintAsync()
                ?? await _authService.ResolveLoginHintFromCacheAsync();

            var createEndpointUrl = EndpointHelper.GetCreateEndpointUrl(config.Environment);
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(config.Environment);

            _logger.LogDebug("Create endpoint URL: {Url}", createEndpointUrl);

            var requestBody = new JsonObject
            {
                ["agentIdentityBlueprintId"] = agentBlueprintId,
                ["callbackUri"] = messagingEndpoint,
                ["tenantId"] = tenantId,
            };

            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool forceRefresh = attempt > 0;

                var authToken = await _authService.GetAccessTokenAsync(audience, tenantId, forceRefresh: forceRefresh, userId: currentUser);
                if (string.IsNullOrWhiteSpace(authToken))
                {
                    _logger.LogError("Failed to acquire authentication token");
                    return EndpointRegistrationResult.Failed;
                }

                using var httpClient = Services.Internal.HttpClientFactory.CreateAuthenticatedClient(authToken, correlationId: correlationId);

                using var response = await httpClient.PostAsync(
                    createEndpointUrl,
                    new StringContent(requestBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Backend configuration set successfully.");
                    return EndpointRegistrationResult.Created;
                }

                var errorContent = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    _logger.LogInformation("Backend configuration already exists.");
                    return EndpointRegistrationResult.AlreadyExists;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    _logger.LogWarning("Received 401 Unauthorized — cached token may be stale. Retrying with a fresh token...");
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.BadRequest &&
                    TryGetErrorCode(errorContent) == "Invalid roles" && attempt == 0)
                {
                    _logger.LogWarning(
                        "Access token is missing the required Agent ID role — " +
                        "this can happen when a role was assigned after the token was cached. " +
                        "Retrying with a fresh token...");
                    continue;
                }

                if (IsContractMismatchResponse(response, errorContent))
                {
                    LogContractMismatch(errorContent);
                    return EndpointRegistrationResult.SkippedDueToRollout;
                }

                _logger.LogError("Failed to set backend configuration. Status: {Status}", response.StatusCode);
                _logger.LogError("Response: {Error}", errorContent);
                return EndpointRegistrationResult.Failed;
            }

            return EndpointRegistrationResult.Failed;
        }
        catch (AzureAuthenticationException ex)
        {
            _logger.LogError("Authentication failed: {Message}", ex.IssueDescription);
            return EndpointRegistrationResult.Failed;
        }
        catch (JsonException ex)
        {
            _logger.LogError("Failed to parse tenant information: {Message}", ex.Message);
            return EndpointRegistrationResult.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error setting backend configuration: {Message}", ex.Message);
            return EndpointRegistrationResult.Failed;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ClearBackendConfigurationAsync(
        string agentBlueprintId,
        string? correlationId = null)
    {
        _logger.LogInformation("Clearing backend configuration for Agent Blueprint...");
        _logger.LogDebug("   Agent Blueprint ID: {AgentBlueprintId}", agentBlueprintId);

        try
        {
            var config = await _configService.LoadAsync();
            var tenantId = config.TenantId;

            if (string.IsNullOrEmpty(tenantId))
            {
                _logger.LogError("Could not determine tenant ID for backend configuration");
                return false;
            }

            var currentUser = await AzCliHelper.ResolveLoginHintAsync()
                ?? await _authService.ResolveLoginHintFromCacheAsync();

            var deleteEndpointUrl = EndpointHelper.GetDeleteEndpointUrl(config.Environment);
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(config.Environment);

            _logger.LogDebug("Delete endpoint URL: {Url}", deleteEndpointUrl);

            var requestBody = new JsonObject
            {
                ["agentIdentityBlueprintId"] = agentBlueprintId,
                ["tenantId"] = tenantId,
            };

            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool forceRefresh = attempt > 0;

                var authToken = await _authService.GetAccessTokenAsync(audience, tenantId, forceRefresh: forceRefresh, userId: currentUser);
                if (string.IsNullOrWhiteSpace(authToken))
                {
                    _logger.LogError("Failed to acquire authentication token");
                    return false;
                }

                using var httpClient = Services.Internal.HttpClientFactory.CreateAuthenticatedClient(authToken, correlationId: correlationId);

                using var request = new HttpRequestMessage(HttpMethod.Delete, deleteEndpointUrl)
                {
                    Content = new StringContent(requestBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                };

                using var response = await httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Backend configuration cleared successfully.");
                    return true;
                }

                var errorContent = await response.Content.ReadAsStringAsync();

                // Treat NotFound as idempotent success — nothing to clear.
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogInformation("Backend configuration not found — already cleared.");
                    return true;
                }

                // BadRequest may also indicate "not found" in some server versions.
                if (response.StatusCode == HttpStatusCode.BadRequest &&
                    ResponseDetailsContains(errorContent, "not found"))
                {
                    _logger.LogInformation("Backend configuration not found — already cleared.");
                    return true;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    _logger.LogWarning("Received 401 Unauthorized — cached token may be stale. Retrying with a fresh token...");
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.BadRequest &&
                    TryGetErrorCode(errorContent) == "Invalid roles" && attempt == 0)
                {
                    _logger.LogWarning(
                        "Access token is missing the required Agent ID role — " +
                        "this can happen when a role was assigned after the token was cached. " +
                        "Retrying with a fresh token...");
                    continue;
                }

                if (IsContractMismatchResponse(response, errorContent))
                {
                    LogContractMismatch(errorContent);
                    // Rollout in progress — nothing actually registered, so "clear" is effectively a no-op success.
                    return true;
                }

                _logger.LogError("Failed to clear backend configuration. Status: {Status}", response.StatusCode);
                _logger.LogError("Response: {Error}", errorContent);
                return false;
            }

            return false;
        }
        catch (AzureAuthenticationException ex)
        {
            _logger.LogError("Authentication failed: {Message}", ex.IssueDescription);
            return false;
        }
        catch (JsonException ex)
        {
            _logger.LogError("Failed to parse tenant information: {Message}", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error clearing backend configuration: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Detects whether the server rejected the request because it is still running the
    /// pre-migration ABS contract. The signature is HTTP 400 + validation errors naming any
    /// legacy field (e.g. AzureBotServiceInstanceName or MessagingEndpoint).
    /// TEMPORARY: remove along with SkippedDueToRollout once v1/v2 versioning is in place.
    /// </summary>
    private static bool IsContractMismatchResponse(HttpResponseMessage response, string errorContent)
    {
        if (response.StatusCode != HttpStatusCode.BadRequest &&
            response.StatusCode != HttpStatusCode.UnprocessableEntity)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(errorContent))
        {
            return false;
        }

        // Only match on AzureBotServiceInstanceName — it is uniquely ABS-shaped and cannot
        // plausibly appear in a Teams Graph validation error. Do NOT match on generic field
        // names like "MessagingEndpoint"/"CallbackUri"; the new Teams Graph contract itself
        // validates those fields, so matching them would silently mask real 400s as rollout
        // skips.
        return errorContent.Contains("AzureBotServiceInstanceName", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Logs the contract-mismatch condition at INFO level before the rollout cutoff and
    /// WARNING after, so the operator sees it escalate if it persists past the expected date.
    /// </summary>
    private void LogContractMismatch(string errorContent)
    {
        var message = "Server does not yet recognize the Teams Graph backend configuration contract — " +
                      "MCP Platform rollout is still in progress on this environment. Skipping registration.";

        if (DateTime.UtcNow < ConfigConstants.TeamsGraphRolloutCompleteOnUtc)
        {
            _logger.LogInformation("{Message}", message);
        }
        else
        {
            _logger.LogWarning(
                "{Message} (Rollout was expected to complete by {CutoffUtc:O}.)",
                message,
                ConfigConstants.TeamsGraphRolloutCompleteOnUtc);
        }

        _logger.LogDebug("Server response body: {Body}", errorContent);
    }

    /// <summary>
    /// Parses a JSON error response and returns the value of the top-level "error" field,
    /// which is a stable machine-readable code. Returns null if parsing fails or field is absent.
    /// </summary>
    private static string? TryGetErrorCode(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String)
            {
                return errorElement.GetString();
            }
        }
        catch { /* ignore parse errors */ }
        return null;
    }

    private static bool ResponseDetailsContains(string? content, string substring)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("details", out var detailsElement) &&
                detailsElement.ValueKind == JsonValueKind.String)
            {
                var details = detailsElement.GetString();
                return details != null && details.Contains(substring, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { /* ignore parse errors */ }
        return false;
    }
}
