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
    /// The timeout elapsed without detecting a grant, or the user pressed Enter to skip
    /// verification. The CLI did NOT observe the grant directly. Callers must NOT update
    /// persisted consent state on this outcome and must keep the consent URL visible so the
    /// user can verify manually (for example via 'a365 query-entra inheritance').
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
    /// Optional test-only override. When set to <c>true</c>, both overloads of
    /// <c>PollAdminConsentAsync</c> and <see cref="CheckConsentExistsAsync"/>
    /// short-circuit and return <c>true</c> immediately without performing any Graph or az-cli calls.
    /// This prevents unit tests that exercise the admin-consent path from polling Graph for
    /// the full timeout (180s) and from launching a real browser via <c>BrowserHelper.TryOpenUrl</c>.
    /// AsyncLocal scoping prevents leaks across parallel xUnit test classes; tests that set this
    /// must still reset it in a finally/Dispose block. Not intended for production code.
    /// </summary>
    public static bool BypassConsentChecksForTests
    {
        get => _bypassConsentChecks.Value;
        set => _bypassConsentChecks.Value = value;
    }

    private static readonly AsyncLocal<bool> _bypassConsentChecks = new();

    /// <summary>
    /// Non-blocking check for a buffered Enter keypress. Used by the consent polling loop
    /// to let the operator skip waiting and continue without blocking on stdin.
    /// Safe when stdin is redirected (e.g. test/CI): returns false and any other buffered keys
    /// are consumed harmlessly. Returns true only when an Enter key was pressed and consumed.
    /// </summary>
    private static bool TryConsumeEnterKey()
    {
        try
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Console.KeyAvailable throws when stdin is redirected. Treat as "no Enter".
        }
        return false;
    }

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
            "Waiting for admin consent to be granted. Open the URL above in a browser and complete the consent flow. The CLI will continue automatically (timeout: {TimeoutSeconds}s).",
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

                // Delay between polls. If cancellation is requested this will throw OperationCanceledException,
                // which we catch below and treat as a graceful cancellation resulting in 'false'.
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            }

            logger.LogWarning(
                "Admin consent was not detected within {TimeoutSeconds}s. Continuing — you can re-run this command after granting consent.",
                timeoutSeconds);
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
    /// a grant, or when the user pressed Enter to skip verification — the grant was NOT directly
    /// observed. Callers must NOT update persisted consent state on this outcome and must keep
    /// the consent URL visible so the user can verify manually.
    /// <see cref="ConsentPollResult.NotDetected"/> when the blueprint SP id is not available,
    /// or when polling was cancelled.
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
            "Waiting for admin consent to be granted. Complete the consent flow in the browser. The CLI will continue automatically (timeout: {TimeoutSeconds}s).",
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
                        "Still waiting for admin consent... ({ElapsedSeconds}s / {TimeoutSeconds}s). Press Enter to skip verification and continue.",
                        elapsedSeconds, timeoutSeconds);
                }

                if (TryConsumeEnterKey())
                {
                    logger.LogInformation("Continuing. Run 'a365 query-entra inheritance' later to confirm permissions if needed.");
                    return ConsentPollResult.AssumedComplete;
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

                // Short-poll loop so an Enter keypress is detected within 250 ms rather than
                // waiting a full intervalSeconds before the next Graph check.
                var pollEnd = DateTime.UtcNow.AddSeconds(intervalSeconds);
                while (DateTime.UtcNow < pollEnd && !ct.IsCancellationRequested)
                {
                    if (TryConsumeEnterKey())
                    {
                        logger.LogInformation("Continuing. Run 'a365 query-entra inheritance' later to confirm permissions if needed.");
                        return ConsentPollResult.AssumedComplete;
                    }
                    await Task.Delay(250, ct);
                }
            }

            logger.LogWarning(
                "Admin consent was not detected within {TimeoutSeconds}s. Continuing — run 'a365 query-entra inheritance' later to verify.",
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
}
