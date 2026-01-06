// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging;

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
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Looking up blueprint by displayName: {DisplayName}", displayName);

            // Escape single quotes in displayName for OData filter
            var escapedDisplayName = displayName.Replace("'", "''");
            var filter = $"displayName eq '{escapedDisplayName}' and signInAudience eq '{signInAudience}'";

            var doc = await _graphApiService.GraphGetAsync(
                tenantId,
                $"/beta/applications?$filter={Uri.EscapeDataString(filter)}",
                cancellationToken);

            if (doc == null)
            {
                _logger.LogDebug("No blueprints found with displayName: {DisplayName}", displayName);
                return new BlueprintLookupResult
                {
                    Found = false,
                    LookupMethod = "displayName"
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

            // Take first match (if multiple exist, log warning)
            var firstMatch = valueElement[0];
            var objectId = firstMatch.GetProperty("id").GetString();
            var appId = firstMatch.GetProperty("appId").GetString();
            var foundDisplayName = firstMatch.GetProperty("displayName").GetString();

            if (valueElement.GetArrayLength() > 1)
            {
                _logger.LogWarning("Multiple blueprints found with displayName '{DisplayName}'. Using first match: {ObjectId}", 
                    displayName, objectId);
            }

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
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Looking up service principal by appId: {AppId}", appId);

            var filter = $"appId eq '{appId}'";
            var doc = await _graphApiService.GraphGetAsync(
                tenantId,
                $"/v1.0/servicePrincipals?$filter={Uri.EscapeDataString(filter)}",
                cancellationToken);

            if (doc == null)
            {
                _logger.LogDebug("No service principal found with appId: {AppId}", appId);
                return new ServicePrincipalLookupResult
                {
                    Found = false,
                    LookupMethod = "appId"
                };
            }

            var root = doc.RootElement;
            if (!root.TryGetProperty("value", out var valueElement) || valueElement.GetArrayLength() == 0)
            {
                _logger.LogDebug("No service principal found with appId: {AppId}", appId);
                return new ServicePrincipalLookupResult
                {
                    Found = false,
                    LookupMethod = "appId"
                };
            }

            var firstMatch = valueElement[0];
            var objectId = firstMatch.GetProperty("id").GetString();
            var displayName = firstMatch.GetProperty("displayName").GetString();

            _logger.LogDebug("Found service principal: {DisplayName} (ObjectId: {ObjectId}, AppId: {AppId})", 
                displayName, objectId, appId);

            return new ServicePrincipalLookupResult
            {
                Found = true,
                ObjectId = objectId,
                AppId = appId,
                DisplayName = displayName,
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

/// <summary>
/// Result of blueprint application lookup operation.
/// </summary>
public class BlueprintLookupResult
{
    public bool Found { get; set; }
    public string? ObjectId { get; set; }
    public string? AppId { get; set; }
    public string? DisplayName { get; set; }
    public string? LookupMethod { get; set; }
    public bool RequiresPersistence { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result of service principal lookup operation.
/// </summary>
public class ServicePrincipalLookupResult
{
    public bool Found { get; set; }
    public string? ObjectId { get; set; }
    public string? AppId { get; set; }
    public string? DisplayName { get; set; }
    public string? LookupMethod { get; set; }
    public bool RequiresPersistence { get; set; }
    public string? ErrorMessage { get; set; }
}
