// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for discovering and looking up agent blueprint applications and service principals.
/// Implements dual-path discovery: primary lookup by objectId, fallback to query by displayName.
/// </summary>
public class BlueprintLookupService
{
    private readonly ILogger<BlueprintLookupService> _logger;
    private readonly GraphApiService _graphApiService;

    public BlueprintLookupService(ILogger<BlueprintLookupService> logger, GraphApiService graphApiService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _graphApiService = graphApiService ?? throw new ArgumentNullException(nameof(graphApiService));
    }

    /// <summary>
    /// Gets or sets the custom client app ID to use for Microsoft Graph authentication.
    /// </summary>
    public string? CustomClientAppId
    {
        get => _graphApiService.CustomClientAppId;
        set => _graphApiService.CustomClientAppId = value;
    }

    /// <summary>
    /// Get blueprint application by object ID (primary path).
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication</param>
    /// <param name="objectId">The blueprint application object ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Lookup result with blueprint details if found</returns>
    public async Task<BlueprintLookupResult> GetApplicationByObjectIdAsync(
        string tenantId,
        string objectId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Looking up blueprint by objectId: {ObjectId}", objectId);

            var doc = await _graphApiService.GraphGetAsync(
                tenantId,
                $"/beta/applications/{objectId}",
                cancellationToken);

            if (doc == null)
            {
                _logger.LogDebug("Blueprint not found with objectId: {ObjectId}", objectId);
                return new BlueprintLookupResult
                {
                    Found = false,
                    LookupMethod = "objectId"
                };
            }

            var root = doc.RootElement;
            var appId = root.GetProperty("appId").GetString();
            var displayName = root.GetProperty("displayName").GetString();

            _logger.LogDebug("Found blueprint: {DisplayName} (ObjectId: {ObjectId}, AppId: {AppId})", 
                displayName, objectId, appId);

            return new BlueprintLookupResult
            {
                Found = true,
                ObjectId = objectId,
                AppId = appId,
                DisplayName = displayName,
                LookupMethod = "objectId"
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to look up blueprint by objectId: {ObjectId}", objectId);
            return new BlueprintLookupResult
            {
                Found = false,
                LookupMethod = "objectId",
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Get blueprint application by display name and sign-in audience (fallback path for migration).
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication</param>
    /// <param name="displayName">The blueprint display name to search for</param>
    /// <param name="signInAudience">The sign-in audience (default: AzureADMultipleOrgs)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Lookup result with blueprint details if found</returns>
    public async Task<BlueprintLookupResult> GetApplicationByDisplayNameAsync(
        string tenantId,
        string displayName,
        string signInAudience = "AzureADMultipleOrgs",
        string? preferredObjectId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Looking up blueprint by displayName: {DisplayName}", displayName);

            // Escape single quotes in displayName for OData filter
            var escapedDisplayName = displayName.Replace("'", "''");
            var filter = $"displayName eq '{escapedDisplayName}' and signInAudience eq '{signInAudience}'";

            var response = await _graphApiService.GraphGetWithResponseAsync(
                tenantId,
                $"/beta/applications?$filter={Uri.EscapeDataString(filter)}",
                scopes: [AuthenticationConstants.ApplicationReadAllScope],
                ct: cancellationToken);

            if (!response.IsSuccess)
            {
                response.Json?.Dispose();
                var errorMessage = $"Graph application lookup failed with HTTP {response.StatusCode} {response.ReasonPhrase}.";
                _logger.LogDebug(
                    "Blueprint lookup by displayName failed with HTTP {StatusCode} {ReasonPhrase}: {Body}",
                    response.StatusCode,
                    response.ReasonPhrase,
                    response.Body);
                return new BlueprintLookupResult
                {
                    Found = false,
                    LookupMethod = "displayName",
                    ErrorMessage = errorMessage
                };
            }

            using var doc = response.Json;
            if (doc == null)
            {
                return new BlueprintLookupResult
                {
                    Found = false,
                    LookupMethod = "displayName",
                    ErrorMessage = "Graph application lookup returned an empty response."
                };
            }

            var root = doc.RootElement;
            if (!root.TryGetProperty("value", out var valueElement) || valueElement.GetArrayLength() == 0)
            {
                _logger.LogDebug("No blueprints found with displayName: {DisplayName}", displayName);
                return new BlueprintLookupResult
                {
                    Found = false,
                    LookupMethod = "displayName"
                };
            }

            JsonElement? selectedMatch = null;
            if (!string.IsNullOrWhiteSpace(preferredObjectId))
            {
                foreach (var candidate in valueElement.EnumerateArray())
                {
                    if (string.Equals(
                            candidate.GetProperty("id").GetString(),
                            preferredObjectId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        selectedMatch = candidate;
                        break;
                    }
                }
            }

            if (selectedMatch is null && valueElement.GetArrayLength() == 1)
            {
                selectedMatch = valueElement[0];
            }

            if (selectedMatch is null)
            {
                var errorMessage = string.IsNullOrWhiteSpace(preferredObjectId)
                    ? $"Multiple blueprints were found with display name '{displayName}'."
                    : $"Multiple blueprints were found with display name '{displayName}', but none matched the stored object ID '{preferredObjectId}'.";
                _logger.LogWarning("{ErrorMessage}", errorMessage);
                return new BlueprintLookupResult
                {
                    Found = false,
                    LookupMethod = "displayName",
                    ErrorMessage = errorMessage
                };
            }

            var objectId = selectedMatch.Value.GetProperty("id").GetString();
            var appId = selectedMatch.Value.GetProperty("appId").GetString();
            var foundDisplayName = selectedMatch.Value.GetProperty("displayName").GetString();

            _logger.LogDebug("Found blueprint: {DisplayName} (ObjectId: {ObjectId}, AppId: {AppId})", 
                foundDisplayName, objectId, appId);

            return new BlueprintLookupResult
            {
                Found = true,
                ObjectId = objectId,
                AppId = appId,
                DisplayName = foundDisplayName,
                LookupMethod = "displayName",
                RequiresPersistence = true
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to look up blueprint by displayName: {DisplayName}", displayName);
            return new BlueprintLookupResult
            {
                Found = false,
                LookupMethod = "displayName",
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Get service principal by object ID (primary path).
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication</param>
    /// <param name="objectId">The service principal object ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Lookup result with service principal details if found</returns>
    public async Task<ServicePrincipalLookupResult> GetServicePrincipalByObjectIdAsync(
        string tenantId,
        string objectId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Looking up service principal by objectId: {ObjectId}", objectId);

            var doc = await _graphApiService.GraphGetAsync(
                tenantId,
                $"/v1.0/servicePrincipals/{objectId}",
                cancellationToken);

            if (doc == null)
            {
                _logger.LogDebug("Service principal not found with objectId: {ObjectId}", objectId);
                return new ServicePrincipalLookupResult
                {
                    Found = false,
                    LookupMethod = "objectId"
                };
            }

            var root = doc.RootElement;
            var appId = root.GetProperty("appId").GetString();
            var displayName = root.GetProperty("displayName").GetString();

            _logger.LogDebug("Found service principal: {DisplayName} (ObjectId: {ObjectId}, AppId: {AppId})", 
                displayName, objectId, appId);

            return new ServicePrincipalLookupResult
            {
                Found = true,
                ObjectId = objectId,
                AppId = appId,
                DisplayName = displayName,
                LookupMethod = "objectId"
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to look up service principal by objectId: {ObjectId}", objectId);
            return new ServicePrincipalLookupResult
            {
                Found = false,
                LookupMethod = "objectId",
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Get service principal by app ID (fallback path for migration).
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication</param>
    /// <param name="appId">The application (client) ID to search for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Lookup result with service principal details if found</returns>
    public async Task<ServicePrincipalLookupResult> GetServicePrincipalByAppIdAsync(
        string tenantId,
        string appId,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? scopes = null)
    {
        try
        {
            _logger.LogDebug("Looking up service principal by appId: {AppId}", appId);

            // Pass scopes so the caller can opt-in to a MSAL token with Application.Read.All.
            // Without Application.Read.All the az CLI token causes Graph to return an empty array silently.
            var objectId = await _graphApiService.LookupServicePrincipalByAppIdAsync(tenantId, appId, cancellationToken, scopes);

            if (objectId == null)
            {
                _logger.LogDebug("No service principal found with appId: {AppId}", appId);
                return new ServicePrincipalLookupResult
                {
                    Found = false,
                    LookupMethod = "appId"
                };
            }

            _logger.LogDebug("Found service principal (ObjectId: {ObjectId}, AppId: {AppId})", objectId, appId);

            // Note: DisplayName is not queried in this lookup path ($select=id only) — callers must not rely on it being populated.
            return new ServicePrincipalLookupResult
            {
                Found = true,
                ObjectId = objectId,
                AppId = appId,
                LookupMethod = "appId",
                RequiresPersistence = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to look up service principal by appId: {AppId}", appId);
            return new ServicePrincipalLookupResult
            {
                Found = false,
                LookupMethod = "appId",
                ErrorMessage = ex.Message
            };
        }
    }
}
