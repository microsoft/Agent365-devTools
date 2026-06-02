// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Orchestrates setup for blueprint-based agent deployments.
/// Runs the same steps as DW (infrastructure, blueprint, permissions) then appends
/// two non-DW-only steps: Agent Identity creation and agent registration.
///
/// Steps:
///   1. Requirements validation
///   2. Blueprint creation (shared with DW)
///   3. Batch permissions on the blueprint (shared with DW pipeline; non-DW spec set:
///      Observability API, Power Platform API, custom). MAC reads from the blueprint,
///      so stamping here gives the same set visibility there.
///   4. Agent Identity creation via POST /beta/servicePrincipals/Microsoft.Graph.AgentIdentity
///   5. Agent Identity permission grants (same spec set as step 3) — OBO or S2S
///   6. Agent registration via Graph API (copilot/agentRegistrations)
/// </summary>
internal static class NonDwBlueprintSetupOrchestrator
{

    /// <summary>
    /// Prints a dry-run plan showing all resources that would be created or configured,
    /// using actual names and values from the loaded config. Makes no API calls.
    /// </summary>
    public static void PrintDryRunPlan(Agent365Config config, ILogger logger, bool isBootstrap = false, string[]? rawArgs = null, bool skipRequirements = false, bool isM365 = false, bool agentRegistrationOnly = false, string? authMode = null, string? messagingEndpointOverride = null)
    {
        var sub = new string(' ', SetupHelpers.DryRunValCol);
        // --messaging-endpoint flag (if supplied) wins over the init-only config value for the plan.
        var plannedEndpoint = !string.IsNullOrWhiteSpace(messagingEndpointOverride)
            ? messagingEndpointOverride
            : config.MessagingEndpoint;

        // Use explicitly-passed tokens when available; fall back to a known-correct default.
        // Environment.GetCommandLineArgs() is unreliable in dotnet tool / test hosting scenarios.
        var cmdArgs = rawArgs is { Length: > 0 }
            ? string.Join(" ", rawArgs.Where(a => !a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)))
            : "setup all";
        logger.LogInformation("Dry run: a365 {Args} --dry-run", cmdArgs);
        logger.LogInformation("");

        if (agentRegistrationOnly)
        {
            logger.LogInformation("The following steps would be performed (--agent-registration-only).");
            logger.LogInformation("");
            logger.LogInformation("  Steps 1-4 (Prerequisites, Blueprint, Inheritable Permissions, Blueprint Permission Grants) are skipped.");
            logger.LogInformation("");

            var identityDisplayName = config.AgentIdentityDisplayName ?? "Agent";
            var registrationDisplayName = identityDisplayName.EndsWith(" Identity", StringComparison.OrdinalIgnoreCase)
                ? identityDisplayName[..^" Identity".Length].TrimEnd() + " Agent"
                : identityDisplayName;

            if (!string.IsNullOrWhiteSpace(config.AgenticAppId))
                logger.LogInformation(SetupHelpers.DryRunRow(5, "Agent identity") + "reuse: {DisplayName} (ID: {AgentId})", identityDisplayName, config.AgenticAppId);
            else
                logger.LogInformation(SetupHelpers.DryRunRow(5, "Agent identity") + "create: {DisplayName}", identityDisplayName);

            if (!string.IsNullOrWhiteSpace(config.AgentRegistrationId))
                logger.LogInformation(SetupHelpers.DryRunRow(6, "Agent Registration") + "reuse: {DisplayName} (ID: {RegistrationId})", registrationDisplayName, config.AgentRegistrationId);
            else
                logger.LogInformation(SetupHelpers.DryRunRow(6, "Agent Registration") + "register: {DisplayName}", registrationDisplayName);

            if (isM365)
            {
                var endpointDetail = string.IsNullOrWhiteSpace(plannedEndpoint)
                    ? "deferred — pass --messaging-endpoint <url> or configure after deploy"
                    : $"register via Teams Graph: {plannedEndpoint}";
                logger.LogInformation(SetupHelpers.DryRunRow(7, "Messaging endpoint") + endpointDetail);
            }
            else
            {
                logger.LogInformation(SetupHelpers.DryRunRow(7, "Messaging endpoint") + "skipped (non-M365 agent)");
            }

            logger.LogInformation(SetupHelpers.DryRunRow(8, "Project settings") + "write to appsettings.json");

            logger.LogInformation("");
            logger.LogInformation("No changes will be made. Run without --dry-run to apply.");
            return;
        }

        logger.LogInformation("The following steps would be performed.");
        logger.LogInformation("");

        // 1. Prerequisites
        if (skipRequirements)
            logger.LogInformation(SetupHelpers.DryRunRow(1, "Prerequisites") + "skip (--skip-requirements)");
        else if (isBootstrap)
            logger.LogInformation(SetupHelpers.DryRunRow(1, "Prerequisites") + "validate (Azure CLI, PowerShell modules)");
        else
            logger.LogInformation(SetupHelpers.DryRunRow(1, "Prerequisites") + "validate (PowerShell modules, Azure CLI, client app)");

        // 2. Blueprint
        var blueprintDisplayName = config.AgentBlueprintDisplayName ?? config.AgentIdentityDisplayName ?? "Agent Blueprint";
        var blueprintExists = !string.IsNullOrWhiteSpace(config.AgentBlueprintId);
        if (blueprintExists)
        {
            SetupHelpers.PrintDryRunBlueprintReuseRows(logger, config.AgentBlueprintId!, step: 2);
        }
        else
        {
            logger.LogInformation(SetupHelpers.DryRunRow(2, "Blueprint") + "create (multi-tenant): {DisplayName}", blueprintDisplayName);
            logger.LogInformation(sub + "create service principal");
            logger.LogInformation(sub + "create client secret");
            logger.LogInformation(sub + "create federated identity credential (FIC)");
            logger.LogInformation(sub + "create managed identity");
        }

        // 3. Inheritable Permissions — non-DW spec set (Observability API, Power Platform API, custom)
        //    stamped on the blueprint via SetInheritablePermissionsAsync so MAC and other dependent
        //    systems can see them. The same set is applied to the agent identity SP in step 5.
        var selectedAuthMode = authMode ?? config.AuthMode;
        var effectiveMode = string.IsNullOrWhiteSpace(selectedAuthMode)
            ? "obo"
            : selectedAuthMode.Trim().ToLowerInvariant();
        logger.LogInformation(SetupHelpers.DryRunRow(3, "Inheritable Permissions") + "configure for Observability API, Power Platform API, and custom permissions (Global Administrator required; consent URL printed if absent)");

        // 4. Blueprint Permission Grants — per authMode. The consent URL targets the blueprint
        //    app, and S2S app-role assignments are persisted as grants flowing from the blueprint;
        //    grouping here keeps all blueprint-side rows (2 Blueprint, 3 Inheritable Permissions,
        //    4 Blueprint Permission Grants) contiguous.
        if (effectiveMode is "obo")
            logger.LogInformation(SetupHelpers.DryRunRow(4, "Blueprint Permission Grants") + "delegated grants — attempted programmatically for the signed-in principal (403 may indicate additional delegated consent or permissions are required)");
        else if (effectiveMode is "s2s")
            logger.LogInformation(SetupHelpers.DryRunRow(4, "Blueprint Permission Grants") + "S2S app roles — attempted programmatically ({Roles} required if 403)", AuthenticationConstants.S2SGrantRequiredRoles);
        else if (effectiveMode is "both")
            logger.LogInformation(SetupHelpers.DryRunRow(4, "Blueprint Permission Grants") + "delegated grants for the signed-in principal + S2S app roles — attempted programmatically; {Roles} required for S2S if 403", AuthenticationConstants.S2SGrantRequiredRoles);

        // 5. Agent identity (created after blueprint-side grants so all blueprint rows are grouped)
        var agentIdentityDisplayName = config.AgentIdentityDisplayName ?? "Agent";
        var agentRegistrationDisplayName = agentIdentityDisplayName.EndsWith(" Identity", StringComparison.OrdinalIgnoreCase)
            ? agentIdentityDisplayName[..^" Identity".Length].TrimEnd() + " Agent"
            : agentIdentityDisplayName;
        if (!string.IsNullOrWhiteSpace(config.AgenticAppId))
            logger.LogInformation(SetupHelpers.DryRunRow(5, "Agent identity") + "reuse: {DisplayName} (ID: {AgentId})", agentIdentityDisplayName, config.AgenticAppId);
        else
            logger.LogInformation(SetupHelpers.DryRunRow(5, "Agent identity") + "create: {DisplayName}", agentIdentityDisplayName);

        // 6. Agent Registration
        if (!string.IsNullOrWhiteSpace(config.AgentRegistrationId))
            logger.LogInformation(SetupHelpers.DryRunRow(6, "Agent Registration") + "reuse: {DisplayName} (ID: {RegistrationId})", agentRegistrationDisplayName, config.AgentRegistrationId);
        else
            logger.LogInformation(SetupHelpers.DryRunRow(6, "Agent Registration") + "register: {DisplayName}", agentRegistrationDisplayName);

        // 7. Messaging endpoint (M365 opt-in)
        if (isM365)
        {
            var endpointDetail = string.IsNullOrWhiteSpace(plannedEndpoint)
                ? "deferred — pass --messaging-endpoint <url> or configure after deploy"
                : $"register via Teams Graph: {plannedEndpoint}";
            logger.LogInformation(SetupHelpers.DryRunRow(7, "Messaging endpoint") + endpointDetail);
        }
        else
        {
            logger.LogInformation(SetupHelpers.DryRunRow(7, "Messaging endpoint") + "skipped (non-M365 agent)");
        }

        // 8. Project settings
        logger.LogInformation(SetupHelpers.DryRunRow(8, "Project settings") + "write to appsettings.json");

        logger.LogInformation("");
        logger.LogInformation("No changes will be made. Run without --dry-run to apply.");
    }

    /// <summary>
    /// Checks whether any required CLI app permissions are missing from the tenant's consent grant.
    /// If so, lists them, asks the user for confirmation, and grants consent if confirmed.
    /// Skipped when ClientAppId is not configured (consent is not applicable).
    /// </summary>
    private static async Task EnsureConsentWithPromptAsync(SetupContext ctx)
    {
        var clientAppId = ctx.Config.ClientAppId;
        var tenantId = ctx.Config.TenantId;

        if (string.IsNullOrWhiteSpace(clientAppId) || string.IsNullOrWhiteSpace(tenantId))
            return;

        List<string> unconsented;
        try
        {
            unconsented = await ctx.ClientAppValidator.GetUnconsentedRequiredPermissionsAsync(
                clientAppId, tenantId, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Could not check consent status (non-fatal): {Message}", ex.Message);
            return;
        }

        if (unconsented is null || unconsented.Count == 0)
            return;

        ctx.Logger.LogInformation("");
        ctx.Logger.LogInformation("The following required permissions are not yet consented for your client app ({ClientAppId}):", clientAppId);
        using (ctx.Logger.Indent())
        {
            foreach (var p in unconsented)
                ctx.Logger.LogInformation("{Permission}", p);
        }
        ctx.Logger.LogInformation("");

        // OAuth2 grant operations require Global Administrator. Skip the prompt for non-admins
        // so we don't ask them to confirm an operation they cannot complete; print the admin
        // consent URL for hand-off instead.
        var roleCheck = await ctx.GraphApiService.IsCurrentUserAdminAsync(tenantId, ctx.CancellationToken);
        if (roleCheck == Models.RoleCheckResult.DoesNotHaveRole)
        {
            ctx.Logger.LogWarning("Granting tenant-wide consent requires a tenant administrator. Setup will continue and may fail if these permissions are required at runtime.");
            var url = Exceptions.ClientAppValidationException.BuildAdminConsentUrl(clientAppId, tenantId);
            if (!string.IsNullOrWhiteSpace(url))
            {
                ctx.Logger.LogInformation("Share the following URL with a tenant administrator so they can grant consent:");
                ctx.Logger.LogInformation("  {Url}", url);
            }
            return;
        }

        var confirmed = await ctx.ConfirmationProvider.ConfirmAsync("Grant admin consent for these permissions now? [y/N]: ");
        ctx.CancellationToken.ThrowIfCancellationRequested();

        if (!confirmed)
        {
            ctx.Logger.LogWarning("Admin consent not granted. Setup may fail if these permissions are required.");
            return;
        }

        ctx.Logger.LogInformation("Granting admin consent...");
        try
        {
            await ctx.ClientAppValidator.GrantConsentForPermissionsAsync(
                clientAppId, unconsented, tenantId, ctx.CancellationToken);
            ctx.Logger.LogInformation("Admin consent granted for: {Permissions}", string.Join(", ", unconsented));
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Could not grant admin consent (non-fatal): {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Executes the full non-DW blueprint setup:
    ///   1. Requirements validation
    ///   2. Blueprint creation (shared with DW)
    ///   3. Batch permissions (Graph + A365 Tools only)
    ///   4. Agent Instance registration
    /// </summary>
    /// <returns>Exit code: 0 on success, 1 on fatal failure.</returns>
    public static async Task<int> ExecuteAsync(SetupContext ctx)
    {
        ctx.Results.IsNonDwBlueprintFlow = true;
        ctx.Results.TenantId = ctx.Config.TenantId;
        // Bootstrap already printed the "Running..." banner before auth steps; skip here to avoid duplication.
        if (!ctx.IsBootstrap)
        {
            ctx.Logger.LogInformation("Running \"a365 {Args}\"...", string.Join(" ", Environment.GetCommandLineArgs().Skip(1)));
            ctx.Logger.LogInformation("");
        }
        ctx.Logger.LogDebug("TraceId: {TraceId}", ctx.CorrelationId);

        List<ResourcePermissionSpec> specs = [];

        try
        {
            if (ctx.AgentInstanceOnly)
            {
                ctx.Logger.LogInformation("NOTE: --agent-registration-only flag set. Skipping requirements, blueprint, and permissions steps.");
                ctx.Logger.LogInformation("");
                ctx.Results.PrerequisitesSkipped = true;
                ctx.Results.BatchPermissionsPhase1Completed = true;
                ctx.Results.PermissionGrantsSkipped = true;

                // Fallback: if AgenticAppId is missing, try to find it via API using blueprint ID + display name.
                if (string.IsNullOrWhiteSpace(ctx.Config.AgenticAppId))
                {
                    if (!string.IsNullOrWhiteSpace(ctx.Config.AgentBlueprintId))
                    {
                        var identityDisplayName = ctx.Config.AgentIdentityDisplayName ?? "Agent";
                        ctx.Logger.LogInformation("Agent identity ID not in config. Querying by display name '{Name}'...", identityDisplayName);
                        var foundId = await ctx.BlueprintService.FindExistingAgentIdentityAsync(
                            ctx.Config.TenantId!,
                            ctx.Config.AgentBlueprintId!,
                            identityDisplayName,
                            ctx.CancellationToken);
                        if (!string.IsNullOrWhiteSpace(foundId))
                        {
                            ctx.Config.AgenticAppId = foundId;
                            await ctx.ConfigService.SaveStateAsync(ctx.Config);
                            ctx.Logger.LogInformation("Found agent identity (ID: {Id}). Using it for registration.", foundId);
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(ctx.Config.AgenticAppId))
                {
                    ctx.Logger.LogError("Agent identity ID is not set in config. Run 'a365 setup all' first to create the agent identity, then retry with --agent-registration-only.");
                    ctx.Results.AgentIdentityFailed = true;
                    ctx.Results.AgentRegistrationFailed = true;
                    ctx.Results.Errors.Add("Agent identity ID not found in config. Run 'a365 setup all' (without --agent-registration-only) to create it first.");
                }
                else
                {
                    ctx.Results.AgentIdentityCreated = true;
                    ctx.Results.AgentIdentityAlreadyExisted = true;
                    ctx.Results.AgentIdentityId = ctx.Config.AgenticAppId;
                    ctx.Results.AgentIdentityDisplayName = ctx.Config.AgentIdentityDisplayName;
                    ctx.Results.BlueprintId = ctx.Config.AgentBlueprintId;
                    // Consent check required — AgentRegistration.ReadWrite.All must be consented for the registration token.
                    await EnsureConsentWithPromptAsync(ctx);
                    await ExecuteAgentIdentityAndRegistrationAsync(ctx, specs, skipIdentityAndPermissions: true);
                }
            }
            else
            {
                ctx.Results.PrerequisitesSkipped = ctx.SkipRequirements;
                ctx.Results.InfrastructureSkipped = true;

                // Step 1: Requirements validation
                if (!ctx.SkipRequirements)
                {
                    var checks = AllSubcommand.GetNonDwChecks(ctx.AuthValidator, ctx.ClientAppValidator, isBootstrap: ctx.IsBootstrap);
                    try
                    {
                        await RequirementsSubcommand.RunChecksOrExitAsync(checks, ctx.Config, ctx.Logger, ctx.CancellationToken);
                    }
                    catch (Exception reqEx) when (reqEx is not OperationCanceledException && reqEx is not CleanExitException)
                    {
                        ctx.Logger.LogError("Requirements check failed: {Message}", reqEx.Message);
                        ctx.Logger.LogDebug(reqEx, "Requirements check exception details");
                        ctx.Logger.LogInformation("To bypass requirement validation, rerun with --skip-requirements.");
                        return 1;
                    }
                }
                else
                {
                    ctx.Logger.LogInformation("Requirements validation skipped (--skip-requirements flag used)");
                }

                // Step 1.5: Consent check — detect missing consent for required permissions and prompt.
                await EnsureConsentWithPromptAsync(ctx);

                // Step 2: Infrastructure — always a no-op (deploy command removed; hosting is externally managed)
                await AllSubcommand.ExecuteInfrastructureStepAsync(ctx);

                // Step 3: Blueprint creation (shared with DW)
                await AllSubcommand.ExecuteBlueprintStepAsync(ctx);

                // Step 4: Build permission specs — stamps Graph, manifest MCP audiences, Observability,
                // Power Platform, custom permissions, and Messaging Bot (only when isM365). Mirrors DW.
                var buildResult = await AllSubcommand.BuildPermissionSpecsAsync(ctx);
                specs = buildResult.specs;

                // Stamp the same spec set on the blueprint so MAC and other dependent systems
                // can discover, interpret, and reason over permissions. Mirrors the DW flow.
                // Non-fatal: a failure here (e.g. caller lacks Global Administrator) logs a warning
                // and continues so the agent-identity grants below still apply.
                await AllSubcommand.ExecuteBatchPermissionsStepAsync(
                    ctx, specs, buildResult.scopesByAudience,
                    knownBlueprintSpObjectId: ctx.Config.AgentBlueprintServicePrincipalObjectId);

                // If admin consent wasn't granted (non-GA caller), persist per-resource consent URLs
                // and a combined URL so a Global Administrator can complete the hand-off out-of-band.
                // Messaging Bot is gated on isM365 to avoid AADSTS650053 in tenants without the Bot SP.
                // V2 audience routing (issue #429): pass the full scopesByAudience map so per-server
                // audiences land on the bare appId GUID resource identifier rather than collapsing
                // onto the WorkIQ Tools URI. api:// is NOT used — per-server SPs have
                // identifierUris null and the bare GUID is what's in servicePrincipalNames.
                SetupHelpers.ApplyConsentUrlsIfNeeded(
                    ctx, buildResult.mcpResourceAppId, ctx.Config.AgentApplicationScopes, buildResult.mcpScopes,
                    isM365: ctx.IsM365,
                    mcpScopesByAudience: buildResult.scopesByAudience,
                    mcpAudienceDisplayNames: buildResult.serverNamesByAudience);

                // Save state before agent identity steps so progress (blueprint stamping outcomes,
                // consent URLs) is not lost on failure in the steps below.
                await ctx.ConfigService.SaveStateAsync(ctx.Config);

                // Steps 5-8: Agent identity creation, permission grants, registration, project settings.
                await ExecuteAgentIdentityAndRegistrationAsync(ctx, specs);
            }
        }
        catch (Agent365Exception ex)
        {
            var logFilePath = Services.ConfigService.GetCommandLogPath(Constants.CommandNames.Setup);
            Exceptions.ExceptionHandler.HandleAgent365Exception(ex, logFilePath: logFilePath);
            ctx.Results.Errors.Add(ex.Message);
        }
        catch (FileNotFoundException fnfEx)
        {
            ctx.Logger.LogError("Setup failed: {Message}", fnfEx.Message);
            ctx.Results.Errors.Add(fnfEx.Message);
        }
        catch (OperationCanceledException)
        {
            ctx.Logger.LogInformation("");
            ctx.Logger.LogInformation("Setup cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "Setup failed: {Message}", ex.Message);
            ctx.Results.Errors.Add(ex.Message);
        }

        // Display summary — always, even when errors occurred above.
        // IsNonDwBlueprintFlow=true was set at the top of this method; DisplaySetupSummary reads that
        // flag directly to pick the non-DW step layout and action-required content.
        ctx.Logger.LogInformation("");
        SetupHelpers.DisplaySetupSummary(ctx.Results, ctx.Logger);

        return ctx.Results.HasErrors ? 1 : 0;
    }

    /// <summary>
    /// Executes Steps 5-8: agent identity creation, permission grants, agent registration,
    /// and project settings sync. Called from both the normal path and the --agent-registration-only
    /// shortcut path.
    /// When <paramref name="skipIdentityAndPermissions"/> is true (--agent-registration-only),
    /// identity creation and permission grants are skipped — only registration and project settings run.
    /// </summary>
    private static async Task ExecuteAgentIdentityAndRegistrationAsync(
        SetupContext ctx,
        List<ResourcePermissionSpec> specs,
        bool skipIdentityAndPermissions = false)
    {
        // Step 5: Create Agent Identity via Agent Identity Graph API.
        // Skipped when --agent-registration-only: identity result flags are pre-set by the caller.
        if (!skipIdentityAndPermissions)
        {
            ctx.Logger.LogInformation("");

            if (!string.IsNullOrWhiteSpace(ctx.Config.AgenticAppId))
            {
                ctx.Logger.LogInformation("Agent identity already created (ID: {AgentId}). Skipping.", ctx.Config.AgenticAppId);
                ctx.Results.AgentIdentityCreated = true;
                ctx.Results.AgentIdentityAlreadyExisted = true;
                ctx.Results.AgentIdentityId = ctx.Config.AgenticAppId;
                ctx.Results.AgentIdentityDisplayName = ctx.Config.AgentIdentityDisplayName;
            }
            else
            {
                var agentIdentityDisplayName = ctx.Config.AgentIdentityDisplayName
                    ?? "Agent";

                // API-level idempotency: check if an identity with this display name already exists
                // for the blueprint. Handles re-runs where the generated config was cleared.
                var existingIdentityId = await ctx.BlueprintService.FindExistingAgentIdentityAsync(
                    ctx.Config.TenantId!,
                    ctx.Config.AgentBlueprintId!,
                    agentIdentityDisplayName,
                    ctx.CancellationToken);

                if (!string.IsNullOrWhiteSpace(existingIdentityId))
                {
                    ctx.Logger.LogInformation("Found existing agent identity (ID: {AgentId}). Skipping creation.", existingIdentityId);
                    ctx.Config.AgenticAppId = existingIdentityId;
                    await ctx.ConfigService.SaveStateAsync(ctx.Config);
                    ctx.Results.AgentIdentityCreated = true;
                    ctx.Results.AgentIdentityAlreadyExisted = true;
                    ctx.Results.AgentIdentityId = existingIdentityId;
                    ctx.Results.AgentIdentityDisplayName = agentIdentityDisplayName;
                }
                else
                {
                    // Agent identity creation via blueprint client credentials (app-only).
                    // AgentIdentity.CreateAsManager is auto-granted to Blueprint apps — no custom app permission required.
                    ctx.Logger.LogInformation("Creating agent identity...");
                    if (string.IsNullOrWhiteSpace(ctx.Config.AgentBlueprintClientSecret))
                    {
                        ctx.Results.AgentIdentityFailed = true;
                        ctx.Logger.LogError("Blueprint client secret is not available. Re-run 'a365 setup blueprint' to create it.");
                        return;
                    }

                    var plainSecret = SecretProtectionHelper.UnprotectSecret(
                        ctx.Config.AgentBlueprintClientSecret,
                        ctx.Config.AgentBlueprintClientSecretProtected,
                        ctx.Logger);
                    var agentId = await ctx.GraphApiService.CreateAgentIdentityAsync(
                        ctx.Config.TenantId!,
                        ctx.Config.AgentBlueprintId!,
                        plainSecret,
                        agentIdentityDisplayName,
                        ctx.CancellationToken);

                    if (agentId is not null)
                    {
                        ctx.Config.AgenticAppId = agentId;
                        await ctx.ConfigService.SaveStateAsync(ctx.Config);
                        ctx.Results.AgentIdentityCreated = true;
                        ctx.Results.AgentIdentityId = agentId;
                        ctx.Results.AgentIdentityDisplayName = agentIdentityDisplayName;
                        using (ctx.Logger.Indent())
                            ctx.Logger.LogInformation("Agent identity created (ID: {AgentId})", agentId);
                        ctx.Logger.LogInformation("");
                    }
                    else if (!ctx.Results.AgentIdentityFailed)
                    {
                        ctx.Results.AgentIdentityFailed = true;
                        ctx.Results.Warnings.Add("Agent identity creation failed. Ensure blueprint setup completed and the client secret is available.");
                        ctx.Logger.LogWarning("Agent identity creation failed. Ensure blueprint setup completed and the client secret is available.");
                    }
                }
            }

            // Step 5a: Grant permissions to the agent identity, gated by authMode.
            if (!string.IsNullOrWhiteSpace(ctx.Config.AgenticAppId))
            {
                ctx.Results.EffectiveAuthMode = ctx.IsBothMode ? Models.AuthMode.Both : ctx.IsS2sMode ? Models.AuthMode.S2s : Models.AuthMode.Obo;

                // OBO and Both: delegated permissions for the agent identity are inherited from the
                // blueprint via the inheritable permissions configured in Phase 1 plus the tenant-wide
                // admin consent granted via the /v2.0/adminconsent URL in Phase 2. No per-identity
                // POST /oauth2PermissionGrants call is needed (and would require
                // DelegatedPermissionGrant.ReadWrite.All, which the CLI's token never carries).

                // S2S and Both: app role assignments (requires Global Admin; falls back to PowerShell instructions).
                if (ctx.IsS2sMode || ctx.IsBothMode)
                    await GrantOrInstructAgentIdentityAppPermissionsAsync(ctx, specs);
            }
        }

        // Step 6: Register agent via Graph API (copilot/agentRegistrations).

        // The agent registration represents the agent itself, not the Entra identity.
        // Strip " Identity" suffix so the registry entry reads "<name> Agent", not "<name> Identity".
        var agentDisplayName = ctx.Config.AgentIdentityDisplayName
            ?? "Agent";
        if (agentDisplayName.EndsWith(" Identity", StringComparison.OrdinalIgnoreCase))
            agentDisplayName = agentDisplayName[..^" Identity".Length].TrimEnd() + " Agent";

        ctx.Logger.LogInformation("");
        ctx.Logger.LogInformation("Registering agent...");

        if (string.IsNullOrWhiteSpace(ctx.Config.AgenticAppId))
        {
            var registrationSkippedMessage =
                "Agent registration failed: agent identity ID is not available. " +
                "Ensure the agent identity was created successfully, then retry with: a365 setup all --agent-registration-only";
            ctx.Results.Warnings.Add(registrationSkippedMessage);
            using (ctx.Logger.Indent())
                ctx.Logger.LogWarning(registrationSkippedMessage);
            ctx.Results.AgentRegistrationFailed = true;
        }
        else
        {

        // If a registration ID is already stored, verify it still exists before skipping creation.
        string? registrationId = null;
        bool registrationAlreadyExisted = false;

        if (!string.IsNullOrWhiteSpace(ctx.Config.AgentRegistrationId))
        {
            var exists = await ctx.GraphApiService.AgentRegistrationExistsAsync(
                ctx.Config.TenantId!,
                ctx.Config.AgentRegistrationId!,
                ctx.CancellationToken);

            if (exists == true)
            {
                registrationId = ctx.Config.AgentRegistrationId;
                registrationAlreadyExisted = true;
                using (ctx.Logger.Indent())
                    ctx.Logger.LogInformation("Agent already registered (ID: {RegistrationId}). Skipping.", registrationId);
                ctx.Logger.LogInformation("");
            }
            else if (exists == false)
            {
                // 404 confirmed — stored ID no longer exists in the registry.
                using (ctx.Logger.Indent())
                    ctx.Logger.LogInformation("Stored registration ID {RegistrationId} no longer exists; creating a new registration.", ctx.Config.AgentRegistrationId);
                ctx.Config.AgentRegistrationId = null;
                // Persist the cleared ID immediately so a subsequent failure does not leave a
                // stale value on disk that would cause the same stale-ID check to repeat.
                await ctx.ConfigService.SaveStateAsync(ctx.Config);
            }
            else
            {
                // Verification inconclusive (auth or transient error) — preserve the stored ID
                // to avoid unintended re-registration. The user can clear the config manually if needed.
                using (ctx.Logger.Indent())
                    ctx.Logger.LogWarning("Could not verify agent registration {RegistrationId} (auth or transient error); retaining stored value.", ctx.Config.AgentRegistrationId);
                registrationId = ctx.Config.AgentRegistrationId;
                registrationAlreadyExisted = true;
            }
        }

        if (string.IsNullOrWhiteSpace(registrationId))
        {
            var (newId, fromConflict) = await ctx.GraphApiService.RegisterAgentInstanceAsyncV2(
                ctx.Config.TenantId!,
                agentDisplayName,
                ctx.Config.AgentDescription,
                ctx.Config.AgentBlueprintId,
                ctx.Config.AgenticAppId,
                ctx.Config.ClientAppId,
                ctx.CancellationToken);
            registrationId = newId;
            if (fromConflict) registrationAlreadyExisted = true;
        }

        if (registrationId is not null)
        {
            ctx.Config.AgentRegistrationId = registrationId;
            await ctx.ConfigService.SaveStateAsync(ctx.Config);
            ctx.Results.AgentInstanceRegistered = true;
            ctx.Results.AgentRegistrationAlreadyExisted = registrationAlreadyExisted;
            ctx.Results.AgentInstanceId = registrationId;
            ctx.Results.AgentRegistrationDisplayName = agentDisplayName;
            if (!registrationAlreadyExisted)
            {
                using (ctx.Logger.Indent())
                    ctx.Logger.LogInformation("Agent registered (ID: {RegistrationId})", registrationId);
                ctx.Logger.LogInformation("");
            }
        }
        else
        {
            ctx.Results.AgentRegistrationFailed = true;
            ctx.Results.Warnings.Add("Agent registration failed via Graph copilot/agentRegistrations API.");
            ctx.Logger.LogWarning("Agent registration failed via Graph copilot/agentRegistrations API.");
        }

        } // end else (AgenticAppId present)

        // Step 6.5: Messaging endpoint registration — --m365 gated; no-op for non-M365 agents.
        // Skipped for --agent-registration-only (skipIdentityAndPermissions) — endpoint is already registered.
        // Phase separator is emitted inside ExecuteMessagingEndpointStepAsync after the
        // non-M365 early-return so non-M365 runs don't accumulate a stray blank line.
        if (!skipIdentityAndPermissions)
            await AllSubcommand.ExecuteMessagingEndpointStepAsync(ctx);

        // Sync project settings — skipped for --agent-registration-only; the user's intent is purely
        // to register the agent, not to regenerate appsettings files.
        if (!skipIdentityAndPermissions)
        {
            ctx.Logger.LogInformation("");
            ctx.Logger.LogInformation("Updating project settings...");
            using (ctx.Logger.Indent())
            {
                ctx.Results.ProjectSettingsWritten = await ProjectSettingsSyncHelper.ExecuteAsync(
                    ctx.ConfigFile.FullName, ctx.Config,
                    ctx.PlatformDetector, ctx.Logger);
            }
        }
    }

    /// <summary>
    /// Attempts to grant app role assignments on the agent identity SP for S2S access.
    /// Requires Agent ID Administrator, Application Administrator, or Global Administrator. When the signed-in user lacks
    /// one of those roles, prints PowerShell instructions covering only the app permission section.
    /// </summary>
    internal static async Task GrantOrInstructAgentIdentityAppPermissionsAsync(
        SetupContext ctx,
        List<ResourcePermissionSpec> specs)
    {
        var s2sSpecs = specs.Where(s => s.AppRoleScopes is { Length: > 0 }).ToList();
        if (s2sSpecs.Count == 0)
        {
            ctx.Logger.LogDebug("No app role specs for agent identity S2S grants; skipping.");
            return;
        }

        // AgenticAppId is the SP object ID returned by CreateAgentIdentityDelegatedAsync /
        // FindExistingAgentIdentityAsync — use it directly without an appId→SP lookup.
        var agentIdentitySpObjectId = ctx.Config.AgenticAppId;
        if (string.IsNullOrWhiteSpace(agentIdentitySpObjectId))
        {
            ctx.Logger.LogWarning("Agent identity SP object ID is missing. App role assignments must be granted manually.");
            ctx.Results.AgentIdentityS2SOutcome = Models.GrantOutcome.Failed;
            return;
        }

        ctx.Logger.LogDebug("Attempting S2S app role assignments on agent identity ({SpId})...", agentIdentitySpObjectId);

        var failedSpecs = new List<ResourcePermissionSpec>();
        foreach (var spec in s2sSpecs)
        {
            var grantResult = await ctx.BlueprintService.GrantAppRoleAssignmentAsync(
                ctx.Config.TenantId!,
                agentIdentitySpObjectId,
                spec.ResourceAppId,
                spec.AppRoleScopes!,
                Constants.AuthenticationConstants.RequiredPermissionGrantScopes,
                ctx.CancellationToken);

            if (grantResult.AllSucceeded)
                ctx.Logger.LogDebug("S2S app roles granted on {ResourceName} to agent identity (already assigned: {AlreadyAssigned}).",
                    spec.ResourceName, grantResult.AllAlreadyAssigned);
            else
            {
                ctx.Logger.LogDebug("S2S app role assignment failed for {ResourceName} — user likely lacks a required role ({Roles}).", spec.ResourceName, AuthenticationConstants.S2SGrantRequiredRoles);
                failedSpecs.Add(spec);
            }
        }

        if (failedSpecs.Count == 0)
        {
            using (ctx.Logger.Indent())
                ctx.Logger.LogInformation("S2S app role assignments granted to agent identity.");
            ctx.Results.AgentIdentityS2SOutcome = Models.GrantOutcome.Granted;
            return;
        }

        // Non-admin fallback: print PowerShell instructions for only the failed resources.
        ctx.Results.AgentIdentityS2SOutcome = Models.GrantOutcome.Failed;
        ctx.Logger.LogInformation("");
        ctx.Logger.LogInformation("S2S app role assignments require {Roles}. Run the following PowerShell:", AuthenticationConstants.S2SGrantRequiredRoles);
        ctx.Logger.LogInformation("");
        ctx.Logger.LogInformation("  # Connect to Microsoft Graph");
        ctx.Logger.LogInformation("  Connect-MgGraph -TenantId '{TenantId}' -Scopes 'AppRoleAssignment.ReadWrite.All','Application.Read.All' -UseDeviceCode", ctx.Config.TenantId);
        ctx.Logger.LogInformation("");
        ctx.Logger.LogInformation("  $agentSpId = '{AgentSpId}'", agentIdentitySpObjectId);

        foreach (var spec in failedSpecs)
        {
            ctx.Logger.LogInformation("");
            ctx.Logger.LogInformation("  # {ResourceName}", spec.ResourceName);
            ctx.Logger.LogInformation("  $resourceSp = Get-MgServicePrincipal -Filter \"appId eq '{ResourceAppId}'\"", spec.ResourceAppId);
            foreach (var role in spec.AppRoleScopes!)
            {
                ctx.Logger.LogInformation("  $roleId = ($resourceSp.AppRoles | Where-Object {{ $_.Value -eq '{Role}' }}).Id", role);
                ctx.Logger.LogInformation("  New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $agentSpId -PrincipalId $agentSpId -ResourceId $resourceSp.Id -AppRoleId $roleId");
            }
        }

        ctx.Logger.LogInformation("");
        ctx.Results.Warnings.Add(
            $"S2S app role assignments require {AuthenticationConstants.S2SGrantRequiredRoles}. PowerShell instructions have been printed above.");
    }
}
