// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Linq;
using System.Threading;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Permissions subcommand - Configures OAuth2 permission grants and inheritable permissions
/// Required Permissions: Global Administrator (for admin consent)
/// </summary>
internal static class PermissionsSubcommand
{
    /// <summary>
    /// Returns the requirement checks for <c>setup permissions mcp</c>.
    /// </summary>
    public static List<IRequirementCheck> GetMcpChecks(AzureAuthValidator auth)
        => SetupCommand.GetBaseChecks(auth);

    /// <summary>
    /// Returns the requirement checks for <c>setup permissions bot</c>.
    /// </summary>
    public static List<IRequirementCheck> GetBotChecks(AzureAuthValidator auth)
        => SetupCommand.GetBaseChecks(auth);

    /// <summary>
    /// Returns the requirement checks for <c>setup permissions custom</c>.
    /// </summary>
    public static List<IRequirementCheck> GetCustomChecks(AzureAuthValidator auth)
        => SetupCommand.GetBaseChecks(auth);

    public static Command CreateCommand(
        ILogger logger,
        AzureAuthValidator authValidator,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        IConfirmationProvider confirmationProvider,
        IBootstrapConfigResolver? resolver = null)
    {
        var permissionsCommand = new Command("permissions",
            "Configure OAuth2 permission grants and inheritable permissions on the blueprint\n" +
            "Required role: Agent ID Developer for inheritable permissions; Global Administrator\n" +
            "for tenant-wide OAuth2 consent. Non-admins get a unified /v2.0/adminconsent URL to forward.\n");

        // Add subcommands
        permissionsCommand.AddCommand(CreateMcpSubcommand(logger, authValidator, configService, executor, graphApiService, blueprintService, confirmationProvider, resolver));
        permissionsCommand.AddCommand(CreateBotSubcommand(logger, authValidator, configService, executor, graphApiService, blueprintService, confirmationProvider, resolver));
        permissionsCommand.AddCommand(CreateCustomSubcommand(logger, authValidator, configService, executor, graphApiService, blueprintService, confirmationProvider, resolver));
        permissionsCommand.AddCommand(CopilotStudioSubcommand.CreateCommand(logger, authValidator, configService, executor, graphApiService, blueprintService, resolver));

        return permissionsCommand;
    }

    /// <summary>
    /// MCP permissions subcommand
    /// </summary>
    private static Command CreateMcpSubcommand(
        ILogger logger,
        AzureAuthValidator authValidator,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        IConfirmationProvider confirmationProvider,
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command("mcp",
            "Configure MCP server OAuth2 grants and inheritable permissions\n" +
            "Required role: Agent ID Developer; Global Administrator for tenant-wide OAuth2 consent\n" +
            "(non-admins receive a unified /v2.0/adminconsent URL to forward to a Global Administrator).\n\n");

        var agentNameOption = new Option<string?>(
            ["--agent-name", "-n"],
            description: "Agent base name. When provided, no config file is required.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Overrides auto-detection. Use with --agent-name.");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Show detailed output");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            description: "Show what would be done without executing");

        var removeLegacyScopesOption = new Option<bool>(
            "--remove-legacy-scopes",
            description: "Remove shared ATG audience scopes from the blueprint.\n" +
                         "Only use after V2 SDK is confirmed live — agents on V1 SDK will lose tool access.");

        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(removeLegacyScopesOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var configFile = new FileInfo("a365.config.json");
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var removeLegacyScopes = context.ParseResult.GetValueForOption(removeLegacyScopesOption);
            var ct = context.GetCancellationToken();

            if (dryRun)
            {
                var dryRunConfig = await DryRunHelper.TryLoadConfigForDryRunAsync(agentName, tenantIdFlag, configFile, resolver, configService, isCleanupMode: true, ct);
                if (dryRunConfig is null)
                {
                    logger.LogInformation("Dry run: a365 setup permissions mcp --dry-run");
                    logger.LogInformation("  Would configure MCP server OAuth2 grants and inheritable permissions.");
                    logger.LogInformation("No changes made. Run without --dry-run to execute.");
                    return;
                }

                var manifestPath = Path.Combine(dryRunConfig.DeploymentProjectPath ?? string.Empty, McpConstants.ToolingManifestFileName);

                logger.LogInformation("Dry run: Configure MCP Permissions");
                logger.LogInformation("  Blueprint: {BlueprintId}", dryRunConfig.AgentBlueprintId);

                var dryRunAtgAppId = ConfigConstants.GetAgent365ToolsResourceAppId(dryRunConfig.Environment);
                if (removeLegacyScopes)
                {
                    var allScopes = await ManifestHelper.GetScopesByAudienceAsync(manifestPath, excludeLegacyAtg: false, resolvedAtgAppId: dryRunAtgAppId);
                    var remainingScopes = allScopes
                        .Where(kvp => !string.Equals(kvp.Key, dryRunAtgAppId, StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
                    var removedAudiences = allScopes.Keys
                        .Where(k => !remainingScopes.ContainsKey(k))
                        .ToList();

                    if (removedAudiences.Count > 0)
                    {
                        logger.LogInformation("Would REMOVE (--remove-legacy-scopes):");
                        foreach (var audience in removedAudiences)
                            logger.LogInformation("  - Resource: {Audience}  Scopes: {Scopes}",
                                audience, string.Join(", ", allScopes[audience]));
                    }

                    logger.LogInformation("Would CONFIGURE:");
                    foreach (var (audience, scopes) in remainingScopes)
                        logger.LogInformation("  - Resource: {Audience}  Scopes: {Scopes}",
                            audience, string.Join(", ", scopes));
                }
                else
                {
                    var scopesByAudience = await ManifestHelper.GetScopesByAudienceAsync(manifestPath, excludeLegacyAtg: false, resolvedAtgAppId: dryRunAtgAppId);
                    logger.LogInformation("Would configure OAuth2 grants and inheritable permissions:");
                    foreach (var (audience, scopes) in scopesByAudience)
                        logger.LogInformation("  - Resource: {Audience}  Scopes: {Scopes}",
                            audience, string.Join(", ", scopes));
                }

                logger.LogInformation("No changes made. Run without --dry-run to execute.");
                return;
            }

            Agent365Config? setupConfig;
            if (resolver != null)
                setupConfig = await resolver.ResolveAsync(agentName, tenantIdFlag, configFile, isCleanupMode: true, ct);
            else
                setupConfig = await configService.LoadAsync(configFile.FullName);
            if (setupConfig is null) { context.ExitCode = 1; return; }

            if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
            {
                logger.LogError("Blueprint ID not found. Run 'a365 setup blueprint' first.");
                context.ExitCode = 1;
                return;
            }

            // Configure GraphApiService with custom client app ID if available
            if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
            {
                graphApiService.CustomClientAppId = setupConfig.ClientAppId;
            }

            // Verify system requirements (PowerShell modules are required for Graph operations).
            // Skipped in dry-run: PowerShellModulesRequirementCheck can auto-install modules,
            // which would be a side effect in a mode that is supposed to be non-mutating.
            var mcpChecks = GetMcpChecks(authValidator);
            await RequirementsSubcommand.RunChecksOrExitAsync(mcpChecks, setupConfig, logger, ct);

            // Confirmation gate for --remove-legacy-scopes — requires explicit opt-in
            if (removeLegacyScopes)
            {
                logger.LogWarning(
                    "WARNING: --remove-legacy-scopes will permanently remove the shared ATG audience ({AtgAppId}) " +
                    "from the agent blueprint. Any agent instances still using the old SDK will immediately lose " +
                    "access to MCP tools. Ensure all agent instances have been upgraded to the new SDK before proceeding.",
                    McpConstants.WorkIQToolsProdAppId);

                var confirmed = await confirmationProvider.ConfirmAsync("Continue? [y/N]: ");
                if (!confirmed)
                {
                    logger.LogInformation("Aborted.");
                    return;
                }
            }

            await ConfigureMcpPermissionsAsync(
                configFile.FullName,
                logger,
                configService,
                executor,
                graphApiService,
                blueprintService,
                setupConfig,
                false,
                removeLegacyAtgScopes: removeLegacyScopes,
                confirmationProvider: confirmationProvider);

        });

        return command;
    }

    /// <summary>
    /// Bot API permissions subcommand
    /// </summary>
    private static Command CreateBotSubcommand(
        ILogger logger,
        AzureAuthValidator authValidator,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        IConfirmationProvider? confirmationProvider = null,
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command("bot",
            "Configure Messaging Bot API OAuth2 grants and inheritable permissions\n" +
            "Required role: Agent ID Developer; Global Administrator for tenant-wide OAuth2 consent\n" +
            "(non-admins receive a unified /v2.0/adminconsent URL to forward to a Global Administrator).\n\n" +
            "Prerequisites: Blueprint and MCP permissions (run 'a365 setup permissions mcp' first)\n" +
            "Next step: Run 'a365 publish' to package your agent for upload to the Microsoft 365 Admin Center");

        var agentNameOption = new Option<string?>(
            ["--agent-name", "-n"],
            description: "Agent base name. When provided, no config file is required.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Overrides auto-detection. Use with --agent-name.");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Show detailed output");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            description: "Show what would be done without executing");

        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var configFile = new FileInfo("a365.config.json");
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var ct = context.GetCancellationToken();

            if (dryRun)
            {
                var dryRunConfig = await DryRunHelper.TryLoadConfigForDryRunAsync(agentName, tenantIdFlag, configFile, resolver, configService, isCleanupMode: true, ct);
                if (dryRunConfig is null)
                {
                    logger.LogInformation("Dry run: a365 setup permissions bot --dry-run");
                    logger.LogInformation("  Would configure Messaging Bot API OAuth2 grants and inheritable permissions.");
                    logger.LogInformation("No changes made. Run without --dry-run to execute.");
                    return;
                }

                logger.LogInformation("Dry run: Configure Bot API Permissions");
                logger.LogInformation("Would configure Bot API permissions:");
                logger.LogInformation("  - Blueprint: {BlueprintId}", dryRunConfig.AgentBlueprintId);
                logger.LogInformation("  - Messaging Bot API: {Scope}", ConfigConstants.MessagingBotApiAdminConsentScope);
                logger.LogInformation("  - Observability API: {OtelScope} (delegated + application)", ConfigConstants.ObservabilityApiOtelWriteScope);
                logger.LogInformation("  - Power Platform API: Connectivity.Connections.Read");
                logger.LogInformation("No changes made. Run without --dry-run to execute.");
                return;
            }

            Agent365Config? setupConfig;
            if (resolver != null)
                setupConfig = await resolver.ResolveAsync(agentName, tenantIdFlag, configFile, isCleanupMode: true, ct);
            else
                setupConfig = await configService.LoadAsync(configFile.FullName);
            if (setupConfig is null) { context.ExitCode = 1; return; }

            if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
            {
                logger.LogError("Blueprint ID not found. Run 'a365 setup blueprint' first.");
                context.ExitCode = 1;
                return;
            }

            // Configure GraphApiService with custom client app ID if available
            if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
            {
                graphApiService.CustomClientAppId = setupConfig.ClientAppId;
            }

            // Verify system requirements (PowerShell modules are required for Graph operations).
            // Skipped in dry-run: PowerShellModulesRequirementCheck can auto-install modules,
            // which would be a side effect in a mode that is supposed to be non-mutating.
            var botChecks = GetBotChecks(authValidator);
            await RequirementsSubcommand.RunChecksOrExitAsync(botChecks, setupConfig, logger, ct);

            var success = await ConfigureBotPermissionsAsync(
                configFile.FullName,
                logger,
                configService,
                executor,
                setupConfig,
                graphApiService,
                blueprintService,
                false,
                confirmationProvider: confirmationProvider);
            if (!success)
                context.ExitCode = 1;

        });

        return command;
    }

    /// <summary>
    /// Custom blueprint permissions subcommand
    /// </summary>
    private static Command CreateCustomSubcommand(
        ILogger logger,
        AzureAuthValidator authValidator,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        IConfirmationProvider? confirmationProvider = null,
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command("custom",
            "Configure custom resource OAuth2 grants and inheritable permissions\n" +
            "Required role: Agent ID Developer; Global Administrator for tenant-wide OAuth2 consent\n" +
            "(non-admins receive a unified /v2.0/adminconsent URL to forward to a Global Administrator).\n\n" +
            "Prerequisites: Blueprint created (run 'a365 setup blueprint' first)\n");

        var agentNameOption = new Option<string?>(
            ["--agent-name", "-n"],
            description: "Agent base name. When provided, no config file is required.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Overrides auto-detection. Use with --agent-name.");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Show detailed output");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            description: "Show what would be done without executing");

        var resourceAppIdOption = new Option<string?>(
            "--resource-app-id",
            description: "Resource application ID (GUID) for an inline custom permission. Use with --scopes.");

        var scopesOption = new Option<string?>(
            "--scopes",
            description: "Comma-separated delegated scopes for the inline custom permission. Use with --resource-app-id.");

        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(resourceAppIdOption);
        command.AddOption(scopesOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var configFile = new FileInfo("a365.config.json");
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var resourceAppId = context.ParseResult.GetValueForOption(resourceAppIdOption);
            var scopesRaw = context.ParseResult.GetValueForOption(scopesOption);
            var ct = context.GetCancellationToken();

            // Inline mode: --resource-app-id + --scopes bypass the config-file permission list.
            bool isInlineMode = !string.IsNullOrWhiteSpace(resourceAppId) && !string.IsNullOrWhiteSpace(scopesRaw);
            if (!string.IsNullOrWhiteSpace(resourceAppId) && !isInlineMode)
            {
                logger.LogError("--resource-app-id requires --scopes.");
                context.ExitCode = 1;
                return;
            }
            if (!string.IsNullOrWhiteSpace(scopesRaw) && !isInlineMode)
            {
                logger.LogError("--scopes requires --resource-app-id.");
                context.ExitCode = 1;
                return;
            }

            if (dryRun)
            {
                var dryRunConfig = await DryRunHelper.TryLoadConfigForDryRunAsync(agentName, tenantIdFlag, configFile, resolver, configService, isCleanupMode: true, ct);
                if (isInlineMode)
                {
                    logger.LogInformation("Dry run: Configure inline custom permission");
                    logger.LogInformation("  Resource app ID : {ResourceAppId}", resourceAppId);
                    logger.LogInformation("  Scopes          : {Scopes}", scopesRaw);
                }
                else if (dryRunConfig is null)
                {
                    logger.LogInformation("Dry run: a365 setup permissions custom --dry-run");
                    logger.LogInformation("  Would configure custom blueprint OAuth2 grants and inheritable permissions.");
                }
                else
                {
                    logger.LogInformation("Dry run: Configure Custom Blueprint Permissions");
                    if (dryRunConfig.CustomBlueprintPermissions == null || dryRunConfig.CustomBlueprintPermissions.Count == 0)
                    {
                        logger.LogInformation("No custom permissions in config. Any stale permissions in Azure AD would be removed.");
                    }
                    else
                    {
                        logger.LogInformation("Would configure the following custom permissions:");
                        foreach (var customPerm in dryRunConfig.CustomBlueprintPermissions)
                        {
                            var resourceDisplayName = string.IsNullOrWhiteSpace(customPerm.ResourceName)
                                ? customPerm.ResourceAppId
                                : customPerm.ResourceName;
                            logger.LogInformation("  - {ResourceName} ({ResourceAppId})",
                                resourceDisplayName, customPerm.ResourceAppId);
                            logger.LogInformation("    Scopes: {Scopes}",
                                string.Join(", ", customPerm.Scopes));
                        }
                    }
                }
                logger.LogInformation("No changes made. Run without --dry-run to execute.");
                return;
            }

            // Inline-mode argument validation runs BEFORE the resolver so users with a bad GUID
            // or empty scopes get a precise error instead of a misleading "Agent name required"
            // from the config resolver when no config file is present.
            string[]? inlineScopes = null;
            if (isInlineMode)
            {
                if (!Guid.TryParse(resourceAppId, out _))
                {
                    logger.LogError("--resource-app-id must be a valid GUID. Got: {Value}", resourceAppId);
                    context.ExitCode = 1;
                    return;
                }

                inlineScopes = scopesRaw!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct()
                    .ToArray();
                if (inlineScopes.Length == 0)
                {
                    logger.LogError("--scopes must contain at least one non-empty scope value.");
                    context.ExitCode = 1;
                    return;
                }
            }

            Agent365Config? setupConfig;
            if (resolver != null)
                setupConfig = await resolver.ResolveAsync(agentName, tenantIdFlag, configFile, isCleanupMode: true, ct);
            else
                setupConfig = await configService.LoadAsync(configFile.FullName);
            if (setupConfig is null) { context.ExitCode = 1; return; }

            if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
            {
                logger.LogError("Blueprint ID not found. Run 'a365 setup blueprint' first.");
                context.ExitCode = 1;
                return;
            }

            // Configure GraphApiService with custom client app ID if available
            if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
            {
                graphApiService.CustomClientAppId = setupConfig.ClientAppId;
            }

            // Verify system requirements (PowerShell modules are required for Graph operations).
            // Skipped in dry-run: PowerShellModulesRequirementCheck can auto-install modules,
            // which would be a side effect in a mode that is supposed to be non-mutating.
            var customChecks = GetCustomChecks(authValidator);
            await RequirementsSubcommand.RunChecksOrExitAsync(customChecks, setupConfig, logger, ct);

            if (isInlineMode)
            {
                // inlineScopes was validated above before the resolver ran.
                var scopes = inlineScopes!;

                string resourceName;
                try
                {
                    resourceName = await graphApiService.GetServicePrincipalDisplayNameAsync(
                        setupConfig.TenantId, resourceAppId!, ct)
                        ?? CreateFallbackResourceName(resourceAppId);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    resourceName = CreateFallbackResourceName(resourceAppId);
                }

                try
                {
                    await SetupHelpers.EnsureResourcePermissionsAsync(
                        graphApiService, blueprintService, setupConfig,
                        resourceAppId!, resourceName,
                        scopes, logger, ct: ct);
                    logger.LogInformation("");
                    logger.LogInformation("Custom permission configured successfully: {ResourceName} [{Scopes}]",
                        resourceName, string.Join(", ", scopes));
                }
                catch (SetupValidationException ex)
                {
                    // Prefer the structured failure info that EnsureResourcePermissionsAsync attaches
                    // to ex.Context (graphStatusCode, graphErrorCode) — branching on a parsed status
                    // code or error code is precise. Fall back to substring matching for safety in
                    // case Context isn't populated (legacy throw paths or future changes).
                    logger.LogInformation("");
                    bool looksLikeAuthDenied =
                        (ex.Context.TryGetValue("graphStatusCode", out var graphStatus) && graphStatus == "403")
                        || (ex.Context.TryGetValue("graphErrorCode", out var graphErrorCode)
                            && string.Equals(graphErrorCode, "Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase))
                        || ex.Message.Contains("Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("Insufficient privileges", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("insufficient permissions", StringComparison.OrdinalIgnoreCase);

                    if (!looksLikeAuthDenied)
                    {
                        logger.LogError("Failed to configure custom permission: {Message}", ex.Message);
                        context.ExitCode = 1;
                        return;
                    }

                    logger.LogInformation("Custom permission configuration requires tenant admin action.");
                    logger.LogDebug("Underlying error: {Message}", ex.Message);

                    var isGraph = string.Equals(
                        resourceAppId,
                        AuthenticationConstants.MicrosoftGraphResourceAppId,
                        StringComparison.OrdinalIgnoreCase);
                    if (isGraph)
                    {
                        var fullyQualified = scopes.Select(s => $"{AuthenticationConstants.MicrosoftGraphResourceUri}/{s}");
                        var url = SetupHelpers.BuildAdminConsentUrl(
                            setupConfig.TenantId, setupConfig.AgentBlueprintId!, fullyQualified);
                        LogAdminConsentNextSteps(logger, url);
                    }
                    else
                    {
                        logger.LogInformation(
                            "An administrator must grant the blueprint consent for {ResourceName} [{Scopes}] via the Entra portal.",
                            resourceName, string.Join(", ", scopes));
                    }
                    context.ExitCode = 1;
                    return;
                }
            }
            else
            {
                await ConfigureCustomPermissionsAsync(
                    configFile.FullName,
                    logger,
                    configService,
                    executor,
                    graphApiService,
                    blueprintService,
                    setupConfig,
                    false,
                    confirmationProvider: confirmationProvider);
            }

        });

        return command;
    }

    /// <summary>
    /// Reads the required MCP server OAuth2 scopes from the tooling manifest file.
    /// Returns an empty array when the manifest is absent or unreadable.
    /// </summary>
    internal static async Task<string[]> ReadMcpScopesAsync(string manifestPath, ILogger logger)
    {
        var scopes = await ManifestHelper.GetRequiredScopesAsync(manifestPath);
        if (scopes.Length == 0)
            logger.LogDebug("No MCP scopes found in manifest at {ManifestPath} — MCP permissions will be skipped.", manifestPath);
        return scopes;
    }

    /// <summary>
    /// Configures MCP server permissions (OAuth2 grants and inheritable permissions).
    /// Public method that can be called by AllSubcommand.
    /// </summary>
    public static async Task<bool> ConfigureMcpPermissionsAsync(
        string configPath,
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        Models.Agent365Config setupConfig,
        bool iSetupAll,
        SetupResults? setupResults = null,
        CancellationToken cancellationToken = default,
        bool removeLegacyAtgScopes = false,
        IConfirmationProvider? confirmationProvider = null)
    {
        logger.LogInformation("");
        logger.LogInformation("Configuring MCP server permissions...");
        logger.LogInformation("");

        try
        {
            var manifestPath = Path.Combine(setupConfig.DeploymentProjectPath ?? string.Empty, McpConstants.ToolingManifestFileName);

            var atgAppId = ConfigConstants.GetAgent365ToolsResourceAppId(setupConfig.Environment);
            var scopesByAudience = await ManifestHelper.GetScopesByAudienceAsync(
                manifestPath, excludeLegacyAtg: removeLegacyAtgScopes, resolvedAtgAppId: atgAppId);

            // Validate all scopes are known: V1 pattern, V2 value, or metadata scope
            var unknownScopes = scopesByAudience.Values
                .SelectMany(s => s)
                .Where(s =>
                    !McpConstants.IsV1Scope(s) &&
                    !string.Equals(s, McpConstants.V2ScopeValue, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(s, "McpServersMetadata.Read.All", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (unknownScopes.Count > 0)
            {
                foreach (var unknownScope in unknownScopes)
                    logger.LogError("Unknown scope '{Scope}'. Re-run: a365 develop add-mcp-servers.", unknownScope);
                return false;
            }

            var specs = scopesByAudience
                .Select(kvp => new ResourcePermissionSpec(
                    kvp.Key, "Agent 365 Tools", kvp.Value, SetInheritable: true))
                .ToList();

            logger.LogInformation("Configuring permissions for {Count} resource(s):", specs.Count);
            foreach (var spec in specs)
                logger.LogInformation("  {AppId} — {Scopes}",
                    spec.ResourceAppId, string.Join(", ", spec.Scopes));

            var localResults = setupResults ?? new SetupResults();
            // Every spec built above came from scopesByAudience.Keys — they are all MCP
            // per-server audiences. Passing the same set as knownMcpAudienceAppIds ensures
            // the orchestrator's catch-all spec loop routes them through the bare-GUID
            // branch of GetResourceIdentifierUri (api://{appId} would trigger AADSTS500011
            // because per-server SPs have identifierUris=null).
            var (_, _, consentGranted, adminConsentUrl) = await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                graphApiService, blueprintService, setupConfig,
                setupConfig.AgentBlueprintId!, setupConfig.TenantId,
                specs, logger, localResults, cancellationToken,
                knownBlueprintSpObjectId: setupConfig.AgentBlueprintServicePrincipalObjectId,
                confirmationProvider: confirmationProvider,
                commandExecutor: executor,
                knownMcpAudienceAppIds: scopesByAudience.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));

            // Ensure the Action Required block prints the blueprint and tenant context even when this
            // subcommand is run standalone (setup all populates these earlier; standalone runs don't).
            localResults.BlueprintId ??= setupConfig.AgentBlueprintId;
            localResults.TenantId ??= setupConfig.TenantId;

            logger.LogInformation("");
            if (consentGranted)
            {
                logger.LogInformation("MCP server permissions configured successfully");
            }
            else
            {
                logger.LogInformation("MCP server permissions configured; admin consent required");
                LogAdminConsentNextSteps(logger, adminConsentUrl, localResults, specs);
            }
            logger.LogInformation("");
            if (!iSetupAll)
            {
                logger.LogInformation("Next step: 'a365 setup permissions bot' to configure Bot API permissions");
            }

            await configService.SaveStateAsync(setupConfig);
            return consentGranted;
        }
        catch (Exception mcpEx)
        {
            logger.LogError("Failed to configure MCP server permissions: {Message}", mcpEx.Message);
            logger.LogInformation("To configure MCP permissions manually:");
            logger.LogInformation("  1. Ensure the agent blueprint has the required permissions in Azure Portal");
            logger.LogInformation("  2. Grant admin consent for the MCP scopes");
            logger.LogInformation("  3. Run 'a365 setup mcp' to retry MCP permission configuration");
            if (iSetupAll)
            {
                throw;
            }
            return false;
        }
    }

    /// <summary>
    /// Configures Bot API permissions (OAuth2 grants and inheritable permissions).
    /// Public method that can be called by AllSubcommand.
    /// </summary>
    public static async Task<bool> ConfigureBotPermissionsAsync(
        string configPath,
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        Models.Agent365Config setupConfig,
        GraphApiService graphService,
        AgentBlueprintService blueprintService,
        bool iSetupAll,
        SetupResults? setupResults = null,
        CancellationToken cancellationToken = default,
        IConfirmationProvider? confirmationProvider = null)
    {
        if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
        {
            logger.LogError("AgentBlueprintId is missing from configuration. Run 'a365 setup blueprint' first.");
            return false;
        }

        logger.LogInformation("");
        logger.LogInformation("Configuring Messaging Bot API permissions...");
        logger.LogInformation("");

        try
        {
            var specs = new List<ResourcePermissionSpec>(SetupHelpers.GetFixedApiPermissionSpecs(setInheritable: true, isM365: true));

            var localResults = setupResults ?? new SetupResults();
            var (_, _, consentGranted, adminConsentUrl) = await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                graphService, blueprintService, setupConfig,
                setupConfig.AgentBlueprintId!, setupConfig.TenantId,
                specs, logger, localResults, cancellationToken,
                knownBlueprintSpObjectId: setupConfig.AgentBlueprintServicePrincipalObjectId,
                confirmationProvider: confirmationProvider,
                commandExecutor: executor);

            // Ensure the Action Required block prints the blueprint and tenant context even when this
            // subcommand is run standalone (setup all populates these earlier; standalone runs don't).
            localResults.BlueprintId ??= setupConfig.AgentBlueprintId;
            localResults.TenantId ??= setupConfig.TenantId;

            await configService.SaveStateAsync(setupConfig);

            // BlueprintS2SOutcome == Granted means S2S succeeded; any other value (NotApplicable,
            // Failed) treats this as "not in place". Consent state is checked before S2S so the
            // non-admin path still shows "consent required" rather than the S2S warning (S2S is
            // never attempted when consent isn't granted).
            var s2sFailed = localResults.BlueprintS2SOutcome != Models.GrantOutcome.Granted;

            logger.LogInformation("");
            if (!s2sFailed && consentGranted)
            {
                logger.LogInformation("Bot API permissions configured successfully");
            }
            else if (!consentGranted)
            {
                logger.LogInformation("Bot API permissions configured; admin consent required");
                LogAdminConsentNextSteps(logger, adminConsentUrl, localResults, specs);
            }
            else
            {
                logger.LogWarning(
                    "Bot API permissions configured, but S2S app role assignment failed. " +
                    "Re-run 'a365 setup permissions bot' as {Roles} to retry.",
                    AuthenticationConstants.S2SGrantRequiredRoles);
            }
            logger.LogInformation("");
            if (!iSetupAll)
            {
                logger.LogInformation("Next step: Run 'a365 publish' to package your agent for upload to the Microsoft 365 Admin Center.");
            }
            return consentGranted && !s2sFailed;
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to configure Bot API permissions: {Message}", ex.Message);
            if (iSetupAll)
            {
                throw;
            }
            return false;
        }
    }

    /// <summary>
    /// Removes custom inheritable permissions from Azure AD that are no longer present in the config.
    /// Standard (CLI-managed) permissions (MCP, Bot API, Graph, etc.) are never touched.
    /// OAuth2 grants for removed entries are also revoked on a best-effort basis.
    /// </summary>
    internal static async Task RemoveStaleCustomPermissionsAsync(
        ILogger logger,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        Models.Agent365Config setupConfig,
        HashSet<string> desiredCustomIds,
        CancellationToken cancellationToken)
    {
        // Resource app IDs owned by standard setup subcommands — never remove these
        var envAtgAppId = ConfigConstants.GetAgent365ToolsResourceAppId(setupConfig.Environment);
        var protectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            envAtgAppId,
            ConfigConstants.MessagingBotApiAppId,
            ConfigConstants.ObservabilityApiAppId,
            PowerPlatformConstants.PowerPlatformApiResourceAppId,
            AuthenticationConstants.MicrosoftGraphResourceAppId,
        };

        // Protect V2 MCP audience GUIDs — these are managed by 'setup permissions mcp',
        // not by custom-permission reconciliation. Without this, re-running 'setup blueprint'
        // would treat them as stale and remove them.
        var manifestPath = Path.Combine(setupConfig.DeploymentProjectPath ?? string.Empty, McpConstants.ToolingManifestFileName);
        var mcpAudiences = await ManifestHelper.GetScopesByAudienceAsync(manifestPath, resolvedAtgAppId: envAtgAppId);
        foreach (var audienceId in mcpAudiences.Keys)
            protectedIds.Add(audienceId);

        // Must match RequiredPermissionGrantScopes exactly so the PowerShell token acquired
        // for inheritable permissions is reused (same cache key) rather than triggering
        // a second Connect-MgGraph prompt.
        var requiredPermissions = AuthenticationConstants.RequiredPermissionGrantScopes;

        List<(string ResourceAppId, bool ScopesAllAllowed, bool RolesAllAllowed)> currentPermissions;
        try
        {
            currentPermissions = await blueprintService.ListInheritablePermissionsAsync(
                setupConfig.TenantId,
                setupConfig.AgentBlueprintId!,
                requiredPermissions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not fetch current inheritable permissions for reconciliation: {Message}. Skipping cleanup.", ex.Message);
            return;
        }

        var stale = currentPermissions
            .Where(p => !protectedIds.Contains(p.ResourceAppId) && !desiredCustomIds.Contains(p.ResourceAppId))
            .ToList();

        if (stale.Count == 0) return;

        logger.LogInformation("Removing {Count} stale custom permission(s) no longer in config...", stale.Count);

        // Resolve blueprint service principal once for OAuth2 grant revocation
        var permissionGrantScopes = AuthenticationConstants.RequiredPermissionGrantScopes;
        string? blueprintSpObjectId = null;
        try
        {
            blueprintSpObjectId = await graphApiService.LookupServicePrincipalByAppIdAsync(
                setupConfig.TenantId, setupConfig.AgentBlueprintId!, cancellationToken, permissionGrantScopes);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not resolve blueprint service principal for OAuth2 grant cleanup: {Message}", ex.Message);
        }

        foreach (var (resourceAppId, _, _) in stale)
        {
            logger.LogInformation("  Removing stale permission for {ResourceAppId}...", resourceAppId);

            var removed = await blueprintService.RemoveInheritablePermissionsAsync(
                setupConfig.TenantId,
                setupConfig.AgentBlueprintId!,
                resourceAppId,
                requiredPermissions,
                cancellationToken);

            if (removed)
                logger.LogInformation("  - Inheritable permissions removed for {ResourceAppId}", resourceAppId);
            else
                logger.LogWarning("  - Failed to remove inheritable permissions for {ResourceAppId}", resourceAppId);

            // Revoke OAuth2 grant (best-effort — non-blocking if it fails)
            if (!string.IsNullOrWhiteSpace(blueprintSpObjectId))
            {
                try
                {
                    var resourceSpObjectId = await graphApiService.LookupServicePrincipalByAppIdAsync(
                        setupConfig.TenantId, resourceAppId, cancellationToken, permissionGrantScopes);

                    if (!string.IsNullOrWhiteSpace(resourceSpObjectId))
                    {
                        // Calling ReplaceOauth2PermissionGrantAsync with empty scopes revokes the grant
                        var revoked = await blueprintService.ReplaceOauth2PermissionGrantAsync(
                            setupConfig.TenantId,
                            blueprintSpObjectId,
                            resourceSpObjectId,
                            Enumerable.Empty<string>(),
                            cancellationToken);

                        if (revoked)
                            logger.LogInformation("  - OAuth2 grant revoked for {ResourceAppId}", resourceAppId);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning("  - Could not revoke OAuth2 grant for {ResourceAppId}: {Message}. Remove it manually from Azure Portal if needed.", resourceAppId, ex.Message);
                }
            }
        }
    }

    /// <summary>
    /// Creates a fallback resource name from a resource App ID.
    /// Uses safe substring operation with null/length checks.
    /// </summary>
    private static string CreateFallbackResourceName(string? resourceAppId)
    {
        const string prefix = "Custom";
        const int idPrefixLength = 8;

        if (string.IsNullOrWhiteSpace(resourceAppId))
            return $"{prefix}-Unknown";

        var shortId = resourceAppId.Length >= idPrefixLength
            ? resourceAppId.Substring(0, idPrefixLength)
            : resourceAppId;

        return $"{prefix}-{shortId}";
    }

    /// <summary>
    /// Configures custom blueprint permissions (OAuth2 grants and inheritable permissions).
    /// Public method that can be called by AllSubcommand.
    /// </summary>
    /// <param name="configPath">Path to the configuration file</param>
    /// <param name="logger">Logger instance for diagnostic output</param>
    /// <param name="configService">Service for loading and saving configuration</param>
    /// <param name="executor">Command executor for Azure CLI operations</param>
    /// <param name="graphApiService">Service for Microsoft Graph API interactions</param>
    /// <param name="blueprintService">Service for agent blueprint operations</param>
    /// <param name="setupConfig">Current configuration including custom permissions</param>
    /// <param name="isSetupAll">Whether this is called from 'setup all' command (affects error handling)</param>
    /// <param name="setupResults">Optional results tracker for setup operations</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>True if configuration succeeded, false otherwise</returns>
    public static async Task<bool> ConfigureCustomPermissionsAsync(
        string configPath,
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        Models.Agent365Config setupConfig,
        bool isSetupAll,
        SetupResults? setupResults = null,
        CancellationToken cancellationToken = default,
        IConfirmationProvider? confirmationProvider = null)
    {
        logger.LogInformation("");
        logger.LogInformation("Configuring custom blueprint permissions...");
        logger.LogInformation("");

        try
        {
            // Build the set of resource app IDs desired by the current config
            var desiredCustomIds = new HashSet<string>(
                (setupConfig.CustomBlueprintPermissions ?? new List<CustomResourcePermission>())
                    .Select(p => p.ResourceAppId),
                StringComparer.OrdinalIgnoreCase);

            // Reconcile: remove permissions that are no longer in the config
            await RemoveStaleCustomPermissionsAsync(
                logger, graphApiService, blueprintService, setupConfig, desiredCustomIds, cancellationToken);

            if (setupConfig.CustomBlueprintPermissions == null || setupConfig.CustomBlueprintPermissions.Count == 0)
            {
                logger.LogInformation("No custom blueprint permissions specified in config. Skipping.");
                await configService.SaveStateAsync(setupConfig);
                return true;
            }

            var hasValidationFailures = false;
            var specList = new List<ResourcePermissionSpec>();

            foreach (var customPerm in setupConfig.CustomBlueprintPermissions)
            {
                // Auto-resolve resource name if not provided
                if (string.IsNullOrWhiteSpace(customPerm.ResourceName))
                {
                    logger.LogInformation("Resource name not provided, attempting auto-lookup for {ResourceAppId}...",
                        customPerm.ResourceAppId);

                    try
                    {
                        var displayName = await graphApiService.GetServicePrincipalDisplayNameAsync(
                            setupConfig.TenantId,
                            customPerm.ResourceAppId,
                            cancellationToken);

                        if (!string.IsNullOrWhiteSpace(displayName))
                        {
                            customPerm.ResourceName = displayName;
                            logger.LogInformation("  - Auto-resolved resource name: {ResourceName}", displayName);
                        }
                        else
                        {
                            customPerm.ResourceName = CreateFallbackResourceName(customPerm.ResourceAppId);
                            logger.LogWarning("  - Could not resolve resource name, using fallback: {ResourceName}",
                                customPerm.ResourceName);
                        }
                    }
                    catch (Exception ex)
                    {
                        customPerm.ResourceName = CreateFallbackResourceName(customPerm.ResourceAppId);
                        logger.LogWarning("  - Failed to auto-resolve resource name: {Message}. Using fallback: {ResourceName}",
                            ex.Message, customPerm.ResourceName);
                    }
                }

                // Validate
                var (isValid, errors) = customPerm.Validate();
                if (!isValid)
                {
                    logger.LogError("Invalid custom permission configuration: {Errors}",
                        string.Join(", ", errors));
                    if (isSetupAll)
                        throw new SetupValidationException(
                            $"Invalid custom permission: {string.Join(", ", errors)}");
                    hasValidationFailures = true;
                    continue;
                }

                specList.Add(new ResourcePermissionSpec(
                    customPerm.ResourceAppId,
                    customPerm.ResourceName,
                    customPerm.Scopes.ToArray(),
                    SetInheritable: true));
            }

            string? customAdminConsentUrl = null;
            bool customConsentGranted = true;
            var localResults = setupResults ?? new SetupResults();
            if (specList.Count > 0)
            {
                // Operators can paste a ToolingManifest audience appId (e.g. "Windows 365 for
                // Agents MCP", da81128c-...) into customPermissions. If we don't load the
                // manifest here, those entries route through the api://{appId} branch in
                // GetResourceIdentifierUri and trigger AADSTS500011 because per-server SPs
                // have identifierUris=null. Loading the audience set lets the catch-all spec
                // loop route them to the bare-GUID branch.
                var customManifestPath = Path.Combine(setupConfig.DeploymentProjectPath ?? string.Empty, McpConstants.ToolingManifestFileName);
                var customAtgAppId = ConfigConstants.GetAgent365ToolsResourceAppId(setupConfig.Environment);
                var customManifestAudiences = await ManifestHelper.GetScopesByAudienceAsync(
                    customManifestPath, excludeLegacyAtg: false, resolvedAtgAppId: customAtgAppId);

                var (_, _, consentGranted, adminConsentUrl) = await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                    graphApiService, blueprintService, setupConfig,
                    setupConfig.AgentBlueprintId!, setupConfig.TenantId,
                    specList, logger, localResults, cancellationToken,
                    knownBlueprintSpObjectId: setupConfig.AgentBlueprintServicePrincipalObjectId,
                    confirmationProvider: confirmationProvider,
                    commandExecutor: executor,
                    knownMcpAudienceAppIds: customManifestAudiences.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));

                customAdminConsentUrl = adminConsentUrl;
                customConsentGranted = consentGranted;
                if (!consentGranted)
                    hasValidationFailures = true;
            }

            // Ensure the Action Required block prints the blueprint and tenant context even when this
            // subcommand is run standalone (setup all populates these earlier; standalone runs don't).
            localResults.BlueprintId ??= setupConfig.AgentBlueprintId;
            localResults.TenantId ??= setupConfig.TenantId;

            logger.LogInformation("");
            if (hasValidationFailures)
            {
                logger.LogWarning("Custom blueprint permissions completed with validation failures — check errors above");
                if (!customConsentGranted)
                    LogAdminConsentNextSteps(logger, customAdminConsentUrl, localResults, specList);
            }
            else
            {
                logger.LogInformation("Custom blueprint permissions configured successfully");
            }
            logger.LogInformation("");

            await configService.SaveStateAsync(setupConfig);
            return !hasValidationFailures;
        }
        catch (Exception ex)
        {
            if (isSetupAll)
            {
                // Let the caller (AllSubcommand) handle logging
                throw;
            }

            logger.LogError("Failed to configure custom blueprint permissions: {Message}", ex.Message);
            return false;
        }
    }

    private static void LogAdminConsentNextSteps(
        ILogger logger,
        string? adminConsentUrl,
        SetupResults? results = null,
        IReadOnlyList<ResourcePermissionSpec>? specs = null)
    {
        // Rich path: when we have the per-run results and the spec list, surface the same
        // action-required block that 'setup all' produces, including the unified
        // /v2.0/adminconsent URL and any S2S app-role PowerShell guidance.
        if (results is not null && specs is not null)
        {
            SetupHelpers.LogPermissionsActionRequired(logger, results, specs, adminConsentUrl);
            return;
        }

        if (string.IsNullOrWhiteSpace(adminConsentUrl))
        {
            logger.LogInformation("Ask a tenant administrator to grant consent for the blueprint app's required permissions.");
            return;
        }

        logger.LogInformation("Share the following URL with a tenant administrator so they can grant consent:");
        logger.LogInformation("  {Url}", adminConsentUrl);
    }
}
