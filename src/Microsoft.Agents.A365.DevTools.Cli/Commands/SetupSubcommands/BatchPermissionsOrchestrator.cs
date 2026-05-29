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
        CommandExecutor? commandExecutor = null,
        bool skipSpProvisioning = false,
        IReadOnlyCollection<string>? knownMcpAudienceAppIds = null)
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
            if (isGlobalAdmin)
            {
                var s2sScopes = permScopes.Concat(AuthenticationConstants.RequiredS2SGrantScopes).ToArray();
                var hasS2SWork = !string.IsNullOrWhiteSpace(phase1Result?.BlueprintSpObjectId)
                                 && specs.Any(s => s.AppRoleScopes is { Length: > 0 });

                // Single up-front prompt covering BOTH the primary Graph API path and the az
                // rest fallback. Previously the prompt only existed in the fallback branch, so
                // operators on tenants where the primary path succeeded never saw a confirmation
                // for an admin-level write. The fallback path now reuses the same operator
                // decision rather than re-asking.
                var operatorConfirmedS2S = false;
                if (hasS2SWork)
                {
                    operatorConfirmedS2S = await PromptForBlueprintPermissionGrantAsync(
                        BlueprintPermissionKind.Application, specs, confirmationProvider, logger);
                    if (!operatorConfirmedS2S)
                    {
                        logger.LogInformation("Skipping S2S app role assignment per operator response. The setup summary lists the manual steps.");
                        if (setupResults is not null)
                            setupResults.BlueprintS2SOutcome = Models.GrantOutcome.Failed;
                    }
                }

                if (operatorConfirmedS2S)
                {
                    await PerformS2SGrantsAsync(blueprintService, tenantId, phase1Result!.BlueprintSpObjectId, specs, s2sScopes, logger, setupResults, ct);

                    // When the programmatic Graph API path fails (e.g. CLI token lacks
                    // AppRoleAssignment.ReadWrite.All even for a GA), fall back to issuing the
                    // same writes via `az rest` against the operator's existing az session. A
                    // GA's az token implicitly carries every Graph application permission via
                    // the directory role — including AppRoleAssignment.ReadWrite.All — so
                    // POST /appRoleAssignments succeeds without any additional consent. The
                    // operator already authorized the action at the single prompt above; no
                    // second prompt is required here.
                    if (setupResults?.BlueprintS2SOutcome == Models.GrantOutcome.Failed
                        && commandExecutor != null)
                    {
                        logger.LogDebug("S2S app role assignments could not be completed via the Graph API; falling back to az rest.");
                        var (attempted, succeeded) = await AzRestS2SRunner.TryRunAsync(
                            commandExecutor, phase1Result.BlueprintSpObjectId, specs, logger, ct);
                        if (attempted && succeeded)
                        {
                            logger.LogInformation("Application permissions granted.");
                            setupResults.BlueprintS2SOutcome = Models.GrantOutcome.Granted;
                        }
                        else if (attempted)
                            logger.LogWarning("Some app role assignments did not complete - see output above. Manual steps in summary.");
                        // else: validation rejected the input or no S2S specs were present.
                        // AzRestS2SRunner already logged an actionable warning; Action Required surfaces the rest.
                    }
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
            graph, config, blueprintAppId, tenantId, specs, phase1Result, permScopes, logger, setupResults, ct, commandExecutor, adminCheck, confirmationProvider, skipSpProvisioning, knownMcpAudienceAppIds);

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
    private static string BuildFullyQualifiedScope(string resourceAppId, string scope, bool isMcpAudience = false)
        => SetupHelpers.BuildFullyQualifiedScope(resourceAppId, scope, isMcpAudience);

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
        Models.RoleCheckResult adminCheck = Models.RoleCheckResult.Unknown,
        IConfirmationProvider? confirmationProvider = null,
        bool skipSpProvisioning = false,
        IReadOnlyCollection<string>? knownMcpAudienceAppIds = null)
    {
        // Hold onto the unfiltered spec list so the PowerShell consent fallback can attempt
        // dropped scopes too — the programmatic oauth2PermissionGrants POST is lenient about
        // scope existence and stamping intent is sometimes what the operator actually wants.
        var originalSpecs = specs;

        // Issue #429: filter each spec's Scopes against what the resource SP actually exposes
        // in this tenant. The /v2.0/adminconsent endpoint strictly validates every requested
        // scope and rejects the entire URL with AADSTS650053 on the first unknown scope —
        // a single drift between our spec list and the live resource SP (e.g. Bot Framework
        // dropping "Authorization.ReadWrite" in favor of "AgentData.ReadWrite") blocks every
        // other resource. Dropping unknown scopes here keeps the URL valid; the warnings tell
        // the operator what we filtered out, and the PowerShell fallback (offered after the
        // browser flow fails) can stamp them via the lenient programmatic path if needed.
        IReadOnlyList<ScopeAvailabilityValidator.DroppedScope> droppedScopes =
            Array.Empty<ScopeAvailabilityValidator.DroppedScope>();
        if (phase1Result is { ResourceSpObjectIds.Count: > 0 })
        {
            var validation = await ScopeAvailabilityValidator.ValidateAsync(
                graph, tenantId, specs, phase1Result.ResourceSpObjectIds, logger, ct);
            specs = validation.EffectiveSpecs;
            droppedScopes = validation.DroppedScopes;

            foreach (var d in droppedScopes)
            {
                logger.LogWarning(
                    "Resource '{ResourceName}' ({ResourceAppId}) does not publish delegated scope '{Scope}' — dropping from the unified admin-consent URL to avoid AADSTS650053. " +
                    "If you require this grant, opt into the az rest fallback when prompted; it uses the programmatic oauth2PermissionGrants POST which is lenient about scope existence.",
                    d.ResourceName, d.ResourceAppId, d.Scope);
                setupResults?.Warnings.Add(
                    $"Dropped scope '{d.Scope}' from consent URL — not published on '{d.ResourceName}' ({d.ResourceAppId}). Use the az rest fallback to attempt it.");
            }
        }

        // Build a single combined consent URL covering ALL delegated scopes across every
        // resource stamped on the blueprint (Graph, Agent 365 Tools, Messaging Bot,
        // Observability, Power Platform, ...). The /v2.0/adminconsent endpoint accepts
        // either a fully-qualified Application ID URI (e.g. https://graph.microsoft.com/...)
        // or a bare Application ID GUID (for SPs without a published URI) — both flavors
        // are produced by GetResourceIdentifierUri.
        //
        // Issue #429: an unresolvable resource SP poisons the entire URL. AADSTS650052 is
        // returned for the FIRST scope whose resource has no SP in the tenant
        // ("organization lacks a service principal for ..."). Even when Phase 1 silently
        // failed to create the SP (logWarningOnCreateFailure: false), the spec's scope
        // still landed in the URL pre-fix. Filter to specs whose SP was actually resolved
        // in Phase 1 before building the URL, and surface a warning for each excluded
        // resource so the operator knows which scopes weren't consented.
        var resolvedSpAppIds = phase1Result?.ResourceSpObjectIds is { } map
            ? map.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Find specs whose SP couldn't be resolved in Phase 1 and try to provision them in
        // place by shelling out to 'az ad sp create --id {appId}' against the operator's
        // existing az login (the per-app admin-consent URL pattern was removed because
        // first-party MCP audiences fail it with AADSTS65003 — token-to-self consent).
        // EnsureMissingResourceSpsAsync mutates the resolvedSpAppIds set on success and
        // records MissingSpActions for the rest so the Action Required block renders the
        // recovery steps (the az command + a per-SP /v2.0/adminconsent URL keyed to the
        // blueprint as client). Skips entirely when skipSpProvisioning is true (flag or
        // auto-detected from stdin) or when there is nothing missing. See helper for the
        // full state machine.
        if (resolvedSpAppIds.Count > 0)
        {
            var missingSpecs = specs
                .Where(s => s.Scopes is { Length: > 0 } && !resolvedSpAppIds.Contains(s.ResourceAppId))
                .ToList();
            await EnsureMissingResourceSpsAsync(
                graph, tenantId, blueprintAppId, missingSpecs, resolvedSpAppIds, permScopes,
                skipSpProvisioning, logger, setupResults, ct,
                commandExecutor: commandExecutor,
                confirmationProvider: confirmationProvider,
                knownMcpAudienceAppIds: knownMcpAudienceAppIds);
        }

        // Apply the SP-resolution filter only when Phase 1 produced any results. When
        // Phase 1 returned no resolved SPs at all (auth failure earlier), keep the legacy
        // behavior of including every spec — that surfaces the auth failure path rather
        // than silently dropping every scope here.
        var specsForUrl = resolvedSpAppIds.Count > 0
            ? specs.Where(s => resolvedSpAppIds.Contains(s.ResourceAppId)).ToList()
            : specs.ToList();

        var allScopes = specsForUrl
            .Where(s => s.Scopes is { Length: > 0 })
            .SelectMany(s => s.Scopes.Select(scope => BuildFullyQualifiedScope(
                s.ResourceAppId, scope,
                isMcpAudience: knownMcpAudienceAppIds?.Contains(s.ResourceAppId) ?? false)))
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
        logger.LogInformation("   - Opening browser for admin consent...");
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

        // Issue #429: when the browser polling did not observe a verified grant — either the
        // browser failed to open, the user closed the consent screen without granting, or Entra
        // rejected the URL with an OAuth error (e.g. AADSTS650053) — offer to issue the
        // oauth2PermissionGrants writes via `az rest` against the operator's existing az
        // session. A GA's az token implicitly carries DelegatedPermissionGrant.ReadWrite.All
        // via the directory role, which is what the writes need. Replaces the previous
        // Connect-MgGraph PowerShell fallback (issue #429): pwsh module load + MSAL/WAM
        // browser dance was 5s–2min and unreliable; az rest is synchronous and fast.
        //
        // Pass the *original* (unfiltered) specs to the runner: the programmatic
        // oauth2PermissionGrants POST is lenient about scope existence, so the operator can
        // record intent for scopes the resource SP does not currently publish. This is what
        // the dropped-scope warnings above point them toward.
        if (!consentVerified
            && commandExecutor is not null
            && phase1Result is { } p
            && !string.IsNullOrWhiteSpace(p.BlueprintSpObjectId))
        {
            var shouldRunConsent = await PromptForBlueprintPermissionGrantAsync(
                BlueprintPermissionKind.Delegated, originalSpecs, confirmationProvider, logger);
            if (!shouldRunConsent)
            {
                logger.LogInformation("Admin consent not granted. Re-run setup or grant via the URL above when ready.");
            }
            else
            {
                var (attempted, succeeded) = await AzRestConsentRunner.TryRunAsync(
                    commandExecutor, p.BlueprintSpObjectId, originalSpecs, logger, ct);
                if (attempted && succeeded)
                {
                    logger.LogInformation("Delegated admin consent granted.");
                    if (setupResults is not null)
                    {
                        // Mirror the post-browser-success bookkeeping: a successful run
                        // is just as good as a Verified browser poll for the purpose of "did we
                        // record consent." The caller's persistence gate is (granted && url==null),
                        // and returning verified=true below produces exactly that.
                        setupResults.TenantWideConsentAlreadyExisted = false;
                    }
                    consentGranted = true;
                    consentVerified = true;
                }
                else if (attempted)
                {
                    logger.LogWarning("Admin consent did not complete - see output above. The consent URL remains in the setup summary for manual completion.");
                }
                // else: validation rejected the input or no delegated specs were present.
                // AzRestConsentRunner already logged an actionable warning. The Action Required
                // block surfaces the URL.
            }
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
    /// Issue #429: in-line provisioning of missing resource service principals before the
    /// unified admin-consent URL is built. AADSTS650052 is returned when even one requested
    /// resource lacks an SP in the tenant ("organization lacks a service principal for ...");
    /// the whole URL fails atomically. Phase 1's <c>EnsureServicePrincipalForAppIdAsync</c>
    /// uses <c>POST /servicePrincipals</c> which requires <c>Application.ReadWrite.All</c> on
    /// the CLI token (it does not carry it), so for some first-party multi-tenant apps the
    /// silent SP-creation path fails.
    ///
    /// <para>
    /// The original approach used a per-app <c>/v2.0/adminconsent</c> browser URL with
    /// <c>{appId}/.default</c> scope. That fails with AADSTS65003 for first-party MCP audiences
    /// because (a) it is a "token-to-self" pattern (the app would consent to itself) which
    /// requires preauthorization, and (b) the suggested workaround of using a URI identifier
    /// is not available — the per-server SPs have <c>identifierUris</c> = null. So we shell
    /// out to <c>az ad sp create --id {appId}</c> instead, which uses the operator's existing
    /// <c>az login</c> token. A Global Administrator's az token carries
    /// <c>Application.ReadWrite.All</c> implicitly via the GA directory role, which is exactly
    /// the permission <c>POST /servicePrincipals</c> needs.
    /// </para>
    ///
    /// <para>
    /// For each spec whose <c>ResourceAppId</c> is missing from <paramref name="resolvedSpAppIds"/>:
    /// </para>
    /// <list type="number">
    /// <item><description>Re-queries Graph in case the SP appeared between Phase 1 and now
    /// (operator consented in another window, slow replica caught up). Adds the appId to the
    /// resolved set and skips ahead if found.</description></item>
    /// <item><description>Honors <paramref name="skipSpProvisioning"/>: emits warnings with the
    /// <c>az ad sp create</c> command for manual provisioning and returns. Set via the
    /// <c>--skip-sp-provisioning</c> flag or implicitly when stdin is redirected
    /// (CI / pipe).</description></item>
    /// <item><description>Otherwise, serial loop: per-SP <c>[y/N]</c> confirmation, then
    /// shells out to <c>az ad sp create --id {appId}</c> via <paramref name="commandExecutor"/>.
    /// On exit 0 plus Graph verification, adds to the resolved set; on failure, emits the
    /// warning + manual command as next steps and continues with remaining specs.</description></item>
    /// </list>
    /// </summary>
    internal static async Task EnsureMissingResourceSpsAsync(
        GraphApiService graph,
        string tenantId,
        string blueprintAppId,
        IReadOnlyList<ResourcePermissionSpec> missingSpecs,
        HashSet<string> resolvedSpAppIds,
        string[] permScopes,
        bool skipSpProvisioning,
        ILogger logger,
        SetupResults? setupResults,
        CancellationToken ct,
        CommandExecutor? commandExecutor = null,
        IConfirmationProvider? confirmationProvider = null,
        IReadOnlyCollection<string>? knownMcpAudienceAppIds = null)
    {
        if (missingSpecs.Count == 0) return;

        // Test bypass: short-circuits the entire helper so unit tests for the broader
        // GrantAdminConsentAsync flow do not need to mock az / Graph. Tests that exercise
        // this helper directly set this to false explicitly.
        if (BypassSpProvisioningForTests) return;

        // Pre-flight: re-query each missing SP once. Cheap; eliminates the race where the
        // operator already consented out-of-band between Phase 1 and now, or where a slow
        // Graph replica needed one more probe to catch up.
        var stillMissing = new List<ResourcePermissionSpec>();
        foreach (var spec in missingSpecs)
        {
            var spId = await graph.LookupServicePrincipalByAppIdAsync(tenantId, spec.ResourceAppId, ct, permScopes);
            if (!string.IsNullOrWhiteSpace(spId))
            {
                logger.LogInformation(
                    "Resource '{Name}' ({AppId}): service principal found in tenant — no provisioning needed.",
                    spec.ResourceName, spec.ResourceAppId);
                resolvedSpAppIds.Add(spec.ResourceAppId);
            }
            else
            {
                stillMissing.Add(spec);
            }
        }

        if (stillMissing.Count == 0) return;

        // Non-interactive path (--skip-sp-provisioning set, or stdin redirected, or CI/agent
        // scenario, or no executor passed): emit per-resource warnings with the manual
        // az command and return. The caller's existing exclusion + warning block handles
        // the unified URL build with what's resolvable.
        if (skipSpProvisioning || commandExecutor is null)
        {
            logger.LogInformation("");
            logger.LogInformation(
                "{Count} resource(s) require service principal provisioning. Auto-provisioning is disabled; steps will be listed in the setup summary.",
                stillMissing.Count);
            foreach (var spec in stillMissing)
                RecordMissingSpAction(spec, tenantId, blueprintAppId, logger, setupResults, knownMcpAudienceAppIds);
            return;
        }

        // Interactive path. Each iteration asks the operator, then shells out to
        // 'az ad sp create --id {appId}'. The operator's az login token carries
        // Application.ReadWrite.All implicitly via the Global Administrator directory role,
        // which is what POST /servicePrincipals requires.
        var pluralVerb = stillMissing.Count == 1 ? "is" : "are";
        var pluralNoun = stillMissing.Count == 1 ? "resource service principal" : "resource service principals";
        var maxNameWidth = stillMissing.Max(s => s.ResourceName.Length);

        logger.LogInformation("");
        logger.LogInformation("{Count} {Noun} {Verb} missing in your tenant.", stillMissing.Count, pluralNoun, pluralVerb);
        logger.LogInformation("Provisioning will run 'az ad sp create' using your current az login.");
        logger.LogInformation("You will be prompted before each is provisioned.");
        logger.LogInformation("");

        // Upfront list — name padded so the appId column lines up. Numbering uses "{i}."
        // to match the per-prompt prefix below for visual correspondence.
        for (int i = 0; i < stillMissing.Count; i++)
        {
            var spec = stillMissing[i];
            logger.LogInformation("  {Idx}. {Name}  {AppId}",
                i + 1, spec.ResourceName.PadRight(maxNameWidth), spec.ResourceAppId);
        }

        for (int i = 0; i < stillMissing.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var spec = stillMissing[i];

            logger.LogInformation("");

            // GUID guard: appId originates from manifest / typed config, but custom
            // permissions are user-supplied and reach this loop too. Validate before
            // interpolating into the shell command — defense in depth against injection.
            if (!Guid.TryParse(spec.ResourceAppId, out _))
            {
                logger.LogWarning(
                    "  {Idx}. {Name} ({AppId}): skipping — resource app id is not a valid GUID.",
                    i + 1, spec.ResourceName, spec.ResourceAppId);
                RecordMissingSpAction(spec, tenantId, blueprintAppId, logger, setupResults, knownMcpAudienceAppIds);
                continue;
            }

            // Per-SP confirmation. Default No (must type y). Null confirmationProvider
            // preserves the legacy "auto-yes" behavior under test, mirroring the other
            // PromptForBlueprintPermissionGrantAsync call sites.
            var prompt = $"  {i + 1}. {spec.ResourceName} - Provision via 'az ad sp create'? [y/N]: ";
            var shouldProvision = confirmationProvider is null
                || await confirmationProvider.ConfirmAsync(prompt);
            if (!shouldProvision)
            {
                logger.LogInformation("  Skipped.");
                RecordMissingSpAction(spec, tenantId, blueprintAppId, logger, setupResults, knownMcpAudienceAppIds);
                continue;
            }

            var azArgs = $"ad sp create --id {spec.ResourceAppId}";
            logger.LogInformation("  Running: az {AzArgs}", azArgs);
            var azResult = await commandExecutor.ExecuteAsync(
                "az", azArgs,
                captureOutput: true,
                suppressErrorLogging: true,
                cancellationToken: ct);

            if (!azResult.Success)
            {
                var stderr = string.IsNullOrWhiteSpace(azResult.StandardError) ? azResult.StandardOutput : azResult.StandardError;
                logger.LogWarning("  Failed: {Error}", (stderr ?? string.Empty).Trim());
                RecordMissingSpAction(spec, tenantId, blueprintAppId, logger, setupResults, knownMcpAudienceAppIds);
                continue;
            }

            // az exit 0 plus a parseable SP id in its JSON output is authoritative — the
            // shell-out and the Graph backend are the same Entra tenant, so an SP id in the
            // command output means the SP exists. The previous post-create Graph re-poll
            // produced false "Graph still does not see the SP" warnings on slow replicas
            // even when az clearly succeeded; trusting az output eliminates that.
            string? newSpId = TryExtractSpIdFromAzOutput(azResult.StandardOutput);
            if (!string.IsNullOrWhiteSpace(newSpId))
            {
                logger.LogInformation("  Done. Service principal created for '{Name}' (id: {SpId}).", spec.ResourceName, newSpId);
                resolvedSpAppIds.Add(spec.ResourceAppId);
            }
            else
            {
                // az exited 0 but its stdout did not parse — extremely unusual. Surface the
                // raw output so the operator can diagnose, and record the action so the
                // setup summary surfaces the recovery steps.
                logger.LogWarning(
                    "  az exited 0 but the output did not contain a service principal id. Output: {Output}",
                    (azResult.StandardOutput ?? string.Empty).Trim());
                RecordMissingSpAction(spec, tenantId, blueprintAppId, logger, setupResults, knownMcpAudienceAppIds);
            }
        }

        logger.LogInformation("");
        logger.LogInformation("Continuing with admin consent...");
    }

    /// <summary>
    /// Parses the JSON returned by <c>az ad sp create --id {appId}</c> and extracts the SP
    /// object id from the <c>id</c> property. Returns null when the input is null, empty,
    /// not JSON, or missing the property. The presence of an id is sufficient evidence that
    /// the SP was created — az returns the same JSON the Graph POST returned, in real time,
    /// against the same backend.
    /// </summary>
    internal static string? TryExtractSpIdFromAzOutput(string? azStandardOutput)
    {
        if (string.IsNullOrWhiteSpace(azStandardOutput)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(azStandardOutput);
            if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.String)
                return idEl.GetString();
        }
        catch (System.Text.Json.JsonException) { /* not JSON; return null */ }
        return null;
    }

    /// <summary>
    /// Test-only escape hatch — when true, <see cref="EnsureMissingResourceSpsAsync"/>
    /// returns immediately without opening browsers, polling Graph, or consuming stdin.
    /// Default <strong>false</strong> so the helper actually runs in production. Tests
    /// for <see cref="ConfigureAllPermissionsAsync"/> that do not want the helper firing
    /// must set this to true in their setup. Tests that exercise the helper directly
    /// leave it false. Pattern mirrors <see cref="AdminConsentHelper.BypassConsentChecksForTests"/>.
    /// </summary>
    internal static bool BypassSpProvisioningForTests { get; set; } = false;

    /// <summary>
    /// Builds the <c>az ad sp create</c> command that provisions a missing resource SP in
    /// the operator's tenant. The operator's az login (running as Global Administrator)
    /// carries <c>Application.ReadWrite.All</c> implicitly via the GA directory role, which
    /// is exactly the permission <c>POST /servicePrincipals</c> needs. Returned as a
    /// ready-to-copy command string so the same form appears in the live "Running: ..." log
    /// line and in the warning next-steps block.
    /// </summary>
    internal static string BuildAzAdSpCreateCommand(string resourceAppId) =>
        $"az ad sp create --id {resourceAppId}";

    /// <summary>
    /// Records a missing-SP action on <see cref="SetupResults.MissingSpActions"/> so the
    /// setup summary's "Action Required" block renders it as a numbered item. Each entry
    /// carries the two concrete artifacts the operator needs to complete provisioning
    /// without re-running setup:
    /// <list type="number">
    /// <item><description><c>az ad sp create --id {appId}</c> — provisions the SP in the tenant.</description></item>
    /// <item><description>Per-SP <c>/v2.0/adminconsent</c> URL keyed to the blueprint as
    /// client and this resource's scopes as the request. After step 1 succeeds, clicking
    /// this URL grants the blueprint consent for this one resource additively (does not
    /// wipe other resources' grants), avoiding any need to re-run <c>a365 setup all</c>.</description></item>
    /// </list>
    /// Used by every path that leaves a resource un-provisioned: declined per-SP prompt,
    /// GUID guard rejection, az exiting non-zero, or <c>--skip-sp-provisioning</c>.
    /// </summary>
    private static void RecordMissingSpAction(
        ResourcePermissionSpec spec,
        string tenantId,
        string blueprintAppId,
        ILogger logger,
        SetupResults? setupResults,
        IReadOnlyCollection<string>? knownMcpAudienceAppIds = null)
    {
        _ = logger; // intentionally unused — caller already emits a one-line inline marker
                    // ("Skipped." / "Failed: <error>" / "...invalid GUID...") immediately
                    // before invoking this. The full recovery block (az command + per-SP
                    // consent URL) renders only in the Action Required section so the main
                    // output stays clean. See DisplaySetupSummary's MissingSpActions branch.

        var azCommand = BuildAzAdSpCreateCommand(spec.ResourceAppId);
        var isMcpAudience = knownMcpAudienceAppIds?.Contains(spec.ResourceAppId) ?? false;
        var perSpConsentUrl = BuildPerSpBlueprintConsentUrl(tenantId, blueprintAppId, spec, isMcpAudience);

        setupResults?.MissingSpActions.Add(new MissingSpAction(
            ResourceName: spec.ResourceName,
            ResourceAppId: spec.ResourceAppId,
            Scopes: spec.Scopes?.ToArray() ?? Array.Empty<string>(),
            AzCreateCommand: azCommand,
            PerSpConsentUrl: perSpConsentUrl));
    }

    /// <summary>
    /// Builds the per-SP <c>/v2.0/adminconsent</c> URL the operator clicks AFTER manually
    /// running <c>az ad sp create --id {resourceAppId}</c>. Unlike the broken
    /// "consent the MCP app to itself" pattern (which fails with AADSTS65003 for first-party
    /// token-to-self), this URL uses the BLUEPRINT as the client and the resource's actual
    /// scopes as the request — a normal cross-app consent, additive to whatever the unified
    /// admin-consent URL already granted in the same setup run.
    /// </summary>
    internal static string BuildPerSpBlueprintConsentUrl(
        string tenantId,
        string blueprintAppId,
        ResourcePermissionSpec spec,
        bool isMcpAudience = false)
    {
        var scopes = spec.Scopes ?? Array.Empty<string>();
        var fullyQualified = scopes
            .Select(s => $"{GetResourceUriForBlueprintConsent(spec.ResourceAppId, isMcpAudience)}/{s}");
        var scopeParam = string.Join("%20", fullyQualified.Select(Uri.EscapeDataString));
        var redirectEncoded = Uri.EscapeDataString(AuthenticationConstants.BlueprintConsentRedirectUri);
        return $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent" +
               $"?client_id={blueprintAppId}" +
               $"&scope={scopeParam}" +
               $"&redirect_uri={redirectEncoded}" +
               $"&state={Guid.NewGuid():N}";
    }

    /// <summary>
    /// Resolves the resource identifier used in the per-SP unified-consent URL. Mirrors the
    /// catch-all branches of <see cref="SetupHelpers.GetResourceIdentifierUri"/>: V2 MCP
    /// per-server audiences (signaled via <paramref name="isMcpAudience"/>) use the bare
    /// appId GUID because their SPs have <c>identifierUris=null</c>; every other unknown
    /// resource uses the standard <c>api://{appId}</c> Application ID URI form. Without
    /// this split, a custom resource whose SP omits the bare GUID from
    /// <c>servicePrincipalNames</c> would receive a recovery URL that still fails after
    /// the operator provisions the SP.
    /// </summary>
    private static string GetResourceUriForBlueprintConsent(string resourceAppId, bool isMcpAudience)
        => isMcpAudience ? resourceAppId : $"api://{resourceAppId}";

    /// <summary>
    /// Distinguishes the two flavors of blueprint permission grant for
    /// <see cref="PromptForBlueprintPermissionGrantAsync"/>. Picks which scopes on the
    /// spec are surfaced to the operator (<c>Scopes</c> vs <c>AppRoleScopes</c>) and the
    /// header noun ("delegated" vs "application").
    /// </summary>
    private enum BlueprintPermissionKind { Delegated, Application }

    /// <summary>
    /// Shared confirmation prompt for the two blueprint-permission grant fallbacks
    /// (Phase 2b S2S app roles and Phase 3 delegated admin consent). Mirrors the clean
    /// prompt shape used by
    /// <c>NonDwBlueprintSetupOrchestrator</c>: list the resource:scopes that are about
    /// to land on the blueprint, blank line, single <c>[y/N]</c> question.
    /// <para>
    /// Returns <c>true</c> when the operator opts in (or when no confirmation provider
    /// is supplied — preserves legacy "auto-yes" behavior under test). Returns
    /// <c>false</c> when there is nothing to grant for the requested kind (caller
    /// should treat this as a no-op rather than offering the runner an empty spec list).
    /// </para>
    /// </summary>
    private static async Task<bool> PromptForBlueprintPermissionGrantAsync(
        BlueprintPermissionKind kind,
        IReadOnlyList<ResourcePermissionSpec> specs,
        IConfirmationProvider? confirmationProvider,
        ILogger logger)
    {
        // Per-kind wording: delegated permissions go through admin consent (tenant-wide
        // OAuth2 grant); application permissions are a direct app role assignment on the
        // blueprint SP. Calling the latter "admin consent" is technically incorrect and
        // confused reviewers — keep the two prompts distinct.
        var (header, scopesSelector, confirmPrompt) = kind switch
        {
            BlueprintPermissionKind.Delegated =>
                ("The following delegated permissions will be granted to the agent blueprint:",
                 (Func<ResourcePermissionSpec, IReadOnlyList<string>?>)(s => s.Scopes),
                 "Grant admin consent for these permission(s) now? [y/N]: "),
            BlueprintPermissionKind.Application =>
                ("The following application permissions will be granted to the agent blueprint:",
                 (Func<ResourcePermissionSpec, IReadOnlyList<string>?>)(s => s.AppRoleScopes),
                 "Assign these application permission(s) now? [y/N]: "),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var items = specs
            .Where(s => scopesSelector(s) is { Count: > 0 })
            .Select(s => $"  - {s.ResourceName}: {string.Join(", ", scopesSelector(s)!)}")
            .ToList();

        if (items.Count == 0) return false;

        logger.LogInformation("");
        logger.LogInformation("{Header}", header);
        foreach (var item in items)
            logger.LogInformation("{Item}", item);
        logger.LogInformation("");

        return confirmationProvider is null
            || await confirmationProvider.ConfirmAsync(confirmPrompt);
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
