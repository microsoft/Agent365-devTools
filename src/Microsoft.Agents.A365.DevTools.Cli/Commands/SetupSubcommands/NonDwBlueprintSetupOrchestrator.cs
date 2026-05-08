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
///   3. Batch permissions (shared with DW — dynamic scopes from config)
///   4. Agent Identity creation via POST /beta/servicePrincipals/Microsoft.Graph.AgentIdentity
///   5. Agent registration via Graph API (copilot/agentRegistrations)
/// </summary>
internal static class NonDwBlueprintSetupOrchestrator
{

    /// <summary>
    /// Prints a dry-run plan showing all resources that would be created or configured,
    /// using actual names and values from the loaded config. Makes no API calls.
    /// </summary>
    public static void PrintDryRunPlan(Agent365Config config, ILogger logger, bool isBootstrap = false, string[]? rawArgs = null, bool skipRequirements = false, bool isM365 = false, bool agentRegistrationOnly = false, string? authMode = null)
    {
        var sub = new string(' ', SetupHelpers.DryRunValCol);

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
            logger.LogInformation("  Steps 1-3 (Prerequisites, Blueprint, Inheritable Permissions) are skipped.");
            logger.LogInformation("");

            var identityDisplayName = config.AgentIdentityDisplayName ?? "Agent";
            var registrationDisplayName = identityDisplayName.EndsWith(" Identity", StringComparison.OrdinalIgnoreCase)
                ? identityDisplayName[..^" Identity".Length].TrimEnd() + " Agent"
                : identityDisplayName;

            if (!string.IsNullOrWhiteSpace(config.AgenticAppId))
                logger.LogInformation(SetupHelpers.DryRunRow(4, "Agent identity") + "reuse: {DisplayName} (ID: {AgentId})", identityDisplayName, config.AgenticAppId);
            else
                logger.LogInformation(SetupHelpers.DryRunRow(4, "Agent identity") + "create: {DisplayName}", identityDisplayName);

            logger.LogInformation(SetupHelpers.DryRunRow(5, "Permission Grants") + "skipped (re-run without --agent-registration-only to update grants)");

            if (!string.IsNullOrWhiteSpace(config.AgentRegistrationId))
                logger.LogInformation(SetupHelpers.DryRunRow(6, "Agent Registration") + "reuse: {DisplayName} (ID: {RegistrationId})", registrationDisplayName, config.AgentRegistrationId);
            else
                logger.LogInformation(SetupHelpers.DryRunRow(6, "Agent Registration") + "register: {DisplayName}", registrationDisplayName);

            if (isM365)
            {
                var endpointDetail = string.IsNullOrWhiteSpace(config.MessagingEndpoint)
                    ? "register via Teams Graph (requires 'messagingEndpoint' in config)"
                    : $"register via Teams Graph: {config.MessagingEndpoint}";
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

        // 3. Inheritable Permissions — only applicable to AI Teammate (DW) agents; always skipped here.
        var selectedAuthMode = authMode ?? config.AuthMode;
        var effectiveMode = string.IsNullOrWhiteSpace(selectedAuthMode)
            ? "obo"
            : selectedAuthMode.Trim().ToLowerInvariant();
        logger.LogInformation(SetupHelpers.DryRunRow(3, "Inheritable Permissions") + "skipped (permissions set directly on agent identity)");

        // 4. Agent identity (created before grants so the SP exists to receive them)
        var agentIdentityDisplayName = config.AgentIdentityDisplayName ?? "Agent";
        var agentRegistrationDisplayName = agentIdentityDisplayName.EndsWith(" Identity", StringComparison.OrdinalIgnoreCase)
            ? agentIdentityDisplayName[..^" Identity".Length].TrimEnd() + " Agent"
            : agentIdentityDisplayName;
        if (!string.IsNullOrWhiteSpace(config.AgenticAppId))
            logger.LogInformation(SetupHelpers.DryRunRow(4, "Agent identity") + "reuse: {DisplayName} (ID: {AgentId})", agentIdentityDisplayName, config.AgenticAppId);
        else
            logger.LogInformation(SetupHelpers.DryRunRow(4, "Agent identity") + "create: {DisplayName}", agentIdentityDisplayName);

        // 5. Permission Grants — per authMode, applied to the agent identity SP
        if (effectiveMode is "obo")
            logger.LogInformation(SetupHelpers.DryRunRow(5, "Permission Grants") + "delegated grants — attempted programmatically for the signed-in principal (403 may indicate additional delegated consent or permissions are required)");
        else if (effectiveMode is "s2s")
            logger.LogInformation(SetupHelpers.DryRunRow(5, "Permission Grants") + "S2S app roles — attempted programmatically ({Roles} required if 403)", AuthenticationConstants.S2SGrantRequiredRoles);
        else if (effectiveMode is "both")
            logger.LogInformation(SetupHelpers.DryRunRow(5, "Permission Grants") + "delegated grants for the signed-in principal + S2S app roles — attempted programmatically; {Roles} required for S2S if 403", AuthenticationConstants.S2SGrantRequiredRoles);

        // 6. Agent Registration
        if (!string.IsNullOrWhiteSpace(config.AgentRegistrationId))
            logger.LogInformation(SetupHelpers.DryRunRow(6, "Agent Registration") + "reuse: {DisplayName} (ID: {RegistrationId})", agentRegistrationDisplayName, config.AgentRegistrationId);
        else
            logger.LogInformation(SetupHelpers.DryRunRow(6, "Agent Registration") + "register: {DisplayName}", agentRegistrationDisplayName);

        // 7. Messaging endpoint (M365 opt-in)
        if (isM365)
        {
            var endpointForDisplay = config.MessagingEndpoint;
            var endpointDetail = string.IsNullOrWhiteSpace(endpointForDisplay)
                ? "register via Teams Graph (requires 'messagingEndpoint' in config)"
                : $"register via Teams Graph: {endpointForDisplay}";
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
        foreach (var p in unconsented)
            ctx.Logger.LogInformation("  - {Permission}", p);

        ctx.Logger.LogInformation("");
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

                // Step 4: Build permission specs — non-DW path stamps only Observability API and Power Platform API.
                // Microsoft Graph, Agent 365 Tools (MCP), and Messaging Bot API are excluded.
                var buildResult = await AllSubcommand.BuildPermissionSpecsAsync(ctx, isDw: false);
                specs = buildResult.specs;

                // Phase 2a (inheritable perms) and Phase 2b (AllPrincipals grants + admin consent)
                // are skipped for all authMode values — admin involvement is avoided by design.
                // Delegated and/or app grants are applied to the agent identity SP below, gated by mode.
                ctx.Results.BatchPermissionsPhase1Completed = true;
                ctx.Results.BatchPermissionsPhase2Completed = true;
                ctx.Results.AdminConsentGranted = false;
                ctx.Logger.LogInformation("Inheritable perms and AllPrincipals grants skipped (permissions set directly on agent identity).");

                // Save state before agent identity steps so progress is not lost on failure.
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

        // Display summary — always, even when errors occurred above
        ctx.Logger.LogInformation("");
        SetupHelpers.DisplaySetupSummary(ctx.Results, ctx.Logger, isDw: false);

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
                ctx.Results.EffectiveAuthMode = ctx.IsBothMode ? "both" : ctx.IsS2sMode ? "s2s" : "obo";

                // OBO and Both: principal-scoped delegated grants (no admin required).
                if (ctx.IsOboMode || ctx.IsBothMode)
                    await GrantAgentIdentityPermissionsAsync(ctx, specs);

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
        if (!skipIdentityAndPermissions)
            await AllSubcommand.ExecuteMessagingEndpointStepAsync(ctx);

        // Sync project settings — skipped for --agent-registration-only; the user's intent is purely
        // to register the agent, not to regenerate appsettings files.
        if (!skipIdentityAndPermissions)
        {
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
    /// Grants the same oauth2 permission grants to the Agent Identity SP that the blueprint has.
    /// Called after agent identity creation so the identity can acquire app-only tokens for all
    /// blueprint resources (e.g. Power Platform, Observability API) via the FMI token chain.
    /// This step is idempotent — safe to re-run on subsequent setup invocations.
    /// </summary>
    internal static async Task GrantAgentIdentityPermissionsAsync(
        SetupContext ctx,
        List<ResourcePermissionSpec> specs)
    {
        if (specs.Count == 0)
        {
            ctx.Logger.LogDebug("No permission specs to grant to agent identity; skipping.");
            return;
        }

        ctx.Logger.LogDebug("Granting permissions to agent identity ({AgentId})...", ctx.Config.AgenticAppId);

        // Resolve the current developer's object ID so we can create Principal-scoped grants
        // that don't require GA or Cloud App Admin.
        var currentUserObjectId = await ctx.GraphApiService.GetCurrentUserObjectIdAsync(
            ctx.Config.TenantId!, ctx.CancellationToken);

        if (string.IsNullOrWhiteSpace(currentUserObjectId))
        {
            ctx.Logger.LogWarning(
                "Could not resolve current user object ID. " +
                "Permissions to the agent identity must be granted manually in the Entra portal.");
            ctx.Results.Warnings.Add(
                "Could not resolve current user object ID for Principal-scoped permission grants. " +
                "Grant them manually in the Entra portal.");
            return;
        }

        // For ServiceIdentity SPs (agent identities), id == appId.
        // The SP was already created by CreateAgentIdentityAsync — use the stored ID directly.
        var agentIdentitySpObjectId = ctx.Config.AgenticAppId;

        if (string.IsNullOrWhiteSpace(agentIdentitySpObjectId))
        {
            ctx.Logger.LogWarning(
                "Agent identity ID not found in config. " +
                "Permissions must be granted manually in the Entra portal.");
            return;
        }

        var anyFailed = false;
        foreach (var spec in specs)
        {
            if (spec.Scopes.Length == 0) continue;

            var resourceSpObjectId = await ctx.GraphApiService.EnsureServicePrincipalForAppIdAsync(
                ctx.Config.TenantId!,
                spec.ResourceAppId,
                ctx.CancellationToken,
                Constants.AuthenticationConstants.RequiredPermissionGrantScopes);

            if (string.IsNullOrWhiteSpace(resourceSpObjectId))
            {
                ctx.Logger.LogWarning(
                    "Could not resolve SP for resource {ResourceName} ({ResourceAppId}); skipping.",
                    spec.ResourceName, spec.ResourceAppId);
                anyFailed = true;
                continue;
            }

            // Query the resource SP's published scopes and filter out any that haven't
            // been rolled out to this tenant yet. Attempting to grant a non-existent scope
            // returns Request_BadRequest from Graph, which would surface as a misleading warning.
            var availableScopes = await ctx.GraphApiService.GetAvailableScopeNamesAsync(
                ctx.Config.TenantId!, resourceSpObjectId, ctx.CancellationToken);

            var scopesToGrant = availableScopes.Count > 0
                ? spec.Scopes.Where(s => availableScopes.Contains(s)).ToArray()
                : spec.Scopes; // if the query failed, try all and let Graph surface any real error

            if (scopesToGrant.Length == 0)
            {
                ctx.Logger.LogInformation(
                    "Scopes [{Scopes}] not yet available on {ResourceName} in this tenant — skipping.",
                    string.Join(" ", spec.Scopes), spec.ResourceName);
                continue;
            }

            var granted = await ctx.GraphApiService.CreatePrincipalOauth2PermissionGrantAsync(
                ctx.Config.TenantId!,
                agentIdentitySpObjectId,
                resourceSpObjectId,
                currentUserObjectId,
                scopesToGrant,
                ctx.CancellationToken,
                Constants.AuthenticationConstants.RequiredPermissionGrantScopes);

            if (granted)
                ctx.Logger.LogDebug(
                    "Granted {Scopes} on {ResourceName} to agent identity (principal scope).",
                    string.Join(" ", scopesToGrant), spec.ResourceName);
            else
            {
                ctx.Logger.LogWarning(
                    "Failed to grant {Scopes} on {ResourceName} to agent identity.",
                    string.Join(" ", scopesToGrant), spec.ResourceName);
                anyFailed = true;
            }
        }

        if (anyFailed)
        {
            ctx.Results.AgentIdentityDelegatedGrantPending = true;
            ctx.Results.Warnings.Add(
                "Delegated permissions for the agent identity could not be granted automatically. " +
                "See the Action Required section for PowerShell instructions.");
        }
        else
        {
            var grantedNames = string.Join(", ", specs.Where(s => s.Scopes.Length > 0).Select(s => s.ResourceName));
            using (ctx.Logger.Indent())
                ctx.Logger.LogInformation("Developer-scoped permissions granted ({Resources}).", grantedNames);
            ctx.Results.AgentIdentityPermissionsGranted = true;
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
            ctx.Results.S2SAppRoleGranted = false;
            return;
        }

        ctx.Logger.LogDebug("Attempting S2S app role assignments on agent identity ({SpId})...", agentIdentitySpObjectId);

        var failedSpecs = new List<ResourcePermissionSpec>();
        foreach (var spec in s2sSpecs)
        {
            var granted = await ctx.BlueprintService.GrantAppRoleAssignmentAsync(
                ctx.Config.TenantId!,
                agentIdentitySpObjectId,
                spec.ResourceAppId,
                spec.AppRoleScopes!,
                Constants.AuthenticationConstants.RequiredPermissionGrantScopes,
                ctx.CancellationToken);

            if (granted)
                ctx.Logger.LogDebug("S2S app roles granted on {ResourceName} to agent identity.", spec.ResourceName);
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
            ctx.Results.S2SAppRoleGranted = true;
            return;
        }

        // Non-admin fallback: print PowerShell instructions for only the failed resources.
        ctx.Results.S2SAppRoleGranted = false;
        ctx.Logger.LogInformation("");
        ctx.Logger.LogInformation("S2S app role assignments require {Roles}. Run the following PowerShell:", AuthenticationConstants.S2SGrantRequiredRoles);
        ctx.Logger.LogInformation("");
        ctx.Logger.LogInformation("  # Connect to Microsoft Graph");
        ctx.Logger.LogInformation("  Connect-MgGraph -TenantId '{TenantId}' -Scopes 'AppRoleAssignment.ReadWrite.All', 'Directory.Read.All'", ctx.Config.TenantId);
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
