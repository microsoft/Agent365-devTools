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
/// Orchestrates setup for blueprint-based non-AI Teammate agent deployments.
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
    public static void PrintDryRunPlan(Agent365Config config, ILogger logger, bool isBootstrap = false, string[]? rawArgs = null, bool skipRequirements = false)
    {
        var sub = new string(' ', SetupHelpers.DryRunValCol);

        // Use explicitly-passed tokens when available; fall back to a known-correct default.
        // Environment.GetCommandLineArgs() is unreliable in dotnet tool / test hosting scenarios.
        var cmdArgs = rawArgs is { Length: > 0 }
            ? string.Join(" ", rawArgs.Where(a => !a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)))
            : "setup all";
        logger.LogInformation("Dry run: a365 {Args} --dry-run", cmdArgs);
        logger.LogInformation("");
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

        // 3. Inheritable Permissions
        var permsList = new List<string> { "Observability API", "Power Platform API" };
        if (config.CustomBlueprintPermissions?.Count > 0)
            foreach (var custom in config.CustomBlueprintPermissions)
                permsList.Add(custom.ResourceName ?? custom.ResourceAppId);
        logger.LogInformation(SetupHelpers.DryRunRow(3, "Inheritable Permissions") + "configure for {Permissions}", string.Join(", ", permsList));

        // 4. Permission Grants
        var blueprintIdForCmd = config.AgentBlueprintId ?? "<blueprint-id>";
        logger.LogInformation(SetupHelpers.DryRunRow(4, "Permission Grants") + "admin approval required — a365 setup admin --blueprint-id {BlueprintId}", blueprintIdForCmd);

        // 5. Agent identity
        var identityDisplayName = config.AgentIdentityDisplayName ?? "Agent";
        var registrationDisplayName = identityDisplayName.EndsWith(" Identity", StringComparison.OrdinalIgnoreCase)
            ? identityDisplayName[..^" Identity".Length].TrimEnd() + " Agent"
            : identityDisplayName;
        if (!string.IsNullOrWhiteSpace(config.AgenticAppId))
            logger.LogInformation(SetupHelpers.DryRunRow(5, "Agent identity") + "reuse: {DisplayName} (ID: {AgentId})", identityDisplayName, config.AgenticAppId);
        else
            logger.LogInformation(SetupHelpers.DryRunRow(5, "Agent identity") + "create: {DisplayName}", identityDisplayName);

        // 6. Agent Registration
        if (!string.IsNullOrWhiteSpace(config.AgentRegistrationId))
            logger.LogInformation(SetupHelpers.DryRunRow(6, "Agent Registration") + "reuse: {DisplayName} (ID: {RegistrationId})", registrationDisplayName, config.AgentRegistrationId);
        else
            logger.LogInformation(SetupHelpers.DryRunRow(6, "Agent Registration") + "register: {DisplayName}", registrationDisplayName);

        // 7. Project settings
        logger.LogInformation(SetupHelpers.DryRunRow(7, "Project settings") + "write to appsettings.json");

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
                ctx.Logger.LogInformation("NOTE: --agent-instance-only flag set. Skipping requirements, blueprint, and permissions steps.");
                ctx.Logger.LogInformation("");
                // Populate results so the summary shows previous steps as already completed
                ctx.Results.BlueprintCreated = true;
                ctx.Results.BlueprintAlreadyExisted = true;
                ctx.Results.BlueprintId = ctx.Config.AgentBlueprintId;
                ctx.Results.BlueprintDisplayName = ctx.Config.AgentBlueprintDisplayName;
                ctx.Results.BatchPermissionsPhase2Completed = true;
                ctx.Results.AdminConsentGranted = true;
                // Still check and prompt for consent even when skipping other steps — consent
                // is required for the registration call and may have been missed in a prior run.
                await EnsureConsentWithPromptAsync(ctx);
                await ExecuteAgentIdentityAndRegistrationAsync(ctx, specs);
            }
            else
            {
                ctx.Results.PrerequisitesSkipped = ctx.SkipRequirements;
                ctx.Results.InfrastructureSkipped = !ctx.Config.NeedDeployment || ctx.SkipInfrastructure;

                // Step 1: Requirements validation
                if (!ctx.SkipRequirements)
                {
                    var includeInfra = !ctx.SkipInfrastructure && ctx.Config.NeedDeployment;
                    var checks = AllSubcommand.GetNonDwChecks(ctx.AuthValidator, ctx.ClientAppValidator, includeInfra, isBootstrap: ctx.IsBootstrap);
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

                // Step 2: Infrastructure (shared with DW, skipped when NeedDeployment=false or --skip-infrastructure)
                await AllSubcommand.ExecuteInfrastructureStepAsync(ctx);

                // Step 3: Blueprint creation (shared with DW)
                await AllSubcommand.ExecuteBlueprintStepAsync(ctx);

                // Step 4: Batch permissions — non-DW path stamps only Observability API and Power Platform API.
                // Microsoft Graph, Agent 365 Tools (MCP), and Messaging Bot API are excluded.
                var buildResult = await AllSubcommand.BuildPermissionSpecsAsync(ctx, isDw: false);
                specs = buildResult.specs;
                var mcpResourceAppId = buildResult.mcpResourceAppId;

                await AllSubcommand.ExecuteBatchPermissionsStepAsync(ctx, specs);

                SetupHelpers.ApplyConsentUrlsIfNeeded(ctx, mcpResourceAppId, graphScopes: [], mcpScopes: [], isDw: false);

                // Save state after permissions (before agent identity creation, so progress
                // is not lost if subsequent steps fail).
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
    /// and project settings sync. Called from both the normal path and the --agent-instance-only
    /// shortcut path.
    /// </summary>
    private static async Task ExecuteAgentIdentityAndRegistrationAsync(
        SetupContext ctx,
        List<ResourcePermissionSpec> specs)
    {
        // Step 5: Create Agent Identity via Agent Identity Graph API.
        ctx.Logger.LogInformation("");

        if (!string.IsNullOrWhiteSpace(ctx.Config.AgenticAppId))
        {
            ctx.Logger.LogInformation("Agent identity already created (ID: {AgentId}). Skipping.", ctx.Config.AgenticAppId);
            ctx.Results.AgentIdentityCreated = true;
            ctx.Results.AgentIdentityId = ctx.Config.AgenticAppId;
            ctx.Results.AgentIdentityDisplayName = ctx.Config.AgentIdentityDisplayName;
        }
        else
        {
            var agentIdentityDisplayName = ctx.Config.AgentIdentityDisplayName
                ?? ctx.Config.WebAppName
                ?? "Agent";

            // Agent identity creation via delegated flow (AgentIdentity.Create.All).
            // Agent ID Developer role is sufficient — client credentials are not required.
            ctx.Logger.LogInformation("Creating agent identity...");
            var agentId = await ctx.GraphApiService.CreateAgentIdentityDelegatedAsync(
                ctx.Config.TenantId!,
                ctx.Config.AgentBlueprintId!,
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
                ctx.Results.Warnings.Add("Agent identity creation failed. Ensure you have the Agent ID Developer or Agent ID Administrator role in this tenant.");
                ctx.Logger.LogWarning("Agent identity creation failed. Ensure you have the Agent ID Developer or Agent ID Administrator role in this tenant.");
            }
        }

        // Step 5a: Grant permissions to the agent identity (non-admin path only).
        // If the batch permissions step already granted AllPrincipals admin consent, skip this.
        if (!string.IsNullOrWhiteSpace(ctx.Config.AgenticAppId) && !ctx.Results.AdminConsentGranted)
        {
            await GrantAgentIdentityPermissionsAsync(ctx, specs);
        }

        // Step 6: Register agent via Graph API (copilot/agentRegistrations).

        // The agent registration represents the agent itself, not the Entra identity.
        // Strip " Identity" suffix so the registry entry reads "<name> Agent", not "<name> Identity".
        var agentDisplayName = ctx.Config.AgentIdentityDisplayName
            ?? ctx.Config.WebAppName
            ?? "Agent";
        if (agentDisplayName.EndsWith(" Identity", StringComparison.OrdinalIgnoreCase))
            agentDisplayName = agentDisplayName[..^" Identity".Length].TrimEnd() + " Agent";

        ctx.Logger.LogInformation("");
        if (!string.IsNullOrWhiteSpace(ctx.Config.AgentRegistrationId))
        {
            ctx.Logger.LogInformation("Registering agent...");
            using (ctx.Logger.Indent())
                ctx.Logger.LogInformation("Agent already registered (ID: {RegistrationId}). Skipping.", ctx.Config.AgentRegistrationId);
            ctx.Logger.LogInformation("");
            ctx.Results.AgentInstanceRegistered = true;
            ctx.Results.AgentInstanceId = ctx.Config.AgentRegistrationId;
            ctx.Results.AgentRegistrationDisplayName = agentDisplayName;
        }
        else
        {
            ctx.Logger.LogInformation("Registering agent...");

            var registrationId = await ctx.GraphApiService.RegisterAgentInstanceAsyncV2(
                ctx.Config.TenantId!,
                agentDisplayName,
                ctx.Config.AgentDescription,
                ctx.Config.AgentBlueprintId,
                ctx.Config.AgenticAppId,
                ctx.Config.ClientAppId,
                ctx.CancellationToken);

            if (registrationId is not null)
            {
                ctx.Config.AgentRegistrationId = registrationId;
                await ctx.ConfigService.SaveStateAsync(ctx.Config);
                ctx.Results.AgentInstanceRegistered = true;
                ctx.Results.AgentInstanceId = registrationId;
                ctx.Results.AgentRegistrationDisplayName = agentDisplayName;
                using (ctx.Logger.Indent())
                    ctx.Logger.LogInformation("Agent registered (ID: {RegistrationId})", registrationId);
                ctx.Logger.LogInformation("");
            }
            else
            {
                ctx.Results.AgentRegistrationFailed = true;
                ctx.Results.Warnings.Add("Agent registration failed via Graph copilot/agentRegistrations API.");
                ctx.Logger.LogWarning("Agent registration failed via Graph copilot/agentRegistrations API.");
            }
        }

        // Sync all settings (ServiceConnection, TokenValidation, Agent365Observability) to the app config file.
        ctx.Logger.LogInformation("Updating project settings...");
        using (ctx.Logger.Indent())
        {
            // Pass ctx.Config directly so AgentDescription and AgentIdentityDisplayName
            // derived from --agent-name are written rather than stale values from disk.
            await ProjectSettingsSyncHelper.ExecuteAsync(
                ctx.ConfigFile.FullName, ctx.Config,
                ctx.PlatformDetector, ctx.Logger);
            ctx.Results.ProjectSettingsWritten = true;
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

        var agentIdentitySpObjectId = await ctx.GraphApiService.EnsureServicePrincipalForAppIdAsync(
            ctx.Config.TenantId!,
            ctx.Config.AgenticAppId!,
            ctx.CancellationToken,
            Constants.AuthenticationConstants.RequiredPermissionGrantScopes);

        if (string.IsNullOrWhiteSpace(agentIdentitySpObjectId))
        {
            ctx.Logger.LogWarning(
                "Could not resolve service principal for agent identity ({AgentId}). " +
                "Permissions must be granted manually in the Entra portal.",
                ctx.Config.AgenticAppId);
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
            ctx.Results.Warnings.Add(
                "One or more permissions could not be granted to the agent identity. " +
                "Check the log output and grant them manually in the Entra portal.");
        else
        {
            var grantedNames = string.Join(", ", specs.Where(s => s.Scopes.Length > 0).Select(s => s.ResourceName));
            using (ctx.Logger.Indent())
                ctx.Logger.LogInformation("Developer-scoped permissions granted ({Resources}).", grantedNames);
            ctx.Results.AgentIdentityPermissionsGranted = true;
        }
    }
}
