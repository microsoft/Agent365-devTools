// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

/// <summary>
/// Helper methods for admin consent flows that use az cli to poll Graph resources.
/// Kept intentionally small and focused so it can be reused across commands/runners.
/// </summary>
public static class AdminConsentHelper
{
    /// <summary>
    /// Polls Azure AD/Graph (via az rest) to detect an oauth2 permission grant for the provided appId.
    /// Mirrors the behavior previously implemented in A365SetupRunner.PollAdminConsentAsync.
    /// </summary>
    public static async Task<bool> PollAdminConsentAsync(
        CommandExecutor executor,
        ILogger logger,
        string appId,
        string scopeDescriptor,
        int timeoutSeconds,
        int intervalSeconds,
        CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        string? spId = null;

        try
        {
            while ((DateTime.UtcNow - start).TotalSeconds < timeoutSeconds && !ct.IsCancellationRequested)
            {
                if (spId == null)
                {
                    var spResult = await executor.ExecuteAsync("az",
                        $"rest --method GET --url \"https://graph.microsoft.com/v1.0/servicePrincipals?$filter=appId eq '{appId}'\"",
                        captureOutput: true, suppressErrorLogging: true, cancellationToken: ct);

                    if (spResult.Success)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(spResult.StandardOutput);
                            var value = doc.RootElement.GetProperty("value");
                            if (value.GetArrayLength() > 0)
                            {
                                spId = value[0].GetProperty("id").GetString();
                            }
                        }
                        catch { }
                    }
                }

                if (spId != null)
                {
                    var grants = await executor.ExecuteAsync("az",
                        $"rest --method GET --url \"https://graph.microsoft.com/v1.0/oauth2PermissionGrants?$filter=clientId eq '{spId}'\"",
                        captureOutput: true, suppressErrorLogging: true, cancellationToken: ct);

                    if (grants.Success)
                    {
                        try
                        {
                            using var gdoc = JsonDocument.Parse(grants.StandardOutput);
                            var arr = gdoc.RootElement.GetProperty("value");
                            if (arr.GetArrayLength() > 0)
                            {
                                logger.LogInformation("Consent granted ({ScopeDescriptor}).", scopeDescriptor);
                                return true;
                            }
                        }
                        catch { }
                    }
                }

                // Delay between polls. If cancellation is requested this will throw OperationCanceledException,
                // which we catch below and treat as a graceful cancellation resulting in 'false'.
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            // Treat cancellation as a graceful timeout/no-consent scenario
            logger.LogDebug("Polling for admin consent was cancelled or timed out for app {AppId} ({Scope}).", appId, scopeDescriptor);
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
                ct);

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
