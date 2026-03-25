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
///   3. Batch permissions — Graph delegated + Agent 365 Tools + Messaging Bot + Observability + Power Platform
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

    internal static readonly string[] MessagingBotApiPermissions =
        ["Authorization.ReadWrite", "user_impersonation"];

    internal static readonly string[] ObservabilityApiPermissions =
        ["user_impersonation"];

    internal static readonly string[] PowerPlatformApiPermissions =
        ["Connectivity.Connections.Read"];

    /// <summary>
    /// Prints a dry-run plan showing all resources that would be created or configured,
    /// using actual names and values from the loaded config. Makes no API calls.
    /// </summary>
    public static void PrintDryRunPlan(Agent365Config config, ILogger logger)
    {
        var displayName = config.AgentIdentityDisplayName;
        var existingBlueprint = !string.IsNullOrWhiteSpace(config.AgentBlueprintId);
        var existingAgentId = !string.IsNullOrWhiteSpace(config.AgenticAppId);
        var existingInstance = !string.IsNullOrWhiteSpace(config.AgentInstanceId);

        logger.LogInformation("Non-DW Blueprint Setup Plan (dry run — no changes will be made)");
        logger.LogInformation("");

        // Blueprint
        logger.LogInformation("  Blueprint");
        if (existingBlueprint)
            logger.LogInformation("    Reuse Blueprint:         \"{DisplayName}\"  id: {BlueprintId}",
                displayName, config.AgentBlueprintId);
        else
            logger.LogInformation("    Create Blueprint:        \"{DisplayName}\"  (multi-tenant)", displayName);
        logger.LogInformation("    Assign API Permissions:  Microsoft Graph: {GraphScopes}",
            string.Join(", ", GraphDelegatedPermissions));
        logger.LogInformation("                             Agent 365 Tools: {A365Scopes}",
            string.Join(", ", Agent365ToolsDelegatedPermissions));
        logger.LogInformation("    Configure Blueprint SP:  inherited permissions");
        logger.LogInformation("");

        // Agent Instance
        logger.LogInformation("  Agent Instance");
        if (existingAgentId)
            logger.LogInformation("    Reuse Agent ID:          Blueprint Instance  id: {AgentId}  tenant: {TenantId}",
                config.AgenticAppId, config.TenantId);
        else
            logger.LogInformation("    Create Agent ID:         Blueprint Instance  tenant: {TenantId}",
                config.TenantId);
        logger.LogInformation("");

        // Register
        logger.LogInformation("  Register");
        if (existingInstance)
            logger.LogInformation("    Reuse Agent Instance:    already registered  id: {InstanceId}",
                config.AgentInstanceId);
        else
            logger.LogInformation("    Register Agent Instance: via Agent Instance Graph API  (no manifest)");
        logger.LogInformation("             NOTE: Requires 'Agent Registry Administrator' role in Entra ID");
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

            // Step 1.5: Consent check — detect missing consent for required permissions and prompt.
            // Requirements check passes even when individual scopes are missing from the grant
            // (ValidateAdminConsentAsync only verifies that ANY required permission is consented).
            // We surface any gap here so the user can confirm before we proceed.
            await EnsureConsentWithPromptAsync(ctx);

            // Step 2: Blueprint creation (shared with DW)
            await AllSubcommand.ExecuteBlueprintStepAsync(ctx);

            // Step 3: Batch permissions — same full spec list as DW blueprints.
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
                new ResourcePermissionSpec(
                    ConfigConstants.MessagingBotApiAppId,
                    "Messaging Bot API",
                    MessagingBotApiPermissions,
                    SetInheritable: true),
                new ResourcePermissionSpec(
                    ConfigConstants.ObservabilityApiAppId,
                    "Observability API",
                    ObservabilityApiPermissions,
                    SetInheritable: true),
                new ResourcePermissionSpec(
                    PowerPlatformConstants.PowerPlatformApiResourceAppId,
                    "Power Platform API",
                    PowerPlatformApiPermissions,
                    SetInheritable: true),
            };

            await AllSubcommand.ExecuteBatchPermissionsStepAsync(
                ctx, specs,
                knownBlueprintSpObjectId: ctx.Config.AgentBlueprintServicePrincipalObjectId);

            SetupHelpers.ApplyConsentUrlsIfNeeded(ctx, mcpResourceAppId, GraphDelegatedPermissions, Agent365ToolsDelegatedPermissions);

            // Save state after permissions (before agent identity creation, so progress
            // is not lost if subsequent steps fail).
            await ctx.ConfigService.SaveStateAsync(ctx.Config);

            // Step 4: Create Agent Identity via Agent Identity Graph API.
            createAgentIdentity:
            ctx.Logger.LogInformation("");

            if (!string.IsNullOrWhiteSpace(ctx.Config.AgenticAppId))
            {
                ctx.Logger.LogInformation("Agent identity already created (ID: {AgentId}). Skipping.", ctx.Config.AgenticAppId);
                ctx.Results.AgentIdentityCreated = true;
                ctx.Results.AgentIdentityId = ctx.Config.AgenticAppId;
            }
            else if (string.IsNullOrWhiteSpace(ctx.Config.AgentBlueprintClientSecret))
            {
                ctx.Results.Errors.Add(
                    "Agent identity creation failed: Blueprint client secret is not configured. " +
                    "This should have been created during blueprint setup.");
                ctx.Logger.LogError(
                    "Agent identity creation failed: Blueprint client secret is not configured. " +
                    "Ensure the blueprint setup completed successfully.");
            }
            else
            {
                var agentIdentityDisplayName = ctx.Config.AgentIdentityDisplayName
                    ?? ctx.Config.WebAppName
                    ?? "Agent";

                var clientSecret = Microsoft.Agents.A365.DevTools.Cli.Helpers.SecretProtectionHelper.UnprotectSecret(
                    ctx.Config.AgentBlueprintClientSecret,
                    ctx.Config.AgentBlueprintClientSecretProtected,
                    ctx.Logger);

                ctx.Logger.LogInformation("Creating agent identity...");
                var agentId = await ctx.GraphApiService.CreateAgentIdentityAsync(
                    ctx.Config.TenantId!,
                    ctx.Config.AgentBlueprintId!,
                    clientSecret,
                    agentIdentityDisplayName,
                    ctx.CancellationToken);

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
                        "Ensure the blueprint has the required permissions " +
                        "(Application.ReadWrite.All, AgentIdentity.Create.OwnedBy).");
                    ctx.Logger.LogError(
                        "Agent identity creation failed. " +
                        "Ensure the blueprint has the required permissions.");
                }
            }

            // Step 5: Register Agent Instance via Agent Instance Graph API.
            ctx.Logger.LogInformation("");

            if (!string.IsNullOrWhiteSpace(ctx.Config.AgentInstanceId))
            {
                ctx.Logger.LogInformation("Agent instance already registered (ID: {InstanceId}). Skipping.", ctx.Config.AgentInstanceId);
                ctx.Results.AgentInstanceRegistered = true;
                ctx.Results.AgentInstanceId = ctx.Config.AgentInstanceId;
            }
            else
            {
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
                        "Ensure you have the 'Agent Registry Administrator' role in Entra ID.");
                    ctx.Logger.LogError(
                        "Agent instance registration failed. " +
                        "Ensure you have the 'Agent Registry Administrator' role in Entra ID.");
                }
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
