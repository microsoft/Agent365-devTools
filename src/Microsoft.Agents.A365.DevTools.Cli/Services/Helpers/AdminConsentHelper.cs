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
/// Helper methods for admin consent flows that use az cli to poll Graph resources.
/// Kept intentionally small and focused so it can be reused across commands/runners.
/// </summary>
public static class AdminConsentHelper
{
    /// <summary>
    /// Optional test-only override. When set to <c>true</c>, both
    /// <see cref="CheckConsentExistsAsync"/> and the Graph-backed
    /// <see cref="PollAdminConsentAsync(Services.GraphApiService, ILogger, string, string, string, int, int, CancellationToken)"/>
    /// short-circuit and return <c>true</c> immediately without performing any Graph calls.
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
    /// Non-blocking check for a buffered Enter keypress. Used by the canary 403 polling loop
    /// to let an impatient user short-circuit verification without blocking on stdin.
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
                if (elapsedSeconds > 0 && elapsedSeconds - lastProgressReportSeconds >= 60)
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
    /// </summary>
    public static async Task<bool> PollAdminConsentAsync(
        Services.GraphApiService graphApiService,
        ILogger logger,
        string tenantId,
        string clientSpId,
        string scopeDescriptor,
        int timeoutSeconds,
        int intervalSeconds,
        CancellationToken ct)
    {
        if (BypassConsentChecksForTests)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(clientSpId))
        {
            logger.LogDebug("Blueprint service principal ID not available, falling back to az rest polling.");
            return false;
        }

        var start = DateTime.UtcNow;
        int lastProgressReportSeconds = 0;

        // Canary call to detect whether the caller is allowed to read oauth2PermissionGrants
        // at all. Reading the grants collection requires DelegatedPermissionGrant.Read.All in
        // the token's scp claim — a privilege the CLI's blueprint-app token does not carry.
        // When the canary returns 403, polling is impossible: we cannot observe the grant
        // landing no matter how long we wait. Degrade to an interactive prompt so the user
        // can confirm completion manually rather than burning the full 180s timeout in silence.
        try
        {
            var canary = await graphApiService.GraphGetWithResponseAsync(
                tenantId,
                $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{clientSpId}'&$top=1",
                scopes: AuthenticationConstants.RequiredPermissionGrantScopes,
                ct: ct);

            if (canary.StatusCode == 403)
            {
                logger.LogInformation(
                    "Waiting for admin consent (up to {TimeoutSeconds}s). Complete the consent screen in the browser. Press Enter when you're done, or Ctrl+C to abort.",
                    timeoutSeconds);
                logger.LogDebug(
                    "Auto-verification disabled: current token lacks DelegatedPermissionGrant.Read.All required to read oauth2PermissionGrants. Will re-check every {IntervalSeconds}s in case the read permission becomes available.",
                    intervalSeconds);

                var canaryStart = DateTime.UtcNow;
                int canaryLastProgress = 0;
                while ((DateTime.UtcNow - canaryStart).TotalSeconds < timeoutSeconds && !ct.IsCancellationRequested)
                {
                    // Non-blocking Enter check between polls so an impatient user can short-circuit.
                    if (TryConsumeEnterKey())
                    {
                        logger.LogInformation("Continuing. Run 'a365 query-entra inheritance' later to confirm permissions if needed.");
                        return true;
                    }

                    var elapsed = (int)(DateTime.UtcNow - canaryStart).TotalSeconds;
                    if (elapsed > 0 && elapsed - canaryLastProgress >= 30)
                    {
                        canaryLastProgress = elapsed;
                        logger.LogInformation(
                            "Still waiting... ({ElapsedSeconds}s / {TimeoutSeconds}s). Press Enter when consent is complete.",
                            elapsed, timeoutSeconds);
                    }

                    try
                    {
                        var retry = await graphApiService.GraphGetWithResponseAsync(
                            tenantId,
                            $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{clientSpId}'&$top=1",
                            scopes: AuthenticationConstants.RequiredPermissionGrantScopes,
                            ct: ct);

                        if (retry.IsSuccess && retry.Json is { } rdoc &&
                            rdoc.RootElement.TryGetProperty("value", out var rarr) &&
                            rarr.GetArrayLength() > 0)
                        {
                            logger.LogInformation("Consent granted ({ScopeDescriptor}).", scopeDescriptor);
                            return true;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Canary re-check failed; will retry.");
                    }

                    // Short delay loop so Enter is detected promptly without spamming Graph.
                    var pollEnd = DateTime.UtcNow.AddSeconds(intervalSeconds);
                    while (DateTime.UtcNow < pollEnd && !ct.IsCancellationRequested)
                    {
                        if (TryConsumeEnterKey())
                        {
                            logger.LogInformation("Continuing. Run 'a365 query-entra inheritance' later to confirm permissions if needed.");
                            return true;
                        }
                        await Task.Delay(250, ct);
                    }
                }

                logger.LogWarning(
                    "Admin consent not confirmed within {TimeoutSeconds}s. Continuing — run 'a365 query-entra inheritance' later to verify.",
                    timeoutSeconds);
                return true;
            }

            // If the canary succeeded and already shows a grant, short-circuit.
            if (canary.IsSuccess && canary.Json is { } cdoc &&
                cdoc.RootElement.TryGetProperty("value", out var carr) &&
                carr.GetArrayLength() > 0)
            {
                logger.LogInformation("Consent granted ({ScopeDescriptor}).", scopeDescriptor);
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Canary call failed for a non-403 reason (e.g. transient network error). Fall through
            // to the normal polling loop, which has its own retry/error handling.
            logger.LogDebug(ex, "Canary call before admin consent polling failed; continuing with regular polling.");
        }

        // Only emit the "waiting..." banner once we have decided to actually poll. Emitting it
        // before the canary contradicts the interactive prompt the canary may print on 403.
        logger.LogInformation(
            "Waiting for admin consent to be granted. Complete the consent flow in the browser. The CLI will continue automatically (timeout: {TimeoutSeconds}s).",
            timeoutSeconds);

        try
        {
            while ((DateTime.UtcNow - start).TotalSeconds < timeoutSeconds && !ct.IsCancellationRequested)
            {
                var elapsedSeconds = (int)(DateTime.UtcNow - start).TotalSeconds;
                if (elapsedSeconds > 0 && elapsedSeconds - lastProgressReportSeconds >= 60)
                {
                    lastProgressReportSeconds = elapsedSeconds;
                    logger.LogInformation(
                        "Still waiting for admin consent... ({ElapsedSeconds}s / {TimeoutSeconds}s).",
                        elapsedSeconds, timeoutSeconds);
                }

                // Mirror original az-rest polling behavior: check for any grant for clientId.
                // No resourceId filter or scope check — consent just needs to exist.
                var grantDoc = await graphApiService.GraphGetAsync(
                    tenantId,
                    $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{clientSpId}'",
                    ct,
                    AuthenticationConstants.RequiredPermissionGrantScopes);

                if (grantDoc != null &&
                    grantDoc.RootElement.TryGetProperty("value", out var arr) &&
                    arr.GetArrayLength() > 0)
                {
                    logger.LogInformation("Consent granted ({ScopeDescriptor}).", scopeDescriptor);
                    return true;
                }

                logger.LogDebug("No consent grants found for blueprint SP {ClientSpId} yet.", clientSpId);

                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            }

            logger.LogWarning(
                "Admin consent was not detected within {TimeoutSeconds}s. Continuing — you can re-run this command after granting consent.",
                timeoutSeconds);
            return false;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Polling for admin consent was cancelled or timed out for SP {ClientSpId} ({Scope}).", clientSpId, scopeDescriptor);
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

            var grantDoc = await graphApiService.GraphGetAsync(
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
