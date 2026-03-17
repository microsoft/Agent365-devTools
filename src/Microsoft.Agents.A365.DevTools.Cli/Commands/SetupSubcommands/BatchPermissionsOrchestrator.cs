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
/// Phase 1 — Resolve service principals (non-admin):
///   Pre-warms the delegated token and resolves all service principal IDs once
///   (blueprint + resources). A single SP resolution with retry replaces the per-resource
///   retry loop that previously caused retry-exhaustion for non-admins.
///   Note: requiredResourceAccess is NOT updated here — it is not supported for Agent Blueprints.
///
/// Phase 2 — Configure inherited permissions (Agent ID Administrator or Global Administrator):
///   Creates programmatic OAuth2 grants and sets inheritable permissions on the blueprint
///   using the SP IDs resolved in Phase 1. Requires Agent ID Administrator role minimum.
///
/// Phase 3 — Grant admin consent (Global Administrator only, or URL for non-admins):
///   Verifies or requests a single browser-based admin consent covering all resources.
///   Skipped if Phase 2 grants already satisfy the consent check. Returns a consolidated
///   consent URL for non-admins instead of attempting consent multiple times.
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

        var permScopes = AuthenticationConstants.RequiredPermissionGrantScopes;

        // --- Resolve service principals ---
        logger.LogInformation("");
        logger.LogInformation("Resolving service principals...");

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

        // --- Configure OAuth2 grants and inheritable permissions ---
        logger.LogInformation("");
        logger.LogInformation("Configuring OAuth2 grants and inheritable permissions...");

        var inheritedPermissionsConfigured = false;
        Dictionary<string, (bool configured, bool alreadyExisted)> inheritedResults =
            new(StringComparer.OrdinalIgnoreCase);

        if (phase1Result == null)
        {
            logger.LogWarning("Skipping OAuth2 grants and inheritable permissions: authentication to Microsoft Graph failed.");
        }
        else
        {
            // Attempt Phase 2 directly — Agent ID Administrator and Global Administrator can
            // both set inheritable permissions. We do not check IsCurrentUserAgentIdAdminAsync
            // upfront because RoleManagement.Read.Directory is not consented on the client app
            // and would trigger an admin approval prompt. Instead, if the user lacks the required
            // role, SetInheritablePermissionsAsync returns 403 which is caught silently via
            // IsInsufficientPrivilegesError — one consolidated warning is emitted and remaining
            // specs are skipped without additional API calls.
            try
            {
                inheritedResults = await ConfigureInheritedPermissionsAsync(
                    graph, blueprintService, blueprintAppId, tenantId, specs,
                    phase1Result, permScopes, logger, setupResults, ct);

                var inheritableSpecs = specs.Where(s => s.SetInheritable).ToList();
                inheritedPermissionsConfigured = inheritableSpecs.Count == 0 ||
                    inheritableSpecs.All(s =>
                        inheritedResults.TryGetValue(s.ResourceAppId, out var r) && r.configured);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to configure OAuth2 grants and inheritable permissions: {Message}. Continuing.", ex.Message);
            }
        }

        // --- Admin consent ---
        logger.LogInformation("");
        logger.LogInformation("Checking admin consent...");

        var (consentGranted, consentUrl, clientAppConsentUrl) = await GrantAdminConsentAsync(
            graph, config, blueprintAppId, tenantId, specs, phase1Result, permScopes, logger, setupResults, ct);

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
        // Include Directory.Read.All so the Phase 3 IsCurrentUserAdminAsync call reuses this
        // cached token instead of triggering an additional browser prompt. Directory.Read.All
        // is confirmed consented on the client app (validated by ClientAppRequirementCheck).
        // RoleManagement.Read.Directory is intentionally excluded — it is not consented on the
        // client app and would trigger an admin approval prompt.
        var prewarmScopes = permScopes.Append("Directory.Read.All").ToArray();
        var user = await graph.GraphGetAsync(tenantId, "/v1.0/me?$select=id", ct, scopes: prewarmScopes);
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
                var resourceSpId = await graph.EnsureServicePrincipalForAppIdAsync(
                    tenantId, spec.ResourceAppId, ct, permScopes);

                if (!string.IsNullOrWhiteSpace(resourceSpId))
                {
                    resourceSpObjectIds[spec.ResourceAppId] = resourceSpId;
                    logger.LogDebug("   - Resolved {ResourceName} SP: {SpId}", spec.ResourceName, resourceSpId);
                }
                else
                {
                    logger.LogWarning(
                        "   - Service principal not found for {ResourceName} ({ResourceAppId}). " +
                        "Phase 2 grants will be skipped for this resource.",
                        spec.ResourceName, spec.ResourceAppId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "   - Failed to resolve service principal for {ResourceName}: {Message}. " +
                    "Phase 2 grants will be skipped for this resource.",
                    spec.ResourceName, ex.Message);
            }
        }

        return new BlueprintPermissionsResult(blueprintSpObjectId ?? string.Empty, resourceSpObjectIds);
    }

    /// <summary>
    /// Phase 2: For each spec, creates or updates the OAuth2 permission grant using SP IDs
    /// resolved in Phase 1, then sets inheritable permissions on the blueprint if requested.
    /// Returns per-spec inheritable permissions results for use in ResourceConsents updates.
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
            // OAuth2 grant requires both the blueprint SP and the resource SP.
            // Inheritable permissions use the blueprint app ID directly and always run.
            var hasBlueprintSp = !string.IsNullOrWhiteSpace(phase1Result.BlueprintSpObjectId);
            var hasResourceSp = phase1Result.ResourceSpObjectIds.TryGetValue(spec.ResourceAppId, out var resourceSpId);

            if (hasBlueprintSp && hasResourceSp)
            {
                logger.LogDebug(
                    "   - OAuth2 grant: blueprint -> {ResourceName} [{Scopes}]",
                    spec.ResourceName, string.Join(' ', spec.Scopes));

                var grantResult = await graph.CreateOrUpdateOauth2PermissionGrantAsync(
                    tenantId,
                    phase1Result.BlueprintSpObjectId,
                    resourceSpId!,
                    spec.Scopes,
                    ct,
                    permScopes);

                if (!grantResult)
                {
                    logger.LogWarning(
                        "   - Failed to create OAuth2 permission grant for {ResourceName}. " +
                        "Admin consent may be required.",
                        spec.ResourceName);
                }
                else
                {
                    logger.LogInformation("   - OAuth2 grant configured for {ResourceName}", spec.ResourceName);
                }
            }
            else
            {
                logger.LogDebug(
                    "   - Skipping OAuth2 grant for {ResourceName}: blueprint SP resolved={HasBlueprint}, resource SP resolved={HasResource}.",
                    spec.ResourceName, hasBlueprintSp, hasResourceSp);
            }

            // Inheritable permissions — uses blueprint app ID, not SP object ID.
            if (!spec.SetInheritable)
            {
                inheritedResults[spec.ResourceAppId] = (configured: false, alreadyExisted: false);
                continue;
            }

            // If a previous spec already hit "Insufficient privileges", all remaining specs
            // will fail for the same reason. Skip them without making additional API calls.
            if (insufficientPrivilegesDetected)
            {
                inheritedResults[spec.ResourceAppId] = (configured: false, alreadyExisted: false);
                continue;
            }

            logger.LogInformation(
                "   - Configuring inheritable permissions: {ResourceName} [{Scopes}]",
                spec.ResourceName, string.Join(' ', spec.Scopes));

            var (ok, alreadyExists, err) = await blueprintService.SetInheritablePermissionsAsync(
                tenantId, blueprintAppId, spec.ResourceAppId, spec.Scopes,
                requiredScopes: permScopes, ct);

            inheritedResults[spec.ResourceAppId] = (configured: ok || alreadyExists, alreadyExisted: alreadyExists);

            if (alreadyExists)
            {
                logger.LogInformation("   - Inheritable permissions already configured for {ResourceName}", spec.ResourceName);
            }
            else if (ok)
            {
                logger.LogInformation("   - Inheritable permissions configured for {ResourceName}", spec.ResourceName);
            }
            else
            {
                var friendlyErr = TryExtractGraphErrorMessage(err) ?? err;

                if (IsInsufficientPrivilegesError(err))
                {
                    // Systemic role failure — one consolidated warning covers all resources.
                    insufficientPrivilegesDetected = true;
                    logger.LogWarning(
                        "Inheritable permissions require the Agent ID Administrator or Global Administrator role. " +
                        "Remaining inheritable permission specs will be skipped.");
                    setupResults?.Warnings.Add(
                        "Inheritable permissions require the Agent ID Administrator or Global Administrator role. " +
                        "Grant admin consent to complete this step.");
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
    /// Phase 3: Checks for existing consent (skips browser if found), then either opens the
    /// browser for admins or returns a consolidated consent URL for non-admins.
    /// Updates config.ResourceConsents indirectly via the caller after this method returns.
    /// </summary>
    private static async Task<(bool granted, string? consentUrl, string? clientAppConsentUrl)>
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
        CancellationToken ct)
    {
        // Build a consolidated consent URL that covers all scopes across all specs.
        // Because Phase 1 added all resources to requiredResourceAccess, this single URL
        // grants admin consent for everything when an admin visits it.
        var allScopes = specs.SelectMany(s => s.Scopes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var allScopesEscaped = Uri.EscapeDataString(string.Join(' ', allScopes));
        var consentUrl =
            $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent" +
            $"?client_id={blueprintAppId}" +
            $"&scope={allScopesEscaped}" +
            $"&redirect_uri=https://entra.microsoft.com/TokenAuthorize" +
            $"&state=xyz123";

        // Check if consent already exists (Phase 2 programmatic grants satisfy this check).
        if (phase1Result != null && !string.IsNullOrWhiteSpace(phase1Result.BlueprintSpObjectId))
        {
            var specWithResolvedSp = specs.FirstOrDefault(
                s => phase1Result.ResourceSpObjectIds.ContainsKey(s.ResourceAppId));

            if (specWithResolvedSp != null &&
                phase1Result.ResourceSpObjectIds.TryGetValue(specWithResolvedSp.ResourceAppId, out var resourceSpId))
            {
                var consentExists = await AdminConsentHelper.CheckConsentExistsAsync(
                    graph,
                    tenantId,
                    phase1Result.BlueprintSpObjectId,
                    resourceSpId,
                    specWithResolvedSp.Scopes,
                    logger,
                    ct,
                    scopes: permScopes);

                if (consentExists)
                {
                    logger.LogInformation("Admin consent already granted — skipping browser consent.");
                    return (true, consentUrl, null);
                }
            }
        }

        // Consent not yet detected — check whether the current user can grant it interactively.
        var userIsAdmin = await graph.IsCurrentUserAdminAsync(tenantId, ct);

        if (!userIsAdmin)
        {
            logger.LogWarning(
                "Admin consent is required but the current user does not have an admin role.");

            string? clientAppConsentUrl = null;
            if (!string.IsNullOrWhiteSpace(config.ClientAppId))
            {
                clientAppConsentUrl =
                    $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent" +
                    $"?client_id={config.ClientAppId}" +
                    $"&scope={Uri.EscapeDataString(AuthenticationConstants.RoleManagementReadDirectoryScope)}";
            }

            logger.LogWarning("  A tenant administrator must grant consent at:");
            logger.LogWarning("  {ConsentUrl}", consentUrl);
            if (!string.IsNullOrWhiteSpace(clientAppConsentUrl))
            {
                logger.LogWarning("  To enable admin role detection, also grant consent for the a365 CLI client app:");
                logger.LogWarning("  {ClientAppConsentUrl}", clientAppConsentUrl);
                logger.LogWarning("  This step is optional - setup will still work without it.");
            }
            setupResults?.Warnings.Add($"Admin consent required. Grant at: {consentUrl}");

            return (false, consentUrl, clientAppConsentUrl);
        }

        // Admin path: open browser and poll for the grant.
        logger.LogInformation("Opening browser for admin consent (covers all configured resources)...");
        logger.LogInformation(
            "If the browser does not open automatically, navigate to this URL: {ConsentUrl}", consentUrl);
        BrowserHelper.TryOpenUrl(consentUrl, logger);

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

        return (consentGranted, consentGranted ? null : consentUrl, null);
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
    /// Extracts the human-readable message from a Graph API JSON error response.
    /// Returns null if the input is not a parseable Graph error body.
    /// </summary>
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
}
