// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using IConfirmationProvider = Microsoft.Agents.A365.DevTools.Cli.Services.IConfirmationProvider;

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
///   b) S2S app role assignments (Global Administrator only):
///      POST /servicePrincipals/{id}/appRoleAssignments — honors directory-role bypass
///      for GAs, so it works programmatically without DelegatedPermissionGrant.ReadWrite.All.
///      Non-admins receive PowerShell snippets in the Action Required block instead.
///
/// Phase 3 — Admin consent for delegated scopes (Global Administrator OR handoff to one):
///   Builds a single /v2.0/adminconsent URL covering ALL delegated scopes across every
///   resource (Graph + Agent 365 Tools + Messaging Bot + Observability + Power Platform).
///   For GA: opens the browser and polls until consent is detected.
///   For non-admin: returns the URL so the summary writer can surface it for handoff.
///   This replaces the previous direct POST /v1.0/oauth2PermissionGrants path, which required
///   DelegatedPermissionGrant.ReadWrite.All in the token's scp claim — a privilege A365 tokens
///   never carry — causing every grant to fail in fresh tenants.
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
        string? knownBlueprintSpObjectId = null,
        IConfirmationProvider? confirmationProvider = null,
        CommandExecutor? commandExecutor = null)
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

                // Drive the row-3 "already configured" wording: true only when every inheritable
                // spec succeeded AND every one of them was already in place before this run. The
                // existing per-resource writer in ApplyResourceConsentTrackingAsync at line ~1462
                // only fires from a different code path (the legacy BlueprintSubcommand "set
                // permissions" path); the orchestrator path runs through ConfigureInheritedPermissionsAsync
                // and was previously not populating the summary flag at all — making row 3
                // always render "configured" even on fully idempotent re-runs.
                if (setupResults is not null)
                {
                    setupResults.InheritablePermissionsAlreadyExisted =
                        inheritableSpecs.Count > 0 &&
                        inheritableSpecs.All(s =>
                            inheritedResults.TryGetValue(s.ResourceAppId, out var r)
                            && r.configured
                            && r.alreadyExisted);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to configure inheritable permissions: {Message}. Continuing.", ex.Message);
            }

            // Phase 2b: S2S app role assignments — Global Administrator only.
            // POST /servicePrincipals/{id}/appRoleAssignments honors the directory-role bypass
            // for GAs, so it works programmatically without requiring DelegatedPermissionGrant.ReadWrite.All
            // in the token's scp claim. Non-admin users get the corresponding PowerShell snippet
            // surfaced in the LogPermissionsActionRequired block instead.
            //
            // OAuth2 permission grants (delegated tenant-wide consent) are NOT performed here —
            // they are unified into the /v2.0/adminconsent URL flow handled by GrantAdminConsentAsync
            // below, which works for both admins (interactive consent + poll) and non-admins (URL
            // handoff). Direct POST /v1.0/oauth2PermissionGrants requires DelegatedPermissionGrant.ReadWrite.All
            // in the token's scp claim — a privilege A365 tokens never carry — so the previous
            // programmatic path always failed in fresh tenants. See CHANGELOG for details.
            //
            // confirmationProvider is intentionally unused: the /v2.0/adminconsent browser flow
            // surfaces its own consent screen, which serves as the user-facing confirmation.
            _ = confirmationProvider;
            if (isGlobalAdmin)
            {
                var s2sScopes = permScopes.Concat(AuthenticationConstants.RequiredS2SGrantScopes).ToArray();
                if (!string.IsNullOrWhiteSpace(phase1Result?.BlueprintSpObjectId))
                    await PerformS2SGrantsAsync(blueprintService, tenantId, phase1Result.BlueprintSpObjectId, specs, s2sScopes, logger, setupResults, ct);
                // else: blueprint SP was not resolved — leave BlueprintS2SOutcome = NotApplicable (not attempted)

                // When the programmatic Graph API path fails (e.g. token lacks AppRoleAssignment.ReadWrite.All
                // even for GA), fall back to executing the same PowerShell script automatically.
                if (setupResults?.BlueprintS2SOutcome == Models.GrantOutcome.Failed && commandExecutor != null)
                {
                    logger.LogDebug("S2S app role assignments could not be completed via the Graph API.");
                    logger.LogDebug("Attempting via PowerShell (pwsh)...");
                    var (attempted, succeeded) = await PowerShellS2SRunner.TryRunAsync(
                        commandExecutor, tenantId, blueprintAppId, specs, logger, ct);
                    if (attempted && succeeded)
                    {
                        logger.LogInformation("S2S app role assignments completed via PowerShell.");
                        setupResults.BlueprintS2SOutcome = Models.GrantOutcome.Granted;
                    }
                    else if (attempted)
                        logger.LogWarning("PowerShell execution did not complete — see output above. Manual steps in summary.");
                    // else: pwsh missing / timeout / inputs invalid — PowerShellS2SRunner already
                    // logged an actionable warning. Manual steps appear in the setup summary.
                }
            }
        }

        // Non-admin with S2S specs: mark BlueprintS2SOutcome = Failed so DisplaySetupSummary
        // surfaces the PowerShell S2S block as a hand-off item alongside the consent URL.
        if (!isGlobalAdmin && setupResults is not null && phase1Result is not null)
        {
            var hasS2SSpecs = specs.Any(s => s.AppRoleScopes is { Length: > 0 });
            if (hasS2SSpecs)
                setupResults.BlueprintS2SOutcome = Models.GrantOutcome.Failed;
        }

        // --- Admin consent ---
        var (consentGranted, consentUrl) = await GrantAdminConsentAsync(
            graph, config, blueprintAppId, tenantId, specs, phase1Result, permScopes, logger, setupResults, ct, commandExecutor, adminCheck);

        // Update in-memory ResourceConsents only when consent was directly verified (consentUrl == null).
        // AssumedComplete returns a non-null consentUrl — do not persist in that case since the grant
        // was never directly observed. The caller is responsible for saving via configService.SaveStateAsync.
        if (consentGranted && consentUrl == null && phase1Result != null)
        {
            UpdateResourceConsents(config, specs, inheritedResults);
        }

        // consentUrl is already null for Verified (GrantAdminConsentAsync returns null on verified poll)
        // and non-null for AssumedComplete — preserve it so callers can distinguish the two cases.
        string? adminConsentUrl = consentUrl;
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
                // Read back to confirm the entry exists and both inheritableScopes and inheritableRoles
                // are at kind=allAllowed. Trust the API response only after verification so that
                // transient write failures do not silently pass.
                var (exists, scopesAllAllowed, rolesAllAllowed, verifyErr) = await blueprintService.VerifyInheritablePermissionsAsync(
                    tenantId, blueprintAppId, spec.ResourceAppId, ct, permScopes);

                if (exists && scopesAllAllowed && rolesAllAllowed)
                {
                    inheritedResults[spec.ResourceAppId] = (configured: true, alreadyExisted: alreadyExists);
                    var verb = alreadyExists ? "already configured" : "configured";
                    logger.LogInformation("{ResourceName}: inheritable permissions {Verb} (kind=allAllowed on scopes and roles)", spec.ResourceName, verb);
                }
                else
                {
                    inheritedResults[spec.ResourceAppId] = (configured: false, alreadyExisted: false);
                    var detail = !exists
                        ? (verifyErr ?? "not found in read-back")
                        : $"scopes.kind allAllowed={scopesAllAllowed}, roles.kind allAllowed={rolesAllAllowed}";
                    logger.LogWarning(
                        "Inheritable permissions set for {ResourceName} but verification read-back failed: {Error}",
                        spec.ResourceName, detail);
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
                        "Inheritable permissions require the {Roles} role. " +
                        "Remaining inheritable permission specs will be skipped.",
                        AuthenticationConstants.InheritablePermissionsRequiredRoles);
                    setupResults?.Warnings.Add(
                        $"Inheritable permissions require the {AuthenticationConstants.InheritablePermissionsRequiredRoles} role.");
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
    /// Builds a fully-qualified OAuth2 scope URI for use in the /v2.0/adminconsent URL.
    /// Delegates to <see cref="SetupHelpers.BuildFullyQualifiedScope"/> so the combined-URL
    /// path here and the per-resource URL path in <see cref="SetupHelpers.BuildAdminConsentUrls"/>
    /// emit identical scope identifiers (e.g. <c>https://agent365.svc.cloud.microsoft/Tools.Execute</c>,
    /// not <c>api://{appId}/Tools.Execute</c>).
    /// </summary>
    private static string BuildFullyQualifiedScope(string resourceAppId, string scope, string? resourceName = null)
        => SetupHelpers.BuildFullyQualifiedScope(resourceAppId, scope, resourceName);

    /// <summary>
    /// Grants S2S app role assignments for all specs that carry <see cref="ResourcePermissionSpec.AppRoleScopes"/>.
    /// Idempotent: skips roles already assigned. Sets <see cref="SetupResults.BlueprintS2SOutcome"/> on completion.
    /// Requires Application Administrator, Global Administrator, or Agent ID Administrator and <see cref="AuthenticationConstants.RequiredS2SGrantScopes"/>.
    /// </summary>
    private static async Task PerformS2SGrantsAsync(
        AgentBlueprintService blueprintService,
        string tenantId,
        string blueprintSpObjectId,
        IEnumerable<ResourcePermissionSpec> specs,
        string[] s2sScopes,
        ILogger logger,
        SetupResults? setupResults,
        CancellationToken ct)
    {
        var s2sSpecs = specs.Where(s => s.AppRoleScopes is { Length: > 0 }).ToList();
        if (s2sSpecs.Count == 0)
        {
            // No S2S scopes on any spec — outcome is NotApplicable (default). Do not write Granted
            // here; "no work to do" is not the same as "a grant succeeded" for the summary.
            return;
        }

        logger.LogInformation("");
        logger.LogInformation("Configuring S2S app role assignments...");

        var allS2SOk = true;
        // Aggregate "every requested role was already assigned" across all specs. Initialised
        // true so the early-return-protected loop (zero specs cannot reach here) stays true
        // only when EVERY spec returned AllAlreadyAssigned=true.
        var allAlreadyAssigned = true;
        foreach (var spec in s2sSpecs)
        {
            logger.LogDebug(
                "   - App role assignment: blueprint -> {ResourceName} [{AppRoles}]",
                spec.ResourceName, string.Join(' ', spec.AppRoleScopes!));

            var grantResult = await blueprintService.GrantAppRoleAssignmentAsync(
                tenantId,
                blueprintSpObjectId,
                spec.ResourceAppId,
                spec.AppRoleScopes!,
                requiredScopes: s2sScopes,
                ct: ct);

            if (grantResult.AllSucceeded)
            {
                if (grantResult.AllAlreadyAssigned)
                    logger.LogInformation("   - S2S app role already assigned for {ResourceName}", spec.ResourceName);
                else
                    logger.LogInformation("   - S2S app role assigned for {ResourceName}", spec.ResourceName);
            }
            else
            {
                logger.LogDebug("   - Failed to assign S2S app role for {ResourceName}.", spec.ResourceName);
                // Do not add a Warnings entry here: the Setup Summary's Action Required block
                // already emits a copy-paste PowerShell snippet for the failed S2S grant
                // (gated on BlueprintS2SOutcome=Failed via pendingS2SAction). A bare warning
                // restating "re-run as Application Administrator" duplicates that block without
                // adding actionable detail — keep the actionable Action Required item, drop the
                // redundant warning to reduce summary noise.
                allS2SOk = false;
            }

            // Any spec that newly created at least one assignment, or failed, breaks the "all already assigned" claim.
            if (!grantResult.AllAlreadyAssigned)
                allAlreadyAssigned = false;
        }

        if (setupResults is not null)
        {
            setupResults.BlueprintS2SOutcome = allS2SOk ? Models.GrantOutcome.Granted : Models.GrantOutcome.Failed;
            // Only meaningful when the grant succeeded: distinguishes "everything was already there"
            // from "we POSTed at least one new assignment" for the summary's "already granted" wording.
            setupResults.BlueprintS2SAlreadyAssigned = allS2SOk && allAlreadyAssigned;
        }
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
        CommandExecutor? commandExecutor = null,
        Models.RoleCheckResult adminCheck = Models.RoleCheckResult.Unknown)
    {
        // Build a single combined consent URL covering ALL delegated scopes across every
        // resource stamped on the blueprint (Graph, Agent 365 Tools, Messaging Bot,
        // Observability, Power Platform, ...). The /v2.0/adminconsent endpoint accepts
        // fully-qualified scope URIs for any resource — Graph uses https://graph.microsoft.com/...
        // and other resources use api://{appId}/... — so one URL grants everything at once.
        //
        // This replaces the previous "Graph-only URL + programmatic POST /oauth2PermissionGrants
        // for everything else" model, which failed in fresh tenants because the Graph POST
        // requires DelegatedPermissionGrant.ReadWrite.All in the token's scp claim (a privilege
        // A365 tokens never carry). See CHANGELOG for details.
        var allScopes = specs
            .Where(s => s.Scopes is { Length: > 0 })
            .SelectMany(s => s.Scopes.Select(scope => BuildFullyQualifiedScope(s.ResourceAppId, scope, s.ResourceName)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? consentUrl = allScopes.Count > 0
            ? SetupHelpers.BuildAdminConsentUrl(tenantId, blueprintAppId, allScopes)
            : null;

        // No delegated scopes to consent at all — nothing to do. The caller still surfaces
        // the Action Required block from setupResults if S2S work remains.
        if (consentUrl == null)
        {
            return (true, null);
        }

        // Section header — mirrors PerformS2SGrantsAsync's "Configuring S2S app role assignments..."
        // pattern so the setup output reads as a flat list of sections with per-item bullets.
        // Printed once we know there is delegated work to evaluate (consentUrl != null); applies
        // whether the run is fully idempotent, opens a browser, or hands off a URL to a non-admin.
        logger.LogInformation("");
        logger.LogInformation("Configuring delegated permissions...");

        // Check if consent already exists for ALL resolved resources. The /v2.0/adminconsent
        // browser flow registers grants on the blueprint SP which we can verify here.
        // Prefer the az-cli path when an executor is available: the CLI's MSAL Graph token no
        // longer carries DelegatedPermissionGrant.Read.All (removed in PR #409), so the
        // GraphApiService overload returns 403 and reports "no consent" even when consent
        // actually exists — opening the browser unnecessarily on every re-run. The Azure CLI
        // token carries the directory roles a Global Administrator already holds, which lets
        // it read /v1.0/oauth2PermissionGrants.
        if (phase1Result != null && !string.IsNullOrWhiteSpace(phase1Result.BlueprintSpObjectId))
        {
            var specsWithResolvedSp = specs
                .Where(s => phase1Result.ResourceSpObjectIds.ContainsKey(s.ResourceAppId)
                            && s.Scopes is { Length: > 0 })
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

                    // Filter to consentType='AllPrincipals': the /v2.0/adminconsent flow this
                    // pre-check guards always creates tenant-wide grants. A leftover
                    // 'Principal'-scoped grant (e.g. from an earlier --authmode obo run) that
                    // happens to cover the same scopes would otherwise falsely satisfy the
                    // check and skip the browser, leaving the tenant-wide grant un-created.
                    bool consentExists;
                    if (commandExecutor != null)
                    {
                        // Pass the SP ids Phase 1 already resolved so the helper can skip 2 of its
                        // 3 az rest round-trips per spec — turns ~21s of silent waiting on a 4-spec
                        // setup into a single grants query per spec (~7s total).
                        consentExists = await AdminConsentHelper.CheckConsentExistsAsync(
                            commandExecutor,
                            logger,
                            blueprintAppId,
                            spec.ResourceAppId,
                            spec.Scopes,
                            ct,
                            consentType: "AllPrincipals",
                            blueprintSpObjectId: phase1Result.BlueprintSpObjectId,
                            resourceSpObjectId: resourceSpId);
                    }
                    else
                    {
                        consentExists = await AdminConsentHelper.CheckConsentExistsAsync(
                            graph,
                            tenantId,
                            phase1Result.BlueprintSpObjectId,
                            resourceSpId,
                            spec.Scopes,
                            logger,
                            ct,
                            scopes: permScopes,
                            consentType: "AllPrincipals");
                    }

                    if (!consentExists)
                    {
                        allConsented = false;
                        break;
                    }
                }

                if (allConsented)
                {
                    logger.LogInformation("   - Delegated admin consent already granted for all required scopes");
                    if (setupResults is not null)
                        setupResults.TenantWideConsentAlreadyExisted = true;
                    return (true, null);
                }
            }
        }

        // Consent not yet detected — check whether the current user can grant it interactively.
        // adminCheck was resolved before Phase 2 and passed in to avoid a duplicate Graph call.
        // When phase1Result is null, auth failed entirely — the message must reflect that, not imply
        // we performed a role check and found the user lacks the GA role.
        if (adminCheck == Models.RoleCheckResult.DoesNotHaveRole)
        {
            // Non-admin: do not touch BlueprintS2SOutcome here. Whether blueprint S2S was attempted
            // is determined by PerformS2SGrantsAsync (admin-only path). Writing Failed here would
            // make the summary think an S2S attempt occurred, which suppresses the consent URL
            // action item (see the B2 regression: non-admin OBO non-DW must still surface the URL).
            return (false, consentUrl);
        }

        if (adminCheck == Models.RoleCheckResult.Unknown)
        {
            logger.LogDebug("Admin role check inconclusive — attempting consent anyway; API will surface any permission error.");
        }

        // Admin path: open browser and poll for the grant.
        // The URL covers all delegated scopes for all resources stamped on the blueprint
        // (Graph + Agent 365 Tools + Messaging Bot + Observability + Power Platform).
        // Intentionally do not echo the full consent URL here. The Entra consent screen in
        // the freshly opened browser tab is the user-visible confirmation. If the browser
        // fails to launch, BrowserHelper.TryOpenUrl logs the URL itself, and if consent is
        // not detected within the timeout the Action Required block surfaces the URL again.
        logger.LogInformation("   - Opening browser for admin consent (covers all required delegated permissions)...");
        BrowserHelper.TryOpenUrl(consentUrl!, logger);

        bool consentGranted;
        bool consentVerified;
        if (commandExecutor != null)
        {
            // Use az-cli (az rest) to poll. The Azure CLI token carries GA-level Graph access
            // including DelegatedPermissionGrant.Read.All, which the MSAL delegated token no
            // longer holds since PR #409 removed that scope from the CLI client app registration.
            var found = await AdminConsentHelper.PollAdminConsentAsync(
                commandExecutor, logger, blueprintAppId,
                "All permissions", timeoutSeconds: 180, intervalSeconds: 5, ct);
            consentVerified = found;
            // Browser was opened regardless — either the grant was directly observed (Verified)
            // or the timeout elapsed without observing it (AssumedComplete). Either way, setup
            // proceeds; the Action Required block surfaces the consent URL for AssumedComplete.
            consentGranted = true;
        }
        else if (phase1Result != null && !string.IsNullOrWhiteSpace(phase1Result.BlueprintSpObjectId))
        {
            // Fallback for contexts without az-cli (tests). Graph overload may not detect grants
            // when the token lacks DelegatedPermissionGrant.Read.All, but BypassConsentChecksForTests
            // prevents this branch from running in practice.
            var pollResult = await AdminConsentHelper.PollAdminConsentAsync(
                graph, logger, tenantId, phase1Result.BlueprintSpObjectId,
                "All permissions", timeoutSeconds: 180, intervalSeconds: 5, ct,
                permScopes: AuthenticationConstants.BlueprintOperationScopes);
            consentVerified = pollResult == ConsentPollResult.Verified;
            consentGranted = pollResult != ConsentPollResult.NotDetected;
        }
        else
        {
            // No executor and no blueprint SP — cannot poll. Surface URL for manual completion.
            logger.LogWarning(
                "Cannot poll for consent: blueprint service principal was not resolved. " +
                "Please verify consent was granted at: {ConsentUrl}", consentUrl);
            consentGranted = false;
            consentVerified = false;
        }

        if (consentGranted)
        {
            // Polling success already emits "Consent granted (...)" inside PollAdminConsentAsync;
            // the canary-403 path emits its own "Continuing without auto-verification" message.
            // Do not add a second "Admin consent granted successfully." here — it would be a false
            // claim on the canary path, where we never actually verified the grant.
        }
        else
        {
            logger.LogWarning(
                "Admin consent was not detected within the timeout. " +
                "You can re-run this command after granting consent at: {ConsentUrl}", consentUrl);
            setupResults?.Warnings.Add($"Admin consent not detected within timeout. Grant at: {consentUrl}");
        }

        // Return URL when either polling failed outright OR consent was assumed-complete but not
        // verified. Caller uses (consentGranted && consentUrl == null) as the 'safe to persist' gate.
        return (consentGranted, consentVerified ? null : consentUrl);
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
    /// Performs Phase 1 (SP resolution) and Phase 2b (AllPrincipals OAuth2 grants).
    /// Inheritable permissions are assumed to have been set already by 'a365 setup all'
    /// run by an Agent ID Admin.
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
        string? knownBlueprintSpObjectId = null,
        AgentBlueprintService? blueprintService = null)
    {
        if (specs.Count == 0)
        {
            logger.LogInformation("No permission specs provided — nothing to grant.");
            return (true, null);
        }

        var effectiveSpecs = specs.Where(s => s.Scopes.Length > 0).ToList();
        if (effectiveSpecs.Count == 0)
        {
            var hasS2SSpecs = specs.Any(s => s.AppRoleScopes is { Length: > 0 });
            if (!hasS2SSpecs)
            {
                logger.LogInformation("All permission specs have empty scope and app role lists — nothing to grant.");
                // Nothing to grant — leave BlueprintS2SOutcome at NotApplicable (default).
                return (true, null);
            }
            logger.LogDebug("No delegated scopes to grant — proceeding with S2S app role assignments.");
        }

        var permScopes = AuthenticationConstants.RequiredPermissionGrantScopes;

        // Phase 1: resolve SPs
        logger.LogInformation("");
        logger.LogInformation("Resolving service principals for permission configuration...");

        // When delegated specs are empty but S2S specs exist, resolve SPs from S2S specs instead
        // so the blueprint SP object ID is available for app role assignment.
        var specsForPhase1 = effectiveSpecs.Count > 0
            ? (IReadOnlyList<ResourcePermissionSpec>)effectiveSpecs
            : specs.Where(s => s.AppRoleScopes is { Length: > 0 }).ToList();

        BlueprintPermissionsResult? phase1Result = null;
        try
        {
            phase1Result = await UpdateBlueprintPermissionsAsync(
                graph, blueprintAppId, tenantId, specsForPhase1, permScopes, logger, ct,
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

            var (grantOk, statusCode, errorCode) = await graph.CreateOrUpdateOauth2PermissionGrantWithDetailsAsync(
                tenantId,
                phase1Result.BlueprintSpObjectId,
                resourceSpId,
                spec.Scopes,
                ct,
                permScopes);

            if (!grantOk)
            {
                logger.LogWarning(
                    "   - Failed to create OAuth2 permission grant for {ResourceName} (status {StatusCode}, error {ErrorCode}).",
                    spec.ResourceName, statusCode, errorCode ?? "<none>");
                setupResults.Warnings.Add(
                    $"OAuth2 grant failed for {spec.ResourceName} (status {statusCode}, error {errorCode ?? "<none>"}). Check GA permissions.");
                allGrantsOk = false;
            }
            else
            {
                logger.LogInformation("   - OAuth2 grant configured for {ResourceName}", spec.ResourceName);
            }
        }

        // S2S: Grant app role assignments for specs that carry AppRoleScopes.
        var s2sScopes = permScopes.Concat(AuthenticationConstants.RequiredS2SGrantScopes).ToArray();
        if (blueprintService is not null && !string.IsNullOrWhiteSpace(phase1Result.BlueprintSpObjectId))
            await PerformS2SGrantsAsync(blueprintService, tenantId, phase1Result.BlueprintSpObjectId, specs, s2sScopes, logger, setupResults, ct);
        // else: blueprint service unavailable or SP not resolved — leave BlueprintS2SOutcome = NotApplicable (not attempted)

        return (allGrantsOk, phase1Result.BlueprintSpObjectId);
    }
}
