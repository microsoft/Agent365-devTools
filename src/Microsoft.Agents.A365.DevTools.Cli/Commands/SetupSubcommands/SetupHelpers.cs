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

    internal const int DryRunValCol = 30;
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
    /// Returns the fixed-scope ResourcePermissionSpecs for the three platform APIs that every
    /// own-identity agent blueprint requires: Messaging Bot API, Observability API, and Power Platform API.
    /// Callers control whether the specs set inheritable permissions on the blueprint.
    /// </summary>
    internal static ResourcePermissionSpec[] GetFixedApiPermissionSpecs(bool setInheritable) =>
    [
        new ResourcePermissionSpec(
            ConfigConstants.MessagingBotApiAppId,
            "Messaging Bot API",
            new[] { "Authorization.ReadWrite", "user_impersonation" },
            setInheritable),
        new ResourcePermissionSpec(
            ConfigConstants.ObservabilityApiAppId,
            "Observability API",
            new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
            setInheritable,
            AppRoleScopes: new[] { ConfigConstants.ObservabilityApiOtelWriteScope }),
        new ResourcePermissionSpec(
            PowerPlatformConstants.PowerPlatformApiResourceAppId,
            "Power Platform API",
            new[] { PowerPlatformConstants.PermissionNames.ConnectivityConnectionsRead },
            setInheritable),
    ];

    /// <summary>
    /// Returns the fixed-scope ResourcePermissionSpecs for the non-DW (blueprint) path:
    /// Observability API and Power Platform API only.
    /// Messaging Bot API is DW-only. Microsoft Graph and Agent 365 Tools (MCP) are not
    /// included — they are added by the DW flow via BuildPermissionSpecsAsync.
    /// To enable MCP or Messaging Bot API for non-DW, add their specs here and update
    /// the corresponding consent URL guards in BuildAdminConsentUrls / BuildCombinedConsentUrl.
    /// </summary>
    internal static ResourcePermissionSpec[] GetNonDwFixedApiPermissionSpecs(bool setInheritable) =>
    [
        new ResourcePermissionSpec(
            ConfigConstants.ObservabilityApiAppId,
            "Observability API",
            new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
            setInheritable,
            AppRoleScopes: new[] { ConfigConstants.ObservabilityApiOtelWriteScope }),
        new ResourcePermissionSpec(
            PowerPlatformConstants.PowerPlatformApiResourceAppId,
            "Power Platform API",
            new[] { PowerPlatformConstants.PermissionNames.ConnectivityConnectionsRead },
            setInheritable),
    ];

    /// <summary>
    /// Builds the full resource permission spec list from config for the DW/config-dir flows.
    /// Includes Microsoft Graph, manifest-derived Agent 365 Tools scopes, fixed platform APIs,
    /// and any custom blueprint permissions.
    /// <para>
    /// Pass a pre-computed <paramref name="scopesByAudience"/> to avoid reading the MCP manifest
    /// a second time when the caller already has it (e.g. <c>AllSubcommand.BuildPermissionSpecsAsync</c>).
    /// When <c>null</c>, the manifest is read from <c>config.DeploymentProjectPath</c>.
    /// </para>
    /// </summary>
    internal static async Task<List<ResourcePermissionSpec>> BuildConfiguredPermissionSpecsAsync(
        Agent365Config config,
        bool setInheritable,
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
        specs.AddRange(GetFixedApiPermissionSpecs(setInheritable));

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

        return clientAppId;
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
        string? agentIdentityAppId = null,
        IReadOnlyList<(string ResourceName, string ResourceAppId, string Scope, string PermissionType)>? specs = null)
    {
        specs ??= NonDwAdminConsentSpecs;

        var appSpecs = specs.Where(s => s.PermissionType == "Application").ToList();
        var delegatedSpecs = specs.Where(s => s.PermissionType == "Delegated").ToList();
        var hasAgentIdentity = !string.IsNullOrWhiteSpace(agentIdentityAppId);

        // Option A — Entra portal
        logger.LogInformation("     Option A — Entra portal:");
        logger.LogInformation("       1. Open https://entra.microsoft.com");
        logger.LogInformation("       2. Navigate to: Identity > Applications > App registrations");
        logger.LogInformation("       3. Search for the blueprint app by ID: {BlueprintId}", blueprintId);
        logger.LogInformation("          (switch to 'All applications' tab if not shown under 'Owned applications')");
        logger.LogInformation("       4. Open the app, go to: API permissions");
        logger.LogInformation("       5. Confirm the following permissions are listed:");
        foreach (var group in specs.GroupBy(s => (s.ResourceName, s.Scope)))
        {
            var types = string.Join(", ", group.Select(s => s.PermissionType));
            logger.LogInformation("            - {ResourceName,-20}: {Scope} ({PermTypes})", group.Key.ResourceName, group.Key.Scope, types);
        }
        logger.LogInformation("       6. Click 'Grant admin consent for your organization' and confirm");
        if (hasAgentIdentity && appSpecs.Count > 0)
        {
            logger.LogInformation("       7. Grant Application permissions to the agent identity (required for S2S token acquisition):");
            logger.LogInformation("          - Search for the agent identity app by ID: {AgentIdentityAppId}", agentIdentityAppId);
            logger.LogInformation("          - Open the app, go to: API permissions");
            logger.LogInformation("          - Confirm the following Application permissions are listed:");
            foreach (var (resourceName, _, scope, _) in appSpecs)
                logger.LogInformation("               - {ResourceName,-20}: {Scope} (Application)", resourceName, scope);
            logger.LogInformation("          - Click 'Grant admin consent for your organization' and confirm");
        }

        // Option B — PowerShell (Microsoft Graph SDK)
        // Use Invoke-MgGraphRequest instead of New-MgOauth2PermissionGrant to avoid
        // assembly conflicts when Microsoft.Graph.Identity.SignIns is already partially loaded.
        var mgScopes = new List<string> { "\"Directory.Read.All\"" };
        if (appSpecs.Count > 0) mgScopes.Add("\"AppRoleAssignment.ReadWrite.All\"");
        if (delegatedSpecs.Count > 0) mgScopes.Add("\"DelegatedPermissionGrant.ReadWrite.All\"");

        logger.LogInformation("");
        logger.LogInformation("     Option B — PowerShell (Microsoft.Graph module required):");
        logger.LogInformation("       Install-Module Microsoft.Graph -Scope CurrentUser  # skip if already installed");
        logger.LogInformation("       Connect-MgGraph -Scopes {Scopes}", string.Join(", ", mgScopes));
        logger.LogInformation("       $bp = Get-MgServicePrincipal -Filter \"appId eq '{BlueprintId}'\"", blueprintId);
        if (hasAgentIdentity)
            logger.LogInformation("       $ai = Get-MgServicePrincipal -Filter \"appId eq '{AgentIdentityAppId}'\"", agentIdentityAppId);

        if (appSpecs.Count > 0)
        {
            logger.LogInformation("       # Application permissions (app role assignments — blueprint and agent identity)");
            foreach (var (resourceName, resourceAppId, scope, _) in appSpecs)
            {
                var varName = "$" + resourceName.Replace(" ", "").Replace("API", "").ToLowerInvariant();
                logger.LogInformation("       {Var} = Get-MgServicePrincipal -Filter \"appId eq '{AppId}'\"  # {Name}",
                    varName, resourceAppId, resourceName);
                logger.LogInformation("       $rid = ({Var}.AppRoles | Where-Object {{ $_.Value -eq '{Scope}' }}).Id",
                    varName, scope);
                logger.LogInformation("       New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $bp.Id -PrincipalId $bp.Id -ResourceId {Var}.Id -AppRoleId $rid",
                    varName);
                if (hasAgentIdentity)
                    logger.LogInformation("       New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $ai.Id -PrincipalId $ai.Id -ResourceId {Var}.Id -AppRoleId $rid",
                        varName);
            }
        }

        if (delegatedSpecs.Count > 0)
        {
            logger.LogInformation("       # Delegated permissions (oauth2PermissionGrants)");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (resourceName, resourceAppId, _, _) in delegatedSpecs)
            {
                var idVarName = "$" + resourceName.Replace(" ", "").Replace("API", "").ToLowerInvariant() + "Id";
                if (seen.Add(idVarName))
                    logger.LogInformation("       {Var} = (Get-MgServicePrincipal -Filter \"appId eq '{AppId}'\").Id  # {Name}",
                        idVarName, resourceAppId, resourceName);
            }
            foreach (var (resourceName, _, scope, _) in delegatedSpecs)
            {
                var idVarName = "$" + resourceName.Replace(" ", "").Replace("API", "").ToLowerInvariant() + "Id";
                logger.LogInformation("       Invoke-MgGraphRequest -Method POST -Uri 'https://graph.microsoft.com/v1.0/oauth2PermissionGrants' `");
                logger.LogInformation("         -Body @{{ clientId = $bp.Id; consentType = 'AllPrincipals'; resourceId = {Var}; scope = '{Scope}' }}",
                    idVarName, scope);
            }
        }
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
    /// Display comprehensive setup summary showing what succeeded and what failed
    /// </summary>
    public static void DisplaySetupSummary(SetupResults results, ILogger logger, bool isDw = true)
    {
        // Prefer the flag set on results — it is reliable regardless of which code path calls this.
        var isNonDw = results.IsNonDwBlueprintFlow || !isDw;
        var notRun = "not run (previous step failed)";

        logger.LogInformation("");
        logger.LogInformation("Setup Summary");
        logger.LogInformation("");

        var pendingAdminAction = !results.AdminConsentGranted && results.BatchPermissionsPhase2Completed;
        var pendingS2SAction = results.S2SAppRoleGranted == false;

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

        // Inheritable Permissions: step 3 (non-DW) or step 4 (DW)
        if (results.BlueprintFailed)
            logger.LogInformation(DryRunRow(3 + s, "Inheritable Permissions") + notRun);
        else if (results.BatchPermissionsPhase1Completed)
            logger.LogInformation(DryRunRow(3 + s, "Inheritable Permissions") + "configured");

        // Permission Grants: step 4 (non-DW) or step 5 (DW)
        if (results.BlueprintFailed)
            logger.LogInformation(DryRunRow(4 + s, "Permission Grants") + notRun);
        else if (results.AgentIdentityPermissionsGranted)
            logger.LogInformation(DryRunRow(4 + s, "Permission Grants") + "ok (developer-scoped)");
        else if (results.BatchPermissionsPhase2Completed)
            logger.LogInformation(DryRunRow(4 + s, "Permission Grants") + (results.AdminConsentGranted ? "ok" : "PENDING"));

        // Non-DW only: Agent identity (5) and Agent Registration (6)
        if (isNonDw)
        {
            if (results.BlueprintFailed)
            {
                logger.LogInformation(DryRunRow(5, "Agent identity") + notRun);
                logger.LogInformation(DryRunRow(6, "Agent Registration") + notRun);
            }
            else
            {
                if (results.AgentIdentityCreated)
                {
                    var identityVerb = (results.AgentIdentityAlreadyExisted ? "reused" : "created").PadRight(9);
                    logger.LogInformation(DryRunRow(5, "Agent identity") + identityVerb + " '{Name}' (ID: {Id})",
                        results.AgentIdentityDisplayName ?? "unknown", results.AgentIdentityId ?? "unknown");
                }
                else if (results.AgentIdentityFailed)
                    logger.LogWarning(DryRunRow(5, "Agent identity") + "failed — see warnings");

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
                    logger.LogInformation(DryRunRow(endpointStep, "Messaging endpoint") + "skipped (non-M365 agent; use --m365 to enable)");
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
                    if (string.Equals(results.MessagingEndpointFailureReason, "NotOwner", StringComparison.Ordinal))
                    {
                        logger.LogWarning(DryRunRow(endpointStep, "Messaging endpoint") + "failed (not blueprint owner) — see Action Required");
                    }
                    else if (string.Equals(results.MessagingEndpointFailureReason, "BlueprintMissing", StringComparison.Ordinal))
                    {
                        logger.LogWarning(DryRunRow(endpointStep, "Messaging endpoint") + "not attempted (blueprint creation failed) — see Action Required");
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

        // ── Action Required ────────────────────────────────────────────────────
        var messagingEndpointManualRequired =
            results.MessagingEndpointResult == Models.EndpointRegistrationResult.SkippedContractMismatch;
        var messagingEndpointFailureRequired =
            results.MessagingEndpointResult == Models.EndpointRegistrationResult.Failed;
        var hasActionRequired = pendingAdminAction || results.ClientSecretManualActionRequired || pendingS2SAction || messagingEndpointManualRequired || messagingEndpointFailureRequired;
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
                if (isDw)
                {
                    logger.LogInformation("  {N}. Permission Grants — a Global Administrator must run:", actionCount);
                    logger.LogInformation("     a365 setup admin --blueprint-id {BlueprintId}", adminCmdBlueprintId);
                    if (!string.IsNullOrWhiteSpace(consentUrl))
                    {
                        logger.LogInformation("     Or share this URL with the administrator to grant consent via browser:");
                        logger.LogInformation("       {ConsentUrl}", consentUrl);
                    }
                }
                else
                {
                    logger.LogInformation("  {N}. Permission Grants — a Global Administrator must grant admin consent in the Entra portal:", actionCount);
                    LogNonDwAdminConsentInstructions(logger, adminCmdBlueprintId, results.AgentIdentityId);
                }
            }
            if (pendingS2SAction)
            {
                actionCount++;
                logger.LogInformation("  {N}. Observability API S2S app role — run as Global Administrator (PowerShell):", actionCount);
                logger.LogInformation("       Connect-MgGraph -Scopes 'AppRoleAssignment.ReadWrite.All'");
                logger.LogInformation("       $bp  = Get-MgServicePrincipal -Filter \"appId eq '{BlueprintAppId}'\"", blueprintAppId);
                logger.LogInformation("       $obs = Get-MgServicePrincipal -Filter \"appId eq '{ObsApiAppId}'\"", ConfigConstants.ObservabilityApiAppId);
                logger.LogInformation("       $rid = ($obs.AppRoles | Where-Object {{ $_.Value -eq '{ObsScope}' }}).Id", ConfigConstants.ObservabilityApiOtelWriteScope);
                logger.LogInformation("       New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $bp.Id -PrincipalId $bp.Id -ResourceId $obs.Id -AppRoleId $rid");
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
                if (string.Equals(results.MessagingEndpointFailureReason, "NotOwner", StringComparison.Ordinal))
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
                else if (string.Equals(results.MessagingEndpointFailureReason, "BlueprintMissing", StringComparison.Ordinal))
                {
                    logger.LogInformation("  {N}. Messaging endpoint — not attempted because agent blueprint creation did not", actionCount);
                    logger.LogInformation("     complete. Resolve the blueprint step (see errors above), then re-run just");
                    logger.LogInformation("     the endpoint step:");
                    logger.LogInformation("       a365 setup blueprint --endpoint-only --m365");
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
            logger.LogInformation("");
            logger.LogInformation("Errors:");
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
            || !string.IsNullOrEmpty(results.FederatedCredentialError);

        if (hasNextSteps)
        {
            var nextStepLines = new List<Action>();

            if ((!results.BatchPermissionsPhase2Completed || (!results.AdminConsentGranted && !pendingAdminAction)) && results.HasErrors)
                nextStepLines.Add(() => logger.LogInformation("  To retry permissions: a365 setup all"));

            if (!string.IsNullOrEmpty(results.GraphInheritablePermissionsError))
                nextStepLines.Add(() => logger.LogInformation("  To retry Graph inheritable permissions: a365 setup blueprint"));

            if (!string.IsNullOrEmpty(results.FederatedCredentialError))
            {
                nextStepLines.Add(() =>
                {
                    logger.LogInformation("  Ensure 'AgentIdentityBlueprint.UpdateAuthProperties.All' is consented, then:");
                    logger.LogInformation("    a365 setup blueprint");
                });
            }

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
    /// When <paramref name="isDw"/> is false (non-DW blueprint path), only Observability API and
    /// Power Platform API URLs are generated — Graph, MCP, and Messaging Bot API are excluded.
    /// </summary>
    /// <returns>Display names of the resources for which URLs were saved.</returns>
    internal static List<string> PopulateAdminConsentUrls(
        Agent365Config config,
        string mcpResourceAppId,
        IEnumerable<string> mcpScopes,
        bool isDw = true)
    {
        var graphScopes = isDw ? config.AgentApplicationScopes : Enumerable.Empty<string>();
        var urls = BuildAdminConsentUrls(config.TenantId, config.AgentBlueprintId!, graphScopes, mcpScopes, isDw);

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
    /// Builds per-resource admin consent URLs. DW path produces five resources (Graph, MCP,
    /// Messaging Bot API, Observability API, Power Platform API). Non-DW path produces two
    /// (Observability API and Power Platform API only) — controlled by <paramref name="isDw"/>.
    /// </summary>
    internal static List<(string ResourceName, string ConsentUrl)> BuildAdminConsentUrls(
        string tenantId,
        string blueprintClientId,
        IEnumerable<string> graphScopes,
        IEnumerable<string> mcpScopes,
        bool isDw = true)
    {
        var urls = new List<(string, string)>();

        static string Build(string tenant, string client, string resourceUri, IEnumerable<string> scopes)
            => BuildAdminConsentUrl(tenant, client, scopes.Select(s => $"{resourceUri}/{s}"));

        if (isDw)
        {
            var graphScopeList = graphScopes.ToList();
            if (graphScopeList.Count > 0)
                urls.Add(("Microsoft Graph", Build(tenantId, blueprintClientId, AuthenticationConstants.MicrosoftGraphResourceUri, graphScopeList)));

            var mcpScopeList = mcpScopes.ToList();
            if (mcpScopeList.Count > 0)
                urls.Add(("Agent 365 Tools", Build(tenantId, blueprintClientId, McpConstants.Agent365ToolsIdentifierUri, mcpScopeList)));

            urls.Add(("Messaging Bot API", Build(tenantId, blueprintClientId, ConfigConstants.MessagingBotApiIdentifierUri, new[] { ConfigConstants.MessagingBotApiAdminConsentScope })));
        }

        // Observability API is required for both DW and non-DW paths.
        urls.Add(("Observability API", Build(tenantId, blueprintClientId, ConfigConstants.ObservabilityApiIdentifierUri, new[] { ConfigConstants.ObservabilityApiAdminConsentScope })));
        urls.Add(("Power Platform API", Build(tenantId, blueprintClientId, PowerPlatformConstants.PowerPlatformApiIdentifierUri, new[] { PowerPlatformConstants.PermissionNames.ConnectivityConnectionsRead })));

        return urls;
    }

    /// <summary>
    /// Builds a single combined /v2.0/adminconsent URL for the DW path only.
    /// Covers Graph, MCP, Messaging Bot API, Observability API, and Power Platform API.
    ///
    /// Non-DW path: Observability API and Power Platform API are NOT included here.
    /// The /v2.0/adminconsent endpoint requires scopes to be registered as
    /// oauth2PermissionScopes on the resource SP in the tenant. These resource SPs are
    /// not guaranteed to exist in all tenants, causing AADSTS650053. For non-DW,
    /// admin consent for these APIs is handled programmatically via 'a365 setup admin'.
    /// </summary>
    internal static string BuildCombinedConsentUrl(
        string tenantId,
        string blueprintClientId,
        IEnumerable<string> graphScopes,
        IEnumerable<string> mcpScopes,
        bool isDw = true)
    {
        var allScopes = new List<string>();
        if (isDw)
        {
            foreach (var s in graphScopes)
                allScopes.Add($"{AuthenticationConstants.MicrosoftGraphResourceUri}/{s}");
            foreach (var s in mcpScopes)
                allScopes.Add($"{McpConstants.Agent365ToolsIdentifierUri}/{s}");
            allScopes.Add($"{ConfigConstants.MessagingBotApiIdentifierUri}/{ConfigConstants.MessagingBotApiAdminConsentScope}");
            allScopes.Add($"{ConfigConstants.ObservabilityApiIdentifierUri}/{ConfigConstants.ObservabilityApiAdminConsentScope}");
        }
        allScopes.Add($"{PowerPlatformConstants.PowerPlatformApiIdentifierUri}/{PowerPlatformConstants.PermissionNames.ConnectivityConnectionsRead}");
        return BuildAdminConsentUrl(tenantId, blueprintClientId, allScopes);
    }

    /// <summary>
    /// Populates per-resource consent URLs in config and sets <see cref="SetupResults.CombinedConsentUrl"/>
    /// when the running account is not a Global Administrator. Called by both DW and non-DW setup paths
    /// after the batch permissions step.
    /// When <paramref name="isDw"/> is false, only Observability API and Power Platform API URLs are
    /// generated — Graph, MCP, and Messaging Bot API are excluded.
    /// No-op if admin consent was already granted or blueprint ID is absent.
    /// </summary>
    internal static void ApplyConsentUrlsIfNeeded(
        SetupContext ctx,
        string mcpResourceAppId,
        IEnumerable<string> graphScopes,
        IEnumerable<string> mcpScopes,
        bool isDw = true)
    {
        if (ctx.Results.AdminConsentGranted || string.IsNullOrWhiteSpace(ctx.Config.AgentBlueprintId))
            return;

        var consentResourceNames = PopulateAdminConsentUrls(ctx.Config, mcpResourceAppId, mcpScopes, isDw);
        ctx.Results.ConsentUrlsSavedToPath = ctx.GeneratedConfigPath;
        ctx.Results.ConsentResourceNames.AddRange(consentResourceNames);
        ctx.Results.CombinedConsentUrl = BuildCombinedConsentUrl(
            ctx.Config.TenantId!, ctx.Config.AgentBlueprintId!,
            graphScopes, mcpScopes, isDw);
    }

    /// <summary>
    /// Displays the setup summary for 'a365 setup admin' — shows grant results and
    /// a Graph Explorer query the administrator can use to verify the grants.
    /// </summary>
    public static void DisplayAdminSetupSummary(
        SetupResults results,
        string? blueprintSpObjectId,
        ILogger logger)
    {
        logger.LogInformation("");
        logger.LogInformation("Admin Setup Summary");
        logger.LogInformation("");

        // Numbered step rows — mirrors the setup admin dry-run
        logger.LogInformation(DryRunRow(1, "Prerequisites") + "ok");
        if (!string.IsNullOrWhiteSpace(blueprintSpObjectId))
            logger.LogInformation(DryRunRow(2, "Blueprint") + "resolved   (SP: {SpObjectId})", blueprintSpObjectId);
        else
            logger.LogInformation(DryRunRow(2, "Blueprint") + "resolved");
        logger.LogInformation(DryRunRow(3, "Permission Grants") + (results.AdminConsentGranted ? "ok" : "failed"));
        if (results.S2SAppRoleGranted == true)
            logger.LogInformation(DryRunRow(4, "S2S app role") + "ok ({Scope})", ConfigConstants.ObservabilityApiOtelWriteScope);

        if (results.Errors.Count > 0)
        {
            logger.LogInformation("");
            logger.LogInformation("Errors:");
            foreach (var error in results.Errors)
                logger.LogError("  {Error}", error);
        }

        if (results.Warnings.Count > 0)
        {
            logger.LogInformation("");
            logger.LogInformation("Warnings:");
            foreach (var warning in results.Warnings)
                logger.LogWarning("  {Warning}", warning);
        }

        if (!string.IsNullOrWhiteSpace(blueprintSpObjectId))
        {
            logger.LogInformation("");
            logger.LogInformation("Verify grants:");
            logger.LogInformation("  GET https://graph.microsoft.com/v1.0/oauth2PermissionGrants?$filter=clientId eq '{SpObjectId}'", blueprintSpObjectId);
        }

        logger.LogInformation("");

        if (results.HasErrors)
            logger.LogWarning("Admin setup completed with errors");
        else if (results.HasWarnings)
            logger.LogInformation("Admin setup completed with warnings");
        else
            logger.LogInformation("Admin setup completed successfully");
    }

    /// <summary>
    /// Prints the dry-run plan for the own-identity agent (--ownidentity true) path of setup all.
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
            logger.LogInformation(DryRunRow(3, "Blueprint") + "create (multi-tenant)");
            logger.LogInformation(sub + "create service principal");
            logger.LogInformation(sub + "create client secret");
            logger.LogInformation(sub + "create federated identity credential (FIC)");
            logger.LogInformation(sub + "create managed identity");
        }

        // 4. Inheritable Permissions
        logger.LogInformation(DryRunRow(4, "Inheritable Permissions") + "configure for Microsoft Graph, Agent 365 Tools, Messaging Bot API, Observability API, Power Platform API");

        // 5. Permission Grants
        logger.LogInformation(DryRunRow(5, "Permission Grants") + "admin approval required — a365 setup admin --blueprint-id <blueprint-id>");

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
            logger.LogInformation(DryRunRow(6, "Messaging endpoint") + "skip (non-M365 agent; pass --m365 to enable)");
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

        var response = await graph.CreateOrUpdateOauth2PermissionGrantAsync(
            config.TenantId, blueprintSpObjectId, resourceSpObjectId, scopes, ct, permissionGrantScopes);

        if (!response)
        {
            throw new SetupValidationException(
                $"Failed to create/update OAuth2 permission grant from blueprint {config.AgentBlueprintId} to {resourceName} {resourceAppId}. " +
                "This may be due to insufficient permissions. Ensure you have DelegatedPermissionGrant.ReadWrite.All permission.");
        }

        // 3. Set inheritable permissions (for agent blueprints)
        bool inheritanceConfigured = false;
        bool inheritanceAlreadyExisted = false;
        string? inheritanceError = null;

        if (setInheritablePermissions)
        {
            logger.LogInformation("   - Configuring inheritable permissions: blueprint {Blueprint} to resourceAppId {ResourceAppId} scopes [{Scopes}]",
                config.AgentBlueprintId, resourceAppId, string.Join(' ', scopes));

            // Use custom client app auth for inheritable permissions - Azure CLI doesn't support this operation.
            // Reuse permissionGrantScopes (which already includes AgentIdentityBlueprint.UpdateAuthProperties.All)
            // so all Graph PowerShell calls in this method share a single Connect-MgGraph session/cache entry.
            var (ok, alreadyExists, err) = await blueprintService.SetInheritablePermissionsAsync(
                config.TenantId, config.AgentBlueprintId, resourceAppId, scopes, requiredScopes: permissionGrantScopes, ct);

            if (!ok && !alreadyExists)
            {
                throw new SetupValidationException($"Failed to set inheritable permissions: {err}. " +
                    "Ensure you have AgentIdentityBlueprint.UpdateAuthProperties.All permission in your custom client app.");
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
                        var (exists, verifiedScopes, verifyError) = await blueprintService.VerifyInheritablePermissionsAsync(
                            config.TenantId, config.AgentBlueprintId, resourceAppId, ct, permissionGrantScopes);
                        return (exists, verifiedScopes, verifyError);
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

                var (exists, verifiedScopes, verifyError) = verificationResult;

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
                else
                {
                    // Check if all required scopes are present
                    var missingScopes = scopes.Except(verifiedScopes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase).ToArray();
                    if (missingScopes.Length > 0)
                    {
                        var warning = $"{resourceName} inheritable permissions incomplete. " +
                            $"Missing scopes: [{string.Join(", ", missingScopes)}]. " +
                            $"Expected: [{string.Join(", ", scopes)}]. " +
                            $"Found: [{string.Join(", ", verifiedScopes ?? Array.Empty<string>())}]. " +
                            $"Run 'a365 setup permissions bot' to retry.";
                        logger.LogWarning(warning);
                        setupResults?.Warnings.Add(warning);
                    }
                    else
                    {
                        logger.LogInformation("   - Verified: {ResourceName} inheritable permissions correctly configured", resourceName);
                    }
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
                logger.LogWarning("Configure 'messagingEndpoint' in a365.config.json and re-run 'a365 setup blueprint' to register the endpoint.");
                return (Models.EndpointRegistrationResult.Failed, "Other");
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
