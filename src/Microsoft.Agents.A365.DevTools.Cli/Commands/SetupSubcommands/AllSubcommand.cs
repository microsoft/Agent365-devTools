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
        FederatedCredentialService federatedCredentialService)
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

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(skipInfrastructureOption);
        command.AddOption(skipRequirementsOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var config = context.ParseResult.GetValueForOption(configOption)!;
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var skipInfrastructure = context.ParseResult.GetValueForOption(skipInfrastructureOption);
            var skipRequirements = context.ParseResult.GetValueForOption(skipRequirementsOption);
            var ct = context.GetCancellationToken();

            // Generate correlation ID at workflow entry point
            var correlationId = HttpClientFactory.GenerateCorrelationId();
            logger.LogDebug("Starting setup all (CorrelationId: {CorrelationId})", correlationId);

            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Complete Agent 365 Setup");
                logger.LogInformation("This would execute the following operations:");
                logger.LogInformation("");

                if (!skipRequirements)
                {
                    logger.LogInformation("  0. Validate prerequisites (PowerShell modules, etc.)");
                }
                else
                {
                    logger.LogInformation("  0. [SKIPPED] Requirements validation (--skip-requirements flag used)");
                }
                
                if (!skipInfrastructure)
                {
                    logger.LogInformation("  1. Create Azure infrastructure");
                }
                else
                {
                    logger.LogInformation("  1. [SKIPPED] Azure infrastructure (--skip-infrastructure flag used)");
                }
                
                logger.LogInformation("  2. Create agent blueprint (Entra ID application)");
                logger.LogInformation("  3. Configure MCP server permissions");
                logger.LogInformation("  4. Configure Bot API permissions");
                logger.LogInformation("  5. Register blueprint messaging endpoint and sync project settings");
                logger.LogInformation("No actual changes will be made.");
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
                    catch (Exception reqEx) when (reqEx is not OperationCanceledException)
                    {
                        logger.LogError(reqEx, "Requirements check failed with an unexpected error: {Message}", reqEx.Message);
                        logger.LogError("If you want to bypass requirement validation, rerun this command with the --skip-requirements flag.");
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

                // Step 1: Infrastructure (optional)
                try
                {

                    var (setupInfra, infraAlreadyExisted) = await InfrastructureSubcommand.CreateInfrastructureImplementationAsync(
                        logger,
                        config.FullName,
                        generatedConfigPath,
                        executor,
                        platformDetector,
                        setupConfig.NeedDeployment,
                        skipInfrastructure,
                        ct);

                    setupResults.InfrastructureCreated = skipInfrastructure ? false : setupInfra;
                    setupResults.InfrastructureAlreadyExisted = infraAlreadyExisted;
                }
                catch (Agent365Exception infraEx)
                {
                    setupResults.InfrastructureCreated = false;
                    setupResults.Errors.Add($"Infrastructure: {infraEx.Message}");
                    throw;
                }
                catch (Exception infraEx)
                {
                    setupResults.InfrastructureCreated = false;
                    setupResults.Errors.Add($"Infrastructure: {infraEx.Message}");
                    logger.LogError("Failed to create infrastructure: {Message}", infraEx.Message);
                    throw;
                }

                // Step 2: Blueprint
                try
                {
                    var result = await BlueprintSubcommand.CreateBlueprintImplementationAsync(
                        setupConfig,
                        config,
                        executor,
                        authValidator,
                        logger,
                        skipInfrastructure,
                        true,
                        configService,
                        botConfigurator,
                        platformDetector,
                        graphApiService,
                        blueprintService,
                        blueprintLookupService,
                        federatedCredentialService,
                        skipEndpointRegistration: true,
                        correlationId: correlationId,
                        options: new BlueprintCreationOptions(DeferConsent: true));

                    setupResults.BlueprintCreated = result.BlueprintCreated;
                    setupResults.BlueprintAlreadyExisted = result.BlueprintAlreadyExisted;

                    // Graph permissions and admin consent are deferred to the batch orchestrator
                    // (DeferConsent: true above). Flags are updated in Step 4 after the orchestrator runs.
                    if (result.GraphInheritablePermissionsFailed)
                    {
                        setupResults.GraphInheritablePermissionsError = result.GraphInheritablePermissionsError
                            ?? "Microsoft Graph inheritable permissions failed to configure";
                        setupResults.Warnings.Add($"Microsoft Graph inheritable permissions: {setupResults.GraphInheritablePermissionsError}");
                    }
                    else
                    {
                        setupResults.GraphInheritablePermissionsConfigured = true;
                    }

                    // Track Federated Identity Credential status
                    setupResults.FederatedCredentialConfigured = result.FederatedCredentialConfigured;
                    if (!result.FederatedCredentialConfigured && !string.IsNullOrWhiteSpace(result.FederatedCredentialError))
                    {
                        setupResults.FederatedCredentialError = result.FederatedCredentialError;
                        setupResults.Warnings.Add($"Federated Identity Credential: {result.FederatedCredentialError}");
                    }

                    if (!result.BlueprintCreated)
                    {
                        throw new GraphApiException(
                            operation: "Create Agent Blueprint",
                            reason: "Blueprint creation failed. This typically indicates missing permissions or insufficient privileges.",
                            isPermissionIssue: true);
                    }

                    // CRITICAL: Wait for file system to ensure config file is fully written
                    // Blueprint creation writes directly to disk and may not be immediately readable
                    logger.LogDebug("Waiting for config file write to complete...");
                    await Task.Delay(2000, ct);

                    // Reload config to get blueprint ID
                    // Use full path to ensure we're reading from the correct location
                    var fullConfigPath = Path.GetFullPath(config.FullName);
                    setupConfig = await configService.LoadAsync(fullConfigPath);
                    setupResults.BlueprintId = setupConfig.AgentBlueprintId;

                    // Validate blueprint ID was properly saved
                    if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
                    {
                        throw new SetupValidationException(
                            "Blueprint creation completed but AgentBlueprintId was not saved to configuration. " +
                            "This is required for the next steps (MCP permissions and Bot permissions).");
                    }

                    // Warn when service principal creation failed (SP object ID missing after blueprint creation).
                    // Setup continues because inheritable permissions use the blueprint objectId, not the SP.
                    // However, agent token exchange will not work until the SP exists.
                    if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintServicePrincipalObjectId))
                    {
                        var spWarning = "Agent blueprint service principal was not created. " +
                            "Inheritable permissions and FIC may not function correctly. " +
                            "Run 'a365 setup blueprint' to retry SP creation.";
                        setupResults.Warnings.Add(spWarning);
                        logger.LogWarning(spWarning);
                    }
                }
                catch (Agent365Exception blueprintEx)
                {
                    setupResults.BlueprintCreated = false;
                    setupResults.MessagingEndpointRegistered = false;
                    setupResults.Errors.Add($"Blueprint: {blueprintEx.Message}");
                    throw;
                }
                catch (Exception blueprintEx)
                {
                    setupResults.BlueprintCreated = false;
                    setupResults.MessagingEndpointRegistered = false;
                    setupResults.Errors.Add($"Blueprint: {blueprintEx.Message}");
                    logger.LogError("Failed to create blueprint: {Message}", blueprintEx.Message);
                    throw;
                }

                // Step 3: Configure all permissions (Graph + MCP + Bot x3 + Custom) in a single batch.
                // Phase 1 — update blueprint requiredResourceAccess + resolve SPs once (non-admin).
                // Phase 2 — create OAuth2 grants and inheritable permissions (non-admin).
                // Phase 3 — single admin consent browser or one consolidated URL for non-admins.
                try
                {
                    // Pre-step: remove stale custom permissions before building the spec list.
                    var desiredCustomIds = new HashSet<string>(
                        (setupConfig.CustomBlueprintPermissions ?? new List<CustomResourcePermission>())
                            .Select(p => p.ResourceAppId),
                        StringComparer.OrdinalIgnoreCase);
                    await PermissionsSubcommand.RemoveStaleCustomPermissionsAsync(
                        logger, graphApiService, blueprintService, setupConfig, desiredCustomIds, ct);

                    // Build combined spec list.
                    var mcpManifestPath = Path.Combine(
                        setupConfig.DeploymentProjectPath ?? string.Empty,
                        McpConstants.ToolingManifestFileName);
                    var mcpScopes = await PermissionsSubcommand.ReadMcpScopesAsync(mcpManifestPath, logger);
                    var mcpResourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(setupConfig.Environment);

                    var specs = new List<ResourcePermissionSpec>
                    {
                        new ResourcePermissionSpec(
                            AuthenticationConstants.MicrosoftGraphResourceAppId,
                            "Microsoft Graph",
                            setupConfig.AgentApplicationScopes.ToArray(),
                            SetInheritable: true),
                        new ResourcePermissionSpec(
                            mcpResourceAppId,
                            "Agent 365 Tools",
                            mcpScopes,
                            SetInheritable: true),
                        new ResourcePermissionSpec(
                            ConfigConstants.MessagingBotApiAppId,
                            "Messaging Bot API",
                            new[] { "Authorization.ReadWrite", "user_impersonation" },
                            SetInheritable: true),
                        new ResourcePermissionSpec(
                            ConfigConstants.ObservabilityApiAppId,
                            "Observability API",
                            new[] { "user_impersonation" },
                            SetInheritable: true),
                        new ResourcePermissionSpec(
                            PowerPlatformConstants.PowerPlatformApiResourceAppId,
                            "Power Platform API",
                            new[] { "Connectivity.Connections.Read" },
                            SetInheritable: true),
                    };

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
                                SetInheritable: true));
                        }
                    }

                    var (blueprintPermissionsUpdated, inheritedPermissionsConfigured, consentGranted, adminConsentUrl) =
                        await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                            graphApiService, blueprintService, setupConfig,
                            setupConfig.AgentBlueprintId!, setupConfig.TenantId,
                            specs, logger, setupResults, ct,
                            knownBlueprintSpObjectId: setupConfig.AgentBlueprintServicePrincipalObjectId);

                    setupResults.BatchPermissionsPhase1Completed = blueprintPermissionsUpdated;
                    setupResults.BatchPermissionsPhase2Completed = inheritedPermissionsConfigured;
                    setupResults.AdminConsentGranted = consentGranted;
                    setupResults.AdminConsentUrl = adminConsentUrl;

                    List<string>? consentResourceNames = null;
                    if (!consentGranted && !string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
                    {
                        consentResourceNames = SetupHelpers.PopulateAdminConsentUrls(setupConfig, mcpResourceAppId, mcpScopes);
                    }

                    await configService.SaveStateAsync(setupConfig);

                    // Only advertise the path after the save has succeeded — the file must exist
                    // before we tell the caller where to find the consent URLs.
                    if (consentResourceNames is not null)
                    {
                        setupResults.ConsentUrlsSavedToPath = generatedConfigPath;
                        setupResults.ConsentResourceNames.AddRange(consentResourceNames);
                        setupResults.CombinedConsentUrl = SetupHelpers.BuildCombinedConsentUrl(
                            setupConfig.TenantId!, setupConfig.AgentBlueprintId!,
                            setupConfig.AgentApplicationScopes, mcpScopes);
                    }
                }
                catch (Exception permEx)
                {
                    setupResults.BatchPermissionsPhase2Completed = false;
                    setupResults.AdminConsentGranted = false;
                    setupResults.Errors.Add($"Permissions: {permEx.Message}");
                    logger.LogWarning("Permissions configuration failed: {Message}. Setup will continue, but permissions must be configured manually.", permEx.Message);
                }

                // Step 4: Messaging endpoint registration is temporarily disabled pending a backend fix.
                // Run 'a365 setup blueprint --endpoint-only' to register the endpoint manually
                // once the backend supports it. Documentation will be updated accordingly.

                // Display verification URLs and setup summary
                await SetupHelpers.DisplayVerificationInfoAsync(config, logger);
                logger.LogInformation("");
                SetupHelpers.DisplaySetupSummary(setupResults, logger);
            }
            catch (Agent365Exception ex)
            {
                var logFilePath = ConfigService.GetCommandLogPath(CommandNames.Setup);
                ExceptionHandler.HandleAgent365Exception(ex, logFilePath: logFilePath);
                Environment.Exit(1);
            }
            catch (FileNotFoundException fnfEx)
            {
                logger.LogError("Setup failed: {Message}", fnfEx.Message);
                ExceptionHandler.ExitWithCleanup(1);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Setup failed: {Message}", ex.Message);
                throw;
            }
        });

        return command;
    }
}
