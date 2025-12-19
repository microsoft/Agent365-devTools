// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Validates that a client app exists and has the required permissions for a365 CLI operations.
/// </summary>
public sealed class ClientAppValidator : IClientAppValidator
{
    private readonly ILogger<ClientAppValidator> _logger;
    private readonly CommandExecutor _executor;

    private const string GraphApiBaseUrl = "https://graph.microsoft.com/v1.0";
    private const string GraphTokenResource = "https://graph.microsoft.com";

    public ClientAppValidator(ILogger<ClientAppValidator> logger, CommandExecutor executor)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <summary>
    /// Ensures the client app exists and has required permissions granted.
    /// Throws ClientAppValidationException if validation fails.
    /// Does not log - caller is responsible for error presentation.
    /// </summary>
    /// <param name="clientAppId">The client app ID to validate</param>
    /// <param name="tenantId">The tenant ID where the app should exist</param>
    /// <param name="ct">Cancellation token</param>
    /// <exception cref="ClientAppValidationException">Thrown when validation fails</exception>
    public async Task EnsureValidClientAppAsync(
        string clientAppId,
        string tenantId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Step 1: Validate GUID format
        if (!Guid.TryParse(clientAppId, out _))
        {
            throw ClientAppValidationException.ValidationFailed(
                $"clientAppId must be a valid GUID format (received: {clientAppId})",
                new List<string>(),
                clientAppId);
        }

        if (!Guid.TryParse(tenantId, out _))
        {
            throw ClientAppValidationException.ValidationFailed(
                $"tenantId must be a valid GUID format (received: {tenantId})",
                new List<string>(),
                clientAppId);
        }

        try
        {
            // Step 2: Acquire Graph token
            var graphToken = await AcquireGraphTokenAsync(ct);
            if (string.IsNullOrWhiteSpace(graphToken))
            {
                throw ClientAppValidationException.ValidationFailed(
                    "Failed to acquire Microsoft Graph access token",
                    new List<string> { "Ensure you are logged in with 'az login'" },
                    clientAppId);
            }

            // Step 3: Verify app exists
            var appInfo = await GetClientAppInfoAsync(clientAppId, graphToken, ct);
            if (appInfo == null)
            {
                throw ClientAppValidationException.AppNotFound(clientAppId, tenantId);
            }

            _logger.LogDebug("Found client app: {DisplayName} ({AppId})", appInfo.DisplayName, clientAppId);

            // Step 4: Validate permissions in manifest
            var missingPermissions = await ValidatePermissionsConfiguredAsync(appInfo, graphToken, ct);
            
            // Step 4.5: For any unresolvable permissions (beta APIs), check oauth2PermissionGrants as fallback
            if (missingPermissions.Count > 0)
            {
                var consentedPermissions = await GetConsentedPermissionsAsync(clientAppId, graphToken, ct);
                // Remove permissions that have been consented even if not in app registration
                missingPermissions.RemoveAll(p => consentedPermissions.Contains(p, StringComparer.OrdinalIgnoreCase));
                
                if (consentedPermissions.Count > 0)
                {
                    _logger.LogDebug("Found {Count} consented permissions via oauth2PermissionGrants (including beta APIs)", consentedPermissions.Count);
                }
            }
            
            if (missingPermissions.Count > 0)
            {
                throw ClientAppValidationException.MissingPermissions(clientAppId, missingPermissions);
            }

            // Step 5: Verify admin consent
            if (!await ValidateAdminConsentAsync(clientAppId, graphToken, ct))
            {
                throw ClientAppValidationException.MissingAdminConsent(clientAppId);
            }

            // Step 6: Verify and fix redirect URIs
            await EnsureRedirectUrisAsync(clientAppId, graphToken, ct);

            _logger.LogDebug("Client app validation successful for {ClientAppId}", clientAppId);
        }
        catch (ClientAppValidationException)
        {
            // Re-throw validation exceptions as-is
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "JSON parsing error during validation");
            throw ClientAppValidationException.ValidationFailed(
                "Failed to parse Microsoft Graph response",
                new List<string> { ex.Message },
                clientAppId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unexpected error during validation");
            throw ClientAppValidationException.ValidationFailed(
                "Unexpected error during client app validation",
                new List<string> { ex.Message },
                clientAppId);
        }
    }

    /// <summary>
    /// Ensures the client app has required redirect URIs configured for Microsoft Graph PowerShell SDK.
    /// Automatically adds missing redirect URIs if needed (self-healing).
    /// </summary>
    /// <param name="clientAppId">The client app ID</param>
    /// <param name="graphToken">Microsoft Graph access token</param>
    /// <param name="ct">Cancellation token</param>
    public async Task EnsureRedirectUrisAsync(
        string clientAppId,
        string graphToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphToken);

        try
        {
            _logger.LogDebug("Checking redirect URIs for client app {ClientAppId}", clientAppId);

            // Get current redirect URIs
            var appCheckResult = await _executor.ExecuteAsync(
                "az",
                $"rest --method GET --url \"{GraphApiBaseUrl}/applications?$filter=appId eq '{clientAppId}'&$select=id,publicClient\" --headers \"Authorization=Bearer {graphToken}\"",
                cancellationToken: ct);

            if (!appCheckResult.Success)
            {
                _logger.LogWarning("Could not verify redirect URIs: {Error}", appCheckResult.StandardError);
                return;
            }

            var response = JsonNode.Parse(appCheckResult.StandardOutput);
            var apps = response?["value"]?.AsArray();

            if (apps == null || apps.Count == 0)
            {
                _logger.LogWarning("Client app not found when checking redirect URIs");
                return;
            }

            var app = apps[0]!.AsObject();
            var objectId = app["id"]?.GetValue<string>();
            var publicClient = app["publicClient"]?.AsObject();
            var currentRedirectUris = publicClient?["redirectUris"]?.AsArray()
                ?.Select(uri => uri?.GetValue<string>())
                .Where(uri => !string.IsNullOrWhiteSpace(uri))
                .Select(uri => uri!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Check if required URIs are present
            var missingUris = AuthenticationConstants.RequiredRedirectUris
                .Where(uri => !currentRedirectUris.Contains(uri))
                .ToList();

            if (missingUris.Count == 0)
            {
                _logger.LogDebug("All required redirect URIs are configured");
                return;
            }

            // Add missing URIs
            _logger.LogInformation("Adding missing redirect URIs to client app: {MissingUris}",
                string.Join(", ", missingUris));

            var allUris = currentRedirectUris.Union(missingUris).ToList();
            var urisJson = string.Join(",", allUris.Select(uri => $"\"{uri}\""));

            var patchBody = $"{{\"publicClient\":{{\"redirectUris\":[{urisJson}]}}}}";
            // Escape the JSON body for PowerShell: replace " with ""
            var escapedBody = patchBody.Replace("\"", "\"\"");
            var patchResult = await _executor.ExecuteAsync(
                "az",
                $"rest --method PATCH --url \"{GraphApiBaseUrl}/applications/{objectId}\" --headers \"Content-Type=application/json\" \"Authorization=Bearer {graphToken}\" --body \"{escapedBody}\"",
                cancellationToken: ct);

            if (!patchResult.Success)
            {
                _logger.LogWarning("Failed to update redirect URIs: {Error}", patchResult.StandardError);
                return;
            }

            _logger.LogInformation("Successfully added redirect URIs: {AddedUris}",
                string.Join(", ", missingUris));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error ensuring redirect URIs (non-fatal)");
        }
    }

    #region Private Helper Methods

    private async Task<string?> AcquireGraphTokenAsync(CancellationToken ct)
    {
        _logger.LogDebug("Acquiring Microsoft Graph token for validation...");
        
        var tokenResult = await _executor.ExecuteAsync(
            "az",
            $"account get-access-token --resource {GraphTokenResource} --query accessToken -o tsv",
            cancellationToken: ct);

        if (!tokenResult.Success || string.IsNullOrWhiteSpace(tokenResult.StandardOutput))
        {
            _logger.LogDebug("Token acquisition failed: {Error}", tokenResult.StandardError);
            return null;
        }

        return tokenResult.StandardOutput.Trim();
    }

    private async Task<ClientAppInfo?> GetClientAppInfoAsync(string clientAppId, string graphToken, CancellationToken ct)
    {
        _logger.LogDebug("Checking if client app exists in tenant...");
        
        var appCheckResult = await _executor.ExecuteAsync(
            "az",
            $"rest --method GET --url \"{GraphApiBaseUrl}/applications?$filter=appId eq '{clientAppId}'&$select=id,appId,displayName,requiredResourceAccess\" --headers \"Authorization=Bearer {graphToken}\"",
            cancellationToken: ct);

        if (!appCheckResult.Success)
        {
            // Check for Continuous Access Evaluation (CAE) token issues
            if (appCheckResult.StandardError.Contains("TokenCreatedWithOutdatedPolicies", StringComparison.OrdinalIgnoreCase) ||
                appCheckResult.StandardError.Contains("InvalidAuthenticationToken", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Azure CLI token is stale due to Continuous Access Evaluation. Refreshing token automatically...");
                
                // Force token refresh
                var refreshResult = await _executor.ExecuteAsync(
                    "az",
                    $"account get-access-token --resource {GraphTokenResource} --query accessToken -o tsv",
                    cancellationToken: ct);
                
                if (refreshResult.Success && !string.IsNullOrWhiteSpace(refreshResult.StandardOutput))
                {
                    var freshToken = refreshResult.StandardOutput.Trim();
                    _logger.LogDebug("Token refreshed successfully, retrying...");
                    
                    // Retry with fresh token
                    var retryResult = await _executor.ExecuteAsync(
                        "az",
                        $"rest --method GET --url \"{GraphApiBaseUrl}/applications?$filter=appId eq '{clientAppId}'&$select=id,appId,displayName,requiredResourceAccess\" --headers \"Authorization=Bearer {freshToken}\"",
                        cancellationToken: ct);
                    
                    if (retryResult.Success)
                    {
                        appCheckResult = retryResult;
                    }
                    else
                    {
                        _logger.LogDebug("App query failed after token refresh: {Error}", retryResult.StandardError);
                        return null;
                    }
                }
            }
            
            if (!appCheckResult.Success)
            {
                _logger.LogDebug("App query failed: {Error}", appCheckResult.StandardError);
                return null;
            }
        }

        var appResponse = JsonNode.Parse(appCheckResult.StandardOutput);
        var apps = appResponse?["value"]?.AsArray();

        if (apps == null || apps.Count == 0)
        {
            return null;
        }

        var app = apps[0]!.AsObject();
        return new ClientAppInfo(
            app["id"]?.GetValue<string>() ?? string.Empty,
            app["displayName"]?.GetValue<string>() ?? string.Empty,
            app["requiredResourceAccess"]?.AsArray());
    }

    private async Task<List<string>> ValidatePermissionsConfiguredAsync(
        ClientAppInfo appInfo,
        string graphToken,
        CancellationToken ct)
    {
        var missingPermissions = new List<string>();

        if (appInfo.RequiredResourceAccess == null || appInfo.RequiredResourceAccess.Count == 0)
        {
            return AuthenticationConstants.RequiredClientAppPermissions.ToList();
        }

        // Find Microsoft Graph resource in required permissions
        var graphResource = appInfo.RequiredResourceAccess
            .Select(r => r?.AsObject())
            .FirstOrDefault(obj => obj?["resourceAppId"]?.GetValue<string>() == AuthenticationConstants.MicrosoftGraphResourceAppId);

        if (graphResource == null)
        {
            return AuthenticationConstants.RequiredClientAppPermissions.ToList();
        }

        var resourceAccess = graphResource["resourceAccess"]?.AsArray();
        if (resourceAccess == null || resourceAccess.Count == 0)
        {
            return AuthenticationConstants.RequiredClientAppPermissions.ToList();
        }

        // Build set of configured permission IDs
        var configuredPermissionIds = resourceAccess
            .Select(access => access?.AsObject())
            .Select(accessObj => new
            {
                PermissionId = accessObj?["id"]?.GetValue<string>(),
                PermissionType = accessObj?["type"]?.GetValue<string>()
            })
            .Where(x => x.PermissionType == "Scope" && !string.IsNullOrWhiteSpace(x.PermissionId))
            .Select(x => x.PermissionId!)
            .ToHashSet();

        // Resolve ALL permission IDs dynamically from Microsoft Graph
        // This ensures compatibility across different tenants and API versions
        var permissionNameToIdMap = await ResolvePermissionIdsAsync(graphToken, ct);

        // Check each required permission
        foreach (var permissionName in AuthenticationConstants.RequiredClientAppPermissions)
        {
            if (permissionNameToIdMap.TryGetValue(permissionName, out var permissionId))
            {
                if (!configuredPermissionIds.Contains(permissionId))
                {
                    missingPermissions.Add(permissionName);
                }
                _logger.LogDebug("Validated permission {PermissionName} (ID: {PermissionId})", permissionName, permissionId);
            }
            else
            {
                _logger.LogWarning("Could not resolve permission ID for: {PermissionName}", permissionName);
                _logger.LogWarning("This permission may be a beta API or unavailable in your tenant. Validation cannot verify its presence.");
                // Don't add to missing list - we can't verify it
            }
        }

        return missingPermissions;
    }

    /// <summary>
    /// Resolves permission names to their GUIDs by querying Microsoft Graph's published permission definitions.
    /// This approach is tenant-agnostic and works across different API versions.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolvePermissionIdsAsync(string graphToken, CancellationToken ct)
    {
        var permissionNameToIdMap = new Dictionary<string, string>();

        try
        {
            var graphSpResult = await _executor.ExecuteAsync(
                "az",
                $"rest --method GET --url \"{GraphApiBaseUrl}/servicePrincipals?$filter=appId eq '{AuthenticationConstants.MicrosoftGraphResourceAppId}'&$select=id,oauth2PermissionScopes\" --headers \"Authorization=Bearer {graphToken}\"",
                cancellationToken: ct);

            if (!graphSpResult.Success)
            {
                _logger.LogWarning("Failed to query Microsoft Graph for permission definitions");
                return permissionNameToIdMap;
            }

            var graphSpResponse = JsonNode.Parse(graphSpResult.StandardOutput);
            var graphSps = graphSpResponse?["value"]?.AsArray();

            if (graphSps == null || graphSps.Count == 0)
            {
                _logger.LogWarning("No Microsoft Graph service principal found");
                return permissionNameToIdMap;
            }

            var graphSp = graphSps[0]!.AsObject();
            var oauth2PermissionScopes = graphSp["oauth2PermissionScopes"]?.AsArray();

            if (oauth2PermissionScopes == null)
            {
                _logger.LogWarning("No permission scopes found in Microsoft Graph service principal");
                return permissionNameToIdMap;
            }

            // Build map of all available permissions (name -> GUID)
            permissionNameToIdMap = oauth2PermissionScopes
                .Select(scopeNode => scopeNode?.AsObject())
                .Select(scopeObj => new
                {
                    Value = scopeObj?["value"]?.GetValue<string>(),
                    Id = scopeObj?["id"]?.GetValue<string>()
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Value) && !string.IsNullOrWhiteSpace(x.Id))
                .ToDictionary(x => x.Value!, x => x.Id!);

            _logger.LogDebug("Retrieved {Count} permission definitions from Microsoft Graph", permissionNameToIdMap.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not retrieve Microsoft Graph permission definitions: {Message}", ex.Message);
        }

        return permissionNameToIdMap;
    }

    /// <summary>
    /// Gets the list of permissions that have been consented for the app via oauth2PermissionGrants.
    /// This is used as a fallback for beta permissions that may not be visible in the app registration's requiredResourceAccess.
    /// </summary>
    private async Task<HashSet<string>> GetConsentedPermissionsAsync(string clientAppId, string graphToken, CancellationToken ct)
    {
        var consentedPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Get service principal for the app
            var spCheckResult = await _executor.ExecuteAsync(
                "az",
                $"rest --method GET --url \"{GraphApiBaseUrl}/servicePrincipals?$filter=appId eq '{clientAppId}'&$select=id\" --headers \"Authorization=Bearer {graphToken}\"",
                cancellationToken: ct);

            if (!spCheckResult.Success)
            {
                _logger.LogDebug("Could not query service principal for consent check");
                return consentedPermissions;
            }

            var spResponse = JsonNode.Parse(spCheckResult.StandardOutput);
            var servicePrincipals = spResponse?["value"]?.AsArray();

            if (servicePrincipals == null || servicePrincipals.Count == 0)
            {
                _logger.LogDebug("Service principal not found for consent check");
                return consentedPermissions;
            }

            var sp = servicePrincipals[0]!.AsObject();
            var spObjectId = sp["id"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(spObjectId))
            {
                return consentedPermissions;
            }

            // Get oauth2PermissionGrants
            var grantsResult = await _executor.ExecuteAsync(
                "az",
                $"rest --method GET --url \"{GraphApiBaseUrl}/oauth2PermissionGrants?$filter=clientId eq '{spObjectId}'\" --headers \"Authorization=Bearer {graphToken}\"",
                cancellationToken: ct);

            if (!grantsResult.Success)
            {
                _logger.LogDebug("Could not query oauth2PermissionGrants");
                return consentedPermissions;
            }

            var grantsResponse = JsonNode.Parse(grantsResult.StandardOutput);
            var grants = grantsResponse?["value"]?.AsArray();

            if (grants == null || grants.Count == 0)
            {
                return consentedPermissions;
            }

            // Extract all scopes from grants
            foreach (var grant in grants)
            {
                var grantObj = grant?.AsObject();
                var scope = grantObj?["scope"]?.GetValue<string>();
                
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var s in scopes)
                    {
                        consentedPermissions.Add(s);
                    }
                }
            }

            _logger.LogDebug("Found {Count} consented permissions from oauth2PermissionGrants", consentedPermissions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error retrieving consented permissions: {Message}", ex.Message);
        }

        return consentedPermissions;
    }

    private async Task<bool> ValidateAdminConsentAsync(string clientAppId, string graphToken, CancellationToken ct)
    {
        _logger.LogDebug("Checking admin consent status for {ClientAppId}", clientAppId);

        // Get service principal for the app
        var spCheckResult = await _executor.ExecuteAsync(
            "az",
            $"rest --method GET --url \"{GraphApiBaseUrl}/servicePrincipals?$filter=appId eq '{clientAppId}'&$select=id,appId\" --headers \"Authorization=Bearer {graphToken}\"",
            cancellationToken: ct);

        if (!spCheckResult.Success)
        {
            _logger.LogDebug("Could not verify service principal (may not exist yet): {Error}", spCheckResult.StandardError);
            return true; // Best-effort check - will be verified during first interactive authentication
        }

        var spResponse = JsonNode.Parse(spCheckResult.StandardOutput);
        var servicePrincipals = spResponse?["value"]?.AsArray();

        if (servicePrincipals == null || servicePrincipals.Count == 0)
        {
            _logger.LogDebug("Service principal not created yet for this app");
            return true; // Best-effort check - will be verified during first interactive authentication
        }

        var sp = servicePrincipals[0]!.AsObject();
        var spObjectId = sp["id"]?.GetValue<string>();

        // Check OAuth2 permission grants
        var grantsCheckResult = await _executor.ExecuteAsync(
            "az",
            $"rest --method GET --url \"{GraphApiBaseUrl}/oauth2PermissionGrants?$filter=clientId eq '{spObjectId}'\" --headers \"Authorization=Bearer {graphToken}\"",
            cancellationToken: ct);

        if (!grantsCheckResult.Success)
        {
            _logger.LogDebug("Could not verify admin consent status: {Error}", grantsCheckResult.StandardError);
            return true; // Best-effort check
        }

        var grantsResponse = JsonNode.Parse(grantsCheckResult.StandardOutput);
        var grants = grantsResponse?["value"]?.AsArray();

        if (grants == null || grants.Count == 0)
        {
            return false; // No grants found - admin consent missing
        }

        // Check if there's a grant for Microsoft Graph with required scopes
        var hasGraphGrant = grants
            .Select(grant => grant?.AsObject())
            .Select(grantObj => grantObj?["scope"]?.GetValue<string>())
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Any(scope =>
            {
                var grantedScopes = scope!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var foundPermissions = AuthenticationConstants.RequiredClientAppPermissions
                    .Intersect(grantedScopes, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (foundPermissions.Count > 0)
                {
                    _logger.LogDebug("Admin consent verified for {Count} permissions", foundPermissions.Count);
                    return true;
                }
                return false;
            });

        return hasGraphGrant;
    }

    #endregion

    #region Helper Types

    private record ClientAppInfo(string ObjectId, string DisplayName, JsonArray? RequiredResourceAccess);

    #endregion
}
