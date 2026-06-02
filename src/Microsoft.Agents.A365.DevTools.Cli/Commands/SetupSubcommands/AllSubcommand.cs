// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Linq;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// All subcommand - Runs complete setup (all steps in sequence)
/// Orchestrates individual subcommand implementations
/// Required permissions:
///   - Azure Subscription Contributor/Owner (for infrastructure and endpoint)
///   - Agent ID Developer role (for blueprint creation)
///   - Global Administrator (for permission grants and admin consent)
/// </summary>
internal static class AllSubcommand
{
    /// <summary>
    /// Returns the requirement checks for <c>setup all</c>.
    /// Composes SetupCommand base checks + ClientApp.
    /// </summary>
    public static List<Services.Requirements.IRequirementCheck> GetChecks(
        AzureAuthValidator auth,
        IClientAppValidator clientAppValidator)
    {
        var checks = new List<Services.Requirements.IRequirementCheck>(SetupCommand.GetBaseChecks(auth))
        {
            new ClientAppRequirementCheck(clientAppValidator),
            // The wids optional claim is what makes Global Administrator role detection work in the
            // orchestrator's Phase 2b. Without it, Phase 2b silently skips and the blueprint ends up
            // with inheritablePermissions.kind=allAllowed but no grants — MAC sees nothing.
            new WidsOptionalClaimRequirementCheck(clientAppValidator),
        };

        return checks;
    }

    /// <summary>
    /// Returns the requirement checks for <c>setup all --aiteammate false</c> (non-DW blueprint).
    /// Composes SetupCommand base checks + ClientApp + wids-optional-claim (skipped in bootstrap mode).
    /// </summary>
    public static List<Services.Requirements.IRequirementCheck> GetNonDwChecks(
        AzureAuthValidator auth,
        IClientAppValidator clientAppValidator,
        bool isBootstrap = false)
    {
        var checks = new List<Services.Requirements.IRequirementCheck>(SetupCommand.GetBaseChecks(auth));

        // Client app check requires a static config file — not applicable in bootstrap
        // mode where the client app is resolved dynamically via --agent-name.
        if (!isBootstrap)
        {
            checks.Add(new ClientAppRequirementCheck(clientAppValidator));
            checks.Add(new WidsOptionalClaimRequirementCheck(clientAppValidator));
        }

        return checks;
    }

    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        ITeamsGraphBackendConfigurator backendConfigurator,
        AzureAuthValidator authValidator,
        PlatformDetector platformDetector,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        IClientAppValidator clientAppValidator,
        BlueprintLookupService blueprintLookupService,
        FederatedCredentialService federatedCredentialService,
        ArmApiService? armApiService = null,
        IConfirmationProvider? confirmationProvider = null,
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command("all",
            "Run complete Agent 365 setup (all steps in sequence)\n" +
            "Includes: Infrastructure + Blueprint + Permissions + Endpoint\n\n" +
            "Minimum required permissions (Global Administrator has all of these):\n" +
            "  - Azure Subscription Contributor (for infrastructure and endpoint)\n" +
            "  - Agent ID Developer role (for blueprint creation)\n" +
            "  - Global Administrator (for permission grants and admin consent)\n\n");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Show detailed output");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            description: "Show what would be done without executing");

        var skipInfrastructureOption = new Option<bool>(
            "--skip-infrastructure",
            description: "[Deprecated] Azure infrastructure provisioning has been removed. This option is accepted for backward compatibility but has no effect.");
        skipInfrastructureOption.IsHidden = true;

        var skipRequirementsOption = new Option<bool>(
            "--skip-requirements",
            description: "Skip requirements validation check\n" +
                        "Use with caution: setup may fail if prerequisites are not met");

        var aiTeammateOption = new Option<bool>(
            "--aiteammate",
            description: "AI Teammate agent: setup provisions blueprint and permissions only.\n" +
                        "Omit for blueprint-only agent (default): setup auto-creates agent identity SP; no Entra user.\n" +
                        "Overrides the aiTeammate field in a365.config.json");

        var agentRegistrationOnlyOption = new Option<bool>(
            "--agent-registration-only",
            description: "Skip all setup steps and only run agent registration (non-M365 agents only)");

        var agentNameOption = new Option<string?>(
            ["--agent-name", "-n"],
            description: "Agent base name (e.g. \"MyAgent\"). When provided, no config file is required.\n" +
                        "Derives AgentIdentityDisplayName=\"<name> Identity\" and AgentBlueprintDisplayName=\"<name> Blueprint\".\n" +
                        "TenantId is auto-detected from 'az account show' (override with --tenant-id).\n" +
                        $"ClientAppId is resolved by looking up \"{Constants.AuthenticationConstants.WellKnownClientAppDisplayName}\" in your tenant.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Overrides auto-detection from 'az account show'.");

        var m365Option = new Option<bool>(
            "--m365",
            description: "Treat this agent as an M365 agent. When set, registers the messaging endpoint via MCP Platform. " +
                        "Default is false (opt-in); omit this flag for non-M365 agents.");

        var authModeOption = new Option<string?>(
            "--authmode",
            description: "Authentication pattern for the agent identity (blueprint agents only).\n" +
                         "  obo  — on-behalf-of (default); principal-scoped delegated grants; no admin consent needed.\n" +
                         "  s2s  — service-to-service; app permissions on agent identity; Global Admin needed or PowerShell fallback.\n" +
                         "  both — delegated grants (OBO) and app permissions (S2S).\n" +
                         "Not supported with --aiteammate true.");

        var skipSpProvisioningOption = new Option<bool>(
            "--skip-sp-provisioning",
            description: "Skip the interactive in-line provisioning of missing resource service principals.\n" +
                        "Default: setup detects resources (e.g. V2 MCP per-server audiences) whose SP is missing\n" +
                        "from this tenant, prompts per-resource, and shells out to 'az ad sp create --id <appId>'\n" +
                        "using the operator's existing az login. With --skip-sp-provisioning, missing SPs are\n" +
                        "excluded from the unified admin-consent URL and surfaced as numbered items in the Action\n" +
                        "Required block, each with the az command and a per-SP consent URL.\n" +
                        "Implicitly enabled when stdin is redirected (CI / coding-agent / pipe scenarios).");

        var messagingEndpointOption = new Option<string?>(
            "--messaging-endpoint",
            description: "HTTPS URL where the deployed M365 agent receives messages (--m365 only).\n" +
                        "When supplied, the endpoint is registered as part of setup. When omitted, an\n" +
                        "interactive run prompts for it and a non-interactive run defers it — the endpoint\n" +
                        "is a post-deploy artifact, so it can be set later with\n" +
                        "'a365 setup blueprint --endpoint-only --m365 --messaging-endpoint <url>'.");

        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(skipInfrastructureOption);
        command.AddOption(skipRequirementsOption);
        command.AddOption(aiTeammateOption);
        command.AddOption(agentRegistrationOnlyOption);
        command.AddOption(m365Option);
        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(authModeOption);
        command.AddOption(skipSpProvisioningOption);
        command.AddOption(messagingEndpointOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var config = new FileInfo("a365.config.json");
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var skipInfrastructure = context.ParseResult.GetValueForOption(skipInfrastructureOption);
            var skipRequirements = context.ParseResult.GetValueForOption(skipRequirementsOption);
            // --skip-sp-provisioning flag (off by default). Also auto-on when stdin is
            // redirected so CI / coding-agent / pipe scenarios don't hang on the per-SP
            // prompt loop.
            var skipSpProvisioningFlag = context.ParseResult.GetValueForOption(skipSpProvisioningOption);
            var skipSpProvisioning = skipSpProvisioningFlag || Console.IsInputRedirected;
            // Tri-state: null = not specified (respect config), true/false = explicit override.
            // Option<bool> means bare --aiteammate sets it to true without requiring "true" as a value.
            bool? aiTeammateFlag = context.ParseResult.CommandResult.FindResultFor(aiTeammateOption) != null
                ? context.ParseResult.GetValueForOption(aiTeammateOption)
                : null;
            var agentRegistrationOnly = context.ParseResult.GetValueForOption(agentRegistrationOnlyOption);
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            bool isM365 = context.ParseResult.GetValueForOption(m365Option);
            var authMode = context.ParseResult.GetValueForOption(authModeOption)?.ToLowerInvariant();
            // Distinguish "option omitted" from "option explicitly passed empty" — the latter must be a
            // hard error, not silently treated as omitted (which would prompt/defer instead).
            var messagingEndpointSpecified = context.ParseResult.CommandResult.FindResultFor(messagingEndpointOption) != null;
            var messagingEndpointFlag = context.ParseResult.GetValueForOption(messagingEndpointOption)?.Trim();
            var ct = context.GetCancellationToken();

            if (messagingEndpointSpecified && string.IsNullOrWhiteSpace(messagingEndpointFlag))
            {
                logger.LogError("--messaging-endpoint requires an HTTPS URL value (e.g. https://my-agent.example.com/api/messages).");
                context.ExitCode = 1;
                return;
            }

            // --messaging-endpoint validation: must be a well-formed HTTPS URL when supplied.
            if (!string.IsNullOrWhiteSpace(messagingEndpointFlag) &&
                (!Uri.TryCreate(messagingEndpointFlag, UriKind.Absolute, out var msgEndpointUri) ||
                 msgEndpointUri.Scheme != Uri.UriSchemeHttps))
            {
                logger.LogError("Invalid --messaging-endpoint value '{Value}'. Provide a valid HTTPS URL (e.g. https://my-agent.example.com/api/messages).", messagingEndpointFlag);
                context.ExitCode = 1;
                return;
            }

            // --authmode validation
            if (authMode is not null && authMode is not ("obo" or "s2s" or "both"))
            {
                logger.LogError("Invalid --authmode value '{Value}'. Allowed values: obo, s2s, both.", authMode);
                context.ExitCode = 1;
                return;
            }
            if (authMode is not null && aiTeammateFlag == true)
            {
                if (authMode == "obo")
                {
                    logger.LogWarning("--authmode obo is redundant with --aiteammate — AI Teammate agents always use OBO. Flag ignored.");
                }
                else
                {
                    logger.LogError("--authmode {AuthMode} is not supported with --aiteammate — AI Teammate agents always use OBO via agent user identity.", authMode);
                    context.ExitCode = 1;
                    return;
                }
            }

            // Generate correlation ID at workflow entry point
            var correlationId = HttpClientFactory.GenerateCorrelationId();
            logger.LogDebug("Starting setup all (CorrelationId: {CorrelationId})", correlationId);

            // --- Agent type resolution ---
            // Blueprint agent is the default. AI Teammate agent requires --aiteammate true explicitly.
            Agent365Config? nonDwConfig = null;
            bool isBootstrap = !string.IsNullOrWhiteSpace(agentName);

            if (aiTeammateFlag != true)
            {
                if (isBootstrap)
                {
                    if (dryRun)
                    {
                        // Dry-run: build config from flags only — no az CLI subprocess needed.
                        // TenantId is not shown in the plan so detection is skipped intentionally.
                        nonDwConfig = new Agent365Config
                        {
                            TenantId = tenantIdFlag ?? string.Empty,
                            ClientAppId = string.Empty,
                            AgentIdentityDisplayName = $"{agentName} Identity",
                            AgentBlueprintDisplayName = $"{agentName} Blueprint",
                            AgentDescription = agentName,
                            AiTeammate = false,
                            UseBlueprint = true,
                        };
                    }
                    else
                    {
                        // Print banner first so it appears before any auth output
                        var bootstrapRawArgs = context.ParseResult.Tokens.Select(t => t.Value).ToArray();
                        logger.LogInformation("Running \"a365 {Args}\"...", string.Join(" ", bootstrapRawArgs));
                        logger.LogInformation("");

                        // Real run: resolve client app ID from Entra
                        nonDwConfig = resolver != null
                            ? await resolver.ResolveAsync(agentName!, tenantIdFlag, config, isCleanupMode: false, ct)
                            : await BuildBootstrapConfigAsync(agentName!, tenantIdFlag, executor, graphApiService, logger, ct);
                        if (nonDwConfig is null)
                        {
                            context.ExitCode = 1;
                            return;
                        }

                        // Log resolved config so the user can verify the inferred values
                        logger.LogInformation("Bootstrap config resolved:");
                        using (logger.Indent())
                        {
                            logger.LogInformation("TenantId:             {TenantId}", nonDwConfig.TenantId);
                            logger.LogInformation("ClientAppId:          {ClientAppId}", nonDwConfig.ClientAppId);
                            logger.LogInformation("BlueprintDisplayName: {Name}", nonDwConfig.AgentBlueprintDisplayName);
                            logger.LogInformation("IdentityDisplayName:  {Name}", nonDwConfig.AgentIdentityDisplayName);
                        }
                        logger.LogInformation("");

                        // If existing config files belong to a different tenant (e.g. the user ran
                        // 'az login' with a different account), back them up and remove them so this
                        // run starts with a clean state and does not inherit stale resource IDs.
                        if (resolver != null)
                            await resolver.BackupAndClearStaleConfigAsync(config.FullName, nonDwConfig.TenantId!);
                        else
                            await BackupAndClearStaleConfigAsync(config.FullName, nonDwConfig.TenantId!, logger);

                        // Write a365.config.json so the resolved bootstrap settings are persisted in the
                        // current working directory and reused consistently by later setup and cleanup steps.
                        if (!File.Exists(config.FullName))
                        {
                            if (resolver != null)
                                await resolver.WriteBootstrapConfigAsync(nonDwConfig, config.FullName);
                            else
                                await WriteBootstrapConfigFileAsync(nonDwConfig, config.FullName, logger);
                        }

                        // Merge stored IDs from the existing generated config (if present) so re-running
                        // with --agent-name reuses previously created resources. Registration has no
                        // lookup endpoint so the stored ID is the only idempotency key; blueprint and
                        // identity IDs are needed for the --agent-registration-only API fallback.
                        var bootstrapGenPath = Path.Combine(
                            config.DirectoryName ?? Environment.CurrentDirectory,
                            "a365.generated.config.json");
                        if (File.Exists(bootstrapGenPath))
                        {
                            try
                            {
                                var genConfig = await configService.LoadAsync(config.FullName, bootstrapGenPath);
                                if (!string.IsNullOrWhiteSpace(genConfig.AgentRegistrationId) && string.IsNullOrWhiteSpace(nonDwConfig.AgentRegistrationId))
                                    nonDwConfig.AgentRegistrationId = genConfig.AgentRegistrationId;
                                if (!string.IsNullOrWhiteSpace(genConfig.AgentBlueprintId) && string.IsNullOrWhiteSpace(nonDwConfig.AgentBlueprintId))
                                    nonDwConfig.AgentBlueprintId = genConfig.AgentBlueprintId;
                                if (!string.IsNullOrWhiteSpace(genConfig.AgenticAppId) && string.IsNullOrWhiteSpace(nonDwConfig.AgenticAppId))
                                    nonDwConfig.AgenticAppId = genConfig.AgenticAppId;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                logger.LogDebug(ex, "Could not merge generated config in bootstrap mode; proceeding without stored IDs.");
                            }
                        }
                    }
                }
                else
                {
                    // Config file path: load from a365.config.json, merged with generated config when present.
                    var nonDwGenPath = Path.Combine(config.DirectoryName ?? Environment.CurrentDirectory, "a365.generated.config.json");
                    try
                    {
                        nonDwConfig = File.Exists(nonDwGenPath)
                            ? await configService.LoadAsync(config.FullName, nonDwGenPath)
                            : await configService.LoadAsync(config.FullName);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (ConfigFileNotFoundException ex) when (!dryRun)
                    {
                        logger.LogError("Agent name required. Use --agent-name to specify it:");
                        logger.LogInformation("");
                        logger.LogInformation("  a365 setup all --agent-name <name>");
                        context.ExitCode = ex.ExitCode;
                        return;
                    }
                    catch when (dryRun) { /* config is optional for dry-run; falls through to DW dry-run plan */ }
                    // If aiteammate was not explicitly set, respect what the config says
                    // (allows existing AI Teammate configs to keep working without --aiteammate true)
                    if (nonDwConfig != null && !aiTeammateFlag.HasValue && !nonDwConfig.IsBlueprintAgent && !dryRun)
                        nonDwConfig = null; // fall through to DW path
                }
            }

            // Validate the effective authMode (flag OR config). The CLI flag was validated above;
            // this re-check catches an invalid authMode persisted in a365.config.json that was not
            // caught at load time (e.g. a user manually edited the file with a bad value).
            var effectiveAuthModeForValidation = authMode ?? nonDwConfig?.AuthMode?.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(effectiveAuthModeForValidation) &&
                effectiveAuthModeForValidation is not ("obo" or "s2s" or "both"))
            {
                logger.LogError("Invalid authMode value '{Value}' (from --authmode flag or a365.config.json). Allowed values: obo, s2s, both.", effectiveAuthModeForValidation);
                context.ExitCode = 1;
                return;
            }

            // AI Teammate (DW) agents are M365 agents by design — auto-enable messaging endpoint.
            // --m365 remains opt-in for blueprint agents (non-DW path).
            if (nonDwConfig is null)
                isM365 = true;

            // --messaging-endpoint only takes effect for M365 agents (the messaging endpoint step is
            // skipped otherwise). Fail fast rather than silently ignoring the supplied value.
            if (messagingEndpointSpecified && !isM365)
            {
                logger.LogError("--messaging-endpoint applies only to M365 agents. Add --m365 (or use --aiteammate).");
                context.ExitCode = 1;
                return;
            }

            if (nonDwConfig is not null)
            {
                if (dryRun)
                {
                    var rawArgs = context.ParseResult.Tokens.Select(t => t.Value).ToArray();
                    var effectiveAuthMode = authMode ?? nonDwConfig.AuthMode;
                    NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(nonDwConfig, logger, isBootstrap, rawArgs, skipRequirements, isM365, agentRegistrationOnly, effectiveAuthMode, messagingEndpointFlag);
                    return;
                }

                // Build SetupContext for non-DW blueprint and delegate to orchestrator.
                if (!string.IsNullOrWhiteSpace(nonDwConfig.ClientAppId))
                    graphApiService.CustomClientAppId = nonDwConfig.ClientAppId;

                var nonDwGeneratedConfigPath = Path.Combine(
                    config.DirectoryName ?? Environment.CurrentDirectory,
                    "a365.generated.config.json");

                var nonDwCtx = new SetupContext(
                    config: nonDwConfig,
                    results: new SetupResults(),
                    logger: logger,
                    configFile: config,
                    generatedConfigPath: nonDwGeneratedConfigPath,
                    correlationId: correlationId,
                    skipInfrastructure: skipInfrastructure || isBootstrap,
                    skipRequirements: skipRequirements,
                    cancellationToken: ct,
                    configService: configService,
                    executor: executor,
                    backendConfigurator: backendConfigurator,
                    authValidator: authValidator,
                    platformDetector: platformDetector,
                    graphApiService: graphApiService,
                    blueprintService: blueprintService,
                    blueprintLookupService: blueprintLookupService,
                    federatedCredentialService: federatedCredentialService,
                    clientAppValidator: clientAppValidator,
                    agentInstanceOnly: agentRegistrationOnly,
                    isBootstrap: isBootstrap,
                    isM365: isM365,
                    authMode: authMode ?? nonDwConfig.AuthMode,
                    confirmationProvider: confirmationProvider,
                    skipSpProvisioning: skipSpProvisioning,
                    messagingEndpointOverride: messagingEndpointFlag,
                    nonInteractive: Console.IsInputRedirected);

                context.ExitCode = await NonDwBlueprintSetupOrchestrator.ExecuteAsync(nonDwCtx);
                return;
            }

            // --- AI Teammate agent (default) path ---
            if (dryRun)
            {
                var rawArgs = context.ParseResult.Tokens.Select(t => t.Value).ToArray();
                Agent365Config? dwDryRunConfig = null;
                try
                {
                    var dwGenPath = Path.Combine(config.DirectoryName ?? Environment.CurrentDirectory, "a365.generated.config.json");
                    dwDryRunConfig = File.Exists(dwGenPath)
                        ? await configService.LoadAsync(config.FullName, dwGenPath)
                        : await configService.LoadAsync(config.FullName);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* config is optional for dry-run display */ }
                SetupHelpers.PrintDwSetupAllDryRunPlan(logger, skipInfrastructure, skipRequirements, rawArgs, dwDryRunConfig, isM365, messagingEndpointFlag);
                return;
            }

            var setupResults = new SetupResults();

            try
            {
                // Load configuration
                Agent365Config setupConfig;
                if (isBootstrap)
                {
                    var banner = context.ParseResult.Tokens.Select(t => t.Value).ToArray();
                    logger.LogInformation("Running \"a365 {Args}\"...", string.Join(" ", banner));
                    logger.LogInformation("");
                    var btTenantId = tenantIdFlag;
                    if (string.IsNullOrWhiteSpace(btTenantId))
                        btTenantId = await SetupHelpers.ResolveBootstrapTenantIdAsync(null, executor, logger);
                    if (string.IsNullOrWhiteSpace(btTenantId)) { context.ExitCode = 1; return; }
                    var btClientAppId = await SetupHelpers.ResolveBootstrapClientAppIdAsync(btTenantId, graphApiService, logger, ct);
                    if (string.IsNullOrWhiteSpace(btClientAppId)) { context.ExitCode = 1; return; }
                    graphApiService.CustomClientAppId = btClientAppId;
                    var dwBootstrap = new Agent365Config
                    {
                        TenantId = btTenantId,
                        ClientAppId = btClientAppId,
                        AgentIdentityDisplayName = $"{agentName} Identity",
                        AgentBlueprintDisplayName = $"{agentName} Blueprint",
                        AgentDescription = agentName!,
                        AiTeammate = true,
                    };
                    if (resolver != null)
                    {
                        await resolver.BackupAndClearStaleConfigAsync(config.FullName, dwBootstrap.TenantId!);
                        if (!File.Exists(config.FullName))
                            await resolver.WriteBootstrapConfigAsync(dwBootstrap, config.FullName);
                    }
                    setupConfig = dwBootstrap;
                }
                else
                {
                    // Check for tenant mismatch before loading — if the user switched az login
                    // tenants since the last setup run, back up stale config and start clean.
                    if (resolver != null && await resolver.CheckAndBackupStaleConfigAsync(config.FullName, ct))
                    {
                        logger.LogInformation("Run 'a365 setup all --agent-name <name>' to set up for the new tenant.");
                        context.ExitCode = 1;
                        return;
                    }

                    try
                    {
                        setupConfig = await configService.LoadAsync(config.FullName);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (ConfigFileNotFoundException ex)
                    {
                        logger.LogError("Agent name required. Use --agent-name to specify it:");
                        logger.LogInformation("");
                        logger.LogInformation("  a365 setup all --agent-name <name>");
                        context.ExitCode = ex.ExitCode;
                        return;
                    }
                }

                // Configure GraphApiService with custom client app ID if available
                // This ensures inheritable permissions operations use the validated custom app
                if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
                {
                    graphApiService.CustomClientAppId = setupConfig.ClientAppId;
                }

                setupResults.PrerequisitesSkipped = skipRequirements;
                setupResults.InfrastructureSkipped = true;
                setupResults.TenantId = setupConfig.TenantId;

                // Validate all prerequisites in one pass
                if (!skipRequirements)
                {
                    var checks = AllSubcommand.GetChecks(authValidator, clientAppValidator);

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

                logger.LogDebug("All validations passed. Starting setup execution...");

                logger.LogInformation("Running all setup steps... (TraceId: {TraceId})", correlationId);
                if (skipRequirements)
                    logger.LogInformation("NOTE: Requirements validation skipped (--skip-requirements flag used)");
                logger.LogInformation("");

                var generatedConfigPath = Path.Combine(
                    config.DirectoryName ?? Environment.CurrentDirectory,
                    "a365.generated.config.json");

                // Build the shared step context for the DW flow.
                var ctx = new SetupContext(
                    config: setupConfig,
                    results: setupResults,
                    logger: logger,
                    configFile: config,
                    generatedConfigPath: generatedConfigPath,
                    correlationId: correlationId,
                    skipInfrastructure: skipInfrastructure || isBootstrap,
                    skipRequirements: skipRequirements,
                    cancellationToken: ct,
                    configService: configService,
                    executor: executor,
                    backendConfigurator: backendConfigurator,
                    authValidator: authValidator,
                    platformDetector: platformDetector,
                    graphApiService: graphApiService,
                    blueprintService: blueprintService,
                    blueprintLookupService: blueprintLookupService,
                    federatedCredentialService: federatedCredentialService,
                    clientAppValidator: clientAppValidator,
                    isM365: isM365,
                    skipSpProvisioning: skipSpProvisioning,
                    messagingEndpointOverride: messagingEndpointFlag,
                    nonInteractive: Console.IsInputRedirected);

                // Step 1: Infrastructure (optional, DW only)
                await ExecuteInfrastructureStepAsync(ctx);

                // Step 2: Blueprint
                await ExecuteBlueprintStepAsync(ctx);

                // Step 3: Configure all permissions in a batch.
                var (specs, mcpResourceAppId, mcpScopes, mcpScopesByAudience, mcpServerNamesByAudience) = await BuildPermissionSpecsAsync(ctx);

                await ExecuteBatchPermissionsStepAsync(
                    ctx, specs, mcpScopesByAudience,
                    knownBlueprintSpObjectId: ctx.Config.AgentBlueprintServicePrincipalObjectId);

                SetupHelpers.ApplyConsentUrlsIfNeeded(
                    ctx, mcpResourceAppId, ctx.Config.AgentApplicationScopes, mcpScopes,
                    isM365: ctx.IsM365,
                    mcpScopesByAudience: mcpScopesByAudience,
                    mcpAudienceDisplayNames: mcpServerNamesByAudience);

                await ctx.ConfigService.SaveStateAsync(ctx.Config, ctx.GeneratedConfigPath);

                // Step 4: Messaging endpoint registration — --m365 gated; no-op for non-M365 agents.
                await ExecuteMessagingEndpointStepAsync(ctx);

                logger.LogInformation("");

                // Sync all settings (ServiceConnection, TokenValidation, Agent365Observability) to the app config file.
                setupResults.ProjectSettingsWritten = await ProjectSettingsSyncHelper.ExecuteAsync(
                    ctx.ConfigFile.FullName, ctx.GeneratedConfigPath,
                    ctx.ConfigService, ctx.PlatformDetector, ctx.Logger);

                // Display verification URLs and setup summary
                await SetupHelpers.DisplayVerificationInfoAsync(config, logger);
                logger.LogInformation("");
                SetupHelpers.DisplaySetupSummary(setupResults, logger);
            }
            catch (Agent365Exception ex)
            {
                var logFilePath = ConfigService.GetCommandLogPath(CommandNames.Setup);
                ExceptionHandler.HandleAgent365Exception(ex, logFilePath: logFilePath);
                setupResults.Errors.Add(ex.Message);
                logger.LogInformation("");
                SetupHelpers.DisplaySetupSummary(setupResults, logger);
                ExceptionHandler.ExitWithCleanup(1);
            }
            catch (FileNotFoundException fnfEx)
            {
                logger.LogError("Setup failed: {Message}", fnfEx.Message);
                setupResults.Errors.Add(fnfEx.Message);
                logger.LogInformation("");
                SetupHelpers.DisplaySetupSummary(setupResults, logger);
                ExceptionHandler.ExitWithCleanup(1);
            }
            catch (OperationCanceledException)
            {
                // Must sit before the catch-all below so Ctrl+C bypasses DisplaySetupSummary,
                // which would render not-yet-attempted phases as "failed".
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Setup failed: {Message}", ex.Message);
                setupResults.Errors.Add(ex.Message);
                logger.LogInformation("");
                SetupHelpers.DisplaySetupSummary(setupResults, logger);
                throw;
            }
        });

        return command;
    }

    // -------------------------------------------------------------------------
    // Shared step methods — called by both DW (AllSubcommand) and non-DW
    // (NonDwBlueprintSetupOrchestrator). Steps are intentionally non-fatal
    // when appropriate (Permissions) so partial progress is preserved and
    // the caller can report what succeeded.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Step 2 — Creates or reuses the Agent Identity Blueprint in Entra.
    /// Reloads <see cref="SetupContext.Config"/> from disk after blueprint writes
    /// <c>AgentBlueprintId</c> to the generated config file.
    /// Throws on fatal failure so the caller's outer try/catch can handle it.
    /// </summary>
    internal static async Task ExecuteBlueprintStepAsync(SetupContext ctx)
    {
        try
        {
            var result = await BlueprintSubcommand.CreateBlueprintImplementationAsync(
                ctx.Config,
                ctx.ConfigFile,
                ctx.Executor,
                ctx.AuthValidator,
                ctx.Logger,
                ctx.SkipInfrastructure,
                isSetupAll: true,
                ctx.ConfigService,
                ctx.BackendConfigurator,
                ctx.PlatformDetector,
                ctx.GraphApiService,
                ctx.BlueprintService,
                ctx.BlueprintLookupService,
                ctx.FederatedCredentialService,
                skipEndpointRegistration: true,
                correlationId: ctx.CorrelationId,
                cancellationToken: ctx.CancellationToken,
                options: new BlueprintCreationOptions(DeferConsent: true),
                loginHintResolver: ctx.LoginHintResolver);

            ctx.Results.BlueprintCreated = result.BlueprintCreated;
            ctx.Results.BlueprintAlreadyExisted = result.BlueprintAlreadyExisted;

            // Graph permissions and admin consent are deferred to the batch orchestrator
            // (DeferConsent: true above). Flags are updated in the batch permissions step.
            if (result.GraphInheritablePermissionsFailed)
            {
                ctx.Results.GraphInheritablePermissionsError = result.GraphInheritablePermissionsError
                    ?? "Microsoft Graph inheritable permissions failed to configure";
                ctx.Results.Warnings.Add($"Microsoft Graph inheritable permissions: {ctx.Results.GraphInheritablePermissionsError}");
            }
            else
            {
                ctx.Results.GraphInheritablePermissionsConfigured = true;
            }

            ctx.Results.FederatedCredentialConfigured = result.FederatedCredentialConfigured;
            if (!result.FederatedCredentialConfigured && !string.IsNullOrWhiteSpace(result.FederatedCredentialError))
            {
                ctx.Results.FederatedCredentialError = result.FederatedCredentialError;
                ctx.Results.Warnings.Add($"Federated Identity Credential: {result.FederatedCredentialError}");
            }

            if (result.ClientSecretManualActionRequired)
                ctx.Results.ClientSecretManualActionRequired = true;

            if (!result.BlueprintCreated)
            {
                throw new GraphApiException(
                    operation: "Create Agent Blueprint",
                    reason: "Blueprint creation failed. This typically indicates missing permissions or insufficient privileges.",
                    isPermissionIssue: true);
            }

            // In bootstrap mode, CreateBlueprintImplementationAsync already sets AgentBlueprintId
            // (and related properties) directly on ctx.Config. The static a365.config.json does
            // not exist on disk, so LoadAsync would throw ConfigFileNotFoundException.
            if (!ctx.IsBootstrap)
            {
                // Reload config to get blueprint ID and any other dynamic properties written to disk.
                // Retry up to 5 times with 500ms backoff to handle transient file-system flush delays.
                var fullConfigPath = Path.GetFullPath(ctx.ConfigFile.FullName);
                Agent365Config? reloaded = null;
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    await Task.Delay(500, ctx.CancellationToken);
                    try
                    {
                        reloaded = await ctx.ConfigService.LoadAsync(fullConfigPath);
                        if (!string.IsNullOrWhiteSpace(reloaded.AgentBlueprintId))
                            break;
                    }
                    catch (Exception ex)
                    {
                        ctx.Logger.LogDebug(ex, "Config reload attempt {Attempt} failed; retrying", attempt + 1);
                    }
                }
                if (reloaded is not null)
                    ctx.Config = reloaded;
            }
            ctx.Results.BlueprintId = ctx.Config.AgentBlueprintId;
            ctx.Results.BlueprintDisplayName = ctx.Config.AgentBlueprintDisplayName;

            // Validate blueprint ID was properly saved
            if (string.IsNullOrWhiteSpace(ctx.Config.AgentBlueprintId))
            {
                throw new SetupValidationException(
                    "Blueprint creation completed but AgentBlueprintId was not saved to configuration. " +
                    "This is required for the next steps (MCP permissions and Bot permissions).");
            }

            // Track whether the service principal was created (SP object ID present after blueprint creation).
            ctx.Results.BlueprintServicePrincipalCreated = !string.IsNullOrWhiteSpace(ctx.Config.AgentBlueprintServicePrincipalObjectId);
            if (!ctx.Results.BlueprintServicePrincipalCreated)
            {
                var spWarning = "Agent blueprint service principal was not created. " +
                    "Inheritable permissions and FIC may not function correctly. " +
                    "Run 'a365 setup blueprint' to retry SP creation.";
                ctx.Results.Warnings.Add(spWarning);
                ctx.Logger.LogWarning(spWarning);
            }
        }
        catch (Agent365Exception blueprintEx)
        {
            ctx.Results.BlueprintCreated = false;
            ctx.Results.BlueprintFailed = true;
            ctx.Results.MessagingEndpointRegistered = false;
            ctx.Results.Errors.Add($"Blueprint: {blueprintEx.Message}");
            throw;
        }
        catch (Exception blueprintEx)
        {
            ctx.Results.BlueprintCreated = false;
            ctx.Results.BlueprintFailed = true;
            ctx.Results.MessagingEndpointRegistered = false;
            ctx.Results.Errors.Add($"Blueprint: {blueprintEx.Message}");
            ctx.Logger.LogError("Failed to create blueprint: {Message}", blueprintEx.Message);
            throw;
        }
    }

    /// <summary>
    /// Step 3 (core) — Configures permissions for all supplied resource specs via the
    /// three-phase <see cref="BatchPermissionsOrchestrator"/>. Updates
    /// <see cref="SetupResults"/> with phase outcomes.
    ///
    /// Non-fatal: a permissions failure logs a warning and continues so callers can
    /// display a partial-success summary. State save is the caller's responsibility
    /// (DW and non-DW have different post-processing before saving).
    /// </summary>
    internal static async Task ExecuteBatchPermissionsStepAsync(
        SetupContext ctx,
        List<ResourcePermissionSpec> specs,
        IReadOnlyDictionary<string, string[]> mcpScopesByAudience,
        string? knownBlueprintSpObjectId = null)
    {
        // Required parameter — every caller must thread the loaded ToolingManifest
        // audience map through. Forgetting it would route V2 MCP per-server audiences
        // to api://{appId} and trigger AADSTS500011 (see commit 7a1e317's incomplete
        // wiring of the non-DW path).
        var knownMcpAudienceAppIds = mcpScopesByAudience.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            var (blueprintPermissionsUpdated, inheritedPermissionsConfigured, consentGranted, adminConsentUrl) =
                await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                    ctx.GraphApiService, ctx.BlueprintService, ctx.Config,
                    ctx.Config.AgentBlueprintId!, ctx.Config.TenantId!,
                    specs, ctx.Logger, ctx.Results, ctx.CancellationToken,
                    knownBlueprintSpObjectId: knownBlueprintSpObjectId,
                    confirmationProvider: ctx.ConfirmationProvider,
                    commandExecutor: ctx.Executor,
                    skipSpProvisioning: ctx.SkipSpProvisioning,
                    knownMcpAudienceAppIds: knownMcpAudienceAppIds);

            ctx.Results.BatchPermissionsPhase1Completed = blueprintPermissionsUpdated;
            ctx.Results.BatchPermissionsPhase2Completed = inheritedPermissionsConfigured;
            ctx.Results.TenantWideConsentOutcome =
                consentGranted && adminConsentUrl == null ? Models.GrantOutcome.Granted :
                consentGranted ? Models.GrantOutcome.Unverified :
                Models.GrantOutcome.Failed;
            ctx.Results.AdminConsentUrl = adminConsentUrl;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception permEx)
        {
            ctx.Results.BatchPermissionsPhase2Completed = false;
            ctx.Results.TenantWideConsentOutcome = Models.GrantOutcome.Failed;
            ctx.Results.Errors.Add($"Permissions: {permEx.Message}");
            ctx.Logger.LogWarning("Permissions configuration failed: {Message}. Setup will continue, but permissions must be configured manually.", permEx.Message);
        }
    }

    /// <summary>
    /// Step — Registers the messaging endpoint with Teams Graph via MCP Platform when
    /// <see cref="SetupContext.IsM365"/> is true. No-op for non-M365 agents; those users
    /// are expected to configure the endpoint manually in the Teams Developer Portal.
    /// <para>
    /// Non-fatal: <see cref="SetupValidationException"/> is caught, logged, and added to
    /// <see cref="SetupResults.Warnings"/> so setup continues and the summary surfaces the failure.
    /// </para>
    /// </summary>
    internal static async Task ExecuteMessagingEndpointStepAsync(SetupContext ctx)
    {
        // Not an M365 agent — leave MessagingEndpointResult null so the summary shows "skipped".
        if (!ctx.IsM365)
            return;

        // Phase separator: emit only after the non-M365 early-return so the non-M365 run
        // does not get a stray blank line followed by silent no-op output.
        ctx.Logger.LogInformation("");

        // Blueprint step failed; there is no blueprint to attach an endpoint to. Record this as
        // a distinct Failed + "BlueprintMissing" so the summary doesn't mislead the user with the
        // "non-M365 agent" wording reserved for null.
        if (string.IsNullOrWhiteSpace(ctx.Config.AgentBlueprintId))
        {
            ctx.Logger.LogWarning("Messaging endpoint registration skipped: agent blueprint ID is missing (the blueprint step likely failed).");
            ctx.Results.MessagingEndpointResult = Models.EndpointRegistrationResult.Failed;
            ctx.Results.MessagingEndpointFailureReason = MessagingEndpointFailureReasons.BlueprintMissing;
            ctx.Results.MessagingEndpoint = ctx.Config.MessagingEndpoint;
            ctx.Results.Warnings.Add("Messaging endpoint: agent blueprint ID is missing, so endpoint registration was not attempted. Resolve the blueprint creation failure first, then re-run 'a365 setup blueprint --endpoint-only --m365'.");
            return;
        }

        // Endpoint: --messaging-endpoint flag wins, else the init-only config value. Absent = deferred.
        var endpoint = !string.IsNullOrWhiteSpace(ctx.MessagingEndpointOverride)
            ? ctx.MessagingEndpointOverride
            : ctx.Config.MessagingEndpoint;

        // No endpoint and non-interactive (CI / coding agent): defer silently, before the header.
        if (string.IsNullOrWhiteSpace(endpoint) && ctx.NonInteractive)
        {
            ctx.Results.MessagingEndpointResult = Models.EndpointRegistrationResult.Failed;
            ctx.Results.MessagingEndpointFailureReason = MessagingEndpointFailureReasons.NotConfigured;
            ctx.Results.MessagingEndpoint = null;
            return;
        }

        // Single section header; the prompt (if any) and registration both render beneath it.
        ctx.Logger.LogInformation("Configuring messaging endpoint...");
        using (ctx.Logger.Indent())
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                endpoint = await PromptForMessagingEndpointAsync(ctx);

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                // Deferred (blank prompt): summary renders "configure after you deploy", not a failure.
                ctx.Results.MessagingEndpointResult = Models.EndpointRegistrationResult.Failed;
                ctx.Results.MessagingEndpointFailureReason = MessagingEndpointFailureReasons.NotConfigured;
                ctx.Results.MessagingEndpoint = null;
                return;
            }

            try
            {
                var (result, failureReason) = await SetupHelpers.RegisterBlueprintMessagingEndpointAsync(
                    ctx.Config,
                    ctx.Logger,
                    ctx.BackendConfigurator,
                    overrideEndpointUrl: endpoint,
                    correlationId: ctx.CorrelationId);

                ctx.Results.MessagingEndpointResult = result;
                ctx.Results.MessagingEndpoint = ctx.Config.BotMessagingEndpoint ?? endpoint;

                if (result == Models.EndpointRegistrationResult.Created ||
                    result == Models.EndpointRegistrationResult.AlreadyExists)
                {
                    ctx.Results.MessagingEndpointRegistered = true;
                    ctx.Results.EndpointAlreadyExisted = result == Models.EndpointRegistrationResult.AlreadyExists;
                }
                else if (result == Models.EndpointRegistrationResult.Failed)
                {
                    ctx.Results.MessagingEndpointFailureReason = failureReason;
                }
            }
            catch (SetupValidationException ex)
            {
                // Config problem (e.g. invalid URL) — don't rethrow; surface as a summary warning.
                ctx.Logger.LogWarning("Messaging endpoint registration skipped: {Message}", ex.Message);
                ctx.Results.MessagingEndpointResult = Models.EndpointRegistrationResult.Failed;
                ctx.Results.MessagingEndpointFailureReason = MessagingEndpointFailureReasons.Other;
                ctx.Results.MessagingEndpoint = ctx.Config.MessagingEndpoint;
                ctx.Results.Warnings.Add($"Messaging endpoint: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Prompts for the messaging endpoint URL. Returns the entered HTTPS URL, or null to defer
    /// (blank entry). The caller prints the section header and opens the indent scope.
    /// </summary>
    private static async Task<string?> PromptForMessagingEndpointAsync(SetupContext ctx)
    {
        if (ctx.NonInteractive)
            return null;

        ctx.Logger.LogInformation("The HTTPS URL where your deployed agent receives messages.");
        ctx.Logger.LogInformation("Leave blank to configure it later, after you deploy the agent.");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            // Console.Write bypasses the log formatter's indent scope, so prepend the level-1 indent.
            Console.Write("    Messaging endpoint URL: ");
            var entered = ConsoleHelper.ReadLineCancellable(ctx.CancellationToken)?.Trim();

            if (string.IsNullOrWhiteSpace(entered))
                return null;

            if (Uri.TryCreate(entered, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                return entered;

            ctx.Logger.LogWarning("Enter a valid HTTPS URL (e.g. https://my-agent.example.com/api/messages), or leave blank to skip.");
        }

        return null;
    }

    /// <summary>
    /// Step 3 (pre) — Removes stale custom permissions and builds the full resource permission
    /// spec list from dynamic config values (AgentApplicationScopes, MCP manifest, CustomBlueprintPermissions).
    /// Shared by both DW and non-DW flows so permissions are always consistent — the only difference
    /// is that non-M365 agents exclude Messaging Bot API.
    /// </summary>
    internal static async Task<(List<ResourcePermissionSpec> specs, string mcpResourceAppId, string[] mcpScopes, Dictionary<string, string[]> scopesByAudience, Dictionary<string, List<string>> serverNamesByAudience)> BuildPermissionSpecsAsync(SetupContext ctx)
    {
        var desiredCustomIds = new HashSet<string>(
            (ctx.Config.CustomBlueprintPermissions ?? new List<CustomResourcePermission>())
                .Select(p => p.ResourceAppId),
            StringComparer.OrdinalIgnoreCase);
        await PermissionsSubcommand.RemoveStaleCustomPermissionsAsync(
            ctx.Logger, ctx.GraphApiService, ctx.BlueprintService, ctx.Config, desiredCustomIds, ctx.CancellationToken);

        var mcpManifestPath = Path.Combine(
            ctx.Config.DeploymentProjectPath ?? string.Empty,
            McpConstants.ToolingManifestFileName);
        var mcpResourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(ctx.Config.Environment);
        var scopesByAudience = await ManifestHelper.GetScopesByAudienceAsync(mcpManifestPath, excludeLegacyAtg: false, resolvedAtgAppId: mcpResourceAppId);
        var serverNamesByAudience = await ManifestHelper.GetServerNamesByAudienceAsync(mcpManifestPath, mcpResourceAppId);
        // V1-compatible: extract ATG scopes for consent URL helpers (empty for V2-only manifests)
        var mcpScopes = scopesByAudience.TryGetValue(mcpResourceAppId, out var atgScopes) ? atgScopes : Array.Empty<string>();

        // Pass the already-computed scopesByAudience and serverNamesByAudience to avoid
        // reading the MCP manifest a second time. BuildConfiguredPermissionSpecsAsync stamps
        // Graph + manifest MCP audiences + fixed APIs (Bot only when isM365) + custom permissions
        // for both DW and non-DW agents; serverNamesByAudience drives the per-server display
        // names so V2 audiences read as e.g. "mcp_MailTools" rather than "Agent 365 Tools".
        var specs = await SetupHelpers.BuildConfiguredPermissionSpecsAsync(
            ctx.Config, setInheritable: true, isM365: ctx.IsM365, scopesByAudience, serverNamesByAudience);

        // Return the full scopesByAudience map alongside the V1-compat mcpScopes so V2
        // callers (ApplyConsentUrlsIfNeeded) can route per-server audiences to the bare
        // appId GUID resource identifier instead of collapsing them onto the WorkIQ Tools
        // URI (issue #429). api://{appId} is NOT used — per-server SPs have identifierUris
        // null and only the bare appId GUID is in servicePrincipalNames, so api:// triggers
        // AADSTS500011. serverNamesByAudience flows through to ApplyConsentUrlsIfNeeded so
        // the Action Required block's per-audience consent URLs display the same per-server
        // names the spec list uses.
        return (specs, mcpResourceAppId, mcpScopes, scopesByAudience, serverNamesByAudience);
    }

    /// <summary>
    /// Builds a minimal <see cref="Agent365Config"/> from <paramref name="agentName"/> without
    /// requiring an <c>a365.config.json</c> file on disk.
    /// <list type="bullet">
    ///   <item>TenantId: from <paramref name="tenantIdFlag"/> or auto-detected via <c>az account show</c></item>
    ///   <item>ClientAppId: resolved by searching Entra for <see cref="AuthenticationConstants.WellKnownClientAppDisplayName"/></item>
    /// </list>
    /// Returns <c>null</c> and logs errors if validation fails.
    /// </summary>
    private static async Task<Agent365Config?> BuildBootstrapConfigAsync(
        string agentName,
        string? tenantIdFlag,
        CommandExecutor executor,
        GraphApiService graphApiService,
        ILogger logger,
        CancellationToken ct)
    {
        // Resolve tenant ID
        var tenantId = await SetupHelpers.ResolveBootstrapTenantIdAsync(tenantIdFlag, executor, logger);
        if (tenantId is null)
            return null;

        var clientAppId = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
            tenantId, graphApiService, logger, ct);
        if (string.IsNullOrWhiteSpace(clientAppId))
            return null;

        graphApiService.CustomClientAppId = clientAppId;

        // Build minimal config and validate
        var config = new Agent365Config
        {
            TenantId = tenantId,
            ClientAppId = clientAppId,
            AgentIdentityDisplayName = $"{agentName} Identity",
            AgentBlueprintDisplayName = $"{agentName} Blueprint",
            AgentDescription = agentName,
            AiTeammate = false,
            UseBlueprint = true,
        };

        var errors = config.ValidateNonDwMinimal();
        if (errors.Count > 0)
        {
            foreach (var err in errors)
                logger.LogError("{Error}", err);
            return null;
        }

        return config;
    }

    /// <summary>
    /// Writes a minimal <c>a365.config.json</c> to <paramref name="path"/> from the bootstrap config so
    /// that subsequent <see cref="IConfigService.SaveStateAsync"/> calls detect a local static config and
    /// save the generated file to the local directory instead of the global %LocalAppData% directory.
    /// Only the init-only (static) fields are persisted; dynamic/generated fields belong in
    /// <c>a365.generated.config.json</c> and are written there by each setup step.
    /// </summary>
    private static async Task WriteBootstrapConfigFileAsync(
        Agent365Config config,
        string path,
        ILogger logger)
    {
        var staticFields = new Dictionary<string, object?>
        {
            ["tenantId"] = config.TenantId,
            ["clientAppId"] = config.ClientAppId,
            ["agentIdentityDisplayName"] = config.AgentIdentityDisplayName,
            ["agentBlueprintDisplayName"] = config.AgentBlueprintDisplayName,
            ["agentDescription"] = config.AgentDescription,
            ["aiTeammate"] = config.AiTeammate,
            ["useBlueprint"] = config.UseBlueprint,
        };

        var json = JsonSerializer.Serialize(staticFields, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
        logger.LogDebug("Wrote bootstrap config to {Path}", path);
    }

    /// <summary>
    /// When running bootstrap setup (--agent-name), checks whether config files already in the
    /// current directory belong to a different tenant than the one currently signed in. If so,
    /// backs both files up with a timestamp suffix and removes the originals so setup starts clean
    /// without inheriting stale resource IDs from a previous run.
    /// </summary>
    internal static async Task BackupAndClearStaleConfigAsync(
        string configPath,
        string resolvedTenantId,
        ILogger logger)
    {
        if (!File.Exists(configPath))
            return;

        // Read tenantId from the existing static config without loading the full model.
        // shouldBackup is true when: (a) the file is unreadable/malformed, or (b) the tenant
        // is present and explicitly differs from the resolved tenant.
        bool shouldBackup = false;
        string? existingTenantId = null;
        try
        {
            var json = await File.ReadAllTextAsync(configPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tenantId", out var prop))
            {
                existingTenantId = prop.GetString();
                shouldBackup = !string.IsNullOrWhiteSpace(existingTenantId) &&
                               !string.Equals(existingTenantId, resolvedTenantId, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Unreadable or malformed config — back it up so setup starts clean.
            shouldBackup = true;
        }

        if (!shouldBackup)
            return;

        logger.LogInformation(
            "Detected tenant change — previous setup was for tenant {OldTenant}, " +
            "current session is tenant {NewTenant}. Starting fresh setup for the new tenant.",
            existingTenantId, resolvedTenantId);

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var configDir = Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory;

        var configBackup = configPath + ".bak." + timestamp;
        File.Move(configPath, configBackup);
        logger.LogDebug("Backed up: {File}", Path.GetFileName(configBackup));

        var generatedPath = Path.Combine(configDir, "a365.generated.config.json");
        if (File.Exists(generatedPath))
        {
            var generatedBackup = generatedPath + ".bak." + timestamp;
            File.Move(generatedPath, generatedBackup);
            logger.LogDebug("Backed up: {File}", Path.GetFileName(generatedBackup));
        }
    }

    /// <summary>Step 1 — Infrastructure step (no-op, deploy command removed).</summary>
    internal static Task ExecuteInfrastructureStepAsync(SetupContext ctx)
    {
        ctx.Results.InfrastructureSkipped = true;
        ctx.Results.InfrastructureCreated = false;
        return Task.CompletedTask;
    }
}
