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
    /// Composes SetupCommand base checks + Location + ClientApp + optional Infrastructure.
    /// </summary>
    public static List<Services.Requirements.IRequirementCheck> GetChecks(
        AzureAuthValidator auth,
        IClientAppValidator clientAppValidator,
        bool includeInfrastructure)
    {
        var checks = new List<Services.Requirements.IRequirementCheck>(SetupCommand.GetBaseChecks(auth))
        {
            new LocationRequirementCheck(),
            new ClientAppRequirementCheck(clientAppValidator),
        };

        if (includeInfrastructure)
        {
            checks.Add(new InfrastructureRequirementCheck());
        }

        return checks;
    }

    /// <summary>
    /// Returns the requirement checks for <c>setup all --aiteammate false</c> (non-DW blueprint).
    /// Mirrors DW checks: includes Location and optionally Infrastructure when the agent needs deployment.
    /// </summary>
    public static List<Services.Requirements.IRequirementCheck> GetNonDwChecks(
        AzureAuthValidator auth,
        IClientAppValidator clientAppValidator,
        bool includeInfrastructure)
    {
        var checks = new List<Services.Requirements.IRequirementCheck>(SetupCommand.GetBaseChecks(auth))
        {
            new LocationRequirementCheck(),
            new ClientAppRequirementCheck(clientAppValidator),
        };

        if (includeInfrastructure)
        {
            checks.Add(new InfrastructureRequirementCheck());
        }

        return checks;
    }

    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        IBotConfigurator botConfigurator,
        AzureAuthValidator authValidator,
        PlatformDetector platformDetector,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        IClientAppValidator clientAppValidator,
        BlueprintLookupService blueprintLookupService,
        FederatedCredentialService federatedCredentialService,
        ArmApiService? armApiService = null)
    {
        var command = new Command("all",
            "Run complete Agent 365 setup (all steps in sequence)\n" +
            "Includes: Infrastructure + Blueprint + Permissions + Endpoint\n\n" +
            "Minimum required permissions (Global Administrator has all of these):\n" +
            "  - Azure Subscription Contributor (for infrastructure and endpoint)\n" +
            "  - Agent ID Developer role (for blueprint creation)\n" +
            "  - Global Administrator (for permission grants and admin consent)\n\n");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Configuration file path");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Show detailed output");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            description: "Show what would be done without executing");

        var skipInfrastructureOption = new Option<bool>(
            "--skip-infrastructure",
            description: "Skip Azure infrastructure creation (use if infrastructure already exists)\n" +
                        "This will still create: Blueprint + Permissions + Endpoint");

        var skipRequirementsOption = new Option<bool>(
            "--skip-requirements",
            description: "Skip requirements validation check\n" +
                        "Use with caution: setup may fail if prerequisites are not met");

        var aiTeammateOption = new Option<bool?>(
            "--aiteammate",
            description: "true = AI Teammate / Digital Worker, false = non-AI Teammate agent (blueprint, default)\n" +
                        "Overrides the aiTeammate field in a365.config.json");

        var agentInstanceOnlyOption = new Option<bool>(
            "--agent-instance-only",
            description: "Skip all setup steps and only run agent instance registration (--aiteammate false only)");

        var agentNameOption = new Option<string?>(
            ["--agent-name", "-n"],
            description: "Agent base name (e.g. \"MyAgent\"). When provided, no config file is required.\n" +
                        "Derives AgentIdentityDisplayName=\"<name> Agent\" and AgentBlueprintDisplayName=\"<name> Blueprint\".\n" +
                        "TenantId is auto-detected from 'az account show' (override with --tenant-id).\n" +
                        $"ClientAppId is resolved by looking up \"{Constants.AuthenticationConstants.WellKnownClientAppDisplayName}\" in your tenant.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Overrides auto-detection from 'az account show'.");

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(skipInfrastructureOption);
        command.AddOption(skipRequirementsOption);
        command.AddOption(aiTeammateOption);
        command.AddOption(agentInstanceOnlyOption);
        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var config = context.ParseResult.GetValueForOption(configOption)!;
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var skipInfrastructure = context.ParseResult.GetValueForOption(skipInfrastructureOption);
            var skipRequirements = context.ParseResult.GetValueForOption(skipRequirementsOption);
            var aiTeammateFlag = context.ParseResult.GetValueForOption(aiTeammateOption);
            var agentInstanceOnly = context.ParseResult.GetValueForOption(agentInstanceOnlyOption);
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var ct = context.GetCancellationToken();

            // Generate correlation ID at workflow entry point
            var correlationId = HttpClientFactory.GenerateCorrelationId();
            logger.LogDebug("Starting setup all (CorrelationId: {CorrelationId})", correlationId);

            // --- Agent type resolution ---
            // Non-DW (blueprint) is the default. DW requires --aiteammate true explicitly.
            Agent365Config? nonDwConfig = null;
            bool isBootstrap = !string.IsNullOrWhiteSpace(agentName);

            if (aiTeammateFlag != true)
            {
                if (isBootstrap)
                {
                    if (dryRun)
                    {
                        // Dry-run: detect tenant only (no client app lookup needed for display)
                        var dryRunTenantId = tenantIdFlag;
                        if (string.IsNullOrWhiteSpace(dryRunTenantId))
                            dryRunTenantId = await DetectTenantIdAsync(executor, logger, ct);
                        nonDwConfig = new Agent365Config
                        {
                            TenantId = dryRunTenantId ?? "(unknown — run 'az login' or pass --tenant-id)",
                            ClientAppId = string.Empty,
                            AgentIdentityDisplayName = $"{agentName} Agent",
                            AgentBlueprintDisplayName = $"{agentName} Blueprint",
                            AgentDescription = agentName,
                            NeedDeployment = false,
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
                        nonDwConfig = await BuildBootstrapConfigAsync(
                            agentName!, tenantIdFlag, executor, graphApiService, logger, ct);
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
                            logger.LogInformation("NeedDeployment:       {NeedDeployment}", nonDwConfig.NeedDeployment);
                        }
                        logger.LogInformation("");

                        // Write a365.config.json to anchor all SaveStateAsync calls to the local directory.
                        // Without it, SaveStateAsync saves to the global %LocalAppData% directory instead,
                        // making 'a365 cleanup' unable to find the resource IDs.
                        if (!File.Exists(config.FullName))
                            await WriteBootstrapConfigFileAsync(nonDwConfig, config.FullName, logger);
                    }
                }
                else
                {
                    // Config file path: load from a365.config.json
                    nonDwConfig = await configService.LoadAsync(config.FullName);
                    // If aiTeammate was not explicitly set, respect what the config says
                    // (allows existing DW configs to keep working without --aiteammate true)
                    if (!aiTeammateFlag.HasValue && !nonDwConfig.IsNonAiTeammate && !dryRun)
                        nonDwConfig = null; // fall through to DW path
                }
            }

            if (nonDwConfig is not null)
            {
                if (dryRun)
                {
                    var rawArgs = context.ParseResult.Tokens.Select(t => t.Value).ToArray();
                    NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(nonDwConfig, logger, isBootstrap, rawArgs, skipRequirements);
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
                    skipRequirements: skipRequirements || isBootstrap,
                    cancellationToken: ct,
                    configService: configService,
                    executor: executor,
                    botConfigurator: botConfigurator,
                    authValidator: authValidator,
                    platformDetector: platformDetector,
                    graphApiService: graphApiService,
                    blueprintService: blueprintService,
                    blueprintLookupService: blueprintLookupService,
                    federatedCredentialService: federatedCredentialService,
                    clientAppValidator: clientAppValidator,
                    agentInstanceOnly: agentInstanceOnly,
                    isBootstrap: isBootstrap);

                context.ExitCode = await NonDwBlueprintSetupOrchestrator.ExecuteAsync(nonDwCtx);
                return;
            }

            // --- Digital Worker (default) path ---
            if (dryRun)
            {
                var rawArgs = context.ParseResult.Tokens.Select(t => t.Value).ToArray();
                Agent365Config? dwDryRunConfig = null;
                try { dwDryRunConfig = await configService.LoadAsync(config.FullName); } catch { /* config is optional for dry-run display */ }
                SetupHelpers.PrintDwSetupAllDryRunPlan(logger, skipInfrastructure, skipRequirements, rawArgs, dwDryRunConfig);
                return;
            }

            var setupResults = new SetupResults();

            try
            {
                // Load configuration
                var setupConfig = await configService.LoadAsync(config.FullName);

                // Configure GraphApiService with custom client app ID if available
                // This ensures inheritable permissions operations use the validated custom app
                if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
                {
                    graphApiService.CustomClientAppId = setupConfig.ClientAppId;
                }

                // Validate all prerequisites in one pass
                if (!skipRequirements)
                {
                    var includeInfra = !skipInfrastructure && setupConfig.NeedDeployment;
                    var checks = AllSubcommand.GetChecks(authValidator, clientAppValidator, includeInfra);

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
                if (skipInfrastructure)
                    logger.LogInformation("NOTE: Infrastructure creation skipped (--skip-infrastructure flag used)");
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
                    skipInfrastructure: skipInfrastructure,
                    skipRequirements: skipRequirements,
                    cancellationToken: ct,
                    configService: configService,
                    executor: executor,
                    botConfigurator: botConfigurator,
                    authValidator: authValidator,
                    platformDetector: platformDetector,
                    graphApiService: graphApiService,
                    blueprintService: blueprintService,
                    blueprintLookupService: blueprintLookupService,
                    federatedCredentialService: federatedCredentialService,
                    clientAppValidator: clientAppValidator);

                // Step 1: Infrastructure (optional, DW only)
                await ExecuteInfrastructureStepAsync(ctx);

                // Step 2: Blueprint
                await ExecuteBlueprintStepAsync(ctx);

                // Step 3: Configure all permissions in a batch.
                var (specs, mcpResourceAppId, mcpScopes) = await BuildPermissionSpecsAsync(ctx);

                await ExecuteBatchPermissionsStepAsync(
                    ctx, specs,
                    knownBlueprintSpObjectId: ctx.Config.AgentBlueprintServicePrincipalObjectId);

                SetupHelpers.ApplyConsentUrlsIfNeeded(ctx, mcpResourceAppId, ctx.Config.AgentApplicationScopes, mcpScopes);

                await ctx.ConfigService.SaveStateAsync(ctx.Config);

                // Sync all settings (ServiceConnection, TokenValidation, Agent365Observability) to the app config file.
                await ProjectSettingsSyncHelper.ExecuteAsync(
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
                ctx.BotConfigurator,
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
                // CRITICAL: Wait for file system to ensure config file is fully written
                // Blueprint creation writes directly to disk and may not be immediately readable
                ctx.Logger.LogDebug("Waiting for config file write to complete...");
                await Task.Delay(2000, ctx.CancellationToken);

                // Reload config to get blueprint ID and any other dynamic properties written to disk
                var fullConfigPath = Path.GetFullPath(ctx.ConfigFile.FullName);
                ctx.Config = await ctx.ConfigService.LoadAsync(fullConfigPath);
            }
            ctx.Results.BlueprintId = ctx.Config.AgentBlueprintId;

            // Validate blueprint ID was properly saved
            if (string.IsNullOrWhiteSpace(ctx.Config.AgentBlueprintId))
            {
                throw new SetupValidationException(
                    "Blueprint creation completed but AgentBlueprintId was not saved to configuration. " +
                    "This is required for the next steps (MCP permissions and Bot permissions).");
            }

            // Warn when service principal creation failed (SP object ID missing after blueprint creation).
            if (string.IsNullOrWhiteSpace(ctx.Config.AgentBlueprintServicePrincipalObjectId))
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
            ctx.Results.MessagingEndpointRegistered = false;
            ctx.Results.Errors.Add($"Blueprint: {blueprintEx.Message}");
            throw;
        }
        catch (Exception blueprintEx)
        {
            ctx.Results.BlueprintCreated = false;
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
        string? knownBlueprintSpObjectId = null)
    {
        try
        {
            var (blueprintPermissionsUpdated, inheritedPermissionsConfigured, consentGranted, adminConsentUrl) =
                await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                    ctx.GraphApiService, ctx.BlueprintService, ctx.Config,
                    ctx.Config.AgentBlueprintId!, ctx.Config.TenantId!,
                    specs, ctx.Logger, ctx.Results, ctx.CancellationToken,
                    knownBlueprintSpObjectId: knownBlueprintSpObjectId);

            ctx.Results.BatchPermissionsPhase1Completed = blueprintPermissionsUpdated;
            ctx.Results.BatchPermissionsPhase2Completed = inheritedPermissionsConfigured;
            ctx.Results.AdminConsentGranted = consentGranted;
            ctx.Results.AdminConsentUrl = adminConsentUrl;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception permEx)
        {
            ctx.Results.BatchPermissionsPhase2Completed = false;
            ctx.Results.AdminConsentGranted = false;
            ctx.Results.Errors.Add($"Permissions: {permEx.Message}");
            ctx.Logger.LogWarning("Permissions configuration failed: {Message}. Setup will continue, but permissions must be configured manually.", permEx.Message);
        }
    }

    /// <summary>
    /// Step 3 (pre) — Removes stale custom permissions and builds the full resource permission
    /// spec list from dynamic config values (AgentApplicationScopes, MCP manifest, CustomBlueprintPermissions).
    /// Shared by both DW and non-DW flows so permissions are always consistent.
    /// </summary>
    internal static async Task<(List<ResourcePermissionSpec> specs, string mcpResourceAppId, string[] mcpScopes)> BuildPermissionSpecsAsync(SetupContext ctx)
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
        var mcpScopes = await PermissionsSubcommand.ReadMcpScopesAsync(mcpManifestPath, ctx.Logger);
        var mcpResourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(ctx.Config.Environment);

        var specs = new List<ResourcePermissionSpec>
        {
            new ResourcePermissionSpec(
                AuthenticationConstants.MicrosoftGraphResourceAppId,
                "Microsoft Graph",
                ctx.Config.AgentApplicationScopes.ToArray(),
                SetInheritable: true),
            new ResourcePermissionSpec(
                mcpResourceAppId,
                "Agent 365 Tools",
                mcpScopes,
                SetInheritable: true),
        };
        specs.AddRange(SetupHelpers.GetFixedApiPermissionSpecs(setInheritable: true));

        foreach (var customPerm in ctx.Config.CustomBlueprintPermissions ?? new List<CustomResourcePermission>())
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
                    SetInheritable: true));
            }
        }

        return (specs, mcpResourceAppId, mcpScopes);
    }

    /// <summary>
    /// Detects the current Azure tenant ID from <c>az account show</c>.
    /// Returns <c>null</c> and logs guidance when not logged in.
    /// </summary>
    private static async Task<string?> DetectTenantIdAsync(
        CommandExecutor executor, ILogger logger, CancellationToken ct)
    {
        var result = await executor.ExecuteAsync("az", "account show --output json", suppressErrorLogging: true);
        if (result.Success && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            try
            {
                var cleaned = JsonDeserializationHelper.CleanAzureCliJsonOutput(result.StandardOutput);
                using var doc = JsonDocument.Parse(cleaned);
                if (doc.RootElement.TryGetProperty("tenantId", out var tid))
                    return tid.GetString();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to parse az account show output");
            }
        }

        logger.LogError("Could not detect tenant. Sign in with 'az login' or pass --tenant-id.");
        return null;
    }

    /// <summary>
    /// Builds a minimal <see cref="Agent365Config"/> from <paramref name="agentName"/> without
    /// requiring an <c>a365.config.json</c> file on disk.
    /// <list type="bullet">
    ///   <item>TenantId: from <paramref name="tenantIdFlag"/> or auto-detected via <c>az account show</c></item>
    ///   <item>ClientAppId: resolved by searching Entra for <see cref="AuthenticationConstants.WellKnownClientAppDisplayName"/></item>
    ///   <item>NeedDeployment: false (external hosting, no Azure infra)</item>
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
        string? tenantId = tenantIdFlag;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            logger.LogInformation("Detecting tenant from 'az account show'...");
            tenantId = await DetectTenantIdAsync(executor, logger, ct);
            if (tenantId is null)
                return null;
        }

        // Resolve ClientAppId from well-known display name
        logger.LogInformation("Resolving client app by display name \"{Name}\"...",
            AuthenticationConstants.WellKnownClientAppDisplayName);
        var clientAppId = await graphApiService.FindApplicationByDisplayNameAsync(
            tenantId, AuthenticationConstants.WellKnownClientAppDisplayName, ct);

        // Build minimal config and validate
        var config = new Agent365Config
        {
            TenantId = tenantId,
            ClientAppId = clientAppId ?? string.Empty,
            AgentIdentityDisplayName = $"{agentName} Agent",
            AgentBlueprintDisplayName = $"{agentName} Blueprint",
            AgentDescription = agentName,
            NeedDeployment = false,
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
            ["needDeployment"] = config.NeedDeployment,
            ["aiTeammate"] = config.AiTeammate,
            ["useBlueprint"] = config.UseBlueprint,
            ["messagingEndpoint"] = "https://placeholder.example.com/api/messages",
        };

        var json = JsonSerializer.Serialize(staticFields, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
        logger.LogDebug("Wrote bootstrap config to {Path}", path);
    }

    /// <summary>Step 1 — Creates Azure infrastructure (optional, skippable via --skip-infrastructure).</summary>
    internal static async Task ExecuteInfrastructureStepAsync(SetupContext ctx)
    {
        try
        {
            var (setupInfra, infraAlreadyExisted) = await InfrastructureSubcommand.CreateInfrastructureImplementationAsync(
                ctx.Logger,
                ctx.ConfigFile.FullName,
                ctx.GeneratedConfigPath,
                ctx.Executor,
                ctx.PlatformDetector,
                ctx.Config.NeedDeployment,
                ctx.SkipInfrastructure,
                ctx.CancellationToken);

            ctx.Results.InfrastructureCreated = (ctx.SkipInfrastructure || !ctx.Config.NeedDeployment) ? false : setupInfra;
            ctx.Results.InfrastructureAlreadyExisted = infraAlreadyExisted;
        }
        catch (Agent365Exception infraEx)
        {
            ctx.Results.InfrastructureCreated = false;
            ctx.Results.Errors.Add($"Infrastructure: {infraEx.Message}");
            throw;
        }
        catch (Exception infraEx)
        {
            ctx.Results.InfrastructureCreated = false;
            ctx.Results.Errors.Add($"Infrastructure: {infraEx.Message}");
            ctx.Logger.LogError("Failed to create infrastructure: {Message}", infraEx.Message);
            throw;
        }
    }
}
