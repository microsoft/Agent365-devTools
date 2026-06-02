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
/// Outcome of an admin-consent polling attempt.
/// Distinguishes verified grants from assumed completion so callers do not
/// mutate persisted consent state on the basis of an unverified observation.
/// </summary>
public enum ConsentPollResult
{
    /// <summary>
    /// An oauth2PermissionGrant for the client SP was observed in Graph. Safe to mark
    /// consent as granted in persisted state.
    /// </summary>
    Verified,

    /// <summary>
    /// The timeout elapsed without detecting a grant. The CLI did NOT observe the grant
    /// directly. Callers must NOT update persisted consent state on this outcome and must
    /// keep the consent URL visible so the user can verify manually (for example via
    /// 'a365 query-entra inheritance').
    /// </summary>
    AssumedComplete,

    /// <summary>
    /// Polling completed without observing a grant and without a canary fallback. Consent was
    /// not detected.
    /// </summary>
    NotDetected
}

/// <summary>
/// Helper methods for admin consent flows that use az cli to poll Graph resources.
/// Kept intentionally small and focused so it can be reused across commands/runners.
/// </summary>
public static class AdminConsentHelper
{
    /// <summary>
    /// Optional test-only override. When set to <c>true</c>, the admin-consent helpers
    /// short-circuit immediately without performing any Graph or az-cli calls, returning the
    /// "success" sentinel appropriate for each overload's return type:
    /// <list type="bullet">
    ///   <item><c>PollAdminConsentAsync(CommandExecutor, ...)</c> returns <c>true</c>.</item>
    ///   <item><c>PollAdminConsentAsync(GraphApiService, ...)</c> returns <see cref="ConsentPollResult.Verified"/>.</item>
    ///   <item><c>CheckConsentExistsAsync(GraphApiService, ...)</c> returns <c>true</c>.</item>
    ///   <item><c>CheckConsentExistsAsync(CommandExecutor, ...)</c> returns <c>true</c>.</item>
    /// </list>
    /// This prevents unit tests that exercise the admin-consent path from polling Graph for
    /// the full timeout (180s) and from launching a real browser via <c>BrowserHelper.TryOpenUrl</c>.
    /// AsyncLocal scoping prevents leaks across parallel xUnit test classes; tests that set this
    /// must still reset it in a finally/Dispose block. Not intended for production code.
    /// </summary>
    internal static bool BypassConsentChecksForTests
    {
        get => _bypassConsentChecks.Value;
        set => _bypassConsentChecks.Value = value;
    }

    private static readonly AsyncLocal<bool> _bypassConsentChecks = new();

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
        if (BypassConsentChecksForTests)
            return true;

        var start = DateTime.UtcNow;
        string? spId = null;
        int lastProgressReportSeconds = 0;

        logger.LogInformation(
            "   Sign in and Accept the permission(s). If the tab shows an error after Accept, consent likely succeeded — the CLI will still detect it (timeout: {TimeoutSeconds}s).",
            timeoutSeconds);

        try
        {
            while ((DateTime.UtcNow - start).TotalSeconds < timeoutSeconds && !ct.IsCancellationRequested)
            {
                var elapsedSeconds = (int)(DateTime.UtcNow - start).TotalSeconds;
                if (elapsedSeconds > 0 && elapsedSeconds - lastProgressReportSeconds >= 30)
                {
                    lastProgressReportSeconds = elapsedSeconds;
                    logger.LogInformation(
                        "Still waiting for admin consent... ({ElapsedSeconds}s / {TimeoutSeconds}s).",
                        elapsedSeconds, timeoutSeconds);
                }

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

                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            }

            logger.LogDebug(
                "Admin consent was not detected within {TimeoutSeconds}s. The browser flow may not have completed, or 'az login' may be signed into a different tenant than the consent target. Verify with 'az account show'.",
                timeoutSeconds);
            return false;
        }
        catch (OperationCanceledException)
        {
            // Propagate so Ctrl+C aborts setup cleanly via AllSubcommand's OCE handler,
            // instead of falling into the az rest fallback prompt with a stale "permission(s)?"
            // question. Mirrors the Graph overload below.
            logger.LogDebug("Polling for admin consent was cancelled for app {AppId} ({Scope}).", appId, scopeDescriptor);
            throw;
        }
    }

    /// <summary>
    /// Polls Microsoft Graph directly (via MSAL token) to detect an oauth2 permission grant.
    /// Preferred over the az-cli-based overload for cross-platform compatibility.
    /// Caller must supply the blueprint service principal object ID directly to avoid
    /// a servicePrincipals $filter query that requires ConsistencyLevel: eventual.
    /// Uses the same token path as <c>query-entra blueprint-scopes</c>, which allows
    /// Global Administrators to read oauth2PermissionGrants via their directory admin access.
    /// </summary>
    /// <returns>
    /// <see cref="ConsentPollResult.Verified"/> when a grant was observed in Graph.
    /// <see cref="ConsentPollResult.AssumedComplete"/> when the timeout elapsed without detecting
    /// a grant — the grant was NOT directly observed. Callers must NOT update persisted consent
    /// state on this outcome and must keep the consent URL visible so the user can verify manually.
    /// <see cref="ConsentPollResult.NotDetected"/> when the blueprint SP id is not available.
    /// Throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled —
    /// callers must let the exception propagate so user Ctrl+C is honored consistently with the
    /// rest of the setup flow.
    /// </returns>
    public static async Task<ConsentPollResult> PollAdminConsentAsync(
        Services.GraphApiService graphApiService,
        ILogger logger,
        string tenantId,
        string clientSpId,
        string scopeDescriptor,
        int timeoutSeconds,
        int intervalSeconds,
        CancellationToken ct,
        IEnumerable<string>? permScopes = null)
    {
        if (BypassConsentChecksForTests)
        {
            return ConsentPollResult.Verified;
        }

        if (string.IsNullOrWhiteSpace(clientSpId))
        {
            logger.LogDebug("Blueprint service principal ID not available, cannot poll for consent.");
            return ConsentPollResult.NotDetected;
        }

        logger.LogInformation(
            "   Sign in and Accept the permission(s). If the tab shows an error after Accept, consent likely succeeded — the CLI will still detect it (timeout: {TimeoutSeconds}s).",
            timeoutSeconds);

        var start = DateTime.UtcNow;
        int lastProgressReportSeconds = 0;

        try
        {
            while ((DateTime.UtcNow - start).TotalSeconds < timeoutSeconds && !ct.IsCancellationRequested)
            {
                var elapsedSeconds = (int)(DateTime.UtcNow - start).TotalSeconds;
                if (elapsedSeconds > 0 && elapsedSeconds - lastProgressReportSeconds >= 30)
                {
                    lastProgressReportSeconds = elapsedSeconds;
                    logger.LogInformation(
                        "Still waiting for admin consent... ({ElapsedSeconds}s / {TimeoutSeconds}s).",
                        elapsedSeconds, timeoutSeconds);
                }

                // Use the caller's full permission scopes so the request uses the broad delegated
                // token (which includes Application.Read.All and other admin-level scopes) rather
                // than the default User.Read-only token, which is denied on oauth2PermissionGrants.
                using var grantsDoc = await graphApiService.GraphGetAsync(
                    tenantId,
                    $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{clientSpId}'",
                    ct,
                    permScopes);

                if (grantsDoc != null &&
                    grantsDoc.RootElement.TryGetProperty("value", out var arr) &&
                    arr.GetArrayLength() > 0)
                {
                    logger.LogInformation("Consent granted ({ScopeDescriptor}).", scopeDescriptor);
                    return ConsentPollResult.Verified;
                }

                logger.LogDebug("No consent grants found for blueprint SP {ClientSpId} yet.", clientSpId);

                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            }

            logger.LogDebug(
                "Admin consent was not detected within {TimeoutSeconds}s. The browser flow may not have completed, or 'az login' may be signed into a different tenant than the consent target. Verify with 'az account show'.",
                timeoutSeconds);
            return ConsentPollResult.AssumedComplete;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Polling for admin consent was cancelled for SP {ClientSpId} ({Scope}).", clientSpId, scopeDescriptor);
            throw;
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
    /// <param name="scopes">OAuth2 scopes for Graph token acquisition. Should include DelegatedPermissionGrant.ReadWrite.All
    /// to read oauth2PermissionGrants. When null, falls back to Azure CLI token which may lack required permissions.</param>
    /// <param name="consentType">Optional consent type filter (e.g. "AllPrincipals" or "Principal").
    /// When specified, only grants with this consent type are considered. When null, any consent type matches.</param>
    /// <returns>True if all required scopes are already granted, false otherwise</returns>
    public static async Task<bool> CheckConsentExistsAsync(
        Services.GraphApiService graphApiService,
        string tenantId,
        string clientSpId,
        string resourceSpId,
        System.Collections.Generic.IEnumerable<string> requiredScopes,
        ILogger logger,
        CancellationToken ct,
        System.Collections.Generic.IEnumerable<string>? scopes = null,
        string? consentType = null)
    {
        if (BypassConsentChecksForTests)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(clientSpId) || string.IsNullOrWhiteSpace(resourceSpId))
        {
            logger.LogDebug("Cannot check consent: missing service principal IDs (Client: {ClientSpId}, Resource: {ResourceSpId})",
                clientSpId ?? "(null)", resourceSpId ?? "(null)");
            return false;
        }

        try
        {
            // Query existing grants — pass scopes so EnsureGraphHeadersAsync uses the MSAL token provider
            // (which has DelegatedPermissionGrant.ReadWrite.All) instead of falling back to the Azure CLI token.
            var filter = $"clientId eq '{clientSpId}' and resourceId eq '{resourceSpId}'";
            if (!string.IsNullOrWhiteSpace(consentType))
            {
                filter += $" and consentType eq '{consentType}'";
            }

            using var grantDoc = await graphApiService.GraphGetAsync(
                tenantId,
                $"/v1.0/oauth2PermissionGrants?$filter={filter}",
                ct,
                scopes);

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

    /// <summary>
    /// Checks whether an oauth2PermissionGrant already covers all required scopes between the
    /// blueprint SP (looked up by appId) and the resource SP (looked up by appId), using the
    /// Azure CLI's token via <c>az rest</c>.
    /// </summary>
    /// <remarks>
    /// This overload exists because the CLI's MSAL Graph token no longer carries
    /// <c>DelegatedPermissionGrant.Read.All</c> (removed in PR #409), so the
    /// <see cref="CheckConsentExistsAsync(Services.GraphApiService, string, string, string, System.Collections.Generic.IEnumerable{string}, ILogger, CancellationToken, System.Collections.Generic.IEnumerable{string}, string)"/>
    /// path returns a 403 and reports "no consent" even when consent actually exists. The Azure
    /// CLI token carries the directory roles a Global Administrator already holds, which lets it
    /// read <c>/v1.0/oauth2PermissionGrants</c>.
    /// </remarks>
    /// <returns>
    /// <c>true</c> only when a grant is found AND its <c>scope</c> string contains every entry in
    /// <paramref name="requiredScopes"/>. <c>false</c> on any error, when no grant exists, or when
    /// the existing grant is missing any required scope (the caller should then open the browser
    /// to re-consent).
    /// </returns>
    public static async Task<bool> CheckConsentExistsAsync(
        CommandExecutor executor,
        ILogger logger,
        string blueprintAppId,
        string resourceAppId,
        System.Collections.Generic.IEnumerable<string> requiredScopes,
        CancellationToken ct,
        string? consentType = null,
        string? blueprintSpObjectId = null,
        string? resourceSpObjectId = null)
    {
        if (BypassConsentChecksForTests)
            return true;

        // Validate both appIds are well-formed GUIDs before interpolating them into the
        // az rest URL filter. Both values originate from config (blueprintAppId from
        // a365.config.json, resourceAppId from custom-permission specs); strict validation
        // here gives defense-in-depth against an attacker-controlled config widening into
        // a malformed Graph query or URL injection.
        if (!Guid.TryParse(blueprintAppId, out _) || !Guid.TryParse(resourceAppId, out _))
        {
            logger.LogDebug("Cannot check consent: invalid GUID format (Blueprint: {BlueprintAppId}, Resource: {ResourceAppId})",
                blueprintAppId ?? "(null)", resourceAppId ?? "(null)");
            return false;
        }

        try
        {
            // Skip SP lookups when the caller already resolved them in Phase 1 — each az rest
            // call costs ~1.7s due to az's Python startup. The orchestrator passes pre-resolved
            // IDs to cut 4-resource setup pre-check from ~21s to ~7s.
            var blueprintSpId = blueprintSpObjectId
                ?? await LookupSpObjectIdByAppIdAsync(executor, blueprintAppId, ct);
            if (blueprintSpId == null)
            {
                logger.LogDebug("Blueprint SP not found for appId {BlueprintAppId} via az rest", blueprintAppId);
                return false;
            }

            var resourceSpId = resourceSpObjectId
                ?? await LookupSpObjectIdByAppIdAsync(executor, resourceAppId, ct);
            if (resourceSpId == null)
            {
                logger.LogDebug("Resource SP not found for appId {ResourceAppId} via az rest", resourceAppId);
                return false;
            }

            var filter = $"clientId eq '{blueprintSpId}' and resourceId eq '{resourceSpId}'";
            if (!string.IsNullOrWhiteSpace(consentType))
                filter += $" and consentType eq '{consentType}'";

            var grantsResult = await executor.ExecuteAsync("az",
                $"rest --method GET --url \"https://graph.microsoft.com/v1.0/oauth2PermissionGrants?$filter={Uri.EscapeDataString(filter)}\"",
                captureOutput: true, suppressErrorLogging: true, cancellationToken: ct);

            if (!grantsResult.Success)
            {
                logger.LogDebug("az rest failed reading oauth2PermissionGrants: {Stderr}", grantsResult.StandardError);
                return false;
            }

            using var grantDoc = JsonDocument.Parse(grantsResult.StandardOutput);
            if (!grantDoc.RootElement.TryGetProperty("value", out var grants) || grants.GetArrayLength() == 0)
            {
                logger.LogDebug("No oauth2PermissionGrants found between blueprint {BlueprintAppId} and resource {ResourceAppId}",
                    blueprintAppId, resourceAppId);
                return false;
            }

            // Aggregate scopes across ALL matching grant rows. Entra sometimes splits scopes across
            // multiple rows for the same (client, resource) pair when consent was given incrementally.
            var grantedScopeSet = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var grant in grants.EnumerateArray())
            {
                if (grant.TryGetProperty("scope", out var scopesEl))
                {
                    var s = scopesEl.GetString() ?? "";
                    foreach (var sc in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        grantedScopeSet.Add(sc);
                }
            }

            var requiredScopeSet = new System.Collections.Generic.HashSet<string>(requiredScopes, StringComparer.OrdinalIgnoreCase);
            bool allScopesPresent = requiredScopeSet.IsSubsetOf(grantedScopeSet);

            if (allScopesPresent)
                logger.LogDebug("All required scopes already granted via az rest: {Scopes}", string.Join(", ", requiredScopes));
            else
                logger.LogDebug("Missing scopes in existing grant (az rest): {MissingScopes}",
                    string.Join(", ", requiredScopeSet.Except(grantedScopeSet)));

            return allScopesPresent;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error checking existing consent via az rest between blueprint {BlueprintAppId} and resource {ResourceAppId}",
                blueprintAppId, resourceAppId);
            return false;
        }
    }

    private static async Task<string?> LookupSpObjectIdByAppIdAsync(
        CommandExecutor executor, string appId, CancellationToken ct)
    {
        var spResult = await executor.ExecuteAsync("az",
            $"rest --method GET --url \"https://graph.microsoft.com/v1.0/servicePrincipals?$filter=appId eq '{appId}'&$select=id\"",
            captureOutput: true, suppressErrorLogging: true, cancellationToken: ct);

        if (!spResult.Success)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(spResult.StandardOutput);
            var value = doc.RootElement.GetProperty("value");
            return value.GetArrayLength() > 0 ? value[0].GetProperty("id").GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
