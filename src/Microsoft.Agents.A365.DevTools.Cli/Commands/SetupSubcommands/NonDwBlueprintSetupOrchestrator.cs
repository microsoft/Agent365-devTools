// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Orchestrates setup for blueprint-based non-AI Teammate agent deployments.
/// Creates an Agent Identity Blueprint in Entra, provisions a Blueprint SP with API permissions,
/// and registers an Agent Instance via the Agent Instance Graph API.
/// No Azure Bot Service, manifest zip, or client secret is required.
///
/// Steps:
///   1. Requirements validation (Azure auth + custom client app)
///   2. Blueprint creation (shared with DW via AllSubcommand.ExecuteBlueprintStepAsync)
///   3. Batch permissions — Graph delegated + Agent 365 Tools delegated only
///   4. Agent Instance registration via POST /beta/agentRegistry/agentInstances
/// </summary>
internal static class NonDwBlueprintSetupOrchestrator
{
    // Microsoft Graph delegated permissions added to the blueprint
    internal static readonly string[] GraphDelegatedPermissions =
    [
        "User.Read", "openid", "profile", "email", "offline_access"
    ];

    // Agent 365 Tools delegated permissions added to the blueprint
    internal static readonly string[] Agent365ToolsDelegatedPermissions =
    [
        "McpServers.Mail.All", "McpServersMetadata.Read.All", "AgentTools.ListMCPServers.All"
    ];

    /// <summary>
    /// Prints a dry-run plan showing all resources that would be created or configured,
    /// using actual names and values from the loaded config. Makes no API calls.
    /// </summary>
    public static void PrintDryRunPlan(Agent365Config config, ILogger logger)
    {
        var displayName = config.AgentIdentityDisplayName;
        var existingBlueprint = !string.IsNullOrWhiteSpace(config.AgentBlueprintId);

        logger.LogInformation("Non-DW Blueprint Setup Plan (dry run — no changes will be made)");
        logger.LogInformation("");

        // Blueprint
        logger.LogInformation("  Blueprint");
        if (existingBlueprint)
            logger.LogInformation("    [REUSE]  Blueprint           \"{DisplayName}\"  id: {BlueprintId}",
                displayName, config.AgentBlueprintId);
        else
            logger.LogInformation("    [CREATE] Blueprint           \"{DisplayName}\"  (multi-tenant)", displayName);
        logger.LogInformation("    [ASSIGN] API Permissions     Microsoft Graph: {GraphScopes}",
            string.Join(", ", GraphDelegatedPermissions));
        logger.LogInformation("                                 Agent 365 Tools: {A365Scopes}",
            string.Join(", ", Agent365ToolsDelegatedPermissions));
        logger.LogInformation("    [CREATE] Blueprint SP        consent to permissions");
        logger.LogInformation("");

        // Agent Instance
        logger.LogInformation("  Agent Instance");
        logger.LogInformation("    [CREATE] Agent ID            Blueprint Instance  tenant: {TenantId}", config.TenantId);
        logger.LogInformation("");

        // Register
        logger.LogInformation("  Register");
        logger.LogInformation("    [REGISTER] Agent Instance    via Agent Instance Graph API  (no manifest)");
        logger.LogInformation("");

        logger.LogInformation("Run without --dry-run to execute these steps.");
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
        ctx.Logger.LogInformation("Running non-DW blueprint setup... (TraceId: {TraceId})", ctx.CorrelationId);
        ctx.Logger.LogInformation("");

        try
        {
            // Step 1: Requirements validation
            if (!ctx.SkipRequirements)
            {
                var checks = AllSubcommand.GetNonDwChecks(ctx.AuthValidator, ctx.ClientAppValidator);
                try
                {
                    await RequirementsSubcommand.RunChecksOrExitAsync(checks, ctx.Config, ctx.Logger, ctx.CancellationToken);
                }
                catch (Exception reqEx) when (reqEx is not OperationCanceledException)
                {
                    ctx.Logger.LogError(reqEx, "Requirements check failed: {Message}", reqEx.Message);
                    ctx.Logger.LogError("If you want to bypass requirement validation, rerun with --skip-requirements.");
                    return 1;
                }
            }
            else
            {
                ctx.Logger.LogInformation("NOTE: Requirements validation skipped (--skip-requirements flag used)");
            }

            // Step 2: Blueprint creation (shared with DW)
            await AllSubcommand.ExecuteBlueprintStepAsync(ctx);

            // Step 3: Batch permissions — Graph delegated + Agent 365 Tools delegated only.
            // Non-DW blueprint agents do not use Azure Bot Service, so Bot API, Observability,
            // and Power Platform are not added to the spec list.
            var mcpResourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(ctx.Config.Environment);

            var specs = new List<ResourcePermissionSpec>
            {
                new ResourcePermissionSpec(
                    AuthenticationConstants.MicrosoftGraphResourceAppId,
                    "Microsoft Graph",
                    GraphDelegatedPermissions,
                    SetInheritable: true),
                new ResourcePermissionSpec(
                    mcpResourceAppId,
                    "Agent 365 Tools",
                    Agent365ToolsDelegatedPermissions,
                    SetInheritable: true),
            };

            await AllSubcommand.ExecuteBatchPermissionsStepAsync(
                ctx, specs,
                knownBlueprintSpObjectId: ctx.Config.AgentBlueprintServicePrincipalObjectId);

            // Save state after permissions (before agent instance registration, so progress
            // is not lost if the registration call fails).
            await ctx.ConfigService.SaveStateAsync(ctx.Config);

            // Step 4: Register Agent Instance via Agent Instance Graph API.
            ctx.Logger.LogInformation("");
            ctx.Logger.LogInformation("Registering agent instance...");

            var agentDisplayName = ctx.Config.AgentIdentityDisplayName
                ?? ctx.Config.WebAppName
                ?? "Agent";

            var instanceId = await ctx.GraphApiService.RegisterAgentInstanceAsync(
                ctx.Config.TenantId!,
                agentDisplayName,
                ctx.Config.AgentBlueprintId,
                ctx.CancellationToken);

            if (instanceId is not null)
            {
                ctx.Config.AgentInstanceId = instanceId;
                await ctx.ConfigService.SaveStateAsync(ctx.Config);
                ctx.Results.AgentInstanceRegistered = true;
                ctx.Results.AgentInstanceId = instanceId;
                ctx.Logger.LogInformation("Agent instance registered (ID: {InstanceId})", instanceId);
            }
            else
            {
                ctx.Results.Errors.Add(
                    "Agent instance registration failed. " +
                    "Ensure you have the Agent Registry Administrator role and " +
                    "AgentInstance.ReadWrite.All is consented.");
                ctx.Logger.LogError(
                    "Agent instance registration failed. " +
                    "Ensure you have the Agent Registry Administrator role and " +
                    "AgentInstance.ReadWrite.All is consented.");
            }
        }
        catch (Agent365Exception ex)
        {
            var logFilePath = Services.ConfigService.GetCommandLogPath(Constants.CommandNames.Setup);
            Exceptions.ExceptionHandler.HandleAgent365Exception(ex, logFilePath: logFilePath);
            return 1;
        }
        catch (FileNotFoundException fnfEx)
        {
            ctx.Logger.LogError("Setup failed: {Message}", fnfEx.Message);
            return 1;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "Setup failed: {Message}", ex.Message);
            return 1;
        }

        // Display summary
        ctx.Logger.LogInformation("");
        SetupHelpers.DisplaySetupSummary(ctx.Results, ctx.Logger);

        return ctx.Results.HasErrors ? 1 : 0;
    }
}
