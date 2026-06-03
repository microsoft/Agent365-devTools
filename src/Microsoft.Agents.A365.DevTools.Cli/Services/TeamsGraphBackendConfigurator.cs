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
    public async Task<(EndpointRegistrationResult Result, string? FailureReason)> SetBackendConfigurationAsync(
        string agentBlueprintId,
        string messagingEndpoint,
        string? correlationId = null)
    {
        // Debug only — the caller's "Configuring messaging endpoint..." header already frames this,
        // and "backend configuration" is internal MCP Platform terminology, not user-facing.
        _logger.LogDebug("Setting backend configuration for Agent Blueprint...");
        _logger.LogDebug("   Agent Blueprint ID: {AgentBlueprintId}", agentBlueprintId);
        _logger.LogDebug("   Callback URI: {Endpoint}", messagingEndpoint);

        try
        {
            var config = await _configService.LoadAsync();
            var tenantId = config.TenantId;

            if (string.IsNullOrEmpty(tenantId))
            {
                _logger.LogError("Could not determine tenant ID to register the messaging endpoint.");
                return (EndpointRegistrationResult.Failed, "Other");
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
                    return (EndpointRegistrationResult.Failed, "Other");
                }

                using var httpClient = Services.Internal.HttpClientFactory.CreateAuthenticatedClient(authToken, correlationId: correlationId);

                using var response = await httpClient.PostAsync(
                    createEndpointUrl,
                    new StringContent(requestBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Registered successfully.");
                    return (EndpointRegistrationResult.Created, null);
                }

                var errorContent = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    _logger.LogInformation("Messaging endpoint already registered.");
                    return (EndpointRegistrationResult.AlreadyExists, null);
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
                    return (EndpointRegistrationResult.SkippedContractMismatch, null);
                }

                _logger.LogError("Failed to register the messaging endpoint. Status: {Status}", response.StatusCode);
                _logger.LogError("Response: {Error}", errorContent);
                return (EndpointRegistrationResult.Failed, ClassifyFailureReason(errorContent));
            }

            return (EndpointRegistrationResult.Failed, "Other");
        }
        catch (AzureAuthenticationException ex)
        {
            _logger.LogError("Authentication failed: {Message}", ex.IssueDescription);
            return (EndpointRegistrationResult.Failed, ClassifyFailureReason(ex.Message));
        }
        catch (JsonException ex)
        {
            _logger.LogError("Failed to parse tenant information: {Message}", ex.Message);
            return (EndpointRegistrationResult.Failed, ClassifyFailureReason(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error registering the messaging endpoint: {Message}", ex.Message);
            return (EndpointRegistrationResult.Failed, ClassifyFailureReason(ex.Message));
        }
    }

    /// <inheritdoc />
    public async Task<bool> ClearBackendConfigurationAsync(
        string agentBlueprintId,
        string? correlationId = null)
    {
        // Debug only — the caller's "Removing messaging endpoint..." header already frames this.
        _logger.LogDebug("Clearing backend configuration for Agent Blueprint...");
        _logger.LogDebug("   Agent Blueprint ID: {AgentBlueprintId}", agentBlueprintId);

        try
        {
            var config = await _configService.LoadAsync();
            var tenantId = config.TenantId;

            if (string.IsNullOrEmpty(tenantId))
            {
                _logger.LogError("Could not determine tenant ID to remove the messaging endpoint.");
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
                    _logger.LogInformation("Removed successfully.");
                    return true;
                }

                var errorContent = await response.Content.ReadAsStringAsync();

                // Treat NotFound as idempotent success — nothing to clear.
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogInformation("Messaging endpoint already removed.");
                    return true;
                }

                // BadRequest may also indicate "not found" in some server versions.
                if (response.StatusCode == HttpStatusCode.BadRequest &&
                    ResponseDetailsContains(errorContent, "not found"))
                {
                    _logger.LogInformation("Messaging endpoint already removed.");
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

                _logger.LogError("Failed to remove messaging endpoint. Status: {Status}", response.StatusCode);
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
            _logger.LogError(ex, "Unexpected error removing the messaging endpoint: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Defensive detector: returns true when the server rejected the request with a known
    /// "wrong contract" signature the CLI can recognize. Today the only recognized signature
    /// is the pre-migration Azure Bot Service validator rejecting on <c>AzureBotServiceInstanceName</c>;
    /// extend this with additional patterns if a future breaking contract change lands.
    /// Callers translate a true result into <see cref="EndpointRegistrationResult.SkippedContractMismatch"/>
    /// and direct the user at the Teams Developer Portal.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: do NOT match on generic field names like "MessagingEndpoint" or
    /// "CallbackUri" — the new Teams Graph contract validates those fields itself, so matching
    /// them would silently mask real 400s as contract mismatches.
    /// </remarks>
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

        return errorContent.Contains("AzureBotServiceInstanceName", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Logs the contract-mismatch condition at INFO level with a user-friendly message. The
    /// loud signal (summary row + Action Required entry) lives in <c>DisplaySetupSummary</c>;
    /// this log is just the inline breadcrumb during the step. Response body is logged at
    /// DEBUG for diagnostics.
    /// </summary>
    private void LogContractMismatch(string errorContent)
    {
        _logger.LogInformation(
            "Automated messaging endpoint registration is not available for this tenant yet. " +
            "You'll need to configure it manually.");

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

    /// <summary>
    /// Classifies a failure response body into an actionable reason for the setup summary.
    /// Returns "NotOwner" when the server response indicates the caller is not a blueprint
    /// owner (Teams Graph's 403 is wrapped as a 400 by MCP Platform and contains the phrase
    /// "not the owner"). Returns "Other" for any other failure content.
    /// </summary>
    private static string ClassifyFailureReason(string? errorContent)
    {
        if (!string.IsNullOrEmpty(errorContent) &&
            errorContent.Contains("not the owner", StringComparison.OrdinalIgnoreCase))
        {
            return MessagingEndpointFailureReasons.NotOwner;
        }

        return MessagingEndpointFailureReasons.Other;
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
