// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Orchestrates setup for blueprint-based non-AI Teammate agent deployments.
/// Runs the same steps as DW (infrastructure, blueprint, permissions) then appends
/// two non-DW-only steps: Agent Identity creation and AgentX agent registration.
///
/// Steps:
///   1. Requirements validation
///   2. Blueprint creation (shared with DW)
///   3. Batch permissions (shared with DW — dynamic scopes from config)
///   4. Agent Identity creation via POST /beta/servicePrincipals/Microsoft.Graph.AgentIdentity
///   5. Agent registration via AgentX Agent Registration API V2
/// </summary>
internal static class NonDwBlueprintSetupOrchestrator
{

    /// <summary>
    /// Prints a dry-run plan showing all resources that would be created or configured,
    /// using actual names and values from the loaded config. Makes no API calls.
    /// </summary>
    public static void PrintDryRunPlan(Agent365Config config, ILogger logger)
    {
        var displayName = config.AgentIdentityDisplayName;
        var existingBlueprint = !string.IsNullOrWhiteSpace(config.AgentBlueprintId);
        var existingAgentId = !string.IsNullOrWhiteSpace(config.AgenticAppId);
        var existingInstance = !string.IsNullOrWhiteSpace(config.AgentRegistrationId);

        logger.LogInformation("Non-DW Blueprint Setup Plan (dry run — no changes will be made)");
        logger.LogInformation("");

        // Step 1: Infrastructure
        if (config.NeedDeployment)
            logger.LogInformation("  1. Create Azure infrastructure");
        else
            logger.LogInformation("  1. Skip: Azure infrastructure (needDeployment=false)");

        // Step 2: Blueprint
        if (existingBlueprint)
            logger.LogInformation("  2. Reuse blueprint: \"{DisplayName}\"  id: {BlueprintId}",
                displayName, config.AgentBlueprintId);
        else
            logger.LogInformation("  2. Create blueprint: \"{DisplayName}\"  (multi-tenant)", displayName);

        // Step 3: Permissions
        logger.LogInformation("  3. Configure permissions:");
        logger.LogInformation("       Microsoft Graph: {GraphScopes}", string.Join(", ", config.AgentApplicationScopes));
        logger.LogInformation("       Agent 365 Tools: (read from mcpToolingManifest.json)");
        logger.LogInformation("       Messaging Bot API, Observability API, Power Platform API");
        if (config.CustomBlueprintPermissions?.Count > 0)
            logger.LogInformation("       Custom: {Custom}", string.Join(", ", config.CustomBlueprintPermissions.Select(p => p.ResourceName ?? p.ResourceAppId)));

        // Step 4: Agent Identity
        if (existingAgentId)
            logger.LogInformation("  4. Reuse agent identity: id={AgentId}", config.AgenticAppId);
        else
            logger.LogInformation("  4. Create agent identity: tenant={TenantId}", config.TenantId);

        // Step 5: Agent Registration
        if (existingInstance)
            logger.LogInformation("  5. Reuse agent registration: id={RegistrationId}", config.AgentRegistrationId);
        else
            logger.LogInformation("  5. Register agent via AgentX Agent Registration API V2");

        logger.LogInformation("");
        logger.LogInformation("Run without --dry-run to execute these steps.");
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
        Console.Write("Grant admin consent for these permissions now? [y/N]: ");
        var answer = Console.ReadLine();

        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
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
        ctx.Logger.LogInformation("Running non-DW blueprint setup... (TraceId: {TraceId})", ctx.CorrelationId);
        ctx.Logger.LogInformation("");

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
                ctx.Results.BatchPermissionsPhase2Completed = true;
                ctx.Results.AdminConsentGranted = true;
                // Still check and prompt for consent even when skipping other steps — consent
                // is required for the registration call and may have been missed in a prior run.
                await EnsureConsentWithPromptAsync(ctx);
                goto createAgentIdentity;
            }

            // Step 1: Requirements validation
            if (!ctx.SkipRequirements)
            {
                var includeInfra = !ctx.SkipInfrastructure && ctx.Config.NeedDeployment;
                var checks = AllSubcommand.GetNonDwChecks(ctx.AuthValidator, ctx.ClientAppValidator, includeInfra);
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
                ctx.Logger.LogInformation("NOTE: Requirements validation skipped (--skip-requirements flag used)");
            }

            // Step 1.5: Consent check — detect missing consent for required permissions and prompt.
            await EnsureConsentWithPromptAsync(ctx);

            // Step 2: Infrastructure (shared with DW, skipped when NeedDeployment=false or --skip-infrastructure)
            await AllSubcommand.ExecuteInfrastructureStepAsync(ctx);

            // Step 3: Blueprint creation (shared with DW)
            await AllSubcommand.ExecuteBlueprintStepAsync(ctx);

            // Step 4: Batch permissions — same dynamic spec list as DW (AgentApplicationScopes + MCP manifest + CustomBlueprintPermissions)
            var (specs, mcpResourceAppId, mcpScopes) = await AllSubcommand.BuildPermissionSpecsAsync(ctx);

            await AllSubcommand.ExecuteBatchPermissionsStepAsync(
                ctx, specs,
                knownBlueprintSpObjectId: ctx.Config.AgentBlueprintServicePrincipalObjectId);

            SetupHelpers.ApplyConsentUrlsIfNeeded(ctx, mcpResourceAppId, ctx.Config.AgentApplicationScopes, mcpScopes);

            // Save state after permissions (before agent identity creation, so progress
            // is not lost if subsequent steps fail).
            await ctx.ConfigService.SaveStateAsync(ctx.Config);

            // Step 5: Create Agent Identity via Agent Identity Graph API.
            createAgentIdentity:
            ctx.Logger.LogInformation("");

            if (!string.IsNullOrWhiteSpace(ctx.Config.AgenticAppId))
            {
                ctx.Logger.LogInformation("Agent identity already created (ID: {AgentId}). Skipping.", ctx.Config.AgenticAppId);
                ctx.Results.AgentIdentityCreated = true;
                ctx.Results.AgentIdentityId = ctx.Config.AgenticAppId;
            }
            else
            {
                var agentIdentityDisplayName = ctx.Config.AgentIdentityDisplayName
                    ?? ctx.Config.WebAppName
                    ?? "Agent";

                // Try delegated flow first (AgentIdentity.Create.All) — no client secret required.
                // Requires Agent ID Administrator, Agent ID Developer, or Global Administrator role.
                ctx.Logger.LogInformation("Creating agent identity (delegated flow)...");
                var agentId = await ctx.GraphApiService.CreateAgentIdentityDelegatedAsync(
                    ctx.Config.TenantId!,
                    ctx.Config.AgentBlueprintId!,
                    agentIdentityDisplayName,
                    ctx.CancellationToken);

                // Fall back to blueprint client credentials if delegated flow failed and secret is available.
                if (agentId is null && !string.IsNullOrWhiteSpace(ctx.Config.AgentBlueprintClientSecret))
                {
                    ctx.Logger.LogInformation("Delegated flow failed — retrying via blueprint client credentials...");

                    var clientSecret = SecretProtectionHelper.UnprotectSecret(
                        ctx.Config.AgentBlueprintClientSecret,
                        ctx.Config.AgentBlueprintClientSecretProtected,
                        ctx.Logger);

                    agentId = await ctx.GraphApiService.CreateAgentIdentityAsync(
                        ctx.Config.TenantId!,
                        ctx.Config.AgentBlueprintId!,
                        clientSecret,
                        agentIdentityDisplayName,
                        ctx.CancellationToken);
                }

                if (agentId is not null)
                {
                    ctx.Config.AgenticAppId = agentId;
                    await ctx.ConfigService.SaveStateAsync(ctx.Config);
                    ctx.Results.AgentIdentityCreated = true;
                    ctx.Results.AgentIdentityId = agentId;
                    ctx.Logger.LogInformation("Agent identity created (ID: {AgentId})", agentId);
                }
                else
                {
                    ctx.Results.Errors.Add(
                        "Agent identity creation failed. " +
                        "Ensure the account has Agent ID Administrator, Agent ID Developer, or Global Administrator role.");
                    ctx.Logger.LogError(
                        "Agent identity creation failed. " +
                        "Ensure the account has Agent ID Administrator, Agent ID Developer, or Global Administrator role.");
                }
            }

            // Step 6: Register Agent via AgentX Agent Registration API V2.
            ctx.Logger.LogInformation("");

            if (!string.IsNullOrWhiteSpace(ctx.Config.AgentRegistrationId))
            {
                ctx.Logger.LogInformation("Agent already registered (ID: {RegistrationId}). Skipping.", ctx.Config.AgentRegistrationId);
                ctx.Results.AgentInstanceRegistered = true;
                ctx.Results.AgentInstanceId = ctx.Config.AgentRegistrationId;
            }
            else
            {
                ctx.Logger.LogInformation("Registering agent...");

                var agentDisplayName = ctx.Config.AgentIdentityDisplayName
                    ?? ctx.Config.WebAppName
                    ?? "Agent";
                // AgentX registration represents the agent itself, not the Entra identity.
                // Normalize any legacy " Identity" suffix to " Agent".
                if (agentDisplayName.EndsWith(" Identity", StringComparison.OrdinalIgnoreCase))
                    agentDisplayName = agentDisplayName[..^" Identity".Length] + " Agent";

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
                    ctx.Logger.LogInformation("Agent registered (ID: {RegistrationId})", registrationId);
                }
                else
                {
                    ctx.Results.Errors.Add("Agent registration failed via AgentX V2 API. See log output above for the HTTP response.");
                    ctx.Logger.LogError("Agent registration failed via AgentX V2 API. See log output above for the HTTP response.");
                }
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
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "Setup failed: {Message}", ex.Message);
            ctx.Results.Errors.Add(ex.Message);
        }

        // Display summary — always, even when errors occurred above
        ctx.Logger.LogInformation("");
        SetupHelpers.DisplaySetupSummary(ctx.Results, ctx.Logger);

        return ctx.Results.HasErrors ? 1 : 0;
    }
}
