// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Admin subcommand - Completes OAuth2 permission grants that require Global Administrator.
///
/// Background: 'a365 setup all' run by an Agent ID Admin or Developer configures inheritable
/// permissions (which do not require GA) but cannot create AllPrincipals oauth2PermissionGrants
/// (which do). This command completes that remaining step.
///
/// Technical limitation: oauth2PermissionGrant creation via the Graph API always requires
/// DelegatedPermissionGrant.ReadWrite.All, an admin-only scope. Additionally, GA bypasses
/// entitlement validation and can grant any scope; non-admin users receive HTTP 403 or 400
/// for all resource SPs. There is no self-service path for non-admin users via the API.
///
/// Required permissions: Global Administrator
/// </summary>
internal static class AdminSubcommand
{
    public static List<IRequirementCheck> GetChecks(AzureAuthValidator auth)
        => SetupCommand.GetBaseChecks(auth);

    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        AzureAuthValidator authValidator,
        GraphApiService graphApiService,
        IConfirmationProvider confirmationProvider)
    {
        var command = new Command(
            "admin",
            "Complete OAuth2 permission grants that require Global Administrator.\n\n" +
            "Run this after 'a365 setup all' has been executed by an Agent ID Admin or Developer.\n" +
            "Point --config-dir at the folder containing the agent's a365.config.json and\n" +
            "a365.generated.config.json files.\n\n" +
            "The permission set is auto-detected from the configuration:\n" +
            "  - DW blueprint (aiTeammate=true):    Graph + A365 Tools + Bot API + Observability + Power Platform\n" +
            "  - Non-DW blueprint (aiTeammate=false): Graph + A365 Tools only\n\n" +
            "For non-DW blueprint flows, this command also attempts agent instance registration\n" +
            "if not yet done. That step requires 'Agent Registry Administrator' role (separate\n" +
            "from Global Administrator). If the running account lacks that role, the OAuth2\n" +
            "grants still complete and a warning is printed for the remaining step.\n\n" +
            "Required permissions:\n" +
            "  - Global Administrator (for OAuth2 grants)\n" +
            "  - Agent Registry Administrator (for non-DW agent instance registration — optional)\n\n" +
            "Typical handoff workflow:\n" +
            "  1. Agent ID Admin runs: a365 setup all\n" +
            "  2. Agent ID Admin shares the config folder with a Global Administrator\n" +
            "  3. Global Admin runs:   a365 setup admin --config-dir \"<path-to-config-folder>\"");

        var configDirOption = new Option<DirectoryInfo>(
            ["--config-dir", "-d"],
            getDefaultValue: () => new DirectoryInfo(Environment.CurrentDirectory),
            description: "Directory containing a365.config.json and a365.generated.config.json");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Show detailed output");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            description: "Show what would be done without executing");

        var skipRequirementsOption = new Option<bool>(
            "--skip-requirements",
            description: "Skip requirements validation check\n" +
                        "Use with caution: setup may fail if prerequisites are not met");

        var yesOption = new Option<bool>(
            ["--yes", "-y"],
            description: "Skip confirmation prompt and proceed automatically");

        command.AddOption(configDirOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(skipRequirementsOption);
        command.AddOption(yesOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            var configDir        = ctx.ParseResult.GetValueForOption(configDirOption)!;
            var dryRun           = ctx.ParseResult.GetValueForOption(dryRunOption);
            var skipRequirements = ctx.ParseResult.GetValueForOption(skipRequirementsOption);
            var yes              = ctx.ParseResult.GetValueForOption(yesOption);
            var ct               = ctx.GetCancellationToken();

            var correlationId = HttpClientFactory.GenerateCorrelationId();
            logger.LogDebug("Starting setup admin (CorrelationId: {CorrelationId})", correlationId);

            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Admin Permission Grants");
                logger.LogInformation("This would execute the following operations:");
                logger.LogInformation("");
                if (!skipRequirements)
                    logger.LogInformation("  0. Validate prerequisites");
                else
                    logger.LogInformation("  0. Skip: Requirements validation (--skip-requirements flag used)");
                logger.LogInformation("  1. Load configuration from: {ConfigDir}", configDir.FullName);
                logger.LogInformation("  2. Resolve blueprint and resource service principals");
                logger.LogInformation("  3. Create AllPrincipals OAuth2 grants (resource set auto-detected from configuration)");
                logger.LogInformation("  4. [Non-DW only] Attempt agent instance registration if not yet done");
                logger.LogInformation("     Requires 'Agent Registry Administrator' role — separate from Global Administrator.");
                logger.LogInformation("     If this account does not have that role, step 4 is skipped with a warning.");
                logger.LogInformation("No actual changes will be made.");
                return;
            }

            var setupResults = new SetupResults();

            try
            {
                var configPath = Path.Combine(configDir.FullName, "a365.config.json");
                if (!File.Exists(configPath))
                {
                    logger.LogError(
                        "Configuration file not found: {ConfigPath}",
                        configPath);
                    logger.LogError(
                        "Ensure the Agent ID Admin has run 'a365 setup all' and shared the config folder.");
                    ExceptionHandler.ExitWithCleanup(1);
                    return;
                }

                var setupConfig = await configService.LoadAsync(configPath);

                if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
                    graphApiService.CustomClientAppId = setupConfig.ClientAppId;

                if (!skipRequirements)
                {
                    var checks = GetChecks(authValidator);
                    try
                    {
                        await RequirementsSubcommand.RunChecksOrExitAsync(
                            checks, setupConfig, logger, ct);
                    }
                    catch (Exception reqEx) when (reqEx is not OperationCanceledException)
                    {
                        logger.LogError(reqEx, "Requirements check failed: {Message}", reqEx.Message);
                        logger.LogError("Rerun with --skip-requirements to bypass.");
                        ExceptionHandler.ExitWithCleanup(1);
                    }
                }

                if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
                {
                    logger.LogError(
                        "AgentBlueprintId is missing from the generated config. " +
                        "Ensure 'a365 setup all' completed blueprint creation before running this command.");
                    ExceptionHandler.ExitWithCleanup(1);
                    return;
                }

                // Build the spec list matching the flow that created the blueprint.
                // Non-DW blueprint: Graph + A365 Tools only (no Bot API, Observability, Power Platform).
                // DW blueprint: full spec list including all resource APIs.
                var mcpResourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(setupConfig.Environment);

                List<ResourcePermissionSpec> specs;

                if (setupConfig.IsNonDwBlueprint)
                {
                    logger.LogDebug("Non-DW blueprint flow detected — using trimmed spec list (Graph + A365 Tools only)");
                    specs = new List<ResourcePermissionSpec>
                    {
                        new ResourcePermissionSpec(
                            AuthenticationConstants.MicrosoftGraphResourceAppId,
                            "Microsoft Graph",
                            NonDwBlueprintSetupOrchestrator.GraphDelegatedPermissions,
                            SetInheritable: false),
                        new ResourcePermissionSpec(
                            mcpResourceAppId,
                            "Agent 365 Tools",
                            NonDwBlueprintSetupOrchestrator.Agent365ToolsDelegatedPermissions,
                            SetInheritable: false),
                    };
                }
                else
                {
                    var mcpManifestPath = Path.Combine(
                        setupConfig.DeploymentProjectPath ?? string.Empty,
                        McpConstants.ToolingManifestFileName);
                    var mcpScopes = await PermissionsSubcommand.ReadMcpScopesAsync(mcpManifestPath, logger);
                    specs = new List<ResourcePermissionSpec>
                    {
                        new ResourcePermissionSpec(
                            AuthenticationConstants.MicrosoftGraphResourceAppId,
                            "Microsoft Graph",
                            setupConfig.AgentApplicationScopes.ToArray(),
                            SetInheritable: false),
                        new ResourcePermissionSpec(
                            mcpResourceAppId,
                            "Agent 365 Tools",
                            mcpScopes,
                            SetInheritable: false),
                        new ResourcePermissionSpec(
                            ConfigConstants.MessagingBotApiAppId,
                            "Messaging Bot API",
                            new[] { "Authorization.ReadWrite", "user_impersonation" },
                            SetInheritable: false),
                        new ResourcePermissionSpec(
                            ConfigConstants.ObservabilityApiAppId,
                            "Observability API",
                            new[] { "user_impersonation" },
                            SetInheritable: false),
                        new ResourcePermissionSpec(
                            PowerPlatformConstants.PowerPlatformApiResourceAppId,
                            "Power Platform API",
                            new[] { "Connectivity.Connections.Read" },
                            SetInheritable: false),
                    };
                }

                foreach (var customPerm in setupConfig.CustomBlueprintPermissions ?? new List<CustomResourcePermission>())
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
                            SetInheritable: false));
                    }
                }

                // Display what will be done and ask for confirmation (unless --yes is set).
                DisplayAdminConsentPreview(setupConfig, specs, logger);

                if (!yes)
                {
                    var confirmed = await confirmationProvider.ConfirmAsync("Do you want to perform this operation? (y/N): ");
                    if (!confirmed)
                    {
                        logger.LogInformation("Operation cancelled.");
                        return;
                    }
                }

                logger.LogInformation("");
                logger.LogInformation("Running admin permission grants... (TraceId: {TraceId})", correlationId);
                if (skipRequirements)
                    logger.LogInformation("NOTE: Requirements validation skipped (--skip-requirements flag used)");

                (bool grantsConfigured, string? blueprintSpObjectId) =
                    await BatchPermissionsOrchestrator.GrantAdminPermissionsAsync(
                        graphApiService, setupConfig,
                        setupConfig.AgentBlueprintId!, setupConfig.TenantId,
                        specs, logger, setupResults, ct,
                        knownBlueprintSpObjectId: setupConfig.AgentBlueprintServicePrincipalObjectId);

                setupResults.AdminConsentGranted = grantsConfigured;

                // For non-DW blueprint flow: also attempt agent instance registration if not yet done.
                // This requires 'Agent Registry Administrator' role — separate from Global Administrator.
                // The admin running this command may or may not hold that role. We attempt it and report.
                if (setupConfig.IsNonDwBlueprint)
                {
                    if (!string.IsNullOrWhiteSpace(setupConfig.AgentInstanceId))
                    {
                        logger.LogInformation("Agent instance already registered (ID: {InstanceId}). Skipping.", setupConfig.AgentInstanceId);
                        setupResults.AgentInstanceRegistered = true;
                        setupResults.AgentInstanceId = setupConfig.AgentInstanceId;
                    }
                    else
                    {
                        logger.LogInformation("");
                        logger.LogInformation("Non-DW blueprint flow: attempting agent instance registration...");
                        logger.LogInformation("NOTE: This step requires 'Agent Registry Administrator' role — separate from Global Administrator.");

                        var agentDisplayName = setupConfig.AgentIdentityDisplayName
                            ?? setupConfig.WebAppName
                            ?? "Agent";

                        var instanceId = await graphApiService.RegisterAgentInstanceAsync(
                            setupConfig.TenantId!,
                            agentDisplayName,
                            setupConfig.AgentBlueprintId,
                            ct);

                        if (instanceId is not null)
                        {
                            setupConfig.AgentInstanceId = instanceId;
                            await configService.SaveStateAsync(setupConfig);
                            setupResults.AgentInstanceRegistered = true;
                            setupResults.AgentInstanceId = instanceId;
                            logger.LogInformation("Agent instance registered (ID: {InstanceId})", instanceId);
                        }
                        else
                        {
                            logger.LogWarning(
                                "Agent instance registration failed — 'Agent Registry Administrator' role is not assigned " +
                                "for this account. The developer must get that role assigned by a tenant admin and run: " +
                                "a365 setup all --aiteammate false --agent-instance-only");
                        }
                    }
                }

                SetupHelpers.DisplayAdminSetupSummary(setupResults, blueprintSpObjectId, logger);
            }
            catch (Agent365Exception ex)
            {
                var logFilePath = ConfigService.GetCommandLogPath(CommandNames.Setup);
                ExceptionHandler.HandleAgent365Exception(ex, logFilePath: logFilePath);
                ExceptionHandler.ExitWithCleanup(1);
            }
            catch (FileNotFoundException fnfEx)
            {
                logger.LogError("Admin setup failed: {Message}", fnfEx.Message);
                ExceptionHandler.ExitWithCleanup(1);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Admin setup failed: {Message}", ex.Message);
                throw;
            }
        });

        return command;
    }

    /// <summary>
    /// Prints a preview of the OAuth2 grants that will be created, so the administrator
    /// can review before approving.
    /// </summary>
    private static void DisplayAdminConsentPreview(
        Agent365Config config,
        IReadOnlyList<ResourcePermissionSpec> specs,
        ILogger logger)
    {
        var displayName = !string.IsNullOrWhiteSpace(config.AgentBlueprintDisplayName)
            ? config.AgentBlueprintDisplayName
            : config.AgentBlueprintId;

        logger.LogWarning("WARNING: The following OAuth2 grants will be created tenant-wide (consentType=AllPrincipals):");
        logger.LogInformation("");
        logger.LogInformation("  Blueprint : {DisplayName} ({BlueprintId})", displayName, config.AgentBlueprintId);
        logger.LogInformation("  Tenant    : {TenantId}", config.TenantId);
        logger.LogInformation("");

        foreach (var spec in specs)
        {
            if (spec.Scopes.Length == 0) continue;
            logger.LogInformation("  - {ResourceName,-20}: {Scopes}",
                spec.ResourceName,
                string.Join(", ", spec.Scopes));
        }

        logger.LogInformation("");
        logger.LogWarning("WARNING: This gives the agent delegated consent for ALL users in the tenant.");
    }
}
