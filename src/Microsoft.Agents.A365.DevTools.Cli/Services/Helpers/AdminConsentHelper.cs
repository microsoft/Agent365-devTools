// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

/// <summary>
/// Helper methods for admin consent flows and verification.
/// Kept intentionally small and focused so it can be reused across commands/runners.
/// </summary>
public static class AdminConsentHelper
{
    /// <summary>
    /// Polls Microsoft Graph API for admin consent by checking for existence of oauth2PermissionGrants
    /// </summary>
    public static async Task<bool> PollAdminConsentAsync(
        Services.GraphApiService graphApiService,
        string tenantId,
        ILogger logger,
        string appId,
        string scopeDescriptor,
        int timeoutSeconds,
        int intervalSeconds,
        CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        string? spId = null;

        // Use delegated scopes so this polling doesn't rely on Azure CLI.
        var scopes = AuthenticationConstants.PermissionGrantAuthScopes;

        try
        {
            while ((DateTime.UtcNow - start).TotalSeconds < timeoutSeconds && !ct.IsCancellationRequested)
            {
                if (spId == null)
                {
                    // Find SP by appId
                    spId = await graphApiService.LookupServicePrincipalByAppIdAsync(
                        tenantId,
                        appId,
                        ct,
                        scopes);

                    // Note: SP propagation can lag; keep polling.
                }

                if (!string.IsNullOrWhiteSpace(spId))
                {
                    // Check if ANY oauth2PermissionGrants exist for that clientId
                    var grantsDoc = await graphApiService.GraphGetAsync(
                        tenantId,
                        $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{spId}'",
                        ct,
                        scopes);

                    if (grantsDoc != null &&
                        grantsDoc.RootElement.TryGetProperty("value", out var arr) &&
                        arr.GetArrayLength() > 0)
                    {
                        logger.LogInformation("Consent granted ({ScopeDescriptor}).", scopeDescriptor);
                        return true;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Polling for admin consent was cancelled or timed out for app {AppId} ({Scope}).", appId, scopeDescriptor);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Polling for admin consent failed for app {AppId} ({Scope}).", appId, scopeDescriptor);
            return false;
        }
    }

    /// <summary>
    /// Checks if admin consent already exists for specified scopes between client and resource service principals.
    /// Returns true if ALL required scopes are present in existing oauth2PermissionGrants.
    /// </summary>
    /// <param name="graphApiService">Graph API service for querying grants</param>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="clientSpId">Client service principal object ID</param>
    /// <param name="resourceSpId">Resource service principal object ID</param>
    /// <param name="requiredScopes">List of required scope names (case-insensitive)</param>
    /// <param name="logger">Logger for diagnostics</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if all required scopes are already granted, false otherwise</returns>
    public static async Task<bool> CheckConsentExistsAsync(
        Services.GraphApiService graphApiService,
        string tenantId,
        string clientSpId,
        string resourceSpId,
        System.Collections.Generic.IEnumerable<string> requiredScopes,
        ILogger logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientSpId) || string.IsNullOrWhiteSpace(resourceSpId))
        {
            logger.LogDebug("Cannot check consent: missing service principal IDs (Client: {ClientSpId}, Resource: {ResourceSpId})",
                clientSpId ?? "(null)", resourceSpId ?? "(null)");
            return false;
        }

        try
        {
            // Query existing grants
            var grantDoc = await graphApiService.GraphGetAsync(
                tenantId,
                $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{clientSpId}' and resourceId eq '{resourceSpId}'",
                ct,
                AuthenticationConstants.PermissionGrantAuthScopes);

            if (grantDoc == null || !grantDoc.RootElement.TryGetProperty("value", out var grants) || grants.GetArrayLength() == 0)
            {
                logger.LogDebug("No oauth2PermissionGrants found between client {ClientSpId} and resource {ResourceSpId}",
                    clientSpId, resourceSpId);
                return false;
            }

            // Check first grant for scopes
            var grant = grants[0];
            if (!grant.TryGetProperty("scope", out var grantedScopes))
            {
                logger.LogDebug("oauth2PermissionGrant missing 'scope' property");
                return false;
            }

            var scopesString = grantedScopes.GetString() ?? "";
            var grantedScopeSet = new System.Collections.Generic.HashSet<string>(
                scopesString.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

            var requiredScopeSet = new System.Collections.Generic.HashSet<string>(requiredScopes, StringComparer.OrdinalIgnoreCase);

            // Check if all required scopes are already granted
            bool allScopesPresent = requiredScopeSet.IsSubsetOf(grantedScopeSet);

            if (allScopesPresent)
            {
                logger.LogDebug("All required scopes already granted: {Scopes}", string.Join(", ", requiredScopes));
            }
            else
            {
                var missing = requiredScopeSet.Except(grantedScopeSet);
                logger.LogDebug("Missing scopes in existing grant: {MissingScopes}", string.Join(", ", missing));
            }

            return allScopesPresent;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error checking existing consent between {ClientSpId} and {ResourceSpId}",
                clientSpId, resourceSpId);
            return false;
        }
    }
}
