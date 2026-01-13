// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for agent blueprint operations including inheritable permissions, OAuth grants,
/// resource access configuration, and blueprint cleanup.
/// </summary>
public class AgentBlueprintService
{
    private readonly ILogger<AgentBlueprintService> _logger;
    private readonly GraphApiService _graphApiService;

    public AgentBlueprintService(ILogger<AgentBlueprintService> logger, GraphApiService graphApiService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _graphApiService = graphApiService ?? throw new ArgumentNullException(nameof(graphApiService));
    }

    /// <summary>
    /// Gets or sets the custom client app ID to use for Microsoft Graph authentication.
    /// This delegates to the underlying GraphApiService.
    /// </summary>
    public string? CustomClientAppId
    {
        get => _graphApiService.CustomClientAppId;
        set => _graphApiService.CustomClientAppId = value;
    }

    /// <summary>
    /// Get inheritable permissions for an agent blueprint
    /// </summary>
    /// <param name="blueprintId">The blueprint ID</param>
    /// <param name="tenantId">The tenant ID for authentication</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>JSON response from the inheritable permissions endpoint</returns>
    public async Task<string?> GetBlueprintInheritablePermissionsAsync(
        string blueprintId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Make the API call to get inheritable permissions
            var doc = await _graphApiService.GraphGetAsync(
                tenantId, 
                $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintId}/inheritablePermissions", 
                cancellationToken,
                AuthenticationConstants.AgentBlueprintAuthScopes);

            if (doc == null)
            {
                _logger.LogError("Failed to retrieve inheritable permissions from Graph API");
                return null;
            }

            _logger.LogInformation("Successfully retrieved inheritable permissions from Graph API");
            return doc.RootElement.GetRawText();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception calling inheritable permissions endpoint");
            return null;
        }
    }

    /// <summary>
    /// Delete an Agent Blueprint application using the special agentIdentityBlueprint endpoint.
    /// 
    /// SPECIAL AUTHENTICATION REQUIREMENTS:
    /// Agent Blueprint deletion requires the AgentIdentityBlueprint.ReadWrite.All delegated permission scope.
    /// This scope is not available through Azure CLI tokens, so we use interactive authentication via
    /// the token provider (same authentication method used during blueprint creation in the setup command).
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication</param>
    /// <param name="blueprintId">The blueprint application ID (object ID or app ID)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deletion succeeded or resource not found; false otherwise</returns>
    public async Task<bool> DeleteAgentBlueprintAsync(
        string tenantId,
        string blueprintId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Deleting agent blueprint application: {BlueprintId}", blueprintId);
            
            // Agent Blueprint deletion requires special delegated permission scope
            var authScopes = AuthenticationConstants.AgentBlueprintAuthScopes;
            
            _logger.LogInformation("Acquiring access token for agent blueprint operations (device code flow may prompt once)...");
            
            // Use the special agentIdentityBlueprint endpoint for deletion
            var deletePath = $"/beta/applications/{blueprintId}/microsoft.graph.agentIdentityBlueprint";
            
            // Use GraphDeleteAsync with the special scopes required for blueprint operations
            var success = await _graphApiService.GraphDeleteAsync(
                tenantId,
                deletePath,
                cancellationToken,
                treatNotFoundAsSuccess: true,
                scopes: authScopes);
            
            if (success)
            {
                _logger.LogInformation("Agent blueprint application deleted successfully");
            }
            else
            {
                _logger.LogError("Failed to delete agent blueprint application");
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception deleting agent blueprint application");
            return false;
        }
    }

    /// <summary>
    /// Deletes the specified agent identity application from the tenant using delegated permissions.
    /// This method deletes the service principal object, not the application registration.
    /// </summary>
    /// <param name="tenantId">The unique identifier of the Azure Active Directory tenant containing the agent identity application.</param>
    /// <param name="applicationId">The unique identifier of the agent identity application to delete.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the delete operation.</param>
    /// <returns>True if deletion succeeded or resource not found; false otherwise</returns>
    public async Task<bool> DeleteAgentIdentityAsync(
        string tenantId,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Deleting agent identity application: {applicationId}", applicationId);

            // Agent Identity deletion requires special delegated permission scope
            var authScopes = AuthenticationConstants.AgentBlueprintAuthScopes;

            _logger.LogInformation("Acquiring access token for agent identity operations (device code flow may prompt once)...");

            // Use the special servicePrincipals endpoint for deletion
            var deletePath = $"/beta/servicePrincipals/{applicationId}";

            // Use GraphDeleteAsync with the special scopes required for identity operations
            return await _graphApiService.GraphDeleteAsync(
                tenantId,
                deletePath,
                cancellationToken,
                treatNotFoundAsSuccess: true,
                scopes: authScopes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception deleting agent identity application");
            return false;
        }
    }

    /// <summary>
    /// Sets inheritable permissions for an agent blueprint with proper scope merging.
    /// Checks if permissions already exist and merges scopes if needed via PATCH.
    /// </summary>
    public virtual async Task<(bool ok, bool alreadyExists, string? error)> SetInheritablePermissionsAsync(
        string tenantId,
        string blueprintId,
        string resourceAppId,
        IEnumerable<string> scopes,
        IEnumerable<string>? authScopes = null,
        CancellationToken ct = default)
    {
        var desiredSet = new HashSet<string>(scopes ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        // Normalize into array form expected by Graph (each element is a single scope string)
        var desiredArray = desiredSet.ToArray();

        try
        {
            // Resolve blueprintId to object ID if needed
            var blueprintObjectId = await ResolveBlueprintObjectIdAsync(tenantId, blueprintId, ct, authScopes);

            // Retrieve existing inheritable permissions
            var getPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions";
            var existingDoc = await _graphApiService.GraphGetAsync(tenantId, getPath, ct, authScopes);

            // Inspect existing entries
            JsonElement? existingEntry = null;
            if (existingDoc != null && existingDoc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    var rId = item.TryGetProperty("resourceAppId", out var r) ? r.GetString() : null;
                    if (string.Equals(rId, resourceAppId, StringComparison.OrdinalIgnoreCase))
                    {
                        existingEntry = item;
                        break;
                    }
                }
            }

            if (existingEntry is not null)
            {
                // Parse existing scopes and merge with desired scopes
                var currentScopes = ParseInheritableScopesFromJson(existingEntry.Value);
                var currentSet = new HashSet<string>(currentScopes, StringComparer.OrdinalIgnoreCase);
                
                if (desiredSet.IsSubsetOf(currentSet))
                {
                    _logger.LogInformation("Inheritable permissions already exist for blueprint {Blueprint} resource {Resource}", blueprintObjectId, resourceAppId);
                    return (ok: true, alreadyExists: true, error: null);
                }

                // Union and PATCH
                currentSet.UnionWith(desiredSet);
                var mergedArray = currentSet.OrderBy(s => s).ToArray();

                var patchPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions/{resourceAppId}";
                var patchPayload = new
                {
                    inheritableScopes = new EnumeratedScopes
                    {
                        Scopes = mergedArray
                    }
                };

                var patched = await _graphApiService.GraphPatchAsync(tenantId, patchPath, patchPayload, ct, authScopes);
                if (!patched)
                {
                    return (ok: false, alreadyExists: false, error: "PATCH failed");
                }

                _logger.LogInformation("Patched inheritable permissions for blueprint {Blueprint} resource {Resource}", blueprintObjectId, resourceAppId);
                return (ok: true, alreadyExists: false, error: null);
            }

            // No existing entry -> create
            var postPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions";
            var postPayload = new
            {
                resourceAppId = resourceAppId,
                inheritableScopes = new EnumeratedScopes
                {
                    Scopes = desiredArray
                }
            };

            var createdResp = await _graphApiService.GraphPostWithResponseAsync(tenantId, postPath, postPayload, ct, authScopes);
            if (!createdResp.IsSuccess)
            {
                var err = string.IsNullOrWhiteSpace(createdResp.Body)
                    ? $"HTTP {createdResp.StatusCode} {createdResp.ReasonPhrase}"
                    : createdResp.Body;
                _logger.LogError("Failed to create inheritable permissions: {Status} {Reason} Body: {Body}", createdResp.StatusCode, createdResp.ReasonPhrase, createdResp.Body);
                return (ok: false, alreadyExists: false, error: err);
            }

            _logger.LogInformation("Created inheritable permissions for blueprint {Blueprint} resource {Resource}", blueprintObjectId, resourceAppId);
            return (ok: true, alreadyExists: false, error: null);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to set inheritable permissions: {Error}", ex.Message);
            return (ok: false, alreadyExists: false, error: ex.Message);
        }
    }

    /// <summary>
    /// Verifies that inheritable permissions are correctly configured for a resource
    /// </summary>
    public virtual async Task<(bool exists, string[] scopes, string? error)> VerifyInheritablePermissionsAsync(
        string tenantId,
        string blueprintId,
        string resourceAppId,
        CancellationToken ct = default,
        IEnumerable<string>? authScopes = null)
    {
        try
        {
            // Resolve blueprintId to object ID if needed
            var blueprintObjectId = await ResolveBlueprintObjectIdAsync(tenantId, blueprintId, ct, authScopes);

            // Retrieve inheritable permissions
            var getPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions";
            var existingDoc = await _graphApiService.GraphGetAsync(tenantId, getPath, ct, authScopes);

            if (existingDoc == null)
            {
                return (exists: false, scopes: Array.Empty<string>(), error: "Failed to retrieve inheritable permissions");
            }

            // Find the entry for this resource
            if (existingDoc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    var rId = item.TryGetProperty("resourceAppId", out var r) ? r.GetString() : null;
                    if (string.Equals(rId, resourceAppId, StringComparison.OrdinalIgnoreCase))
                    {
                        // Found the resource, parse and return scopes
                        var scopesList = ParseInheritableScopesFromJson(item);
                        return (exists: true, scopes: scopesList.ToArray(), error: null);
                    }
                }
            }

            return (exists: false, scopes: Array.Empty<string>(), error: null);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to verify inheritable permissions: {Error}", ex.Message);
            return (exists: false, scopes: Array.Empty<string>(), error: ex.Message);
        }
    }

    /// <summary>
    /// Replaces OAuth2 permission grants for a client/resource pair.
    /// Deletes all existing grants and creates a new one with the specified scopes.
    /// </summary>
    public virtual async Task<bool> ReplaceOauth2PermissionGrantAsync(
        string tenantId,
        string clientSpObjectId,  
        string resourceSpObjectId,
        IEnumerable<string> scopes,
        CancellationToken ct = default)
    {
        // Normalize scopes -> single space-delimited string (Graph's required shape)
        var desiredSet = new HashSet<string>(
            (scopes ?? Enumerable.Empty<string>())
                .SelectMany(s => (s ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            StringComparer.OrdinalIgnoreCase);

        var desiredScopeString = string.Join(' ', desiredSet.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

        // 1) Find existing grant(s) for client resource
        var listDoc = await _graphApiService.GraphGetAsync(
            tenantId,
            $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{clientSpObjectId}' and resourceId eq '{resourceSpObjectId}'",
            ct,
            AuthenticationConstants.PermissionGrantAuthScopes);

        var existing = listDoc?.RootElement.TryGetProperty("value", out var arr) == true ? arr : default;

        // 2) Delete all existing grants for this pair (rare but possible to have >1)
        if (existing.ValueKind == JsonValueKind.Array && existing.GetArrayLength() > 0)
        {
            foreach (var item in existing.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _logger.LogDebug("Deleting existing oauth2PermissionGrant {Id} for client {ClientId} and resource {ResourceId}", 
                        id, clientSpObjectId, resourceSpObjectId);

                    var ok = await _graphApiService.GraphDeleteAsync(
                        tenantId, 
                        $"/v1.0/oauth2PermissionGrants/{id}", 
                        ct, 
                        true,
                        AuthenticationConstants.PermissionGrantAuthScopes);
                    if (!ok)
                    {
                        _logger.LogError("Failed to delete existing oauth2PermissionGrant {Id} for client {ClientId} and resource {ResourceId}. " +
                                       "This may indicate insufficient permissions or the grant is protected. " +
                                       "Required permissions: DelegatedPermissionGrant.ReadWrite.All or Application.ReadWrite.All", 
                                       id, clientSpObjectId, resourceSpObjectId);
                        _logger.LogError("Troubleshooting steps:");
                        _logger.LogError("  1. Verify your account has sufficient Azure AD permissions");
                        _logger.LogError("  2. Check if you are a Global Administrator or Application Administrator");
                        _logger.LogError("  3. Ensure the oauth2PermissionGrant exists and is not system-protected");
                        _logger.LogError("  4. Try running: az login --tenant {TenantId} with elevated privileges", tenantId);
                        
                        throw new InvalidOperationException($"Failed to delete existing oauth2PermissionGrant {id}");
                    }

                    _logger.LogDebug("Successfully deleted oauth2PermissionGrant {Id}", id);
                }
            }
        }

        // If no scopes desired, we're done (revoke only)
        if (desiredSet.Count == 0) return true;

        // 3) Create the new grant with exactly the desired scopes
        var payload = new
        {
            clientId = clientSpObjectId,
            consentType = "AllPrincipals",
            resourceId = resourceSpObjectId,
            scope = desiredScopeString
        };

        var created = await _graphApiService.GraphPostAsync(
            tenantId,
            "/v1.0/oauth2PermissionGrants",
            payload,
            ct,
            AuthenticationConstants.PermissionGrantAuthScopes);

        return created != null;
    }

    public virtual async Task<bool> CreateOrUpdateOauth2PermissionGrantAsync(
        string tenantId,
        string clientSpObjectId,
        string resourceSpObjectId,
        IEnumerable<string> scopes,
        CancellationToken ct = default)
    {
        var desiredScopeString = string.Join(' ', scopes);

        // Read existing
        var listDoc = await _graphApiService.GraphGetAsync(
            tenantId,
            $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{clientSpObjectId}' and resourceId eq '{resourceSpObjectId}'",
            ct,
            AuthenticationConstants.PermissionGrantAuthScopes);

        var existing = listDoc?.RootElement.TryGetProperty("value", out var arr) == true && arr.GetArrayLength() > 0
            ? arr[0]
            : (JsonElement?)null;

        if (existing is null)
        {
            // Create
            var payload = new
            {
                clientId = clientSpObjectId,
                consentType = "AllPrincipals",
                resourceId = resourceSpObjectId,
                scope = desiredScopeString
            };
            var created = await _graphApiService.GraphPostAsync(tenantId, "/v1.0/oauth2PermissionGrants", payload, ct, AuthenticationConstants.PermissionGrantAuthScopes);
            return created != null; // success if response parsed
        }

        // Merge scopes if needed
        var current = existing.Value.TryGetProperty("scope", out var s) ? s.GetString() ?? "" : "";
        var currentSet = new HashSet<string>(current.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var desiredSet = new HashSet<string>(desiredScopeString.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        if (desiredSet.IsSubsetOf(currentSet)) return true; // already satisfied

        currentSet.UnionWith(desiredSet);
        var merged = string.Join(' ', currentSet);

        var id = existing.Value.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(id)) return false;

        return await _graphApiService.GraphPatchAsync(tenantId, $"/v1.0/oauth2PermissionGrants/{id}", new { scope = merged }, ct, AuthenticationConstants.PermissionGrantAuthScopes);
    }

    /// <summary>
    /// Adds required resource access (API permissions) to an application's manifest.
    /// This makes the permissions visible in the Entra portal's "API permissions" blade.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="appId">The application (client) ID to update</param>
    /// <param name="resourceAppId">The resource application ID to add permissions for</param>
    /// <param name="scopes">The permission scope names to add</param>
    /// <param name="isDelegated">True for delegated permissions (Scope), false for application permissions (Role)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if successful, false otherwise</returns>
    public virtual async Task<bool> AddRequiredResourceAccessAsync(
        string tenantId,
        string appId,
        string resourceAppId,
        IEnumerable<string> scopes,
        bool isDelegated = true,
        CancellationToken ct = default)
    {
        try
        {
            var permissionGrantAuthScopes = AuthenticationConstants.PermissionGrantAuthScopes;

            // Get the application object by appId
            var appsDoc = await _graphApiService.GraphGetAsync(tenantId, $"/v1.0/applications?$filter=appId eq '{appId}'&$select=id,requiredResourceAccess", ct, permissionGrantAuthScopes);
            if (appsDoc == null)
            {
                _logger.LogError("Failed to retrieve application with appId {AppId}", appId);
                return false;
            }

            if (!appsDoc.RootElement.TryGetProperty("value", out var appsArray) || appsArray.GetArrayLength() == 0)
            {
                _logger.LogError("Application not found with appId {AppId}", appId);
                return false;
            }

            var app = appsArray[0];
            if (!app.TryGetProperty("id", out var idProp) || string.IsNullOrEmpty(idProp.GetString()))
            {
                _logger.LogError("Application object missing 'id' property or 'id' is null for appId {AppId}", appId);
                return false;
            }
            var objectId = idProp.GetString()!;

            // Get the resource service principal to look up permission IDs
            var resourceSp = await _graphApiService.LookupServicePrincipalByAppIdAsync(tenantId, resourceAppId, ct, permissionGrantAuthScopes);
            if (string.IsNullOrEmpty(resourceSp))
            {
                _logger.LogError("Resource service principal not found for appId {ResourceAppId}", resourceAppId);
                return false;
            }

            // Get the resource SP's published permissions
            var resourceSpDoc = await _graphApiService.GraphGetAsync(tenantId, $"/v1.0/servicePrincipals/{resourceSp}?$select=oauth2PermissionScopes,appRoles", ct, permissionGrantAuthScopes);
            if (resourceSpDoc == null)
            {
                _logger.LogError("Failed to retrieve resource service principal {ResourceSp}", resourceSp);
                return false;
            }

            // Map scope names to permission IDs
            var permissionIds = new List<string>();
            var permissionType = isDelegated ? "Scope" : "Role";
            var permissionsProperty = isDelegated ? "oauth2PermissionScopes" : "appRoles";

            if (resourceSpDoc.RootElement.TryGetProperty(permissionsProperty, out var permissions))
            {
                foreach (var scope in scopes)
                {
                    var found = false;
                    foreach (var permission in permissions.EnumerateArray())
                    {
                        if (permission.TryGetProperty("value", out var valueElement) && 
                            valueElement.GetString()?.Equals(scope, StringComparison.OrdinalIgnoreCase) == true &&
                            permission.TryGetProperty("id", out var idElement))
                        {
                            var idValue = idElement.GetString();
                            if (!string.IsNullOrEmpty(idValue))
                            {
                                permissionIds.Add(idValue);
                                found = true;
                                break;
                            }
                        }
                    }

                    if (!found)
                    {
                        _logger.LogWarning("Permission scope '{Scope}' not found on resource {ResourceAppId}", scope, resourceAppId);
                    }
                }
            }

            if (permissionIds.Count == 0)
            {
                _logger.LogWarning("No valid permission IDs found for scopes: {Scopes}", string.Join(", ", scopes));
                return false;
            }

            // Get existing requiredResourceAccess
            var existingResourceAccess = new List<object>();
            if (app.TryGetProperty("requiredResourceAccess", out var existingArray))
            {
                existingResourceAccess = JsonSerializer.Deserialize<List<object>>(existingArray.GetRawText()) ?? new List<object>();
            }

            // Check if resource already exists in requiredResourceAccess
            var resourceAccessList = existingResourceAccess
                .Select(x => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(x)))
                .ToList();

            var existingResource = resourceAccessList.FirstOrDefault(x => 
                x != null && 
                x.TryGetValue("resourceAppId", out var resId) && 
                resId.GetString() == resourceAppId);

            if (existingResource != null)
            {
                // Add to existing resource access
                var existingAccess = existingResource.TryGetValue("resourceAccess", out var accessElement)
                    ? JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(accessElement.GetRawText()) ?? new List<Dictionary<string, JsonElement>>()
                    : new List<Dictionary<string, JsonElement>>();

                var existingIds = new HashSet<string>(
                    existingAccess
                        .Where(x => x.TryGetValue("id", out var idEl))
                        .Select(x => x["id"].GetString()!)
                );

                foreach (var permId in permissionIds)
                {
                    if (!existingIds.Contains(permId))
                    {
                        existingAccess.Add(new Dictionary<string, JsonElement>
                        {
                            ["id"] = JsonDocument.Parse($"\"{permId}\"").RootElement,
                            ["type"] = JsonDocument.Parse($"\"{permissionType}\"").RootElement
                        });
                    }
                }

                existingResource["resourceAccess"] = JsonDocument.Parse(JsonSerializer.Serialize(existingAccess)).RootElement;
            }
            else
            {
                // Add new resource access entry
                var newResourceAccess = new Dictionary<string, object>
                {
                    ["resourceAppId"] = resourceAppId,
                    ["resourceAccess"] = permissionIds.Select(id => new Dictionary<string, string>
                    {
                        ["id"] = id,
                        ["type"] = permissionType
                    }).ToList()
                };

                resourceAccessList.Add(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(newResourceAccess))!);
            }

            // Update the application with PATCH
            var patchPayload = new
            {
                requiredResourceAccess = resourceAccessList
            };

            var updated = await _graphApiService.GraphPatchAsync(tenantId, $"/v1.0/applications/{objectId}", patchPayload, ct, permissionGrantAuthScopes);
            if (updated)
            {
                _logger.LogInformation("Successfully added required resource access for {ResourceAppId} to application {AppId}", resourceAppId, appId);
            }

            return updated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add required resource access: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Get password credentials (client secrets) for an application.
    /// Note: This only returns metadata (hint, displayName, expiration), not the actual secret values.
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication</param>
    /// <param name="applicationObjectId">The application object ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of password credential metadata</returns>
    public async Task<List<PasswordCredentialInfo>> GetPasswordCredentialsAsync(
        string tenantId,
        string applicationObjectId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Retrieving password credentials for application: {ObjectId}", applicationObjectId);

            var doc = await _graphApiService.GraphGetAsync(
                tenantId,
                $"/v1.0/applications/{applicationObjectId}",
                cancellationToken);

            var credentials = new List<PasswordCredentialInfo>();

            if (doc != null && doc.RootElement.TryGetProperty("passwordCredentials", out var credsArray))
            {
                foreach (var cred in credsArray.EnumerateArray())
                {
                    var displayName = cred.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                    var hint = cred.TryGetProperty("hint", out var h) ? h.GetString() : null;
                    var keyId = cred.TryGetProperty("keyId", out var kid) ? kid.GetString() : null;
                    var endDateTime = cred.TryGetProperty("endDateTime", out var ed) ? ed.GetDateTime() : (DateTime?)null;

                    credentials.Add(new PasswordCredentialInfo
                    {
                        DisplayName = displayName,
                        Hint = hint,
                        KeyId = keyId,
                        EndDateTime = endDateTime
                    });
                }
            }

            return credentials;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve password credentials for application: {ObjectId}", applicationObjectId);
            return new List<PasswordCredentialInfo>();
        }
    }

    private async Task<string> ResolveBlueprintObjectIdAsync(
        string tenantId,
        string blueprintAppId,
        CancellationToken ct = default,
        IEnumerable<string>? authScopes = null)
    {
        authScopes ??= AuthenticationConstants.AgentBlueprintAuthScopes;

        // First try direct access to inheritable permissions endpoint
        var getPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintAppId}/inheritablePermissions";
        var existingDoc = await _graphApiService.GraphGetAsync(tenantId, getPath, ct, authScopes);

        if (existingDoc != null)
        {
            // Direct access worked, blueprintAppId is already an object ID
            return blueprintAppId;
        }

        // Attempt to resolve as appId -> application object id
        var apps = await _graphApiService.GraphGetAsync(tenantId, $"/v1.0/applications?$filter=appId eq '{blueprintAppId}'&$select=id", ct, authScopes);
        if (apps != null && apps.RootElement.TryGetProperty("value", out var arr) && arr.GetArrayLength() > 0)
        {
            var appObj = arr[0];
            if (appObj.TryGetProperty("id", out var idEl))
            {
                var resolvedId = idEl.GetString();
                if (!string.IsNullOrEmpty(resolvedId))
                {
                    return resolvedId;
                }
            }
        }

        // Fallback to original ID if resolution fails
        return blueprintAppId;
    }

    private static List<string> ParseInheritableScopesFromJson(JsonElement entry)
    {
        var scopesList = new List<string>();
        
        if (entry.TryGetProperty("inheritableScopes", out var inheritable) &&
            inheritable.TryGetProperty("scopes", out var scopesEl) && 
            scopesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in scopesEl.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String))
            {
                var raw = s.GetString() ?? string.Empty;
                // Some entries may contain space-separated tokens; split defensively
                foreach (var tok in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    scopesList.Add(tok);
                }
            }
        }
        
        return scopesList;
    }
}
