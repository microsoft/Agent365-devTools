// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Shared helper methods for setup subcommands
/// </summary>
internal static class SetupHelpers
{
    // ── Dry-run layout helpers ─────────────────────────────────────────────────
    // Shared by PrintDwSetupAllDryRunPlan (DW path) and NonDwBlueprintSetupOrchestrator.PrintDryRunPlan
    // (non-DW path) so the column width and blueprint-reuse wording stay in sync.

    // Column width sized so the longest label ("  4. Blueprint Permission Grants" = 32 chars)
    // leaves a 2-space gap before the value column. Whenever a new label is added or renamed,
    // verify it fits within this width minus 2; otherwise the value will run into the label
    // (e.g. "GrantsPENDING") because PadRight is a no-op when the input is already at/over width.
    internal const int DryRunValCol = 34;
    internal static string DryRunRow(string label) => ("  " + label).PadRight(DryRunValCol);
    internal static string DryRunRow(int step, string label) => $"  {step}. {label}".PadRight(DryRunValCol);

    /// <summary>
    /// Prints the six blueprint-reuse rows common to both DW and non-DW dry-run plans.
    /// Called when <c>AgentBlueprintId</c> is already present in config.
    /// </summary>
    internal static void PrintDryRunBlueprintReuseRows(ILogger logger, string blueprintId, int step = 3)
    {
        var sub = new string(' ', DryRunValCol);
        logger.LogInformation(DryRunRow(step, "Blueprint") + "reuse (ID: {BlueprintId})", blueprintId);
        logger.LogInformation(sub + "verify or create service principal");
        logger.LogInformation(sub + "create client secret");
        logger.LogInformation(sub + "verify or create federated identity credential (FIC)");
        logger.LogInformation(sub + "verify or create managed identity");
    }

    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the fixed-scope ResourcePermissionSpecs for the platform APIs that every
    /// agent blueprint requires.
    /// <para>
    /// Observability API and Power Platform API are always included. Messaging Bot API is
    /// included only when <paramref name="isM365"/> is true — non-M365 (blueprint-only) agents
    /// have no messaging surface so Bot scopes serve no purpose.
    /// </para>
    /// </summary>
    internal static ResourcePermissionSpec[] GetFixedApiPermissionSpecs(bool setInheritable, bool isM365)
    {
        var specs = new List<ResourcePermissionSpec>();
        if (isM365)
        {
            specs.Add(new ResourcePermissionSpec(
                ConfigConstants.MessagingBotApiAppId,
                "Messaging Bot API",
                new[] { "Authorization.ReadWrite", "user_impersonation" },
                setInheritable));
        }
        specs.Add(new ResourcePermissionSpec(
            ConfigConstants.ObservabilityApiAppId,
            "Observability API",
            new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
            setInheritable,
            AppRoleScopes: new[] { ConfigConstants.ObservabilityApiOtelWriteScope }));
        specs.Add(new ResourcePermissionSpec(
            PowerPlatformConstants.PowerPlatformApiResourceAppId,
            "Power Platform API",
            new[] { PowerPlatformConstants.PermissionNames.ConnectivityConnectionsRead },
            setInheritable));
        return specs.ToArray();
    }

    /// <summary>
    /// Builds the full resource permission spec list from config. Used by both the DW (AI Teammate)
    /// and non-DW (blueprint-only) setup flows.
    /// <para>
    /// Always includes Microsoft Graph (with <c>config.AgentApplicationScopes</c>),
    /// manifest-derived Agent 365 Tools scopes (when <c>ToolingManifest.json</c> is present),
    /// Observability API, Power Platform API, and any valid custom blueprint permissions.
    /// Messaging Bot API is included only when <paramref name="isM365"/> is true.
    /// </para>
    /// <para>
    /// Pass a pre-computed <paramref name="scopesByAudience"/> to avoid reading the MCP manifest
    /// a second time when the caller already has it (e.g. <c>AllSubcommand.BuildPermissionSpecsAsync</c>).
    /// When <c>null</c>, the manifest is read from <c>config.DeploymentProjectPath</c>.
    /// </para>
    /// </summary>
    internal static async Task<List<ResourcePermissionSpec>> BuildConfiguredPermissionSpecsAsync(
        Agent365Config config,
        bool setInheritable,
        bool isM365 = true,
        Dictionary<string, string[]>? scopesByAudience = null)
    {
        if (scopesByAudience is null)
        {
            var mcpManifestPath = Path.Combine(
                config.DeploymentProjectPath ?? string.Empty,
                McpConstants.ToolingManifestFileName);
            var atgAppId = ConfigConstants.GetAgent365ToolsResourceAppId(config.Environment);
            scopesByAudience = await ManifestHelper.GetScopesByAudienceAsync(mcpManifestPath, excludeLegacyAtg: false, resolvedAtgAppId: atgAppId);
        }

        var specs = new List<ResourcePermissionSpec>
        {
            new(
                AuthenticationConstants.MicrosoftGraphResourceAppId,
                "Microsoft Graph",
                config.AgentApplicationScopes.ToArray(),
                SetInheritable: setInheritable),
        };

        specs.AddRange(scopesByAudience.Select(kvp =>
            new ResourcePermissionSpec(kvp.Key, "Agent 365 Tools", kvp.Value, SetInheritable: setInheritable)));
        specs.AddRange(GetFixedApiPermissionSpecs(setInheritable, isM365));

        foreach (var customPerm in config.CustomBlueprintPermissions ?? new List<CustomResourcePermission>())
        {
            var (isValid, _) = customPerm.Validate();
            if (isValid && !string.IsNullOrWhiteSpace(customPerm.ResourceAppId))
            {
                var resourceName = string.IsNullOrWhiteSpace(customPerm.ResourceName)
                    ? customPerm.ResourceAppId
                    : customPerm.ResourceName;
                specs.Add(new ResourcePermissionSpec(
                    customPerm.ResourceAppId,
                    resourceName,
                    customPerm.Scopes.ToArray(),
                    SetInheritable: setInheritable));
            }
        }

        return specs;
    }

    /// <summary>
    /// Resolves the tenant ID for config-free bootstrap flows.
    /// Uses the explicit flag first, then falls back to the current Azure CLI context.
    /// </summary>
    internal static Task<string?> ResolveBootstrapTenantIdAsync(
        string? tenantIdFlag,
        CommandExecutor executor,
        ILogger logger) =>
        string.IsNullOrWhiteSpace(tenantIdFlag)
            ? TenantDetectionHelper.DetectTenantIdAsync(null, logger, executor)
            : Task.FromResult<string?>(tenantIdFlag);

    /// <summary>
    /// Resolves the client app ID for config-free bootstrap flows.
    /// Optionally prefers a matching local <c>a365.config.json</c> value before falling back to
    /// the well-known Entra display name lookup.
    /// </summary>
    internal static async Task<string?> ResolveBootstrapClientAppIdAsync(
        string tenantId,
        GraphApiService? graphApiService,
        ILogger logger,
        CancellationToken ct,
        bool preferLocalConfig = false)
    {
        string? clientAppId = null;

        if (preferLocalConfig)
        {
            clientAppId = await TryGetLocalClientAppIdAsync(tenantId, logger, ct);
            if (!string.IsNullOrWhiteSpace(clientAppId))
                logger.LogDebug("Using client app ID from local a365.config.json (tenant matches).");
        }

        if (string.IsNullOrWhiteSpace(clientAppId) && graphApiService != null)
        {
            logger.LogInformation("Resolving client app by display name \"{Name}\"...",
                AuthenticationConstants.WellKnownClientAppDisplayName);
            clientAppId = await graphApiService.FindApplicationByDisplayNameAsync(
                tenantId,
                AuthenticationConstants.WellKnownClientAppDisplayName,
                ct);
        }

        if (string.IsNullOrWhiteSpace(clientAppId))
        {
            if (graphApiService == null)
                return null;

            logger.LogInformation("App \"{AppName}\" was not found in tenant {TenantId}.",
                AuthenticationConstants.WellKnownClientAppDisplayName, tenantId);

            logger.LogInformation("Checking tenant permissions...");
            var adminCheck = await graphApiService.IsCurrentUserAdminAsync(tenantId, ct);
            var isAdmin = adminCheck == Models.RoleCheckResult.HasRole;

            string? entered;
            if (isAdmin)
            {
                Console.Write("Enter a client app ID, or [C] to create one: ");
                entered = Console.ReadLine()?.Trim();

                if (string.Equals(entered, "C", StringComparison.OrdinalIgnoreCase))
                    return await CreateAndConsentClientAppAsync(tenantId, graphApiService, logger, ct);
            }
            else
            {
                Console.Write("Enter the client app ID: ");
                entered = Console.ReadLine()?.Trim();
            }

            if (string.IsNullOrWhiteSpace(entered))
            {
                logger.LogInformation("Client app ID entry cancelled.");
                return null;
            }

            logger.LogInformation("Verifying client app ID...");
            if (!await graphApiService.ApplicationExistsByAppIdAsync(tenantId, entered, ct))
            {
                logger.LogError("App ID '{AppId}' was not found in tenant '{TenantId}'. Check the ID and try again.",
                    entered, tenantId);
                return null;
            }

            clientAppId = entered;
        }

        return clientAppId;
    }

    private static async Task<string?> CreateAndConsentClientAppAsync(
        string tenantId,
        GraphApiService graphApiService,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogInformation("Creating app registration '{Name}'...",
            AuthenticationConstants.WellKnownClientAppDisplayName);

        var (appId, spId) = await graphApiService.CreateCliClientAppAsync(
            tenantId, AuthenticationConstants.WellKnownClientAppDisplayName, ct);

        if (string.IsNullOrWhiteSpace(appId))
        {
            logger.LogError("App creation failed. Check errors above.");
            return null;
        }

        logger.LogInformation("App created: {AppId}", appId);

        // Show all required permissions and ask for consent confirmation.
        var consentScopes = AuthenticationConstants.RequiredClientAppPermissions;

        logger.LogInformation("The following permissions will be granted on behalf of all users:");
        logger.LogInformation("  Microsoft Graph / Agent 365 API:");
        foreach (var scope in consentScopes)
            logger.LogInformation("    - {Scope}", scope);

        Console.Write("Grant admin consent for these permissions? [y/N]: ");
        var consentChoice = Console.ReadLine()?.Trim().ToUpperInvariant();

        if (consentChoice != "Y")
        {
            logger.LogInformation("Admin consent skipped. Grant consent manually in the Entra portal and run 'a365 setup requirements' again.");
            return appId;
        }

        if (string.IsNullOrWhiteSpace(spId))
        {
            logger.LogWarning("Service principal not found for the new app — cannot grant admin consent automatically.");
            logger.LogWarning("Grant consent manually in the Entra portal after the SP propagates.");
            return appId;
        }

        logger.LogInformation("Granting admin consent...");
        var graphSpId = await graphApiService.LookupServicePrincipalByAppIdAsync(
            tenantId, AuthenticationConstants.MicrosoftGraphResourceAppId, ct,
            AuthenticationConstants.RequiredPermissionGrantScopes);

        if (string.IsNullOrWhiteSpace(graphSpId))
        {
            logger.LogWarning("Microsoft Graph service principal not found in tenant {TenantId} — admin consent could not be granted.", tenantId);
            return appId;
        }

        var granted = await graphApiService.CreateOrUpdateOauth2PermissionGrantAsync(
            tenantId, spId, graphSpId, consentScopes, ct,
            AuthenticationConstants.RequiredPermissionGrantScopes);

        if (granted)
            logger.LogInformation("Admin consent granted. Run 'a365 setup requirements' again to verify.");
        else
            logger.LogWarning("Admin consent could not be granted. Grant consent manually in the Entra portal and run 'a365 setup requirements' again.");

        return appId;
    }

    private static async Task<string?> TryGetLocalClientAppIdAsync(
        string tenantId,
        ILogger logger,
        CancellationToken ct)
    {
        var localStaticConfigPath = Path.Combine(Environment.CurrentDirectory, ConfigConstants.DefaultConfigFileName);
        if (!File.Exists(localStaticConfigPath))
            return null;

        try
        {
            var staticJson = await File.ReadAllTextAsync(localStaticConfigPath, ct);
            using var staticDoc = JsonDocument.Parse(staticJson);
            var staticRoot = staticDoc.RootElement;
            var configTenantId = GetJsonString(staticRoot, "tenantId");
            var configClientAppId = GetJsonString(staticRoot, "clientAppId");
            return string.Equals(configTenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(configClientAppId)
                ? configClientAppId
                : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not parse {Path} for clientAppId.", localStaticConfigPath);
            return null;
        }
    }

    internal static string? GetJsonString(JsonElement element, string key) =>
        element.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;

    /// <summary>
    /// Fixed permission specs for the non-DW admin consent flow.
    /// Observability API requires both Application (app role for S2S) and Delegated (oauth2 grant for OBO).
    /// Power Platform API requires Delegated only.
    /// Extend this list or pass an override to <see cref="LogNonDwAdminConsentInstructions"/>
    /// when additional APIs are required (e.g. dynamic MCP scopes, custom permissions).
    /// </summary>
    internal static readonly IReadOnlyList<(string ResourceName, string ResourceAppId, string Scope, string PermissionType)> NonDwAdminConsentSpecs =
    [
        ("Observability API",  ConfigConstants.ObservabilityApiAppId,                          ConfigConstants.ObservabilityApiOtelWriteScope,                     "Application"),
        ("Observability API",  ConfigConstants.ObservabilityApiAppId,                          ConfigConstants.ObservabilityApiOtelWriteScope,                     "Delegated"),
        ("Power Platform API", PowerPlatformConstants.PowerPlatformApiResourceAppId,           PowerPlatformConstants.PermissionNames.ConnectivityConnectionsRead,  "Delegated"),
    ];

    /// <summary>
    /// Logs step-by-step instructions for a Global Administrator to grant admin consent
    /// for the blueprint app, with two options: Entra portal and PowerShell.
    /// <para>
    /// Defaults to <see cref="NonDwAdminConsentSpecs"/> (Observability API + Power Platform API).
    /// Pass an explicit <paramref name="specs"/> list to support dynamic or extended permission sets.
    /// </para>
    /// </summary>
    internal static void LogNonDwAdminConsentInstructions(
        ILogger logger,
        string blueprintId,
        IReadOnlyList<(string ResourceName, string ResourceAppId, string Scope, string PermissionType)>? specs = null,
        string? tenantId = null)
    {
        specs ??= NonDwAdminConsentSpecs;
        var delegatedSpecs = specs.Where(s => s.PermissionType == "Delegated").ToList();

        var directLink = $"https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/{blueprintId}/isMSAApp~/false";

        logger.LogInformation("     Option A — Entra portal:");
        logger.LogInformation("       1. Sign in as {Roles} and open:", AuthenticationConstants.DelegatedGrantRequiredRoles);
        logger.LogInformation("            {Link}", directLink);
        logger.LogInformation("       2. Add the following permissions (click 'Add a permission' for each):");
        foreach (var group in delegatedSpecs.GroupBy(s => (s.ResourceName, s.Scope)))
            logger.LogInformation("            - {ResourceName,-20}: {Scope} (Delegated)", group.Key.ResourceName, group.Key.Scope);
        logger.LogInformation("       3. Click 'Grant admin consent for your organization' and confirm");

        logger.LogInformation("");
        logger.LogInformation("     To share with your {Roles}:", AuthenticationConstants.DelegatedGrantRequiredRoles);
        logger.LogInformation("       Blueprint : {BlueprintId}", blueprintId);
        if (!string.IsNullOrWhiteSpace(tenantId))
            logger.LogInformation("       Tenant    : {TenantId}", tenantId);
        logger.LogInformation("       Grant admin consent: {Link}", directLink);
    }

    /// <summary>
    /// Display verification URLs after successful setup
    /// </summary>
    public static async Task DisplayVerificationInfoAsync(FileInfo setupConfigFile, ILogger logger)
    {
        try
        {
            var baseDir = setupConfigFile.DirectoryName ?? Environment.CurrentDirectory;
            var generatedConfigPath = Path.Combine(baseDir, "a365.generated.config.json");
            
            if (!File.Exists(generatedConfigPath))
            {
                logger.LogWarning("Generated config not found - skipping verification info");
                return;
            }

            using var stream = File.OpenRead(generatedConfigPath);
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            var urls = new List<(string Label, string Url)>();

            // Entra ID Application
            if (root.TryGetProperty("agentBlueprintId", out var blueprintProp) && !string.IsNullOrWhiteSpace(blueprintProp.GetString()))
            {
                urls.Add(("Entra ID Application", $"https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/{blueprintProp.GetString()}"));
            }

            if (urls.Count == 0)
                return;

            logger.LogInformation("");
            logger.LogInformation("Verification URLs:");

            foreach (var (label, url) in urls)
            {
                logger.LogInformation("{Label}: {Url}", label, url);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not display verification info: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Display comprehensive setup summary showing what succeeded and what failed.
    /// The DW vs non-DW branch is determined solely by <see cref="SetupResults.IsNonDwBlueprintFlow"/>,
    /// which both orchestrators set explicitly — there is no separate caller-supplied flag.
    /// </summary>
    public static void DisplaySetupSummary(SetupResults results, ILogger logger)
    {
        var isNonDw = results.IsNonDwBlueprintFlow;
        var notRun = "not run (previous step failed)";

        logger.LogInformation("");
        logger.LogInformation("Setup Summary");
        logger.LogInformation("");

        // Intent: derived from the user's --authmode choice (non-DW only). DW does not use --authmode
        // — EffectiveAuthMode is null for DW and the DW-vs-non-DW branch is taken from IsNonDwBlueprintFlow.
        var isBothMode = results.EffectiveAuthMode == Models.AuthMode.Both;
        var isS2sOnlyMode = results.EffectiveAuthMode == Models.AuthMode.S2s;

        // Per-grant outcomes — single-purpose enums, one writer per field.
        var tenantConsentGranted    = results.TenantWideConsentOutcome      == Models.GrantOutcome.Granted;
        var blueprintS2sGranted     = results.BlueprintS2SOutcome           == Models.GrantOutcome.Granted;
        var blueprintS2sFailed      = results.BlueprintS2SOutcome           == Models.GrantOutcome.Failed;
        var agentIdS2sGranted       = results.AgentIdentityS2SOutcome       == Models.GrantOutcome.Granted;
        var agentIdS2sFailed        = results.AgentIdentityS2SOutcome       == Models.GrantOutcome.Failed;
        var agentIdDelegatedGranted = results.AgentIdentityDelegatedOutcome == Models.GrantOutcome.Granted;
        var agentIdDelegatedFailed  = results.AgentIdentityDelegatedOutcome == Models.GrantOutcome.Failed;

        // "Was S2S attempted in this run?"
        //   - DW: PerformS2SGrantsAsync (admin-only) writes BlueprintS2SOutcome; non-attempted = NotApplicable.
        //   - Non-DW: GrantOrInstructAgentIdentityAppPermissionsAsync runs only for S2s or Both mode and writes AgentIdentityS2SOutcome.
        // Either non-NotApplicable outcome means "the S2S path actually executed".
        var isS2SFlow = blueprintS2sGranted || blueprintS2sFailed || agentIdS2sGranted || agentIdS2sFailed;

        // For row-label and "completed" gates, the "S2S" outcome of interest depends on flow:
        //   - Non-DW reports the agent-identity S2S outcome (the user-facing intent).
        //   - DW reports the blueprint S2S outcome.
        var s2sOk     = isNonDw ? agentIdS2sGranted : blueprintS2sGranted;
        var s2sFailed = isNonDw ? agentIdS2sFailed  : blueprintS2sFailed;

        // Delegated success: either tenant-wide consent (DW or non-DW blueprint inheritable scopes)
        // or principal-scoped grants on the agent identity (non-DW path).
        var delegatedOk = tenantConsentGranted || agentIdDelegatedGranted;

        var permissionGrantsCompleted = isS2SFlow
            ? s2sOk && (!isBothMode || delegatedOk)
            : delegatedOk;
        var permissionGrantsPending = isS2SFlow
            ? s2sFailed
            : !permissionGrantsCompleted && results.BatchPermissionsPhase2Completed;
        // Tenant-wide consent is applicable:
        //   - DW: always (the DW path is built on tenant-wide AllPrincipals consent).
        //   - Non-DW: only when the auth mode is Obo or Both (S2s-only relies on app role assignments, no delegated consent).
        var delegatedConsentApplicable = isNonDw ? !isS2sOnlyMode : true;
        // A non-admin run leaves TenantWideConsentOutcome=Failed and produces a consent URL via
        // ApplyConsentUrlsIfNeeded that the user must hand off to a privileged admin. Independent of
        // whether S2S also ran — the URL covers the delegated scopes regardless.
        var pendingAdminAction = delegatedConsentApplicable && !tenantConsentGranted && results.BatchPermissionsPhase2Completed;
        // Per-principal OAuth2 grants on the agent identity SP and the tenant-wide consent URL both
        // ultimately deliver the same Obs+PP delegated scopes to the agent's runtime token. When the
        // admin consent URL hand-off is already pending, the per-principal PowerShell block is
        // redundant — once the admin completes the URL, the agent identity inherits the consent.
        // Only surface the per-principal instructions when the admin URL is not also pending
        // (e.g. admin already granted tenant consent but the per-principal call still failed).
        var pendingDelegatedAction = agentIdDelegatedFailed && !pendingAdminAction;
        var pendingS2SAction = permissionGrantsPending && isS2SFlow;

        if (results.PermissionGrantsSkipped && isNonDw)
        {
            // --agent-registration-only: single focused block showing only the registration outcome.
            if (results.AgentIdentityFailed)
            {
                logger.LogError(DryRunRow("  Agent Registration") + "not attempted — agent identity not found (run 'a365 setup all' to create it first)");
            }
            else if (results.AgentInstanceRegistered)
            {
                var verb = (results.AgentRegistrationAlreadyExisted ? "reused" : "registered").PadRight(12);
                logger.LogInformation(DryRunRow("  Agent Registration") + verb + "'{Name}'",
                    results.AgentRegistrationDisplayName ?? "unknown");
                var sub = new string(' ', DryRunValCol);
                logger.LogInformation(sub + "Registration ID:  {Id}", results.AgentInstanceId ?? "unknown");
                logger.LogInformation(sub + "Agent identity:   {Id}", results.AgentIdentityId ?? "unknown");
                if (!string.IsNullOrWhiteSpace(results.BlueprintId))
                    logger.LogInformation(sub + "Blueprint:        {Id}", results.BlueprintId);
            }
            else if (results.AgentRegistrationFailed)
                logger.LogError(DryRunRow("  Agent Registration") + "failed — see errors");
        }
        else
        {
        // ── Numbered step rows — mirrors the dry-run step list ─────────────────
        // Non-DW omits the Azure hosting step, so all steps after 1 are shifted down by 1.
        var s = isNonDw ? 0 : 1; // step offset: non-DW steps start at 2 (blueprint), DW at 3

        // 1. Prerequisites
        logger.LogInformation(DryRunRow(1, "Prerequisites") + (results.PrerequisitesSkipped ? "skipped" : "validated"));

        // 2. Azure hosting (DW only — not applicable for non-DW blueprint flow)
        if (!isNonDw)
        {
            if (results.InfrastructureSkipped)
                logger.LogInformation(DryRunRow(2, "Azure hosting") + "skipped");
            else if (results.InfrastructureCreated)
                logger.LogInformation(DryRunRow(2, "Azure hosting") + (results.InfrastructureAlreadyExisted ? "reused" : "provisioned"));
            else
                logger.LogError(DryRunRow(2, "Azure hosting") + "failed");
        }

        // Blueprint: step 2 (non-DW) or step 3 (DW)
        if (results.BlueprintCreated)
        {
            var bpStatus = results.BlueprintAlreadyExisted ? "reused" : "created";
            if (!results.BlueprintServicePrincipalCreated)
                logger.LogWarning(DryRunRow(2 + s, "Blueprint") + "{Status} (service principal failed — see warnings)   '{Name}' (ID: {Id})",
                    bpStatus, results.BlueprintDisplayName ?? "unknown", results.BlueprintId ?? "unknown");
            else
                logger.LogInformation(DryRunRow(2 + s, "Blueprint") + "{Status}   '{Name}' (ID: {Id})",
                    bpStatus, results.BlueprintDisplayName ?? "unknown", results.BlueprintId ?? "unknown");
        }
        else if (results.BlueprintFailed)
            logger.LogError(DryRunRow(2 + s, "Blueprint") + "failed");

        // Inheritable Permissions: step 3 (non-DW) or step 4 (DW). Same concept, same Blueprint API
        // call (SetInheritablePermissionsAsync) for both flows, so the label is unified.
        // Gate the "configured" status on Phase 2 (actual inheritance write) rather than Phase 1
        // (just SP resolution + token warm-up). Phase 1 configures nothing on the blueprint; reporting
        // "configured" when only Phase 1 completed misleads non-admin users whose Phase 2 silently
        // 403'd into thinking inheritance is set.
        var permsLabel = "Inheritable Permissions";
        if (results.BlueprintFailed)
            logger.LogInformation(DryRunRow(3 + s, permsLabel) + notRun);
        else if (results.BatchPermissionsPhase2Completed)
            logger.LogInformation(DryRunRow(3 + s, permsLabel) + "configured");
        else if (results.BatchPermissionsPhase1Completed)
            logger.LogWarning(DryRunRow(3 + s, permsLabel) + "PENDING — see Action Required");

        // Non-DW only: Blueprint Permission Grants comes BEFORE Agent identity so all
        // blueprint-side rows (2 Blueprint, 3 Inheritable Permissions, 4 Blueprint Permission
        // Grants) are grouped together. The consent URL printed for non-admin runs targets the
        // blueprint app, so this row belongs in the blueprint section, not the agent-identity
        // section. For DW the step number is 4+s=5 (no Agent identity step in DW).
        var permGrantStep = isNonDw ? 4 : 4 + s;
        if (results.BlueprintFailed)
            logger.LogInformation(DryRunRow(permGrantStep, "Blueprint Permission Grants") + notRun);
        else if (results.PermissionGrantsSkipped)
            logger.LogInformation(DryRunRow(permGrantStep, "Blueprint Permission Grants") + "skipped (--agent-registration-only)");
        else if (isS2SFlow && s2sOk)
        {
            if (isBothMode && !delegatedOk)
                logger.LogInformation(DryRunRow(permGrantStep, "Blueprint Permission Grants") +
                    (pendingDelegatedAction
                        ? "partial (S2S granted; delegated — see Action Required)"
                        : "partial (S2S granted; delegated — see warnings)"));
            else
            {
                var oboAlso = isBothMode && agentIdDelegatedGranted;
                var label = oboAlso ? "granted  S2S app roles + developer-scoped delegated on agent identity" : "granted  S2S app roles";
                logger.LogInformation(DryRunRow(permGrantStep, "Blueprint Permission Grants") + label);
            }
        }
        else if (agentIdDelegatedGranted)
            logger.LogInformation(DryRunRow(permGrantStep, "Blueprint Permission Grants") + "granted  developer-scoped delegated on agent identity");
        else if (pendingDelegatedAction)
            logger.LogWarning(DryRunRow(permGrantStep, "Blueprint Permission Grants") + "PENDING — see Action Required");
        else if (results.BatchPermissionsPhase2Completed)
            logger.LogInformation(DryRunRow(permGrantStep, "Blueprint Permission Grants") + (tenantConsentGranted ? "granted  tenant-wide delegated" : "PENDING"));

        // Non-DW only: Agent identity — step 5 (after blueprint rows are grouped together)
        if (isNonDw)
        {
            if (results.BlueprintFailed)
                logger.LogInformation(DryRunRow(5, "Agent identity") + notRun);
            else if (results.AgentIdentityCreated)
            {
                var identityVerb = (results.AgentIdentityAlreadyExisted ? "reused" : "created").PadRight(9);
                logger.LogInformation(DryRunRow(5, "Agent identity") + identityVerb + " '{Name}' (ID: {Id})",
                    results.AgentIdentityDisplayName ?? "unknown", results.AgentIdentityId ?? "unknown");
            }
            else if (results.AgentIdentityFailed)
                logger.LogWarning(DryRunRow(5, "Agent identity") + "failed — see warnings");
        }

        // Non-DW only: Agent Registration — step 6
        if (isNonDw)
        {
            if (results.BlueprintFailed)
                logger.LogInformation(DryRunRow(6, "Agent Registration") + notRun);
            else
            {
                if (results.AgentInstanceRegistered)
                {
                    var registrationVerb = (results.AgentRegistrationAlreadyExisted ? "reused" : "registered").PadRight(12);
                    logger.LogInformation(DryRunRow(6, "Agent Registration") + registrationVerb + " '{Name}' (ID: {Id})",
                        results.AgentRegistrationDisplayName ?? "unknown", results.AgentInstanceId ?? "unknown");
                }
                else if (results.AgentRegistrationFailed)
                    logger.LogWarning(DryRunRow(6, "Agent Registration") + "failed — see warnings");
            }
        }

        // Messaging endpoint: step 6 for DW (after Permission Grants at 5),
        // step 7 for non-DW (after Agent Registration at 6).
        var endpointStep = isNonDw ? 7 : 6;
        if (results.BlueprintFailed)
        {
            logger.LogInformation(DryRunRow(endpointStep, "Messaging endpoint") + notRun);
        }
        else
        {
            switch (results.MessagingEndpointResult)
            {
                case null:
                    logger.LogInformation(DryRunRow(endpointStep, "Messaging endpoint") + "skipped (non-M365 agent)");
                    break;
                case Models.EndpointRegistrationResult.Created:
                    logger.LogInformation(
                        DryRunRow(endpointStep, "Messaging endpoint") + "registered   '{Endpoint}'",
                        results.MessagingEndpoint ?? "unknown");
                    break;
                case Models.EndpointRegistrationResult.AlreadyExists:
                    logger.LogInformation(
                        DryRunRow(endpointStep, "Messaging endpoint") + "reused       '{Endpoint}'",
                        results.MessagingEndpoint ?? "unknown");
                    break;
                case Models.EndpointRegistrationResult.SkippedContractMismatch:
                    logger.LogWarning(
                        DryRunRow(endpointStep, "Messaging endpoint") + "manual config required — see Action Required");
                    break;
                case Models.EndpointRegistrationResult.Failed:
                default:
                    if (string.Equals(results.MessagingEndpointFailureReason, MessagingEndpointFailureReasons.NotOwner, StringComparison.Ordinal))
                    {
                        logger.LogWarning(DryRunRow(endpointStep, "Messaging endpoint") + "failed (not blueprint owner) — see Action Required");
                    }
                    else if (string.Equals(results.MessagingEndpointFailureReason, MessagingEndpointFailureReasons.BlueprintMissing, StringComparison.Ordinal))
                    {
                        logger.LogWarning(DryRunRow(endpointStep, "Messaging endpoint") + "not attempted (blueprint creation failed) — see Action Required");
                    }
                    else if (string.Equals(results.MessagingEndpointFailureReason, MessagingEndpointFailureReasons.NotConfigured, StringComparison.Ordinal))
                    {
                        logger.LogWarning(DryRunRow(endpointStep, "Messaging endpoint") + "not configured — register manually in the Teams Developer Portal");
                    }
                    else
                    {
                        logger.LogWarning(DryRunRow(endpointStep, "Messaging endpoint") + "failed — see Action Required");
                    }
                    break;
            }
        }

        // Project settings: step 8 for non-DW, step 7 for DW (pushed down by the messaging endpoint row).
        var settingsStep = isNonDw ? 8 : 7;
        if (results.BlueprintFailed)
            logger.LogInformation(DryRunRow(settingsStep, "Project settings") + notRun);
        else if (results.ProjectSettingsWritten)
            logger.LogInformation(DryRunRow(settingsStep, "Project settings") + "written");

        } // end full step table

        // ── Action Required ────────────────────────────────────────────────────
        var messagingEndpointManualRequired =
            results.MessagingEndpointResult == Models.EndpointRegistrationResult.SkippedContractMismatch;
        var messagingEndpointFailureRequired =
            results.MessagingEndpointResult == Models.EndpointRegistrationResult.Failed;
        var hasActionRequired = pendingAdminAction || results.ClientSecretManualActionRequired || pendingS2SAction || pendingDelegatedAction || messagingEndpointManualRequired || messagingEndpointFailureRequired;
        if (hasActionRequired)
        {
            var blueprintAppId = results.BlueprintId ?? "<blueprint-app-id>";
            var consentUrl = results.CombinedConsentUrl ?? results.AdminConsentUrl;

            logger.LogInformation("");
            logger.LogInformation("Action Required:");
            int actionCount = 0;
            if (results.ClientSecretManualActionRequired)
            {
                actionCount++;
                logger.LogInformation("  {N}. Client secret — create manually in the Entra portal for app {AppId}.", actionCount, results.BlueprintId ?? "<blueprint-app-id>");
                logger.LogInformation("     Add it to a365.generated.config.json as 'agentBlueprintClientSecret', then re-run setup.");
                logger.LogInformation("     See: https://learn.microsoft.com/en-us/entra/identity-platform/how-to-add-credentials");
            }
            if (pendingAdminAction)
            {
                actionCount++;
                var adminCmdBlueprintId = results.BlueprintId ?? "<blueprint-id>";

                // Defensive fallback: a non-DW run with no consent URL means ApplyConsentUrlsIfNeeded
                // either short-circuited or was never called. Print the portal walkthrough so the user
                // can still complete the hand-off manually.
                if (isNonDw && string.IsNullOrWhiteSpace(consentUrl))
                {
                    logger.LogInformation("  {N}. Permission Grants — an {Roles} must grant admin consent in the Entra portal:", actionCount, AuthenticationConstants.DelegatedGrantRequiredRoles);
                    LogNonDwAdminConsentInstructions(logger, adminCmdBlueprintId, tenantId: results.TenantId);
                }
                else
                {
                    // Standard hand-off block — used for DW (always) and for non-DW when Slice 5a produced a URL.
                    logger.LogInformation("  {N}. Permission Grants — forward the following to an {Roles}:", actionCount, AuthenticationConstants.DelegatedGrantRequiredRoles);
                    logger.LogInformation("");
                    logger.LogInformation("     Blueprint : {BlueprintId}", adminCmdBlueprintId);
                    if (!string.IsNullOrWhiteSpace(results.TenantId))
                        logger.LogInformation("     Tenant    : {TenantId}", results.TenantId);
                    if (!string.IsNullOrWhiteSpace(consentUrl))
                        logger.LogInformation("     Consent URL: {ConsentUrl}", consentUrl);
                }
            }
            if (pendingS2SAction)
            {
                actionCount++;
                logger.LogInformation("  {N}. Observability API S2S app role (PowerShell):", actionCount);
                logger.LogInformation("     Required role: {Roles}", AuthenticationConstants.S2SGrantRequiredRoles);
                if (!string.IsNullOrWhiteSpace(results.TenantId))
                    logger.LogInformation("       Connect-MgGraph -TenantId '{TenantId}' -Scopes 'AppRoleAssignment.ReadWrite.All','Directory.Read.All'", results.TenantId);
                else
                    logger.LogInformation("       Connect-MgGraph -Scopes 'AppRoleAssignment.ReadWrite.All','Directory.Read.All'");
                if (isNonDw)
                {
                    // Non-DW: grant targets the agent identity SP directly (SP object ID, not an app ID).
                    var agentSpId = results.AgentIdentityId ?? "<agent-identity-sp-object-id>";
                    logger.LogInformation("       $agentSpId = '{AgentSpId}'", agentSpId);
                    logger.LogInformation("       $obs = Get-MgServicePrincipal -Filter \"appId eq '{ObsApiAppId}'\"", ConfigConstants.ObservabilityApiAppId);
                    logger.LogInformation("       $rid = ($obs.AppRoles | Where-Object {{ $_.Value -eq '{ObsScope}' }}).Id", ConfigConstants.ObservabilityApiOtelWriteScope);
                    logger.LogInformation("       New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $agentSpId -PrincipalId $agentSpId -ResourceId $obs.Id -AppRoleId $rid");
                    logger.LogInformation("");
                    if (!string.IsNullOrWhiteSpace(results.TenantId))
                        logger.LogInformation("     Tenant        : {TenantId}", results.TenantId);
                    logger.LogInformation("     Agent Identity: {AgentSpId}", agentSpId);
                }
                else
                {
                    // DW: grant targets the blueprint SP (looked up by app ID).
                    logger.LogInformation("       $bp  = Get-MgServicePrincipal -Filter \"appId eq '{BlueprintAppId}'\"", blueprintAppId);
                    logger.LogInformation("       $obs = Get-MgServicePrincipal -Filter \"appId eq '{ObsApiAppId}'\"", ConfigConstants.ObservabilityApiAppId);
                    logger.LogInformation("       $rid = ($obs.AppRoles | Where-Object {{ $_.Value -eq '{ObsScope}' }}).Id", ConfigConstants.ObservabilityApiOtelWriteScope);
                    logger.LogInformation("       New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $bp.Id -PrincipalId $bp.Id -ResourceId $obs.Id -AppRoleId $rid");
                    logger.LogInformation("");
                    logger.LogInformation("     To share with your {Roles}:", AuthenticationConstants.S2SGrantRequiredRoles);
                    logger.LogInformation("       Blueprint : {BlueprintAppId}", blueprintAppId);
                    if (!string.IsNullOrWhiteSpace(results.TenantId))
                        logger.LogInformation("       Tenant    : {TenantId}", results.TenantId);
                    logger.LogInformation("       Run the PowerShell commands listed above.");
                }
            }
            if (pendingDelegatedAction)
            {
                actionCount++;
                logger.LogInformation("  {N}. Agent identity delegated permissions (PowerShell):", actionCount);
                logger.LogInformation("     Required role: {Roles}", AuthenticationConstants.DelegatedGrantRequiredRoles);
                logger.LogInformation("");
                logger.LogInformation("     Connect-MgGraph -TenantId '{TenantId}' -Scopes 'DelegatedPermissionGrant.ReadWrite.All', 'Directory.Read.All'", results.TenantId ?? "<tenant-id>");
                logger.LogInformation("");
                logger.LogInformation("     $agentSpId = '{AgentSpId}'", results.AgentIdentityId ?? "<agent-identity-sp-id>");
                logger.LogInformation("");
                logger.LogInformation("     # Observability API");
                logger.LogInformation("     $obsSp = Get-MgServicePrincipal -Filter \"appId eq '{ObsAppId}'\"", ConfigConstants.ObservabilityApiAppId);
                logger.LogInformation("     $body  = @{{ clientId = $agentSpId; consentType = 'AllPrincipals'; resourceId = $obsSp.Id; scope = '{ObsScope}' }} | ConvertTo-Json", ConfigConstants.ObservabilityApiOtelWriteScope);
                logger.LogInformation("     Invoke-MgGraphRequest -Method POST -Uri 'https://graph.microsoft.com/v1.0/oauth2PermissionGrants' -Body $body -ContentType 'application/json'");
                logger.LogInformation("");
                logger.LogInformation("     # Power Platform API");
                logger.LogInformation("     $ppSp  = Get-MgServicePrincipal -Filter \"appId eq '{PpAppId}'\"", PowerPlatformConstants.PowerPlatformApiResourceAppId);
                logger.LogInformation("     $body  = @{{ clientId = $agentSpId; consentType = 'AllPrincipals'; resourceId = $ppSp.Id; scope = '{PpScope}' }} | ConvertTo-Json", PowerPlatformConstants.PermissionNames.ConnectivityConnectionsRead);
                logger.LogInformation("     Invoke-MgGraphRequest -Method POST -Uri 'https://graph.microsoft.com/v1.0/oauth2PermissionGrants' -Body $body -ContentType 'application/json'");
            }
            if (messagingEndpointManualRequired)
            {
                actionCount++;
                logger.LogInformation("  {N}. Messaging endpoint — automated registration is not available for this tenant yet.", actionCount);
                logger.LogInformation("     Register it manually in the Teams Developer Portal:");
                logger.LogInformation("       {Url}", ConfigConstants.TeamsDeveloperPortalConfigureEndpointUrl);
            }
            if (messagingEndpointFailureRequired)
            {
                actionCount++;
                if (string.Equals(results.MessagingEndpointFailureReason, MessagingEndpointFailureReasons.NotOwner, StringComparison.Ordinal))
                {
                    logger.LogInformation("  {N}. Messaging endpoint — you are not an owner of the blueprint, so automated", actionCount);
                    logger.LogInformation("     registration was refused by the server. To complete this step, either:");
                    logger.LogInformation("");
                    logger.LogInformation("     A. Ask the blueprint owner to register the endpoint manually in the Teams");
                    logger.LogInformation("        Developer Portal:");
                    logger.LogInformation("          {Url}", ConfigConstants.TeamsDeveloperPortalConfigureEndpointUrl);
                    logger.LogInformation("");
                    logger.LogInformation("     B. Ask the blueprint owner to add you as a co-owner, then re-run just");
                    logger.LogInformation("        the endpoint step (no need to re-run the full setup):");
                    logger.LogInformation("          a365 setup blueprint --endpoint-only --m365");
                }
                else if (string.Equals(results.MessagingEndpointFailureReason, MessagingEndpointFailureReasons.BlueprintMissing, StringComparison.Ordinal))
                {
                    logger.LogInformation("  {N}. Messaging endpoint — not attempted because agent blueprint creation did not", actionCount);
                    logger.LogInformation("     complete. Resolve the blueprint step (see errors above), then re-run just");
                    logger.LogInformation("     the endpoint step:");
                    logger.LogInformation("       a365 setup blueprint --endpoint-only --m365");
                }
                else if (string.Equals(results.MessagingEndpointFailureReason, MessagingEndpointFailureReasons.NotConfigured, StringComparison.Ordinal))
                {
                    logger.LogInformation("  {N}. Messaging endpoint — register manually in the Teams Developer Portal:", actionCount);
                    logger.LogInformation("       {Url}", ConfigConstants.TeamsDeveloperPortalConfigureEndpointUrl);
                }
                else
                {
                    logger.LogInformation("  {N}. Messaging endpoint — registration failed; see the error above for details.", actionCount);
                    logger.LogInformation("     To retry after addressing the issue, re-run just the endpoint step:");
                    logger.LogInformation("       a365 setup blueprint --endpoint-only --m365");
                    logger.LogInformation("");
                    logger.LogInformation("     Or configure the endpoint manually in the Teams Developer Portal:");
                    logger.LogInformation("       {Url}", ConfigConstants.TeamsDeveloperPortalConfigureEndpointUrl);
                }
            }
        }

        if (results.Errors.Count > 0)
        {
            logger.LogError("");
            logger.LogError("Errors:");
            foreach (var error in results.Errors)
                logger.LogError("  {Error}", error);
        }

        // ── Warnings ───────────────────────────────────────────────────────────
        if (results.Warnings.Count > 0)
        {
            logger.LogInformation("");
            logger.LogInformation("Warnings:");
            foreach (var warning in results.Warnings)
                logger.LogWarning("  {Warning}", warning);
        }

        logger.LogInformation("");

        // Overall status line
        if (results.HasErrors)
            logger.LogWarning("Setup completed with errors");
        else if (hasActionRequired)
            logger.LogWarning("Setup completed — action required before proceeding");
        else if (results.HasWarnings)
            logger.LogInformation("Setup completed successfully with warnings");
        else
            logger.LogInformation("Setup completed successfully");

        // Next steps
        var hasNextSteps = results.HasErrors
            || !string.IsNullOrEmpty(results.GraphInheritablePermissionsError)
            || !string.IsNullOrEmpty(results.FederatedCredentialError)
            || results.AgentRegistrationFailed;

        if (hasNextSteps)
        {
            var nextStepLines = new List<Action>();

            if (results.BatchPermissionsPhase1Completed && (!results.BatchPermissionsPhase2Completed || (!tenantConsentGranted && !pendingAdminAction)) && results.HasErrors)
                nextStepLines.Add(() => logger.LogInformation("  To retry permissions: a365 setup all"));

            if (!string.IsNullOrEmpty(results.GraphInheritablePermissionsError))
                nextStepLines.Add(() => logger.LogInformation("  To retry Graph inheritable permissions: a365 setup blueprint"));

            if (!string.IsNullOrEmpty(results.FederatedCredentialError))
            {
                nextStepLines.Add(() =>
                {
                    logger.LogInformation("  Ensure 'AgentIdentityBlueprint.ReadWrite.All' is consented, then:");
                    logger.LogInformation("    a365 setup blueprint");
                });
            }

            if (results.AgentRegistrationFailed)
                nextStepLines.Add(() => logger.LogInformation("  To retry agent registration: a365 setup all --agent-registration-only"));

            if (nextStepLines.Count > 0)
            {
                logger.LogInformation("");
                logger.LogInformation("Next steps:");
                foreach (var line in nextStepLines)
                    line();
            }
        }
    }

    /// <summary>
    /// Populates <c>resourceConsents[*].consentUrl</c> in the generated config for the required
    /// resources. Called when the current user lacks the Global Administrator role so that the URLs
    /// can be saved to <c>a365.generated.config.json</c> and shared with a tenant administrator.
    /// <para>
    /// Graph, Agent 365 Tools (MCP), Observability API, and Power Platform API URLs are always
    /// generated. Messaging Bot API is included only when <paramref name="isM365"/> is true —
    /// non-M365 tenants typically lack the Messaging Bot resource SP and the consent endpoint
    /// returns AADSTS650053 otherwise.
    /// </para>
    /// </summary>
    /// <returns>Display names of the resources for which URLs were saved.</returns>
    internal static List<string> PopulateAdminConsentUrls(
        Agent365Config config,
        string mcpResourceAppId,
        IEnumerable<string> mcpScopes,
        bool isM365 = true)
    {
        var urls = BuildAdminConsentUrls(config.TenantId, config.AgentBlueprintId!, config.AgentApplicationScopes, mcpScopes, isM365);

        // Map resource names to App IDs for upsert into ResourceConsents
        var appIdByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft Graph"]   = AuthenticationConstants.MicrosoftGraphResourceAppId,
            ["Agent 365 Tools"]   = mcpResourceAppId,
            ["Messaging Bot API"] = ConfigConstants.MessagingBotApiAppId,
            ["Observability API"] = ConfigConstants.ObservabilityApiAppId,
            ["Power Platform API"] = PowerPlatformConstants.PowerPlatformApiResourceAppId,
        };

        var populated = new List<string>();
        foreach (var (resourceName, consentUrl) in urls)
        {
            if (!appIdByName.TryGetValue(resourceName, out var appId)) continue;

            var existing = config.ResourceConsents.FirstOrDefault(
                rc => rc.ResourceAppId.Equals(appId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.ConsentUrl = consentUrl;
            }
            else
            {
                config.ResourceConsents.Add(new Models.ResourceConsent
                {
                    ResourceName = resourceName,
                    ResourceAppId = appId,
                    ConsentUrl = consentUrl,
                    ConsentGranted = false,
                });
            }
            populated.Add(resourceName);
        }
        return populated;
    }

    /// <summary>
    /// Builds a single /v2.0/adminconsent URL from fully-qualified scope URIs.
    /// All callers must pass fully-qualified scopes (e.g. "https://graph.microsoft.com/User.Read").
    /// Each scope is individually Uri.EscapeDataString-encoded and joined with %20.
    /// A random GUID state parameter is generated for CSRF protection.
    /// </summary>
    internal static string BuildAdminConsentUrl(string tenantId, string clientId, IEnumerable<string> fullyQualifiedScopes)
    {
        var scopeParam = string.Join("%20", fullyQualifiedScopes.Select(Uri.EscapeDataString));
        var redirectEncoded = Uri.EscapeDataString(AuthenticationConstants.BlueprintConsentRedirectUri);
        return $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent?client_id={clientId}&scope={scopeParam}&redirect_uri={redirectEncoded}&state={Guid.NewGuid():N}";
    }

    /// <summary>
    /// Builds per-resource admin consent URLs covering every resource stamped on the blueprint
    /// (mirrors <see cref="BuildConfiguredPermissionSpecsAsync"/>): Microsoft Graph (when
    /// <paramref name="graphScopes"/> non-empty), Agent 365 Tools (when <paramref name="mcpScopes"/>
    /// non-empty), Messaging Bot API (when <paramref name="isM365"/> is true), Observability API,
    /// and Power Platform API.
    /// <para>
    /// Messaging Bot is gated on <paramref name="isM365"/> because non-M365 tenants typically
    /// lack the Messaging Bot resource SP, in which case the /v2.0/adminconsent endpoint returns
    /// AADSTS650053. The other resources have SPs created during blueprint permission stamping,
    /// so their URLs are always emitted whenever their scopes are present.
    /// </para>
    /// </summary>
    internal static List<(string ResourceName, string ConsentUrl)> BuildAdminConsentUrls(
        string tenantId,
        string blueprintClientId,
        IEnumerable<string> graphScopes,
        IEnumerable<string> mcpScopes,
        bool isM365 = true)
    {
        var urls = new List<(string, string)>();

        static string Build(string tenant, string client, string resourceUri, IEnumerable<string> scopes)
            => BuildAdminConsentUrl(tenant, client, scopes.Select(s => $"{resourceUri}/{s}"));

        var graphScopeList = graphScopes.ToList();
        if (graphScopeList.Count > 0)
            urls.Add(("Microsoft Graph", Build(tenantId, blueprintClientId, AuthenticationConstants.MicrosoftGraphResourceUri, graphScopeList)));

        var mcpScopeList = mcpScopes.ToList();
        if (mcpScopeList.Count > 0)
            urls.Add(("Agent 365 Tools", Build(tenantId, blueprintClientId, McpConstants.Agent365ToolsIdentifierUri, mcpScopeList)));

        if (isM365)
            urls.Add(("Messaging Bot API", Build(tenantId, blueprintClientId, ConfigConstants.MessagingBotApiIdentifierUri, new[] { ConfigConstants.MessagingBotApiAdminConsentScope })));

        urls.Add(("Observability API", Build(tenantId, blueprintClientId, ConfigConstants.ObservabilityApiIdentifierUri, new[] { ConfigConstants.ObservabilityApiOtelWriteScope })));
        urls.Add(("Power Platform API", Build(tenantId, blueprintClientId, PowerPlatformConstants.PowerPlatformApiIdentifierUri, new[] { PowerPlatformConstants.PermissionNames.ConnectivityConnectionsRead })));

        return urls;
    }

    /// <summary>
    /// Builds a single combined /v2.0/adminconsent URL covering every resource stamped on the
    /// blueprint: Graph, Agent 365 Tools (MCP), Observability API, Power Platform API, and
    /// Messaging Bot API (only when <paramref name="isM365"/> is true).
    /// <para>
    /// Messaging Bot is gated on <paramref name="isM365"/> because non-M365 tenants typically
    /// lack the Messaging Bot resource SP, which would cause the entire combined consent grant
    /// to fail with AADSTS650053. All other resources have SPs provisioned during blueprint
    /// permission stamping, so they are safe to include unconditionally.
    /// </para>
    /// </summary>
    internal static string BuildCombinedConsentUrl(
        string tenantId,
        string blueprintClientId,
        IEnumerable<string> graphScopes,
        IEnumerable<string> mcpScopes,
        bool isM365 = true)
    {
        var allScopes = new List<string>();
        foreach (var s in graphScopes)
            allScopes.Add($"{AuthenticationConstants.MicrosoftGraphResourceUri}/{s}");
        foreach (var s in mcpScopes)
            allScopes.Add($"{McpConstants.Agent365ToolsIdentifierUri}/{s}");
        if (isM365)
            allScopes.Add($"{ConfigConstants.MessagingBotApiIdentifierUri}/{ConfigConstants.MessagingBotApiAdminConsentScope}");
        allScopes.Add($"{ConfigConstants.ObservabilityApiIdentifierUri}/{ConfigConstants.ObservabilityApiOtelWriteScope}");
        allScopes.Add($"{PowerPlatformConstants.PowerPlatformApiIdentifierUri}/{PowerPlatformConstants.PermissionNames.ConnectivityConnectionsRead}");
        return BuildAdminConsentUrl(tenantId, blueprintClientId, allScopes);
    }

    /// <summary>
    /// Populates per-resource consent URLs in config and sets <see cref="SetupResults.CombinedConsentUrl"/>
    /// when the running account is not a Global Administrator. Called by both DW and non-DW setup paths
    /// after the batch permissions step.
    /// <para>
    /// Messaging Bot API URLs are included only when <paramref name="isM365"/> is true; all other
    /// resources (Graph, MCP, Observability, Power Platform) are always included so a tenant admin
    /// can complete the hand-off with a single URL. No-op if admin consent was already granted or
    /// the blueprint ID is absent.
    /// </para>
    /// </summary>
    internal static void ApplyConsentUrlsIfNeeded(
        SetupContext ctx,
        string mcpResourceAppId,
        IEnumerable<string> graphScopes,
        IEnumerable<string> mcpScopes,
        bool isM365 = true)
    {
        if (ctx.Results.TenantWideConsentOutcome == Models.GrantOutcome.Granted || string.IsNullOrWhiteSpace(ctx.Config.AgentBlueprintId))
            return;

        var consentResourceNames = PopulateAdminConsentUrls(ctx.Config, mcpResourceAppId, mcpScopes, isM365);
        ctx.Results.ConsentUrlsSavedToPath = ctx.GeneratedConfigPath;
        ctx.Results.ConsentResourceNames.AddRange(consentResourceNames);
        ctx.Results.CombinedConsentUrl = BuildCombinedConsentUrl(
            ctx.Config.TenantId!, ctx.Config.AgentBlueprintId!,
            graphScopes, mcpScopes, isM365);
    }

    /// <summary>
    /// Prints the dry-run plan for the AI Teammate agent (--aiteammate true) path of setup all.
    /// </summary>
    internal static void PrintDwSetupAllDryRunPlan(
        ILogger logger,
        bool skipInfrastructure,
        bool skipRequirements,
        string[] rawArgs,
        Agent365Config? config = null,
        bool isM365 = false)
    {
        var sub = new string(' ', DryRunValCol);

        var cmdArgs = string.Join(' ', rawArgs.Where(a => !a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)));
        logger.LogInformation("Dry run: a365 {Args} --dry-run", cmdArgs);
        logger.LogInformation("");
        logger.LogInformation("The following steps would be performed.");
        logger.LogInformation("");

        // 1. Prerequisites
        if (skipRequirements)
            logger.LogInformation(DryRunRow(1, "Prerequisites") + "skip (--skip-requirements)");
        else
            logger.LogInformation(DryRunRow(1, "Prerequisites") + "validate (PowerShell modules, Azure CLI)");

        // 2. Azure hosting — always externally managed; infrastructure provisioning has been removed
        logger.LogInformation(DryRunRow(2, "Azure hosting") + "skip — hosting is externally managed (provide messagingEndpoint in config)");

        // 3. Blueprint — context-aware when config is available
        if (!string.IsNullOrWhiteSpace(config?.AgentBlueprintId))
        {
            PrintDryRunBlueprintReuseRows(logger, config.AgentBlueprintId!, step: 3);
        }
        else
        {
            var blueprintDisplayName = config?.AgentBlueprintDisplayName;
            if (!string.IsNullOrWhiteSpace(blueprintDisplayName))
                logger.LogInformation(DryRunRow(3, "Blueprint") + "create (multi-tenant): {DisplayName}", blueprintDisplayName);
            else
                logger.LogInformation(DryRunRow(3, "Blueprint") + "create (multi-tenant)");
            logger.LogInformation(sub + "create service principal");
            logger.LogInformation(sub + "create client secret");
            logger.LogInformation(sub + "create federated identity credential (FIC)");
            logger.LogInformation(sub + "create managed identity");
        }

        // 4. Inheritable Permissions
        logger.LogInformation(DryRunRow(4, "Inheritable Permissions") + "configure for Microsoft Graph, Agent 365 Tools, Messaging Bot API, Observability API, Power Platform API");

        // 5. Blueprint Permission Grants
        logger.LogInformation(DryRunRow(5, "Blueprint Permission Grants") + "admin approval required — see 'Action Required' in setup output");

        // 6. Messaging endpoint (M365 opt-in)
        if (isM365)
        {
            var endpointForDisplay = config?.MessagingEndpoint;
            var endpointDetail = string.IsNullOrWhiteSpace(endpointForDisplay)
                ? "register via Teams Graph (requires 'messagingEndpoint' in config)"
                : $"register via Teams Graph: {endpointForDisplay}";
            logger.LogInformation(DryRunRow(6, "Messaging endpoint") + endpointDetail);
        }
        else
        {
            logger.LogInformation(DryRunRow(6, "Messaging endpoint") + "skipped (non-M365 agent)");
        }

        // 7. Project settings (DW has no Agent identity or Agent Registration steps)
        logger.LogInformation(DryRunRow(7, "Project settings") + "write to appsettings.json");

        logger.LogInformation("");
        logger.LogInformation("No changes will be made. Run without --dry-run to apply.");
    }

    /// <summary>
    /// Unified method to configure all permissions (OAuth2 grants, required resource access, inheritable permissions) for a resource
    /// </summary>
    /// <param name="graph">Graph API service</param>
    /// <param name="blueprintService">Agent blueprint service for permissions operations</param>
    /// <param name="config">Agent365 configuration</param>
    /// <param name="resourceAppId">The resource application ID to grant permissions for</param>
    /// <param name="resourceName">Display name of the resource for logging</param>
    /// <param name="scopes">Permission scopes to grant</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="addToRequiredResourceAccess">Whether to add permissions to app manifest (visible in portal)</param>
    /// <param name="setInheritablePermissions">Whether to set inheritable permissions for agent blueprints</param>
    /// <param name="setupResults">Optional setup results for tracking warnings</param>
    /// <param name="ct">Cancellation token</param>
    public static async Task EnsureResourcePermissionsAsync(
        GraphApiService graph,
        AgentBlueprintService blueprintService,
        Agent365Config config,
        string resourceAppId,
        string resourceName,
        string[] scopes,
        ILogger logger,
        bool addToRequiredResourceAccess = true,
        bool setInheritablePermissions = true,
        SetupResults? setupResults = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
            throw new SetupValidationException("AgentBlueprintId (appId) is required.");

        // Use delegated token provider for *all* permission operations to avoid bouncing between Azure CLI auth and Microsoft Graph PowerShell auth.
        var permissionGrantScopes = AuthenticationConstants.RequiredPermissionGrantScopes;

        // Pre-warm the delegated token once
        var user = await graph.GraphGetAsync(
            config.TenantId,
            "/v1.0/me?$select=id",
            ct,
            scopes: permissionGrantScopes);
        
        if (user == null)
        {
            throw new SetupValidationException(
                "Failed to authenticate to Microsoft Graph with delegated permissions. " +
                "Check the errors above for the specific cause. Common causes: " +
                "missing PowerShell module (run 'a365 setup requirements' to install), " +
                "insufficient permissions, or sign-in was cancelled.");
        }

        // Retry: Azure AD service principal propagation can lag 10-30s after blueprint creation.
        var retryHelperSp = new RetryHelper(logger);
        var blueprintSpObjectId = await retryHelperSp.ExecuteWithRetryAsync(
            operation: (innerCt) => graph.LookupServicePrincipalByAppIdAsync(config.TenantId, config.AgentBlueprintId, innerCt, permissionGrantScopes),
            shouldRetry: result => string.IsNullOrWhiteSpace(result),
            maxRetries: 5,
            baseDelaySeconds: 5,
            cancellationToken: ct);

        if (string.IsNullOrWhiteSpace(blueprintSpObjectId))
        {
            throw new SetupValidationException($"Blueprint Service Principal not found for appId {config.AgentBlueprintId}. " +
                "The service principal may not have propagated yet. Wait a few minutes and retry.");
        }

        // Ensure resource service principal exists
        var resourceSpObjectId = await graph.EnsureServicePrincipalForAppIdAsync(config.TenantId, resourceAppId, ct, permissionGrantScopes);
        if (string.IsNullOrWhiteSpace(resourceSpObjectId))
        {
            throw new SetupValidationException($"{resourceName} Service Principal not found for appId {resourceAppId}. " +
                $"Ensure the {resourceName} application is available in your tenant.");
        }

        // 1. Add to required resource access (makes permissions visible in portal)
        if (addToRequiredResourceAccess)
        {
            logger.LogInformation("   - Adding {ResourceName} to blueprint's required resource access", resourceName);
            var addedResourceAccess = await blueprintService.AddRequiredResourceAccessAsync(
                config.TenantId,
                config.AgentBlueprintId,
                resourceAppId,
                scopes,
                isDelegated: true,
                ct,
                requiredScopes: permissionGrantScopes);

            if (!addedResourceAccess)
            {
                logger.LogWarning("Failed to add {ResourceName} to required resource access. Permissions may not be visible in portal.", resourceName);
            }
        }

        // 2. Grant OAuth2 permissions (admin consent)
        logger.LogDebug("   - OAuth2 grant: client {ClientId} to resource {ResourceId} scopes [{Scopes}]",
            blueprintSpObjectId, resourceSpObjectId, string.Join(' ', scopes));

        var grantResult = await graph.CreateOrUpdateOauth2PermissionGrantWithDetailsAsync(
            config.TenantId, blueprintSpObjectId, resourceSpObjectId, scopes, ct, permissionGrantScopes);

        if (!grantResult.Success)
        {
            // Surface the underlying HTTP status and Graph error code in the exception context
            // so callers can branch on the failure reason (for example, distinguishing 403 /
            // Authorization_RequestDenied from network or other failures) without parsing the
            // exception message text.
            var failureContext = new Dictionary<string, string>();
            if (grantResult.StatusCode > 0)
                failureContext["graphStatusCode"] = grantResult.StatusCode.ToString();
            if (!string.IsNullOrEmpty(grantResult.ErrorCode))
                failureContext["graphErrorCode"] = grantResult.ErrorCode;

            throw new SetupValidationException(
                $"Failed to create/update OAuth2 permission grant from blueprint {config.AgentBlueprintId} to {resourceName} {resourceAppId}. " +
                "This may be due to insufficient permissions. Ensure you have AgentIdentityBlueprint.ReadWrite.All permission consented on your client app.",
                context: failureContext.Count > 0 ? failureContext : null);
        }

        // 3. Set inheritable permissions (for agent blueprints)
        bool inheritanceConfigured = false;
        bool inheritanceAlreadyExisted = false;
        string? inheritanceError = null;

        if (setInheritablePermissions)
        {
            logger.LogInformation("   - Configuring inheritable permissions: blueprint {Blueprint} to resourceAppId {ResourceAppId} scopes [{Scopes}]",
                config.AgentBlueprintId, resourceAppId, string.Join(' ', scopes));

            // Use custom client app auth for inheritable permissions — Azure CLI doesn't support this operation.
            // permissionGrantScopes is intentionally empty in the current permission set (the operation that
            // previously required AgentIdentityBlueprint.UpdateAuthProperties.All is now covered by the
            // AgentIdentityBlueprint.ReadWrite.All umbrella in RequiredClientAppPermissions). When the scope
            // collection is empty, SetInheritablePermissionsAsync falls through to the standard token path
            // which already carries every required scope; passing the array here keeps a single call shape
            // regardless of whether the set is empty or repopulated in a future revision.
            var (ok, alreadyExists, err) = await blueprintService.SetInheritablePermissionsAsync(
                config.TenantId, config.AgentBlueprintId, resourceAppId, scopes, requiredScopes: permissionGrantScopes, ct);

            if (!ok && !alreadyExists)
            {
                throw new SetupValidationException($"Failed to set inheritable permissions: {err}. " +
                    "Ensure you have AgentIdentityBlueprint.ReadWrite.All permission consented on your custom client app.");
            }

            if (alreadyExists)
            {
                logger.LogInformation("   - Inheritable permissions already configured for {ResourceName}", resourceName);
            }
            else
            {
                logger.LogInformation("   - Inheritable permissions created for {ResourceName}", resourceName);
            }

            inheritanceConfigured = true;
            inheritanceAlreadyExisted = alreadyExists;

            // Verify inheritable permissions were actually set (non-blocking verification with retry)
            try
            {
                logger.LogInformation("   - Verifying inheritable permissions for {ResourceName}", resourceName);
                
                var retryHelper = new RetryHelper(logger);
                var verificationResult = await retryHelper.ExecuteWithRetryAsync(
                    operation: async (ct) =>
                    {
                        var (exists, scopesAllAllowed, rolesAllAllowed, verifyError) = await blueprintService.VerifyInheritablePermissionsAsync(
                            config.TenantId, config.AgentBlueprintId, resourceAppId, ct, permissionGrantScopes);
                        return (exists, scopesAllAllowed, rolesAllAllowed, verifyError);
                    },
                    shouldRetry: (result) =>
                    {
                        // Retry if permissions don't exist yet (Graph API propagation delay)
                        // Don't retry on actual errors (verifyError != null) - fail fast
                        return !result.exists && string.IsNullOrEmpty(result.verifyError);
                    },
                    maxRetries: 5,
                    baseDelaySeconds: 2,
                    cancellationToken: ct);

                var (exists, scopesAllAllowed, rolesAllAllowed, verifyError) = verificationResult;

                if (!string.IsNullOrEmpty(verifyError))
                {
                    logger.LogWarning("Could not verify {ResourceName} inheritable permissions: {Error}", resourceName, verifyError);
                    setupResults?.Warnings.Add($"Could not verify {resourceName} inheritable permissions: {verifyError}");
                }
                else if (!exists)
                {
                    var warning = $"{resourceName} inheritable permissions not found after configuration. " +
                        $"Agent instances may not inherit these permissions. " +
                        $"Verify manually: GET /beta/applications/microsoft.graph.agentIdentityBlueprint/{config.AgentBlueprintId}/inheritablePermissions";
                    logger.LogWarning(warning);
                    setupResults?.Warnings.Add(warning);
                }
                else if (!scopesAllAllowed || !rolesAllAllowed)
                {
                    var warning = $"{resourceName} inheritable permissions are not at kind=allAllowed for both scopes and roles " +
                        $"(scopes allAllowed={scopesAllAllowed}, roles allAllowed={rolesAllAllowed}). " +
                        $"Agent identities may not inherit the full set. " +
                        $"Re-run 'a365 setup permissions' to reconcile.";
                    logger.LogWarning(warning);
                    setupResults?.Warnings.Add(warning);
                }
                else
                {
                    logger.LogInformation("   - Verified: {ResourceName} inheritable permissions at kind=allAllowed on scopes and roles", resourceName);
                }
            }
            catch (Exception verifyEx)
            {
                // Verification is non-critical - log warning but don't fail setup
                logger.LogWarning("Failed to verify {ResourceName} inheritable permissions: {Message}. Setup will continue.", resourceName, verifyEx.Message);
                setupResults?.Warnings.Add($"Could not verify {resourceName} inheritable permissions: {verifyEx.Message}");
            }
        }

        // Track if permissions already existed for accurate summary logging
        if (setupResults != null && inheritanceConfigured)
        {
            // Update flags based on resource type
            if (resourceName.Contains("Tools", StringComparison.OrdinalIgnoreCase) || 
                resourceName.Contains("MCP", StringComparison.OrdinalIgnoreCase))
            {
                setupResults.McpPermissionsAlreadyExisted = inheritanceAlreadyExisted;
                setupResults.InheritablePermissionsAlreadyExisted = inheritanceAlreadyExisted;
            }
            else if (resourceName.Contains("Bot", StringComparison.OrdinalIgnoreCase))
            {
                setupResults.BotApiPermissionsAlreadyExisted = inheritanceAlreadyExisted;
                setupResults.BotInheritablePermissionsAlreadyExisted = inheritanceAlreadyExisted;
            }
        }

        // 4. Update resource consents collection
        var existingConsent = config.ResourceConsents.FirstOrDefault(rc => 
            rc.ResourceAppId.Equals(resourceAppId, StringComparison.OrdinalIgnoreCase));

        if (existingConsent != null)
        {
            // Update existing consent record
            existingConsent.ConsentGranted = true;
            existingConsent.ConsentTimestamp = DateTime.UtcNow;
            existingConsent.Scopes = scopes.ToList();
            existingConsent.InheritablePermissionsConfigured = inheritanceConfigured;
            existingConsent.InheritablePermissionsAlreadyExist = inheritanceAlreadyExisted;
            existingConsent.InheritablePermissionsError = inheritanceError;
        }
        else
        {
            // Add new consent record
            config.ResourceConsents.Add(new ResourceConsent
            {
                ResourceName = resourceName,
                ResourceAppId = resourceAppId,
                ConsentGranted = true,
                ConsentTimestamp = DateTime.UtcNow,
                Scopes = scopes.ToList(),
                InheritablePermissionsConfigured = inheritanceConfigured,
                InheritablePermissionsAlreadyExist = inheritanceAlreadyExisted,
                InheritablePermissionsError = inheritanceError
            });
        }
    }

    /// <summary>
    /// Registers the Teams Graph backend configuration (messaging endpoint) for the agent blueprint.
    /// </summary>
    /// <param name="setupConfig">Agent365 configuration.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="backendConfigurator">Blueprint backend configurator service.</param>
    /// <param name="overrideEndpointUrl">Optional endpoint URL override (used by --update-endpoint).</param>
    /// <param name="correlationId">Optional correlation ID for tracing.</param>
    /// <returns>
    /// A tuple of (Result, FailureReason) from the Teams Graph call. Callers are expected to
    /// check for <see cref="Models.EndpointRegistrationResult.SkippedContractMismatch"/> to surface
    /// the rollout-in-progress fallback messaging in their summary. FailureReason is "NotOwner"
    /// or "Other" when Result is Failed, null otherwise.
    /// </returns>
    public static async Task<(Models.EndpointRegistrationResult Result, string? FailureReason)> RegisterBlueprintMessagingEndpointAsync(
        Agent365Config setupConfig,
        ILogger logger,
        ITeamsGraphBackendConfigurator backendConfigurator,
        string? overrideEndpointUrl = null,
        string? correlationId = null)
    {
        if (string.IsNullOrEmpty(setupConfig.AgentBlueprintId))
        {
            logger.LogError("Agent Blueprint ID not found. Blueprint creation may have failed.");
            throw new SetupValidationException(
                issueDescription: "Agent blueprint was not found - messaging endpoint cannot be registered.",
                errorDetails: new List<string>
                {
                    "AgentBlueprintId is missing from configuration. This usually means the blueprint creation step failed or a365.generated.config.json is out of sync."
                },
                mitigationSteps: new List<string>
                {
                    "Verify that 'a365 setup' completed Step 1 (Agent blueprint creation) without errors.",
                    "Check a365.generated.config.json for 'agentBlueprintId'. If it's missing or incorrect, re-run 'a365 setup'."
                },
                context: new Dictionary<string, string>
                {
                    ["AgentBlueprintId"] = setupConfig.AgentBlueprintId ?? "<null>"
                });
        }

        string messagingEndpoint;

        if (!string.IsNullOrWhiteSpace(overrideEndpointUrl))
        {
            if (!Uri.TryCreate(overrideEndpointUrl, UriKind.Absolute, out var overrideUri) ||
                overrideUri.Scheme != Uri.UriSchemeHttps)
            {
                logger.LogError("Custom endpoint must be a valid HTTPS URL. Current value: {Endpoint}", overrideEndpointUrl);
                throw new SetupValidationException("Custom endpoint must be a valid HTTPS URL.");
            }

            messagingEndpoint = overrideEndpointUrl;
            logger.LogInformation("   - Using override endpoint URL");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(setupConfig.MessagingEndpoint))
            {
                logger.LogWarning("MessagingEndpoint not configured. Skipping endpoint registration.");
                return (Models.EndpointRegistrationResult.Failed, MessagingEndpointFailureReasons.NotConfigured);
            }

            if (!Uri.TryCreate(setupConfig.MessagingEndpoint, UriKind.Absolute, out var messagingEndpointUri) ||
                messagingEndpointUri.Scheme != Uri.UriSchemeHttps)
            {
                logger.LogError("MessagingEndpoint must be a valid HTTPS URL. Current value: {Endpoint}",
                    setupConfig.MessagingEndpoint);
                throw new SetupValidationException("MessagingEndpoint must be a valid HTTPS URL.");
            }

            messagingEndpoint = setupConfig.MessagingEndpoint;
        }

        logger.LogInformation("   - Registering blueprint messaging endpoint");
        logger.LogInformation("     * Messaging Endpoint: {Endpoint}", messagingEndpoint);
        logger.LogInformation("     * Agent Blueprint ID: {AgentBlueprintId}", setupConfig.AgentBlueprintId);

        var (result, failureReason) = await backendConfigurator.SetBackendConfigurationAsync(
            agentBlueprintId: setupConfig.AgentBlueprintId,
            messagingEndpoint: messagingEndpoint,
            correlationId: correlationId);

        if (result == Models.EndpointRegistrationResult.Created ||
            result == Models.EndpointRegistrationResult.AlreadyExists)
        {
            setupConfig.BotId = setupConfig.AgentBlueprintId;
            setupConfig.BotMsaAppId = setupConfig.AgentBlueprintId;
            setupConfig.BotMessagingEndpoint = messagingEndpoint;
        }

        return (result, failureReason);
    }

}
