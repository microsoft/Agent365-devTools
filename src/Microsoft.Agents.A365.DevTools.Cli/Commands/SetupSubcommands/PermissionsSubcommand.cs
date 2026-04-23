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
            "Configure OAuth2 permission grants and inheritable permissions\n" +
            "Minimum required permissions: Global Administrator\n");

        // Add subcommands
        permissionsCommand.AddCommand(CreateMcpSubcommand(logger, authValidator, configService, executor, graphApiService, blueprintService, confirmationProvider, resolver));
        permissionsCommand.AddCommand(CreateBotSubcommand(logger, authValidator, configService, executor, graphApiService, blueprintService, resolver));
        permissionsCommand.AddCommand(CreateCustomSubcommand(logger, authValidator, configService, executor, graphApiService, blueprintService, resolver));
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
            "Minimum required permissions: Global Administrator\n\n");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Configuration file path");

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

        command.AddOption(configOption);
        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(removeLegacyScopesOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var config = context.ParseResult.GetValueForOption(configOption)!;
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var removeLegacyScopes = context.ParseResult.GetValueForOption(removeLegacyScopesOption);
            var ct = context.GetCancellationToken();

            Agent365Config? setupConfig;
            if (resolver != null)
                setupConfig = await resolver.ResolveAsync(agentName, tenantIdFlag, config, isCleanupMode: true, ct);
            else
                setupConfig = await configService.LoadAsync(config.FullName);
            if (setupConfig is null) { context.ExitCode = 1; return; }

            if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
            {
                logger.LogError("Blueprint ID not found. Run 'a365 setup blueprint' first.");
                ExceptionHandler.ExitWithCleanup(1);
            }

            // Configure GraphApiService with custom client app ID if available
            if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
            {
                graphApiService.CustomClientAppId = setupConfig.ClientAppId;
            }

            // Verify system requirements (PowerShell modules are required for Graph operations).
            // Skipped in dry-run: PowerShellModulesRequirementCheck can auto-install modules,
            // which would be a side effect in a mode that is supposed to be non-mutating.
            if (!dryRun)
            {
                var mcpChecks = GetMcpChecks(authValidator);
                await RequirementsSubcommand.RunChecksOrExitAsync(mcpChecks, setupConfig, logger, CancellationToken.None);
            }

            // Confirmation gate for --remove-legacy-scopes — requires explicit opt-in
            if (removeLegacyScopes && !dryRun)
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

            if (dryRun)
            {
                var manifestPath = Path.Combine(setupConfig.DeploymentProjectPath ?? string.Empty, McpConstants.ToolingManifestFileName);

                logger.LogInformation("DRY RUN: Configure MCP Permissions");
                logger.LogInformation("  Blueprint: {BlueprintId}", setupConfig.AgentBlueprintId);

                var dryRunAtgAppId = ConfigConstants.GetAgent365ToolsResourceAppId(setupConfig.Environment);
                if (removeLegacyScopes)
                {
                    // Parse once, then split into removed (ATG) vs remaining (non-ATG) in memory.
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

                return;
            }

            await ConfigureMcpPermissionsAsync(
                config.FullName,
                logger,
                configService,
                executor,
                graphApiService,
                blueprintService,
                setupConfig,
                false,
                removeLegacyAtgScopes: removeLegacyScopes);

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
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command("bot",
            "Configure Messaging Bot API OAuth2 grants and inheritable permissions\n" +
            "Minimum required permissions: Global Administrator\n\n" +
            "Prerequisites: Blueprint and MCP permissions (run 'a365 setup permissions mcp' first)\n" +
            "Next step: Deploy your agent (run 'a365 deploy' if hosting on Azure)");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Configuration file path");

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

        command.AddOption(configOption);
        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var config = context.ParseResult.GetValueForOption(configOption)!;
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var ct = context.GetCancellationToken();

            Agent365Config? setupConfig;
            if (resolver != null)
                setupConfig = await resolver.ResolveAsync(agentName, tenantIdFlag, config, isCleanupMode: true, ct);
            else
                setupConfig = await configService.LoadAsync(config.FullName);
            if (setupConfig is null) { context.ExitCode = 1; return; }

            if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
            {
                logger.LogError("Blueprint ID not found. Run 'a365 setup blueprint' first.");
                ExceptionHandler.ExitWithCleanup(1);
            }

            // Configure GraphApiService with custom client app ID if available
            if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
            {
                graphApiService.CustomClientAppId = setupConfig.ClientAppId;
            }

            // Verify system requirements (PowerShell modules are required for Graph operations).
            // Skipped in dry-run: PowerShellModulesRequirementCheck can auto-install modules,
            // which would be a side effect in a mode that is supposed to be non-mutating.
            if (!dryRun)
            {
                var botChecks = GetBotChecks(authValidator);
                await RequirementsSubcommand.RunChecksOrExitAsync(botChecks, setupConfig, logger, CancellationToken.None);
            }

            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Configure Bot API Permissions");
                logger.LogInformation("Would configure Bot API permissions:");
                logger.LogInformation("  - Blueprint: {BlueprintId}", setupConfig.AgentBlueprintId);
                logger.LogInformation("  - Messaging Bot API: Authorization.ReadWrite, user_impersonation");
                logger.LogInformation("  - Observability API: {OtelScope} (delegated + application)", ConfigConstants.ObservabilityApiOtelWriteScope);
                logger.LogInformation("  - Power Platform API: Connectivity.Connections.Read");
                return;
            }

            await ConfigureBotPermissionsAsync(
                config.FullName,
                logger,
                configService,
                executor,
                setupConfig,
                graphApiService,
                blueprintService,
                false);

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
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command("custom",
            "Configure custom resource OAuth2 grants and inheritable permissions\n" +
            "Minimum required permissions: Global Administrator\n\n" +
            "Prerequisites: Blueprint created (run 'a365 setup blueprint' first)\n");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Configuration file path");

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

        command.AddOption(configOption);
        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);
        command.AddOption(resourceAppIdOption);
        command.AddOption(scopesOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var config = context.ParseResult.GetValueForOption(configOption)!;
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var resourceAppId = context.ParseResult.GetValueForOption(resourceAppIdOption);
            var scopesRaw = context.ParseResult.GetValueForOption(scopesOption);
            var ct = context.GetCancellationToken();

            Agent365Config? setupConfig;
            if (resolver != null)
                setupConfig = await resolver.ResolveAsync(agentName, tenantIdFlag, config, isCleanupMode: true, ct);
            else
                setupConfig = await configService.LoadAsync(config.FullName);
            if (setupConfig is null) { context.ExitCode = 1; return; }

            if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
            {
                logger.LogError("Blueprint ID not found. Run 'a365 setup blueprint' first.");
                ExceptionHandler.ExitWithCleanup(1);
            }

            // Configure GraphApiService with custom client app ID if available
            if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
            {
                graphApiService.CustomClientAppId = setupConfig.ClientAppId;
            }

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

            // Verify system requirements (PowerShell modules are required for Graph operations).
            // Skipped in dry-run: PowerShellModulesRequirementCheck can auto-install modules,
            // which would be a side effect in a mode that is supposed to be non-mutating.
            if (!dryRun)
            {
                var customChecks = GetCustomChecks(authValidator);
                await RequirementsSubcommand.RunChecksOrExitAsync(customChecks, setupConfig, logger, CancellationToken.None);
            }

            if (dryRun)
            {
                if (isInlineMode)
                {
                    logger.LogInformation("DRY RUN: Configure inline custom permission");
                    logger.LogInformation("  Resource app ID : {ResourceAppId}", resourceAppId);
                    logger.LogInformation("  Scopes          : {Scopes}", scopesRaw);
                }
                else
                {
                    logger.LogInformation("DRY RUN: Configure Custom Blueprint Permissions");
                    if (setupConfig.CustomBlueprintPermissions == null || setupConfig.CustomBlueprintPermissions.Count == 0)
                    {
                        logger.LogInformation("No custom permissions in config. Any stale permissions in Azure AD would be removed.");
                    }
                    else
                    {
                        logger.LogInformation("Would configure the following custom permissions:");
                        foreach (var customPerm in setupConfig.CustomBlueprintPermissions)
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
                return;
            }

            if (isInlineMode)
            {
                if (!Guid.TryParse(resourceAppId, out _))
                {
                    logger.LogError("--resource-app-id must be a valid GUID. Got: {Value}", resourceAppId);
                    context.ExitCode = 1;
                    return;
                }

                var scopes = scopesRaw!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                await SetupHelpers.EnsureResourcePermissionsAsync(
                    graphApiService, blueprintService, setupConfig,
                    resourceAppId!, resourceAppId!,
                    scopes, logger, ct: ct);
            }
            else
            {
                await ConfigureCustomPermissionsAsync(
                    config.FullName,
                    logger,
                    configService,
                    executor,
                    graphApiService,
                    blueprintService,
                    setupConfig,
                    false);
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
        bool removeLegacyAtgScopes = false)
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

            var (_, _, consentGranted, _) = await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                graphApiService, blueprintService, setupConfig,
                setupConfig.AgentBlueprintId!, setupConfig.TenantId,
                specs, logger, setupResults, cancellationToken,
                knownBlueprintSpObjectId: setupConfig.AgentBlueprintServicePrincipalObjectId);

            logger.LogInformation("");
            if (consentGranted)
                logger.LogInformation("MCP server permissions configured successfully");
            else
                logger.LogInformation("MCP server permissions configured; admin consent required");
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
        CancellationToken cancellationToken = default)
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
            var specs = new List<ResourcePermissionSpec>(SetupHelpers.GetFixedApiPermissionSpecs(setInheritable: true));

            var (_, _, consentGranted, _) = await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                graphService, blueprintService, setupConfig,
                setupConfig.AgentBlueprintId!, setupConfig.TenantId,
                specs, logger, setupResults, cancellationToken,
                knownBlueprintSpObjectId: setupConfig.AgentBlueprintServicePrincipalObjectId);

            await configService.SaveStateAsync(setupConfig);

            logger.LogInformation("");
            logger.LogInformation("Bot API permissions configured successfully");
            logger.LogInformation("");
            if (!iSetupAll)
            {
                logger.LogInformation("Next step: Deploy your agent (run 'a365 deploy' if hosting on Azure)");
            }
            return consentGranted;
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

        List<(string ResourceAppId, List<string> Scopes)> currentPermissions;
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

        foreach (var (resourceAppId, _) in stale)
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
        CancellationToken cancellationToken = default)
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

            if (specList.Count > 0)
            {
                var (_, _, consentGranted, _) = await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                    graphApiService, blueprintService, setupConfig,
                    setupConfig.AgentBlueprintId!, setupConfig.TenantId,
                    specList, logger, setupResults, cancellationToken,
                    knownBlueprintSpObjectId: setupConfig.AgentBlueprintServicePrincipalObjectId);

                if (!consentGranted)
                    hasValidationFailures = true;
            }

            logger.LogInformation("");
            if (hasValidationFailures)
                logger.LogWarning("Custom blueprint permissions completed with validation failures — check errors above");
            else
                logger.LogInformation("Custom blueprint permissions configured successfully");
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
}
