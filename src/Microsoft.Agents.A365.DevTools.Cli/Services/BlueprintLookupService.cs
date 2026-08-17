// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
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
        if (!Guid.TryParse(objectId, out var validGuid))
        {
            return new BlueprintLookupResult
            {
                Found = false,
                LookupMethod = "objectId",
                ErrorMessage = "Invalid GUID format."
            };
        }

        try
        {
            _logger.LogDebug("Looking up blueprint by objectId: {ObjectId}", objectId);

            using var doc = await _graphApiService.GraphGetAsync(
                tenantId,
                $"/beta/applications/{validGuid:D}/microsoft.graph.agentIdentityBlueprint?$select=id,appId,displayName",
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
            var match = root.TryGetProperty("value", out var valueElement) &&
                valueElement.ValueKind == JsonValueKind.Object
                ? valueElement
                : root;
            var resolvedObjectId = match.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var appId = match.TryGetProperty("appId", out var appIdProp) ? appIdProp.GetString() : null;
            var displayName = match.TryGetProperty("displayName", out var nameProp) ? nameProp.GetString() : null;
            if (!string.Equals(resolvedObjectId, validGuid.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(appId) ||
                string.IsNullOrWhiteSpace(displayName))
            {
                return new BlueprintLookupResult
                {
                    Found = false,
                    LookupMethod = "objectId",
                    ErrorMessage = "Microsoft Graph returned incomplete or inconsistent blueprint identifiers."
                };
            }

            _logger.LogDebug("Found blueprint: {DisplayName} (ObjectId: {ObjectId}, AppId: {AppId})", 
                displayName, resolvedObjectId, appId);

            return new BlueprintLookupResult
            {
                Found = true,
                ObjectId = resolvedObjectId,
                AppId = appId,
                DisplayName = displayName,
                LookupMethod = "objectId"
            };
        }
        catch (OperationCanceledException)
        {
            throw;
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

            using var doc = await _graphApiService.GraphGetAsync(
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
        catch (OperationCanceledException)
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
    /// Lists all Agent Identity Blueprint applications in the tenant, following <c>@odata.nextLink</c>
    /// pagination. Uses the beta cast collection endpoint
    /// (<c>/beta/applications/microsoft.graph.agentIdentityBlueprint</c>) which returns only
    /// blueprint-typed applications, never other application types.
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All blueprints found, in the order returned by Graph. Empty when none exist.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a page request fails (e.g. authentication or query failure). Callers should
    /// catch this and surface a non-zero exit code rather than reporting an empty result as success.
    /// </exception>
    public virtual async Task<IReadOnlyList<BlueprintLookupResult>> ListBlueprintsAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var results = new List<BlueprintLookupResult>();
        string? nextPath = "/beta/applications/microsoft.graph.agentIdentityBlueprint?$select=id,appId,displayName&$top=100";

        while (!string.IsNullOrWhiteSpace(nextPath))
        {
            _logger.LogDebug("Listing agent identity blueprints: {Path}", nextPath);
            using var doc = await _graphApiService.GraphGetAsync(tenantId, nextPath, cancellationToken);

            if (doc is null)
            {
                throw new InvalidOperationException(
                    "Failed to list Agent Identity Blueprints from Microsoft Graph. This usually indicates " +
                    "an authentication failure or insufficient permissions (AgentIdentityBlueprint.Read.All).");
            }

            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in valueElement.EnumerateArray())
                {
                    results.Add(new BlueprintLookupResult
                    {
                        Found = true,
                        ObjectId = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null,
                        AppId = item.TryGetProperty("appId", out var appIdProp) ? appIdProp.GetString() : null,
                        DisplayName = item.TryGetProperty("displayName", out var nameProp) ? nameProp.GetString() : null,
                        LookupMethod = "list"
                    });
                }
            }

            nextPath = root.TryGetProperty("@odata.nextLink", out var nextLink) ? nextLink.GetString() : null;
        }

        return results;
    }

    /// <summary>
    /// Gets a single Agent Identity Blueprint by its application (client) ID, scoped to
    /// <paramref name="tenantId"/>. Uses the same cast collection endpoint as
    /// <see cref="ListBlueprintsAsync"/> so a match confirms both that the appId exists AND that it
    /// is specifically an Agent Identity Blueprint (not merely any Entra application) belonging to
    /// the caller's tenant.
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication.</param>
    /// <param name="appId">The blueprint's application (client) ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A result whose <see cref="BlueprintLookupResult.Found"/> is <c>false</c> when <paramref name="appId"/>
    /// is not a valid GUID, the query fails, or no matching blueprint exists in this tenant.
    /// </returns>
    public virtual async Task<BlueprintLookupResult> GetBlueprintByAppIdAsync(
        string tenantId,
        string appId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(appId, out var validGuid))
        {
            _logger.LogDebug("Invalid blueprint appId format: {AppId}", appId);
            return new BlueprintLookupResult { Found = false, LookupMethod = "appId", ErrorMessage = "Invalid GUID format." };
        }

        try
        {
            using var doc = await _graphApiService.GraphGetAsync(
                tenantId,
                $"/beta/applications/microsoft.graph.agentIdentityBlueprint?$filter=appId eq '{validGuid:D}'&$select=id,appId,displayName&$top=1",
                cancellationToken);

            if (doc is null)
            {
                _logger.LogDebug("Blueprint lookup by appId {AppId} failed (Graph query returned no result).", appId);
                return new BlueprintLookupResult { Found = false, LookupMethod = "appId", ErrorMessage = "Graph query failed." };
            }

            var root = doc.RootElement;
            if (!root.TryGetProperty("value", out var valueElement) || valueElement.GetArrayLength() == 0)
            {
                _logger.LogDebug("No agent identity blueprint found with appId: {AppId}", appId);
                return new BlueprintLookupResult { Found = false, LookupMethod = "appId" };
            }

            var match = valueElement[0];
            var objectId = match.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var resolvedAppId = match.TryGetProperty("appId", out var appIdProp) ? appIdProp.GetString() : null;
            var displayName = match.TryGetProperty("displayName", out var nameProp) ? nameProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(objectId) ||
                !string.Equals(resolvedAppId, validGuid.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(displayName))
            {
                return new BlueprintLookupResult
                {
                    Found = false,
                    LookupMethod = "appId",
                    ErrorMessage = "Microsoft Graph returned incomplete or inconsistent blueprint identifiers."
                };
            }

            return new BlueprintLookupResult
            {
                Found = true,
                ObjectId = objectId,
                AppId = resolvedAppId,
                DisplayName = displayName,
                LookupMethod = "appId"
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to look up blueprint by appId: {AppId}", appId);
            return new BlueprintLookupResult { Found = false, LookupMethod = "appId", ErrorMessage = ex.Message };
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
