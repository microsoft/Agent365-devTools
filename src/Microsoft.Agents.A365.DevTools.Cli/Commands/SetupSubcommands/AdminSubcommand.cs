// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
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
        IConfirmationProvider confirmationProvider,
        AgentBlueprintService? blueprintService = null,
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command(
            "admin",
            "Complete OAuth2 permission grants that require Global Administrator.\n\n" +
            "Run this after 'a365 setup all' has been executed by an Agent ID Admin or Developer.\n\n" +
            "Two modes:\n" +
            "  --blueprint-id <guid>   Config-free. Pass the blueprint ID shown in 'a365 setup all' output.\n" +
            "                          Creates Observability API and Power Platform API grants only.\n" +
            "                          Tenant is auto-detected from 'az account show'.\n" +
            "  --config-dir <path>     Full mode. Loads config files and creates grants for all APIs\n" +
            "                          configured in a365.config.json.\n\n" +
            "Required permissions:\n" +
            "  - Global Administrator (for OAuth2 grants)\n" +
            "  - Agent Registry Administrator (for non-DW agent instance registration — optional)\n\n" +
            "Typical handoff workflow:\n" +
            "  1. Agent ID Developer runs: a365 setup all --agent-name <name>\n" +
            "  2. Global Admin runs:       a365 setup admin --blueprint-id <blueprint-id from output>");

        var blueprintIdOption = new Option<string?>(
            ["--blueprint-id", "-id"],
            description: "Blueprint app ID (client ID). Config-free mode: skips loading config files.\n" +
                         "Use the ID shown in the 'a365 setup all' output. Tenant is auto-detected from 'az account show'.");

        var agentNameOption = new Option<string?>(
            ["--agent-name", "-n"],
            description: "Agent base name. Alias for config-free mode: resolves blueprint ID from Entra by display name.\n" +
                         "Equivalent to passing --blueprint-id with the ID looked up automatically.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Overrides auto-detection. Use with --agent-name.");

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

        command.AddOption(blueprintIdOption);
        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(configDirOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(skipRequirementsOption);
        command.AddOption(yesOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            var blueprintId      = ctx.ParseResult.GetValueForOption(blueprintIdOption);
            var agentName        = ctx.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag     = ctx.ParseResult.GetValueForOption(tenantIdOption);
            var configDir        = ctx.ParseResult.GetValueForOption(configDirOption)!;
            var dryRun           = ctx.ParseResult.GetValueForOption(dryRunOption);
            var skipRequirements = ctx.ParseResult.GetValueForOption(skipRequirementsOption);
            var yes              = ctx.ParseResult.GetValueForOption(yesOption);
            var ct               = ctx.GetCancellationToken();

            // --agent-name is an alias for config-free mode: resolve blueprint ID via Entra lookup.
            if (string.IsNullOrWhiteSpace(blueprintId) && !string.IsNullOrWhiteSpace(agentName) && resolver != null)
            {
                var bootstrapConfig = await resolver.ResolveAsync(
                    agentName, tenantIdFlag, new FileInfo("a365.config.json"), isCleanupMode: true, ct);
                if (bootstrapConfig is null) { ctx.ExitCode = 1; return; }
                blueprintId = bootstrapConfig.AgentBlueprintId;
                if (string.IsNullOrWhiteSpace(blueprintId))
                {
                    logger.LogError(
                        "Blueprint for agent '{Name}' not found in Entra. " +
                        "Run 'a365 setup blueprint --agent-name {Name}' first.", agentName, agentName);
                    ctx.ExitCode = 1;
                    return;
                }
            }

            var correlationId = HttpClientFactory.GenerateCorrelationId();
            logger.LogDebug("Starting setup admin (CorrelationId: {CorrelationId})", correlationId);

            if (dryRun)
            {
                logger.LogInformation("Dry run: a365 setup admin --dry-run");
                logger.LogInformation("");
                logger.LogInformation("The following steps would be performed.");
                logger.LogInformation("");
                if (!string.IsNullOrWhiteSpace(blueprintId))
                {
                    logger.LogInformation(SetupHelpers.DryRunRow(1, "Prerequisites") + "validate (az account show — tenant detection)");
                    logger.LogInformation(SetupHelpers.DryRunRow(2, "Blueprint") + "resolve (service principal lookup for {BlueprintId})", blueprintId);
                    logger.LogInformation(SetupHelpers.DryRunRow(3, "Permission Grants") + "grant tenant-wide for Observability API, Power Platform API");
                }
                else
                {
                    logger.LogInformation(SetupHelpers.DryRunRow(1, "Prerequisites") + (skipRequirements ? "skip (--skip-requirements)" : "validate"));
                    logger.LogInformation(SetupHelpers.DryRunRow(2, "Blueprint") + "resolve from config: {ConfigDir}", configDir.FullName);
                    logger.LogInformation(SetupHelpers.DryRunRow(3, "Permission Grants") + "grant tenant-wide (resource set from configuration)");
                }
                logger.LogInformation("");
                logger.LogInformation("No changes will be made. Run without --dry-run to apply.");
                return;
            }

            var setupResults = new SetupResults();

            try
            {
                Agent365Config setupConfig;
                List<ResourcePermissionSpec> specs;
                bool isBlueprintIdMode = !string.IsNullOrWhiteSpace(blueprintId);

                if (isBlueprintIdMode)
                {
                    // Config-free path: admin received only the blueprint ID from the developer.
                    // Detect tenant from az account; grant Observability + Power Platform only.
                    var tenantId = await TenantDetectionHelper.DetectTenantIdAsync(null, logger);
                    if (string.IsNullOrWhiteSpace(tenantId))
                    {
                        logger.LogError("Could not detect tenant ID. Run 'az login' and ensure an account is selected.");
                        ExceptionHandler.ExitWithCleanup(1);
                        return;
                    }

                    // Resolve the well-known CLI client app so Graph auth uses delegated permissions.
                    // FindApplicationByDisplayNameAsync uses the default (az CLI) token path and
                    // does not require CustomClientAppId to be set beforehand.
                    var clientAppId = await graphApiService.FindApplicationByDisplayNameAsync(
                        tenantId, AuthenticationConstants.WellKnownClientAppDisplayName, ct);
                    if (!string.IsNullOrWhiteSpace(clientAppId))
                        graphApiService.CustomClientAppId = clientAppId;

                    setupConfig = new Agent365Config { TenantId = tenantId, AgentBlueprintId = blueprintId };
                    specs = SetupHelpers.GetNonDwFixedApiPermissionSpecs(setInheritable: false).ToList();
                }
                else
                {
                    // Config-dir path: load full config from disk.
                    var configPath = Path.Combine(configDir.FullName, "a365.config.json");
                    if (!File.Exists(configPath))
                    {
                        logger.LogError(
                            "No configuration file found at {ConfigPath}.", configPath);
                        logger.LogError(
                            "Ensure the Agent ID Admin has run 'a365 setup all' and shared the config folder, " +
                            "or pass --blueprint-id (or --agent-name) to skip config file loading.");
                        ctx.ExitCode = 1;
                        return;
                    }

                    setupConfig = await configService.LoadAsync(configPath);

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
                        catch (Exception reqEx) when (reqEx is not OperationCanceledException && reqEx is not CleanExitException)
                        {
                            logger.LogError("Requirements check failed: {Message}", reqEx.Message);
                            logger.LogDebug(reqEx, "Requirements check exception details");
                            logger.LogInformation("To bypass requirement validation, rerun with --skip-requirements.");
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

                    specs = await SetupHelpers.BuildConfiguredPermissionSpecsAsync(setupConfig, setInheritable: false);
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

                (bool grantsConfigured, string? blueprintSpObjectId) =
                    await BatchPermissionsOrchestrator.GrantAdminPermissionsAsync(
                        graphApiService, setupConfig,
                        setupConfig.AgentBlueprintId!, setupConfig.TenantId,
                        specs, logger, setupResults, ct,
                        knownBlueprintSpObjectId: setupConfig.AgentBlueprintServicePrincipalObjectId,
                        blueprintService: blueprintService);

                setupResults.AdminConsentGranted = grantsConfigured;

                // Agent instance registration: config-dir path only — display name not available in blueprint-id mode.
                // This requires 'Agent Registry Administrator' role -- separate from Global Administrator.
                // The admin running this command may or may not hold that role. We attempt it and report.
                if (!isBlueprintIdMode && setupConfig.IsNonDwBlueprint)
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
                        logger.LogInformation("NOTE: This step requires 'Agent Registry Administrator' role - separate from Global Administrator.");

                        var agentDisplayName = setupConfig.AgentIdentityDisplayName
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
                                "Agent instance registration failed - 'Agent Registry Administrator' role is not assigned " +
                                "for this account. The developer must get that role assigned by a tenant admin and run: " +
                                "a365 setup all --aiteammate false --agent-instance-only");
                        }
                    }
                }

                SetupHelpers.DisplayAdminSetupSummary(setupResults, blueprintSpObjectId, logger);

                // For autonomous/S2S agents, application-type permissions are needed once resource
                // APIs publish app roles. Until then, the delegated grants above serve as a bridge.
                if (isBlueprintIdMode)
                {
                    logger.LogInformation("");
                    logger.LogInformation("Note: For autonomous/S2S agents, application-type permissions will be required once available.");
                    logger.LogInformation("  Grant them in the Entra portal: App registrations > <blueprint-app> > API permissions > Grant admin consent");
                    logger.LogInformation("  Microsoft Admin Center: https://admin.microsoft.com");
                }
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
