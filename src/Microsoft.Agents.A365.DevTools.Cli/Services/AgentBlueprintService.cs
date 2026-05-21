// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
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
    private readonly RetryHelper _retryHelper;

    public AgentBlueprintService(ILogger<AgentBlueprintService> logger, GraphApiService graphApiService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _graphApiService = graphApiService ?? throw new ArgumentNullException(nameof(graphApiService));
        // RetryHelper with 5 attempts, 2 second base delay for S2S app role assignment transient errors (404 due to SP replication)
        _retryHelper = new RetryHelper(logger, maxRetries: 5, baseDelaySeconds: 2);
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
            var doc = await _graphApiService.GraphGetAsync(tenantId, $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintId}/inheritablePermissions", cancellationToken);

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
    /// Agent Blueprint deletion requires a delegated permission scope.
    /// This scope is not available through Azure CLI tokens, so we use interactive authentication via
    /// the token provider (same authentication method used during blueprint creation in the setup command).
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication</param>
    /// <param name="blueprintId">The blueprint application ID (object ID or app ID)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deletion succeeded or resource not found; false otherwise</returns>
    public virtual async Task<bool> DeleteAgentBlueprintAsync(
        string tenantId,
        string blueprintId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Deleting agent blueprint application: {BlueprintId}", blueprintId);

            var requiredScopes = new[] { AuthenticationConstants.AgentIdentityBlueprintReadWriteAllScope };

            _logger.LogInformation("Acquiring access token with AgentIdentityBlueprint.ReadWrite.All scope...");
            _logger.LogInformation("An authentication dialog will appear to complete sign-in.");

            var deletePath = $"/beta/applications/{blueprintId}/microsoft.graph.agentIdentityBlueprint";

            // Use GraphDeleteAsync with the special scopes required for blueprint operations
            var success = await _graphApiService.GraphDeleteAsync(
                tenantId,
                deletePath,
                cancellationToken,
                treatNotFoundAsSuccess: true,
                scopes: requiredScopes);

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
    public virtual async Task<bool> DeleteAgentIdentityAsync(
        string tenantId,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Deleting agent identity application: {ApplicationId}", applicationId);

            // Agent Identity deletion requires AgentIdentity.DeleteRestore.All — NOT the blueprint scope.
            // DELETE /beta/servicePrincipals/{id} for agent identities uses the AgentIdentity permission family.
            var requiredScopes = new[] { AuthenticationConstants.AgentIdentityDeleteRestoreAllScope };

            _logger.LogInformation("Acquiring access token with AgentIdentity.DeleteRestore.All scope...");
            _logger.LogInformation("An authentication dialog will appear to complete sign-in.");

            var deletePath = $"/beta/servicePrincipals/{applicationId}";

            // Use GraphDeleteAsync with the correct scope for agent identity deletion
            return await _graphApiService.GraphDeleteAsync(
                tenantId,
                deletePath,
                cancellationToken,
                treatNotFoundAsSuccess: true,
                scopes: requiredScopes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception deleting agent identity application");
            return false;
        }
    }

    /// <summary>
    /// Queries Entra ID for all agent identity service principals linked to the given blueprint.
    /// Returns an empty list when no instances are found.
    /// Throws if the query fails so callers can distinguish a true "no instances" result from a query error.
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication.</param>
    /// <param name="blueprintId">The blueprint application ID or object ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of agent instances linked to the blueprint.</returns>
    /// <exception cref="Exception">Thrown when the Graph query fails.</exception>
    public virtual async Task<IReadOnlyList<AgentInstanceInfo>> GetAgentInstancesForBlueprintAsync(
        string tenantId,
        string blueprintId,
        CancellationToken cancellationToken = default)
    {
        var spScopes = new[] { AuthenticationConstants.AgentIdentityReadAllScope };
        var encodedId = Uri.EscapeDataString(blueprintId);

        // Fetch agent identity SPs and agent users for this blueprint sequentially to avoid races on shared HTTP headers
        var spItems = await FetchAllPagesAsync(
            tenantId,
            $"/beta/servicePrincipals/microsoft.graph.agentIdentity?$filter=agentIdentityBlueprintId eq '{encodedId}'&$select=id,displayName",
            spScopes,
            cancellationToken);

        // Agent user query requires AgentIdUser.ReadWrite.All, which is intentionally absent from
        // RequiredClientAppPermissions until create-instance is re-enabled. This means agent user
        // cleanup is also disabled — no agent users exist while create-instance is off, so this is safe.
        List<JsonElement> userItems;
        if (AuthenticationConstants.RequiredClientAppPermissions.Contains(AuthenticationConstants.AgentIdUserReadWriteAllScope))
        {
            var userScopes = new[] { AuthenticationConstants.AgentIdUserReadWriteAllScope };
            userItems = await FetchAllPagesAsync(
                tenantId,
                $"/beta/users/microsoft.graph.agentUser?$filter=agentIdentityBlueprintId eq '{encodedId}'&$select=id,identityParentId",
                userScopes,
                cancellationToken);
        }
        else
        {
            _logger.LogDebug("Skipping agent user query — AgentIdUser.ReadWrite.All not in required permissions (create-instance not enabled)");
            userItems = new List<JsonElement>();
        }
        // Build lookup: identityParentId (SP object ID) -> user object ID
        var userBySpId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in userItems)
        {
            var parentId = user.TryGetProperty("identityParentId", out var p) ? p.GetString() : null;
            var userId = user.TryGetProperty("id", out var uid) ? uid.GetString() : null;
            if (!string.IsNullOrWhiteSpace(parentId) && !string.IsNullOrWhiteSpace(userId))
            {
                userBySpId[parentId] = userId;
            }
        }

        // Correlate SPs with their agent users
        var results = new List<AgentInstanceInfo>();
        foreach (var item in spItems)
        {
            var spId = item.TryGetProperty("id", out var id) ? id.GetString() : null;
            if (string.IsNullOrWhiteSpace(spId))
            {
                continue;
            }

            var displayName = item.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
            userBySpId.TryGetValue(spId, out var agentUserId);

            results.Add(new AgentInstanceInfo
            {
                IdentitySpId = spId,
                DisplayName = displayName,
                AgentUserId = string.IsNullOrWhiteSpace(agentUserId) ? null : agentUserId
            });
        }

        return results;
    }

    /// <summary>
    /// Returns the service principal ID of an existing agent identity for the given blueprint
    /// whose display name matches <paramref name="displayName"/>, or null if none is found.
    /// Wraps <see cref="GetAgentInstancesForBlueprintAsync"/>; exceptions are caught and logged
    /// non-fatally so callers can fall through to creation.
    /// </summary>
    public virtual async Task<string?> FindExistingAgentIdentityAsync(
        string tenantId,
        string blueprintId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var instances = await GetAgentInstancesForBlueprintAsync(tenantId, blueprintId, cancellationToken);
            var match = instances.FirstOrDefault(i =>
                string.Equals(i.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
            // IdentitySpId is the Graph SP object ID — the same value CreateAgentIdentityDelegatedAsync
            // returns and stores in AgenticAppId (both are /beta/servicePrincipals/{id} object IDs).
            return match?.IdentitySpId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not look up existing agent identities for blueprint {BlueprintId} (non-fatal): {Message}",
                blueprintId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Fetches all pages of a Graph API collection, following @odata.nextLink pagination.
    /// Returns the deserialized "value" array items from all pages.
    /// </summary>
    private async Task<List<JsonElement>> FetchAllPagesAsync(
        string tenantId,
        string initialPath,
        string[] requiredScopes,
        CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        string? nextPageUrl = null;
        var isFirstPage = true;

        do
        {
            var requestPath = isFirstPage ? initialPath : nextPageUrl!;
            isFirstPage = false;

            using var doc = await _graphApiService.GraphGetAsync(
                tenantId,
                requestPath,
                cancellationToken,
                requiredScopes);

            if (doc is null)
            {
                _logger.LogError(
                    "Failed to retrieve data from Microsoft Graph for tenant '{TenantId}' and request path '{RequestPath}'. " +
                    "GraphGetAsync returned null, which likely indicates a non-success response or authentication issue.",
                    tenantId,
                    requestPath);

                throw new InvalidOperationException(
                    "Failed to retrieve data from Microsoft Graph. See logs for details about the underlying request failure.");
            }

            if (doc.RootElement.TryGetProperty("value", out var valueArray) &&
                valueArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in valueArray.EnumerateArray())
                {
                    items.Add(item.Clone());
                }
            }

            nextPageUrl = doc.RootElement.TryGetProperty("@odata.nextLink", out var nextLink)
                ? nextLink.GetString()
                : null;
        }
        while (!string.IsNullOrEmpty(nextPageUrl));

        return items;
    }

    /// <summary>
    /// Deletes an agentic user from Entra ID using the agentUsers beta endpoint.
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication.</param>
    /// <param name="agentUserId">The object ID of the agentic user to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if deletion succeeded or user was not found; false on error.</returns>
    public virtual async Task<bool> DeleteAgentUserAsync(
        string tenantId,
        string agentUserId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Deleting agentic user: {AgentUserId}", agentUserId);

            var requiredScopes = new[] { AuthenticationConstants.AgentIdentityBlueprintReadWriteAllScope };
            var deletePath = $"/beta/agentUsers/{agentUserId}";

            var success = await _graphApiService.GraphDeleteAsync(
                tenantId,
                deletePath,
                cancellationToken,
                treatNotFoundAsSuccess: true,
                scopes: requiredScopes);

            if (success)
                _logger.LogInformation("Agentic user deleted successfully");
            else
                _logger.LogError("Failed to delete agentic user: {AgentUserId}", agentUserId);

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception deleting agentic user: {AgentUserId}", agentUserId);
            return false;
        }
    }

    /// <summary>
    /// Configures inheritable permissions on an agent blueprint for the given resource using the
    /// allAllowed (wildcard) pattern on both inheritableScopes and inheritableRoles. The CLI no
    /// longer writes the deprecated enumerated form; whatever scopes and roles are granted to the
    /// blueprint SP for this resource become inheritable by agent identities created from the
    /// blueprint. Schema: https://learn.microsoft.com/en-us/entra/agent-id/configure-inheritable-permissions-blueprints
    /// </summary>
    /// <param name="scopes">Informational only — surfaces in log lines so operators can correlate
    /// the call with the scope set granted to the blueprint SP. Not used to construct the request
    /// body, which is a fixed allAllowed shape.</param>
    public virtual async Task<(bool ok, bool alreadyExists, string? error)> SetInheritablePermissionsAsync(
        string tenantId,
        string blueprintId,
        string resourceAppId,
        IEnumerable<string> scopes,
        IEnumerable<string>? requiredScopes = null,
        CancellationToken ct = default)
    {
        string? blueprintObjectId = null;
        try
        {
            blueprintObjectId = await ResolveBlueprintObjectIdAsync(tenantId, blueprintId, ct, requiredScopes);

            var getPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions";
            var existingDoc = await _graphApiService.GraphGetAsync(tenantId, getPath, ct, requiredScopes);

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
                var (scopesAllAllowed, rolesAllAllowed) = IsAllAllowedEntry(existingEntry.Value);
                if (scopesAllAllowed && rolesAllAllowed)
                {
                    _logger.LogDebug("Inheritable permissions already allAllowed for blueprint {Blueprint} resource {Resource}", blueprintObjectId, resourceAppId);
                    return (ok: true, alreadyExists: true, error: null);
                }

                var patchPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions/{resourceAppId}";
                var patchPayload = new
                {
                    inheritableScopes = new AllAllowedScopes(),
                    inheritableRoles = new AllAllowedRoles()
                };

                bool patched;
                try
                {
                    patched = await _graphApiService.GraphPatchAsync(tenantId, patchPath, patchPayload, ct, requiredScopes);
                }
                catch (Exception patchEx)
                {
                    if (patchEx is OperationCanceledException && ct.IsCancellationRequested) throw;
                    _logger.LogError(patchEx, "Exception during PATCH of inheritable permissions for blueprint {Blueprint} resource {Resource}", blueprintObjectId, resourceAppId);
                    return (ok: false, alreadyExists: false, error: patchEx.Message);
                }

                if (!patched)
                {
                    _logger.LogWarning("PATCH request to update inheritable permissions failed for blueprint {Blueprint} resource {Resource}", blueprintObjectId, resourceAppId);
                    return (ok: false, alreadyExists: false, error: $"Graph PATCH returned false for blueprint {blueprintObjectId} resource {resourceAppId}");
                }

                _logger.LogDebug("Patched inheritable permissions to allAllowed for blueprint {Blueprint} resource {Resource} (granted scopes context: [{Scopes}])",
                    blueprintObjectId, resourceAppId, string.Join(' ', scopes ?? Enumerable.Empty<string>()));
                return (ok: true, alreadyExists: false, error: null);
            }

            var postPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions";
            var postPayload = new
            {
                resourceAppId = resourceAppId,
                inheritableScopes = new AllAllowedScopes(),
                inheritableRoles = new AllAllowedRoles()
            };

            var createdResp = await _graphApiService.GraphPostWithResponseAsync(tenantId, postPath, postPayload, ct, requiredScopes);
            if (!createdResp.IsSuccess)
            {
                var err = string.IsNullOrWhiteSpace(createdResp.Body)
                    ? $"HTTP {createdResp.StatusCode} {createdResp.ReasonPhrase}"
                    : createdResp.Body;
                // 403 means insufficient role (Agent ID Administrator required) — expected for
                // non-admin users; logged at debug to avoid noise. Other failures are warnings.
                if ((int)createdResp.StatusCode == 403)
                    _logger.LogDebug("Inheritable permissions not set (insufficient role): {Status} Body: {Body}", createdResp.StatusCode, createdResp.Body);
                else
                    _logger.LogWarning("Failed to create inheritable permissions: {Status} {Reason} Body: {Body}", createdResp.StatusCode, createdResp.ReasonPhrase, createdResp.Body);
                return (ok: false, alreadyExists: false, error: err);
            }

            _logger.LogDebug("Created allAllowed inheritable permissions for blueprint {Blueprint} resource {Resource} (granted scopes context: [{Scopes}])",
                blueprintObjectId, resourceAppId, string.Join(' ', scopes ?? Enumerable.Empty<string>()));
            return (ok: true, alreadyExists: false, error: null);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            _logger.LogError(ex, "Failed to set inheritable permissions for blueprint {Blueprint} resource {Resource}: {Error}", blueprintObjectId ?? blueprintId, resourceAppId, ex.Message);
            return (ok: false, alreadyExists: false, error: ex.Message);
        }
    }

    /// <summary>
    /// Returns all current inheritable permission entries for the blueprint as structured data.
    /// Each entry contains the resource app ID and its list of scopes.
    /// Returns an empty list if none are configured or if retrieval fails.
    /// </summary>
    public virtual async Task<List<(string ResourceAppId, bool ScopesAllAllowed, bool RolesAllAllowed)>> ListInheritablePermissionsAsync(
        string tenantId,
        string blueprintId,
        IEnumerable<string>? requiredScopes = null,
        CancellationToken ct = default)
    {
        var results = new List<(string ResourceAppId, bool ScopesAllAllowed, bool RolesAllAllowed)>();
        try
        {
            var blueprintObjectId = await ResolveBlueprintObjectIdAsync(tenantId, blueprintId, ct, requiredScopes);
            var getPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions";
            var doc = await _graphApiService.GraphGetAsync(tenantId, getPath, ct, requiredScopes);
            if (doc != null && doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    var resourceAppId = item.TryGetProperty("resourceAppId", out var r) ? r.GetString() : null;
                    if (string.IsNullOrWhiteSpace(resourceAppId)) continue;

                    var (scopesAllAllowed, rolesAllAllowed) = IsAllAllowedEntry(item);
                    results.Add((resourceAppId, scopesAllAllowed, rolesAllAllowed));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to list inheritable permissions: {Error}", ex.Message);
        }

        return results;
    }

    /// <summary>
    /// Bulk-fetches the permissions that have been granted on the blueprint's service principal for
    /// each of the supplied resource app IDs. Returns a per-resource (DelegatedScopes, AppRoleNames)
    /// tuple. Used by the `query-entra inheritance` command so operators can see exactly which scopes
    /// and roles will be inherited by agent identities under kind=allAllowed — the wildcard inherits
    /// whatever is granted on the blueprint SP, so this is the authoritative answer to "what will the
    /// agent's token actually carry?"
    /// </summary>
    /// <param name="blueprintAppId">Application ID of the blueprint (will be resolved to its SP object ID).</param>
    /// <param name="resourceAppIds">Resource app IDs of interest (typically the entries from inheritablePermissions).</param>
    /// <returns>
    /// Dictionary keyed by resource app ID. Resources with no grants on the blueprint SP are
    /// present in the dictionary with empty arrays. Resources whose SP cannot be resolved are
    /// omitted (a debug log is emitted).
    /// </returns>
    public virtual async Task<Dictionary<string, (string[] DelegatedScopes, string[] AppRoleNames)>> GetBlueprintSpGrantsAsync(
        string tenantId,
        string blueprintAppId,
        IEnumerable<string> resourceAppIds,
        IEnumerable<string>? requiredScopes = null,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, (string[] DelegatedScopes, string[] AppRoleNames)>(StringComparer.OrdinalIgnoreCase);
        var appIds = resourceAppIds?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                     ?? new List<string>();
        if (appIds.Count == 0) return result;

        var blueprintSpObjectId = await _graphApiService.LookupServicePrincipalByAppIdAsync(tenantId, blueprintAppId, ct, requiredScopes);
        if (string.IsNullOrWhiteSpace(blueprintSpObjectId))
        {
            _logger.LogWarning("Blueprint service principal not found for app ID {BlueprintAppId} — cannot enumerate granted permissions.", blueprintAppId);
            return result;
        }

        // One bulk fetch each — by resource SP object ID, not by app ID, so we'll need a resolution table.
        var delegatedByResourceSpId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var allGrants = await _graphApiService.GetOauth2PermissionGrantsAsync(tenantId, blueprintSpObjectId, ct);
        foreach (var (resourceSpId, scope, _) in allGrants)
        {
            if (string.IsNullOrWhiteSpace(resourceSpId)) continue;
            if (!delegatedByResourceSpId.TryGetValue(resourceSpId, out var list))
            {
                list = new List<string>();
                delegatedByResourceSpId[resourceSpId] = list;
            }
            // Scopes come as a space-delimited single string per grant — split into individual scopes.
            foreach (var tok in (scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                list.Add(tok);
        }

        var appRoleIdsByResourceSpId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using (var assignmentsDoc = await _graphApiService.GraphGetAsync(
            tenantId, $"/v1.0/servicePrincipals/{blueprintSpObjectId}/appRoleAssignments", ct, scopes: requiredScopes))
        {
            if (assignmentsDoc != null &&
                assignmentsDoc.RootElement.TryGetProperty("value", out var assignmentsArr) &&
                assignmentsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var assignment in assignmentsArr.EnumerateArray())
                {
                    var resId = assignment.TryGetProperty("resourceId", out var r) ? r.GetString() : null;
                    var roleId = assignment.TryGetProperty("appRoleId", out var ar) ? ar.GetString() : null;
                    if (string.IsNullOrWhiteSpace(resId) || string.IsNullOrWhiteSpace(roleId)) continue;
                    if (!appRoleIdsByResourceSpId.TryGetValue(resId, out var list))
                    {
                        list = new List<string>();
                        appRoleIdsByResourceSpId[resId] = list;
                    }
                    list.Add(roleId);
                }
            }
        }

        foreach (var resourceAppId in appIds)
        {
            var resourceSpId = await _graphApiService.LookupServicePrincipalByAppIdAsync(tenantId, resourceAppId, ct, requiredScopes);
            if (string.IsNullOrWhiteSpace(resourceSpId))
            {
                _logger.LogDebug("Resource SP not found for app ID {ResourceAppId} — granted permissions cannot be enumerated.", resourceAppId);
                continue;
            }

            var delegatedScopes = delegatedByResourceSpId.TryGetValue(resourceSpId, out var ds)
                ? ds.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();

            string[] appRoleNames = Array.Empty<string>();
            if (appRoleIdsByResourceSpId.TryGetValue(resourceSpId, out var roleIds) && roleIds.Count > 0)
            {
                // Resolve role IDs -> names by reading the resource SP's appRoles array. Unknown
                // role IDs fall back to a "<role-id>" placeholder so the operator can still see them.
                var roleIdSet = new HashSet<string>(roleIds, StringComparer.OrdinalIgnoreCase);
                var nameById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using var resourceSpDoc = await _graphApiService.GraphGetAsync(
                    tenantId, $"/v1.0/servicePrincipals/{resourceSpId}?$select=appRoles", ct, scopes: requiredScopes);
                if (resourceSpDoc != null &&
                    resourceSpDoc.RootElement.TryGetProperty("appRoles", out var rolesEl) &&
                    rolesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var role in rolesEl.EnumerateArray())
                    {
                        var id = role.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        var name = role.TryGetProperty("value", out var valEl) ? valEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                            nameById[id] = name;
                    }
                }
                appRoleNames = roleIdSet
                    .Select(id => nameById.TryGetValue(id, out var n) ? n : $"<{id}>")
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            result[resourceAppId] = (delegatedScopes, appRoleNames);
        }

        return result;
    }

    /// <summary>
    /// Removes inheritable permissions for a specific resource app ID from the blueprint.
    /// Returns true if the entry was deleted or did not exist, false on failure.
    /// </summary>
    public virtual async Task<bool> RemoveInheritablePermissionsAsync(
        string tenantId,
        string blueprintId,
        string resourceAppId,
        IEnumerable<string>? requiredScopes = null,
        CancellationToken ct = default)
    {
        try
        {
            var blueprintObjectId = await ResolveBlueprintObjectIdAsync(tenantId, blueprintId, ct, requiredScopes);
            var deletePath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions/{resourceAppId}";
            return await _graphApiService.GraphDeleteAsync(tenantId, deletePath, ct, treatNotFoundAsSuccess: true, scopes: requiredScopes);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to remove inheritable permissions for {ResourceAppId}: {Error}", resourceAppId, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Verifies that inheritable permissions are correctly configured for a resource. Returns whether
    /// the entry exists and whether both inheritableScopes and inheritableRoles are at kind=allAllowed.
    /// </summary>
    public virtual async Task<(bool exists, bool scopesAllAllowed, bool rolesAllAllowed, string? error)> VerifyInheritablePermissionsAsync(
        string tenantId,
        string blueprintId,
        string resourceAppId,
        CancellationToken ct = default,
        IEnumerable<string>? requiredScopes = null)
    {
        try
        {
            var blueprintObjectId = await ResolveBlueprintObjectIdAsync(tenantId, blueprintId, ct, requiredScopes);

            var getPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions";
            var existingDoc = await _graphApiService.GraphGetAsync(tenantId, getPath, ct, requiredScopes);

            if (existingDoc == null)
            {
                return (exists: false, scopesAllAllowed: false, rolesAllAllowed: false, error: "Failed to retrieve inheritable permissions");
            }

            if (existingDoc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    var rId = item.TryGetProperty("resourceAppId", out var r) ? r.GetString() : null;
                    if (string.Equals(rId, resourceAppId, StringComparison.OrdinalIgnoreCase))
                    {
                        var (scopesAllAllowed, rolesAllAllowed) = IsAllAllowedEntry(item);
                        return (exists: true, scopesAllAllowed, rolesAllAllowed, error: null);
                    }
                }
            }

            return (exists: false, scopesAllAllowed: false, rolesAllAllowed: false, error: null);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to verify inheritable permissions: {Error}", ex.Message);
            return (exists: false, scopesAllAllowed: false, rolesAllAllowed: false, error: ex.Message);
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
            ct);

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

                    var ok = await _graphApiService.GraphDeleteAsync(tenantId, $"/v1.0/oauth2PermissionGrants/{id}", ct);
                    if (!ok)
                    {
                        _logger.LogError("Failed to delete existing oauth2PermissionGrant {Id} for client {ClientId} and resource {ResourceId}. " +
                                       "This may indicate insufficient permissions or the grant is protected. " +
                                       "The signed-in account must be an Application Administrator or Global Administrator to delete oauth2PermissionGrants.",
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

        var grantResponse = await _graphApiService.GraphPostWithResponseAsync(tenantId, "/v1.0/oauth2PermissionGrants", payload, ct);
        if (!grantResponse.IsSuccess)
        {
            if (grantResponse.StatusCode == 403)
                _logger.LogWarning("Creating oauth2PermissionGrant requires the Global Administrator role (status 403). An admin must grant consent for these permissions.");
            else
                _logger.LogError("Failed to create oauth2PermissionGrant: {Status} {Reason}", grantResponse.StatusCode, grantResponse.ReasonPhrase);
        }
        return grantResponse.IsSuccess;
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
            ct);

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
            var grantResponse = await _graphApiService.GraphPostWithResponseAsync(tenantId, "/v1.0/oauth2PermissionGrants", payload, ct);
            if (!grantResponse.IsSuccess)
            {
                if (grantResponse.StatusCode == 403)
                    _logger.LogWarning("Creating oauth2PermissionGrant requires the Global Administrator role (status 403). An admin must grant consent for these permissions.");
                else
                    _logger.LogError("Failed to create oauth2PermissionGrant: {Status} {Reason}", grantResponse.StatusCode, grantResponse.ReasonPhrase);
            }
            return grantResponse.IsSuccess;
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

        return await _graphApiService.GraphPatchAsync(tenantId, $"/v1.0/oauth2PermissionGrants/{id}", new { scope = merged }, ct);
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
        CancellationToken ct = default,
        IEnumerable<string>? requiredScopes = null)
    {
        try
        {
            // Get the application object by appId
            var appsDoc = await _graphApiService.GraphGetAsync(tenantId, $"/v1.0/applications?$filter=appId eq '{appId}'&$select=id,requiredResourceAccess", ct, scopes: requiredScopes);
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
            var resourceSp = await _graphApiService.LookupServicePrincipalByAppIdAsync(tenantId, resourceAppId, ct, requiredScopes);
            if (string.IsNullOrEmpty(resourceSp))
            {
                _logger.LogError("Resource service principal not found for appId {ResourceAppId}", resourceAppId);
                return false;
            }

            // Get the resource SP's published permissions
            var resourceSpDoc = await _graphApiService.GraphGetAsync(tenantId, $"/v1.0/servicePrincipals/{resourceSp}?$select=oauth2PermissionScopes,appRoles", ct, scopes: requiredScopes);
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

            var updated = await _graphApiService.GraphPatchAsync(tenantId, $"/v1.0/applications/{objectId}", patchPayload, ct, scopes: requiredScopes);
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
        IEnumerable<string>? requiredScopes = null)
    {
        // First try direct access to inheritable permissions endpoint
        var getPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintAppId}/inheritablePermissions";
        var existingDoc = await _graphApiService.GraphGetAsync(tenantId, getPath, ct, requiredScopes);

        if (existingDoc != null)
        {
            // Direct access worked, blueprintAppId is already an object ID
            return blueprintAppId;
        }

        // Attempt to resolve as appId -> application object id
        var apps = await _graphApiService.GraphGetAsync(tenantId, $"/v1.0/applications?$filter=appId eq '{blueprintAppId}'&$select=id", ct, requiredScopes);
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

    /// <summary>
    /// Grants application role assignments (appRoleAssignments) on the blueprint's service principal
    /// for each named app role on the given resource. This enables S2S (service-to-service) access
    /// where the blueprint identity calls the resource using a client-credentials token with no user
    /// context. Idempotent: existing assignments for the same role are skipped.
    /// Requires Global Administrator.
    /// </summary>
    /// <param name="tenantId">Tenant ID.</param>
    /// <param name="blueprintSpObjectId">Object ID of the blueprint's service principal.</param>
    /// <param name="resourceAppId">Application ID of the resource (e.g. Observability API).</param>
    /// <param name="appRoleNames">Names of the app roles to assign (e.g. "Agent365.Observability.OtelWrite").</param>
    /// <param name="requiredScopes">Graph scopes required to perform the operation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when all assignments succeeded or already existed; false if any failed.</returns>
    public virtual async Task<bool> GrantAppRoleAssignmentAsync(
        string tenantId,
        string blueprintSpObjectId,
        string resourceAppId,
        IEnumerable<string> appRoleNames,
        IEnumerable<string>? requiredScopes = null,
        CancellationToken ct = default)
    {
        // De-dup upfront: duplicate names map to the same role ID and would cause a redundant POST.
        var roleNames = appRoleNames?
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        if (roleNames.Count == 0) return true;

        try
        {
            // Resolve the resource service principal.
            var resourceSpId = await _graphApiService.LookupServicePrincipalByAppIdAsync(
                tenantId, resourceAppId, ct, requiredScopes);
            if (string.IsNullOrWhiteSpace(resourceSpId))
            {
                _logger.LogWarning("Resource SP not found for app ID {ResourceAppId} — S2S app role assignment skipped.", resourceAppId);
                return false;
            }

            // Fetch the resource SP's app roles to map names -> IDs.
            using var resourceSpDoc = await _graphApiService.GraphGetAsync(
                tenantId, $"/v1.0/servicePrincipals/{resourceSpId}?$select=appRoles", ct,
                scopes: requiredScopes);
            if (resourceSpDoc == null)
            {
                _logger.LogError("Failed to retrieve app roles for resource SP {ResourceSpId}.", resourceSpId);
                return false;
            }

            // Build a name -> id map from the resource SP's appRoles array.
            var roleIdByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (resourceSpDoc.RootElement.TryGetProperty("appRoles", out var appRolesEl) &&
                appRolesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var role in appRolesEl.EnumerateArray())
                {
                    if (role.TryGetProperty("value", out var valEl) &&
                        role.TryGetProperty("id", out var idEl))
                    {
                        var name = valEl.GetString();
                        var id = idEl.GetString();
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                            roleIdByName[name] = id;
                    }
                }
            }

            // Fetch existing assignments on the blueprint SP to avoid duplicates.
            var existingRoleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var existingDoc = await _graphApiService.GraphGetAsync(
                tenantId,
                $"/v1.0/servicePrincipals/{blueprintSpObjectId}/appRoleAssignments",
                ct, scopes: requiredScopes);
            if (existingDoc != null &&
                existingDoc.RootElement.TryGetProperty("value", out var existingArr) &&
                existingArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var assignment in existingArr.EnumerateArray())
                {
                    if (assignment.TryGetProperty("resourceId", out var resId) &&
                        resId.GetString()?.Equals(resourceSpId, StringComparison.OrdinalIgnoreCase) == true &&
                        assignment.TryGetProperty("appRoleId", out var roleIdEl))
                    {
                        var id = roleIdEl.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) existingRoleIds.Add(id);
                    }
                }
            }

            var allOk = true;
            foreach (var roleName in roleNames)
            {
                if (!roleIdByName.TryGetValue(roleName, out var appRoleId))
                {
                    _logger.LogWarning("App role '{RoleName}' not found on resource {ResourceAppId} — assignment skipped.", roleName, resourceAppId);
                    allOk = false;
                    continue;
                }

                if (existingRoleIds.Contains(appRoleId))
                {
                    _logger.LogDebug("App role '{RoleName}' already assigned on blueprint SP {BpSpId}.", roleName, blueprintSpObjectId);
                    continue;
                }

                var payload = new
                {
                    principalId = blueprintSpObjectId,
                    resourceId = resourceSpId,
                    appRoleId = appRoleId
                };

                // Retry on 404 (transient: service principal not yet fully replicated in directory after creation).
                // maxRetries / baseDelaySeconds come from the _retryHelper instance constructed in the ctor.
                var resp = await _retryHelper.ExecuteWithRetryAsync(
                    async retryCt => await _graphApiService.GraphPostWithResponseAsync(
                        tenantId,
                        $"/v1.0/servicePrincipals/{blueprintSpObjectId}/appRoleAssignments",
                        payload,
                        retryCt,
                        requiredScopes),
                    result => (int)result.StatusCode == 404,
                    cancellationToken: ct);

                if (resp.IsSuccess)
                {
                    _logger.LogDebug("App role '{RoleName}' assigned to blueprint SP {BpSpId}.", roleName, blueprintSpObjectId);
                    existingRoleIds.Add(appRoleId); // prevent duplicate POST if same ID appears again
                }
                else
                {
                    _logger.LogDebug(
                        "Failed to assign app role '{RoleName}': HTTP {Status} {Reason} — {Body}",
                        roleName, (int)resp.StatusCode, resp.ReasonPhrase, resp.Body);
                    allOk = false;
                }
            }

            return allOk;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception granting app role assignments on {ResourceAppId}: {Message}", resourceAppId, ex.Message);
            return false;
        }
    }

    // Reads kind on both inheritableScopes and inheritableRoles of an inheritablePermissions entry
    // returned by GET. Both must be "allAllowed" for the entry to be considered fully migrated to
    // the new wildcard form. The legacy enumerated form leaves both flags false here.
    private static (bool scopesAllAllowed, bool rolesAllAllowed) IsAllAllowedEntry(JsonElement entry)
    {
        return (ReadKindEqualsAllAllowed(entry, "inheritableScopes"),
                ReadKindEqualsAllAllowed(entry, "inheritableRoles"));
    }

    private static bool ReadKindEqualsAllAllowed(JsonElement entry, string propertyName)
    {
        return entry.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Object
            && prop.TryGetProperty("kind", out var kindEl)
            && kindEl.ValueKind == JsonValueKind.String
            && string.Equals(kindEl.GetString(), "allAllowed", StringComparison.OrdinalIgnoreCase);
    }
}
