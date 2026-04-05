// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Orchestrates the three-phase batch permissions flow for agent blueprint setup.
///
/// Phase 1 — Resolve service principals:
///   Pre-warms the delegated token and resolves all service principal IDs once
///   (blueprint + resources). Non-fatal: partial progress is preserved.
///   Note: requiredResourceAccess is NOT updated here — it is not supported for Agent Blueprints.
///
/// Phase 2 — Configure permissions:
///   a) Inheritable permissions (Agent ID Administrator or Global Administrator):
///      Sets inheritable permission scopes on the blueprint via the Blueprint API,
///      then reads them back to verify they are present. Agent ID Admin can do this.
///   b) OAuth2 permission grants (Global Administrator only):
///      Creates AllPrincipals (tenant-wide) oauth2PermissionGrants via Graph API.
///      Requires Global Administrator — skipped for non-admin users.
///      Technical limitation: oauth2PermissionGrant creation via the API always requires
///      DelegatedPermissionGrant.ReadWrite.All which is an admin-only scope. Additionally,
///      GA bypasses entitlement validation and can grant any scope; non-admin users get
///      HTTP 403 (insufficient privileges) or HTTP 400 (entitlement not found) for all
///      five resource SPs. There is no self-service path for non-admin users via the API.
///
/// Phase 3 — Admin consent (Global Administrator only):
///   For GA: skipped entirely — Phase 2b grants satisfy consent.
///   For non-admin: shows the 'a365 setup admin' command to hand off to a GA.
///   The consent URL is still generated for Graph scopes as a fallback reference.
///
/// This class is a parallel implementation alongside SetupHelpers.EnsureResourcePermissionsAsync,
/// which remains unchanged for standalone callers and CopilotStudioSubcommand.
/// </summary>
internal static class BatchPermissionsOrchestrator
{
    /// <summary>
    /// Configures permissions for all supplied resource specs in three sequential phases.
    /// Each phase is non-fatal: a failure logs a warning and continues to the next phase,
    /// so partial progress is preserved and the caller can report what succeeded.
    /// </summary>
    /// <param name="graph">Graph API service (used for SP lookups, OAuth2 grants, admin check).</param>
    /// <param name="blueprintService">Blueprint service (used for requiredResourceAccess and inheritable permissions).</param>
    /// <param name="config">Agent365 configuration — ResourceConsents is updated in-memory on success.</param>
    /// <param name="blueprintAppId">Application (client) ID of the agent blueprint.</param>
    /// <param name="tenantId">Tenant ID.</param>
    /// <param name="specs">Ordered list of resource permission specs to configure.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="setupResults">Optional setup results for tracking warnings (may be null for standalone commands).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Tuple of (blueprintPermissionsUpdated, inheritedPermissionsConfigured, adminConsentGranted, adminConsentUrl).
    /// adminConsentUrl is non-null only when the current user is not an admin and consent was not already present.
    /// </returns>
    public static async Task<(bool blueprintPermissionsUpdated, bool inheritedPermissionsConfigured, bool adminConsentGranted, string? adminConsentUrl)>
    ConfigureAllPermissionsAsync(
        GraphApiService graph,
        AgentBlueprintService blueprintService,
        Agent365Config config,
        string blueprintAppId,
        string tenantId,
        IReadOnlyList<ResourcePermissionSpec> specs,
        ILogger logger,
        SetupResults? setupResults,
        CancellationToken ct,
        string? knownBlueprintSpObjectId = null)
    {
        if (specs.Count == 0)
        {
            logger.LogInformation("No permission specs provided — skipping batch permissions configuration.");
            return (true, true, true, null);
        }

        // Filter out specs with no scopes — they would produce empty OAuth2 grants (HTTP 400).
        // This can happen when the MCP manifest is missing or contains no required scopes.
        var effectiveSpecs = specs.Where(s => s.Scopes.Length > 0).ToList();
        if (effectiveSpecs.Count < specs.Count)
        {
            var skipped = specs.Count - effectiveSpecs.Count;
            logger.LogDebug("Skipping {Count} resource spec(s) with no scopes (manifest missing or empty).", skipped);
        }

        if (effectiveSpecs.Count == 0)
        {
            logger.LogInformation("All permission specs have empty scope lists — skipping batch permissions configuration.");
            return (true, true, true, null);
        }

        // Use filtered list for all downstream phases
        specs = effectiveSpecs;

        var permScopes = AuthenticationConstants.RequiredPermissionGrantScopes;

        BlueprintPermissionsResult? phase1Result = null;
        var blueprintPermissionsUpdated = false;
        try
        {
            phase1Result = await UpdateBlueprintPermissionsAsync(
                graph, blueprintAppId, tenantId, specs, permScopes, logger, ct,
                knownBlueprintSpObjectId);
            blueprintPermissionsUpdated = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to resolve service principals: {Message}. Continuing.", ex.Message);
        }

        // Check admin role once — reused by both Phase 2b (grants) and Phase 3 (consent check).
        // Avoids a duplicate Graph call later.
        // If Phase 1 failed (phase1Result == null), default to DoesNotHaveRole: we cannot
        // authenticate, so interactive consent is impossible — return the URL instead of
        // opening a browser.
        var adminCheck = phase1Result != null
            ? await graph.IsCurrentUserAdminAsync(tenantId, ct)
            : Models.RoleCheckResult.DoesNotHaveRole;
        var isGlobalAdmin = adminCheck == Models.RoleCheckResult.HasRole;

        // --- Phase 2a: Inheritable permissions (Agent ID Admin or GA) ---
        // --- Phase 2b: OAuth2 grants (Global Administrator only) ---
        logger.LogInformation("Configuring inheritable permissions...");

        var inheritedPermissionsConfigured = false;
        Dictionary<string, (bool configured, bool alreadyExisted)> inheritedResults =
            new(StringComparer.OrdinalIgnoreCase);

        if (phase1Result == null)
        {
            logger.LogWarning("Skipping permissions configuration: authentication to Microsoft Graph failed.");
        }
        else
        {
            // Phase 2a: Inheritable permissions — Agent ID Admin and GA can both set these.
            // If the user lacks the required role, SetInheritablePermissionsAsync returns 403
            // which is caught via IsInsufficientPrivilegesError — one consolidated warning is
            // emitted and remaining specs are skipped.
            try
            {
                using (logger.Indent())
                {
                    inheritedResults = await ConfigureInheritedPermissionsAsync(
                        graph, blueprintService, blueprintAppId, tenantId, specs,
                        phase1Result, permScopes, logger, setupResults, ct);
                }

                var inheritableSpecs = specs.Where(s => s.SetInheritable).ToList();
                inheritedPermissionsConfigured = inheritableSpecs.Count == 0 ||
                    inheritableSpecs.All(s =>
                        inheritedResults.TryGetValue(s.ResourceAppId, out var r) && r.configured);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to configure inheritable permissions: {Message}. Continuing.", ex.Message);
            }

            // Phase 2b: OAuth2 grants — Global Administrator only.
            // Technical limitation: oauth2PermissionGrant creation via the Graph API requires
            // DelegatedPermissionGrant.ReadWrite.All (admin-only scope). GA also bypasses
            // entitlement validation. Non-admin users always get 403 or 400 for all resources.
            if (isGlobalAdmin)
            {
                var grantsOk = await ConfigureOauth2GrantsAsync(
                    graph, blueprintAppId, tenantId, specs, phase1Result, permScopes, logger, ct);

                logger.LogInformation("");
                if (grantsOk)
                {
                    logger.LogInformation("Admin consent granted.");
                    UpdateResourceConsents(config, specs, inheritedResults);
                    return (blueprintPermissionsUpdated, inheritedPermissionsConfigured, true, null);
                }

                // Grants failed (e.g. SP propagation lag). Return false so the summary shows
                // the failure and next steps (re-run 'a365 setup admin').
                logger.LogWarning("OAuth2 grants failed — the service principal may still be propagating.");
                logger.LogWarning("Re-run 'a365 setup admin' to retry once propagation is complete.");
                var graphScopes = specs
                    .Where(s => s.ResourceAppId == AuthenticationConstants.MicrosoftGraphResourceAppId)
                    .SelectMany(s => s.Scopes.Select(scope => $"{graph.GraphBaseUrl}/{scope}"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var retryConsentUrl = graphScopes.Count > 0
                    ? SetupHelpers.BuildAdminConsentUrl(tenantId, blueprintAppId, graphScopes)
                    : null;
                return (blueprintPermissionsUpdated, inheritedPermissionsConfigured, false, retryConsentUrl);
            }
        }

        // --- Admin consent ---
        var (consentGranted, consentUrl) = await GrantAdminConsentAsync(
            graph, config, blueprintAppId, tenantId, specs, phase1Result, permScopes, logger, setupResults, ct, adminCheck);

        // Update in-memory ResourceConsents so subsequent runs detect existing state.
        // The caller is responsible for persisting changes via configService.SaveStateAsync.
        if (consentGranted && phase1Result != null)
        {
            UpdateResourceConsents(config, specs, inheritedResults);
        }

        string? adminConsentUrl = consentGranted ? null : consentUrl;
        return (blueprintPermissionsUpdated, inheritedPermissionsConfigured, consentGranted, adminConsentUrl);
    }

    /// <summary>
    /// Phase 1: Pre-warms the delegated token, resolves the blueprint service principal once
    /// (with retry for propagation), then resolves each resource service principal.
    /// Note: requiredResourceAccess is not updated here — it is not supported for Agent Blueprints.
    /// </summary>
    private static async Task<BlueprintPermissionsResult> UpdateBlueprintPermissionsAsync(
        GraphApiService graph,
        string blueprintAppId,
        string tenantId,
        IReadOnlyList<ResourcePermissionSpec> specs,
        string[] permScopes,
        ILogger logger,
        CancellationToken ct,
        string? knownBlueprintSpObjectId = null)
    {
        // 0. Pre-warm delegated token once — prevents bouncing between auth providers
        //    for subsequent Graph calls in this phase.
        var prewarmScopes = permScopes.ToArray();
        using var user = await graph.GraphGetAsync(tenantId, "/v1.0/me?$select=id", ct, scopes: prewarmScopes);
        if (user == null)
        {
            throw new SetupValidationException(
                "Failed to authenticate to Microsoft Graph with delegated permissions. " +
                "Check the errors above for the specific cause.");
        }

        // 1. Attempt to resolve blueprint SP once (no retry).
        // Agent Blueprint SPs are not queryable via the standard /v1.0/servicePrincipals endpoint —
        // the lookup is expected to return null. Logged at debug level only to avoid console noise.
        // Non-fatal: OAuth2 grants are skipped when unresolvable; inheritable permissions use app ID directly.
        string? blueprintSpObjectId = !string.IsNullOrWhiteSpace(knownBlueprintSpObjectId)
            ? knownBlueprintSpObjectId
            : await graph.LookupServicePrincipalByAppIdAsync(tenantId, blueprintAppId, ct, permScopes);

        logger.LogDebug(
            blueprintSpObjectId != null
                ? "Blueprint service principal resolved: {SpObjectId}"
                : "Blueprint service principal not found for {AppId} — OAuth2 grants will be skipped.",
            blueprintSpObjectId ?? blueprintAppId);

        // 2. Per spec: ensure resource service principal exists (creates it if absent).
        var resourceSpObjectIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var spec in specs)
        {
            try
            {
                // Suppress Graph POST warning: non-admin users cannot create SPs and that is expected.
                // Phase 2 grants will be skipped for any resource whose SP cannot be resolved.
                var resourceSpId = await graph.EnsureServicePrincipalForAppIdAsync(
                    tenantId, spec.ResourceAppId, ct, permScopes,
                    logWarningOnCreateFailure: false);

                if (!string.IsNullOrWhiteSpace(resourceSpId))
                {
                    resourceSpObjectIds[spec.ResourceAppId] = resourceSpId;
                    logger.LogDebug("   - Resolved {ResourceName} SP: {SpId}", spec.ResourceName, resourceSpId);
                }
                else
                {
                    logger.LogDebug(
                        "   - Service principal not found for {ResourceName} ({ResourceAppId}). " +
                        "Phase 2 grants will be skipped for this resource.",
                        spec.ResourceName, spec.ResourceAppId);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    "   - Failed to resolve service principal for {ResourceName}: {Message}. " +
                    "Phase 2 grants will be skipped for this resource.",
                    spec.ResourceName, ex.Message);
            }
        }

        return new BlueprintPermissionsResult(blueprintSpObjectId ?? string.Empty, resourceSpObjectIds);
    }

    /// <summary>
    /// Phase 2a: Sets inheritable permissions on the blueprint for each spec, then reads them
    /// back to verify they are present. Uses the blueprint app ID directly (not SP object ID).
    /// Agent ID Administrator and Global Administrator can both perform this operation.
    /// Returns per-spec results indicating whether each resource's permissions are confirmed present.
    /// </summary>
    private static async Task<Dictionary<string, (bool configured, bool alreadyExisted)>>
    ConfigureInheritedPermissionsAsync(
        GraphApiService graph,
        AgentBlueprintService blueprintService,
        string blueprintAppId,
        string tenantId,
        IReadOnlyList<ResourcePermissionSpec> specs,
        BlueprintPermissionsResult phase1Result,
        string[] permScopes,
        ILogger logger,
        SetupResults? setupResults,
        CancellationToken ct)
    {
        var inheritedResults = new Dictionary<string, (bool configured, bool alreadyExisted)>(
            StringComparer.OrdinalIgnoreCase);

        // Track whether we have detected a systemic "Insufficient privileges" failure.
        // On the first such failure we skip all remaining inheritable specs and emit one
        // consolidated warning instead of one warning per resource.
        var insufficientPrivilegesDetected = false;

        foreach (var spec in specs)
        {
            if (!spec.SetInheritable)
            {
                inheritedResults[spec.ResourceAppId] = (configured: false, alreadyExisted: false);
                continue;
            }

            if (insufficientPrivilegesDetected)
            {
                inheritedResults[spec.ResourceAppId] = (configured: false, alreadyExisted: false);
                continue;
            }

            logger.LogDebug(
                "   - Configuring inheritable permissions: {ResourceName} [{Scopes}]",
                spec.ResourceName, string.Join(' ', spec.Scopes));

            var (ok, alreadyExists, err) = await blueprintService.SetInheritablePermissionsAsync(
                tenantId, blueprintAppId, spec.ResourceAppId, spec.Scopes,
                requiredScopes: permScopes, ct);

            if (alreadyExists || ok)
            {
                // Read back to confirm the scopes are present — trust the API response only
                // after verification so that transient write failures do not silently pass.
                var (verified, verifiedScopes, verifyErr) = await blueprintService.VerifyInheritablePermissionsAsync(
                    tenantId, blueprintAppId, spec.ResourceAppId, ct, permScopes);

                if (verified)
                {
                    inheritedResults[spec.ResourceAppId] = (configured: true, alreadyExisted: alreadyExists);
                    var verb = alreadyExists ? "already configured" : "configured";
                    logger.LogInformation("{ResourceName}: inheritable permissions {Verb}", spec.ResourceName, verb);
                }
                else
                {
                    inheritedResults[spec.ResourceAppId] = (configured: false, alreadyExisted: false);
                    logger.LogWarning(
                        "Inheritable permissions set for {ResourceName} but verification read-back failed: {Error}",
                        spec.ResourceName, verifyErr ?? "not found in read-back");
                    setupResults?.Warnings.Add(
                        $"Inheritable permissions for {spec.ResourceName} could not be verified after setting.");
                }
            }
            else
            {
                inheritedResults[spec.ResourceAppId] = (configured: false, alreadyExisted: false);
                var friendlyErr = TryExtractGraphErrorMessage(err) ?? err;

                if (IsInsufficientPrivilegesError(err))
                {
                    insufficientPrivilegesDetected = true;
                    logger.LogWarning(
                        "Inheritable permissions require the Agent ID Administrator or Global Administrator role. " +
                        "Remaining inheritable permission specs will be skipped.");
                    setupResults?.Warnings.Add(
                        "Inheritable permissions require the Agent ID Administrator or Global Administrator role.");
                }
                else
                {
                    logger.LogWarning(
                        "   - Failed to configure inheritable permissions for {ResourceName}: {Error}",
                        spec.ResourceName, friendlyErr);
                    setupResults?.Warnings.Add(
                        $"Failed to configure inheritable permissions for {spec.ResourceName}: {friendlyErr}");
                }
            }
        }

        return inheritedResults;
    }

    /// <summary>
    /// Phase 2b: Creates AllPrincipals (tenant-wide) OAuth2 permission grants for all specs.
    /// Requires Global Administrator. Only called when the current user is confirmed GA.
    /// Returns true if all grants succeeded, false if any grant failed.
    /// </summary>
    private static async Task<bool> ConfigureOauth2GrantsAsync(
        GraphApiService graph,
        string blueprintAppId,
        string tenantId,
        IReadOnlyList<ResourcePermissionSpec> specs,
        BlueprintPermissionsResult phase1Result,
        string[] permScopes,
        ILogger logger,
        CancellationToken ct)
    {
        var hasBlueprintSp = !string.IsNullOrWhiteSpace(phase1Result.BlueprintSpObjectId);
        if (!hasBlueprintSp)
        {
            logger.LogDebug("Skipping OAuth2 grants: blueprint SP was not resolved.");
            return false;
        }

        var allGrantsOk = true;
        foreach (var spec in specs)
        {
            if (!phase1Result.ResourceSpObjectIds.TryGetValue(spec.ResourceAppId, out var resourceSpId))
            {
                logger.LogDebug(
                    "   - Skipping OAuth2 grant for {ResourceName}: resource SP not resolved.",
                    spec.ResourceName);
                allGrantsOk = false;
                continue;
            }

            logger.LogDebug(
                "   - OAuth2 grant (AllPrincipals): blueprint -> {ResourceName} [{Scopes}]",
                spec.ResourceName, string.Join(' ', spec.Scopes));

            var grantResult = await graph.CreateOrUpdateOauth2PermissionGrantAsync(
                tenantId,
                phase1Result.BlueprintSpObjectId,
                resourceSpId,
                spec.Scopes,
                ct,
                permScopes);

            if (!grantResult)
            {
                logger.LogWarning("   - Failed to create OAuth2 permission grant for {ResourceName}.", spec.ResourceName);
                allGrantsOk = false;
            }
            else
                logger.LogInformation("   - OAuth2 grant configured for {ResourceName}", spec.ResourceName);
        }

        return allGrantsOk;
    }

    /// <summary>
    /// Phase 3: Checks for existing consent (skips browser if found), then either opens the
    /// browser for admins or returns a consolidated consent URL for non-admins.
    /// Updates config.ResourceConsents indirectly via the caller after this method returns.
    /// </summary>
    private static async Task<(bool granted, string? consentUrl)>
    GrantAdminConsentAsync(
        GraphApiService graph,
        Agent365Config config,
        string blueprintAppId,
        string tenantId,
        IReadOnlyList<ResourcePermissionSpec> specs,
        BlueprintPermissionsResult? phase1Result,
        string[] permScopes,
        ILogger logger,
        SetupResults? setupResults,
        CancellationToken ct,
        Models.RoleCheckResult adminCheck = Models.RoleCheckResult.Unknown)
    {
        // Build a consent URL covering Microsoft Graph delegated scopes only.
        // The /v2.0/adminconsent scope= parameter accepts only standard OAuth2 delegated scopes.
        // Non-Graph scopes (Bot API Authorization.ReadWrite, Agent Blueprint inheritable permissions,
        // MCP server scopes) are blueprint-specific and cannot be consented via this URL — they are
        // configured via the Agent Blueprint API (inheritable permissions) or are not OAuth2 scopes
        // at all. Including them causes AADSTS650053 (unknown scope on Graph) or AADSTS500011
        // (resource SP not found via api:// identifier URI).
        var graphScopes = specs
            .Where(s => s.ResourceAppId == AuthenticationConstants.MicrosoftGraphResourceAppId)
            .SelectMany(s => s.Scopes.Select(scope => $"https://graph.microsoft.com/{scope}"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Build consent URL only when there are Graph scopes — non-Graph APIs cannot be consented
        // via the /v2.0/adminconsent endpoint. They require Phase 2b (oauth2PermissionGrants via Graph API).
        string? consentUrl = graphScopes.Count > 0
            ? SetupHelpers.BuildAdminConsentUrl(tenantId, blueprintAppId, graphScopes)
            : null;

        // Check if consent already exists for ALL resolved resources (Phase 2b programmatic grants
        // satisfy this check). Run this regardless of whether Graph scopes are present — non-DW
        // blueprints have no Graph scopes but still require oauth2PermissionGrants for Observability
        // and Power Platform APIs created by GA via Phase 2b or 'a365 setup admin'.
        if (phase1Result != null && !string.IsNullOrWhiteSpace(phase1Result.BlueprintSpObjectId))
        {
            var specsWithResolvedSp = specs
                .Where(s => phase1Result.ResourceSpObjectIds.ContainsKey(s.ResourceAppId))
                .ToList();

            if (specsWithResolvedSp.Count > 0)
            {
                bool allConsented = true;
                foreach (var spec in specsWithResolvedSp)
                {
                    if (!phase1Result.ResourceSpObjectIds.TryGetValue(spec.ResourceAppId, out var resourceSpId))
                    {
                        allConsented = false;
                        break;
                    }

                    var consentExists = await AdminConsentHelper.CheckConsentExistsAsync(
                        graph,
                        tenantId,
                        phase1Result.BlueprintSpObjectId,
                        resourceSpId,
                        spec.Scopes,
                        logger,
                        ct,
                        scopes: permScopes);

                    if (!consentExists)
                    {
                        allConsented = false;
                        break;
                    }
                }

                if (allConsented)
                {
                    logger.LogInformation("Admin consent already granted — skipping browser consent.");
                    return (true, consentUrl);
                }
            }
        }

        // Grants not fully in place. When there are no Graph scopes (non-DW path), there is no
        // consent URL to open — the admin must run 'a365 setup admin' to create the oauth2PermissionGrants.
        // No inline message: the caller surfaces this as an Action Required item in the summary.
        if (graphScopes.Count == 0)
        {
            return (false, null);
        }

        // Consent not yet detected — check whether the current user can grant it interactively.
        // adminCheck was resolved before Phase 2 and passed in to avoid a duplicate Graph call.
        // When phase1Result is null, auth failed entirely — the message must reflect that, not imply
        // we performed a role check and found the user lacks the GA role.
        if (adminCheck == Models.RoleCheckResult.DoesNotHaveRole)
        {
            return (false, consentUrl);
        }

        if (adminCheck == Models.RoleCheckResult.Unknown)
        {
            logger.LogDebug("Admin role check inconclusive — attempting consent anyway; API will surface any permission error.");
        }

        // Admin path: open browser and poll for the grant.
        // Note: this URL covers Microsoft Graph delegated scopes only (non-Graph resources use inheritable permissions).
        logger.LogInformation("Opening browser for Microsoft Graph admin consent...");
        logger.LogInformation(
            "If the browser does not open automatically, navigate to this URL: {ConsentUrl}", consentUrl);
        BrowserHelper.TryOpenUrl(consentUrl!, logger);

        bool consentGranted;
        if (phase1Result != null && !string.IsNullOrWhiteSpace(phase1Result.BlueprintSpObjectId))
        {
            consentGranted = await AdminConsentHelper.PollAdminConsentAsync(
                graph, logger, tenantId, phase1Result.BlueprintSpObjectId,
                "All permissions", timeoutSeconds: 180, intervalSeconds: 5, ct);
        }
        else
        {
            // Phase 1 did not resolve blueprint SP — cannot poll. Surface URL for manual completion.
            logger.LogWarning(
                "Cannot poll for consent: blueprint service principal was not resolved. " +
                "Please verify consent was granted at: {ConsentUrl}", consentUrl);
            consentGranted = false;
        }

        if (consentGranted)
        {
            logger.LogInformation("Admin consent granted successfully.");
        }
        else
        {
            logger.LogWarning(
                "Admin consent was not detected within the timeout. " +
                "You can re-run this command after granting consent at: {ConsentUrl}", consentUrl);
            setupResults?.Warnings.Add($"Admin consent not detected within timeout. Grant at: {consentUrl}");
        }

        return (consentGranted, consentGranted ? null : consentUrl);
    }

    /// <summary>
    /// Updates config.ResourceConsents in-memory for each spec based on phase results.
    /// The caller is responsible for persisting the config via configService.SaveStateAsync.
    /// </summary>
    private static void UpdateResourceConsents(
        Agent365Config config,
        IReadOnlyList<ResourcePermissionSpec> specs,
        Dictionary<string, (bool configured, bool alreadyExisted)> inheritedResults)
    {
        foreach (var spec in specs)
        {
            inheritedResults.TryGetValue(spec.ResourceAppId, out var inherited);

            var existing = config.ResourceConsents.FirstOrDefault(rc =>
                rc.ResourceAppId.Equals(spec.ResourceAppId, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.ConsentGranted = true;
                existing.ConsentTimestamp = DateTime.UtcNow;
                existing.Scopes = spec.Scopes.ToList();
                existing.InheritablePermissionsConfigured = inherited.configured;
                existing.InheritablePermissionsAlreadyExist = inherited.alreadyExisted;
                existing.InheritablePermissionsError = null;
            }
            else
            {
                config.ResourceConsents.Add(new ResourceConsent
                {
                    ResourceName = spec.ResourceName,
                    ResourceAppId = spec.ResourceAppId,
                    ConsentGranted = true,
                    ConsentTimestamp = DateTime.UtcNow,
                    Scopes = spec.Scopes.ToList(),
                    InheritablePermissionsConfigured = inherited.configured,
                    InheritablePermissionsAlreadyExist = inherited.alreadyExisted,
                    InheritablePermissionsError = null
                });
            }
        }
    }

    /// <summary>
    /// Returns true when the Graph error response indicates a role-based access failure
    /// (HTTP 403 "Insufficient privileges"). Used to distinguish systemic role failures
    /// from per-resource configuration errors in Phase 2.
    /// </summary>
    private static bool IsInsufficientPrivilegesError(string? err)
    {
        if (string.IsNullOrWhiteSpace(err)) return false;
        return err.Contains("Insufficient privileges", StringComparison.OrdinalIgnoreCase)
            || err.Contains("Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the human-readable message from a Graph API JSON error response.
    /// Returns null if the input is not a parseable Graph error body.
    /// </summary>
    private static string? TryExtractGraphErrorMessage(string? err)
    {
        if (string.IsNullOrWhiteSpace(err)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(err);
            if (doc.RootElement.TryGetProperty("error", out var errorEl) &&
                errorEl.TryGetProperty("message", out var msgEl))
                return msgEl.GetString();
        }
        catch { /* not JSON — return null so caller uses raw value */ }
        return null;
    }

    /// <summary>
    /// Carries resolved service principal IDs from Phase 1 to Phases 2 and 3,
    /// eliminating the need for per-phase SP lookups.
    /// </summary>
    private record BlueprintPermissionsResult(
        string BlueprintSpObjectId,
        IReadOnlyDictionary<string, string> ResourceSpObjectIds);

    /// <summary>
    /// Entry point for 'a365 setup admin'. Performs only Phase 1 (SP resolution) and
    /// Phase 2b (AllPrincipals OAuth2 grants). Inheritable permissions are assumed to
    /// have been set already by 'a365 setup all' run by an Agent ID Admin.
    /// Returns the blueprint SP object ID for the verification query, and a boolean
    /// indicating whether all grants were configured successfully.
    /// </summary>
    public static async Task<(bool grantsConfigured, string? blueprintSpObjectId)>
    GrantAdminPermissionsAsync(
        GraphApiService graph,
        Agent365Config config,
        string blueprintAppId,
        string tenantId,
        IReadOnlyList<ResourcePermissionSpec> specs,
        ILogger logger,
        SetupResults setupResults,
        CancellationToken ct,
        string? knownBlueprintSpObjectId = null)
    {
        if (specs.Count == 0)
        {
            logger.LogInformation("No permission specs provided — nothing to grant.");
            return (true, null);
        }

        var effectiveSpecs = specs.Where(s => s.Scopes.Length > 0).ToList();
        if (effectiveSpecs.Count == 0)
        {
            logger.LogInformation("All permission specs have empty scope lists — nothing to grant.");
            return (true, null);
        }

        var permScopes = AuthenticationConstants.RequiredPermissionGrantScopes;

        // Phase 1: resolve SPs
        logger.LogInformation("");
        logger.LogInformation("Resolving service principals for permission configuration...");

        BlueprintPermissionsResult? phase1Result = null;
        try
        {
            phase1Result = await UpdateBlueprintPermissionsAsync(
                graph, blueprintAppId, tenantId, effectiveSpecs, permScopes, logger, ct,
                knownBlueprintSpObjectId);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to resolve service principals: {Message}. Cannot continue.", ex.Message);
            setupResults.Errors.Add($"Service principal resolution failed: {ex.Message}");
            return (false, null);
        }

        // Phase 2b: AllPrincipals grants (GA only — this command is only for GA)
        logger.LogInformation("");
        logger.LogInformation("Configuring OAuth2 permission grants (tenant-wide)...");

        var allGrantsOk = true;
        foreach (var spec in effectiveSpecs)
        {
            if (!phase1Result.ResourceSpObjectIds.TryGetValue(spec.ResourceAppId, out var resourceSpId))
            {
                logger.LogWarning("   - Skipping OAuth2 grant for {ResourceName}: resource SP not resolved.", spec.ResourceName);
                allGrantsOk = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(phase1Result.BlueprintSpObjectId))
            {
                logger.LogWarning("   - Skipping OAuth2 grant for {ResourceName}: blueprint SP not resolved.", spec.ResourceName);
                allGrantsOk = false;
                continue;
            }

            logger.LogDebug(
                "   - OAuth2 grant (AllPrincipals): blueprint -> {ResourceName} [{Scopes}]",
                spec.ResourceName, string.Join(' ', spec.Scopes));

            var grantResult = await graph.CreateOrUpdateOauth2PermissionGrantAsync(
                tenantId,
                phase1Result.BlueprintSpObjectId,
                resourceSpId,
                spec.Scopes,
                ct,
                permScopes);

            if (!grantResult)
            {
                logger.LogWarning("   - Failed to create OAuth2 permission grant for {ResourceName}.", spec.ResourceName);
                setupResults.Warnings.Add($"OAuth2 grant failed for {spec.ResourceName}. Check GA permissions.");
                allGrantsOk = false;
            }
            else
            {
                logger.LogInformation("   - OAuth2 grant configured for {ResourceName}", spec.ResourceName);
            }
        }

        return (allGrantsOk, phase1Result.BlueprintSpObjectId);
    }
}
