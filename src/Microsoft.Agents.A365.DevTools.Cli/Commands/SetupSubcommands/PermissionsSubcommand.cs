// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Threading;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Permissions subcommand - Configures OAuth2 permission grants and inheritable permissions
/// Required Permissions: Global Administrator (for admin consent)
/// </summary>
internal static class PermissionsSubcommand
{
    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService)
    {
        var permissionsCommand = new Command("permissions",
            "Configure OAuth2 permission grants and inheritable permissions\n" +
            "Minimum required permissions: Global Administrator\n");

        // Add subcommands
        permissionsCommand.AddCommand(CreateMcpSubcommand(logger, configService, executor, graphApiService, blueprintService));
        permissionsCommand.AddCommand(CreateBotSubcommand(logger, configService, executor, graphApiService, blueprintService));
        permissionsCommand.AddCommand(CreateCustomSubcommand(logger, configService, executor, graphApiService, blueprintService));
        permissionsCommand.AddCommand(CopilotStudioSubcommand.CreateCommand(logger, configService, executor, graphApiService, blueprintService));

        return permissionsCommand;
    }

    /// <summary>
    /// MCP permissions subcommand
    /// </summary>
    private static Command CreateMcpSubcommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService)
    {
        var command = new Command("mcp",
            "Configure MCP server OAuth2 grants and inheritable permissions\n" +
            "Minimum required permissions: Global Administrator\n\n");

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

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);

        command.SetHandler(async (config, verbose, dryRun) =>
        {
            var setupConfig = await configService.LoadAsync(config.FullName);

            if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
            {
                logger.LogError("Blueprint ID not found. Run 'a365 setup blueprint' first.");
                Environment.Exit(1);
            }

            // Configure GraphApiService with custom client app ID if available
            if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
            {
                graphApiService.CustomClientAppId = setupConfig.ClientAppId;
            }

            if (dryRun)
            {
                // Read scopes from ToolingManifest.json
                var manifestPath = Path.Combine(setupConfig.DeploymentProjectPath ?? string.Empty, McpConstants.ToolingManifestFileName);
                var toolingScopes = await ManifestHelper.GetRequiredScopesAsync(manifestPath);

                logger.LogInformation("DRY RUN: Configure MCP Permissions");
                logger.LogInformation("Would configure OAuth2 grants and inheritable permissions:");
                logger.LogInformation("  - Blueprint: {BlueprintId}", setupConfig.AgentBlueprintId);
                logger.LogInformation("  - Resource: Agent 365 Tools ({Environment})", setupConfig.Environment);
                logger.LogInformation("  - Scopes: {Scopes}", string.Join(", ", toolingScopes));
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
                false);

        }, configOption, verboseOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Bot API permissions subcommand
    /// </summary>
    private static Command CreateBotSubcommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService)
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

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Show detailed output");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            description: "Show what would be done without executing");

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);

        command.SetHandler(async (config, verbose, dryRun) =>
        {
            var setupConfig = await configService.LoadAsync(config.FullName);

            if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
            {
                logger.LogError("Blueprint ID not found. Run 'a365 setup blueprint' first.");
                Environment.Exit(1);
            }

            // Configure GraphApiService with custom client app ID if available
            if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
            {
                graphApiService.CustomClientAppId = setupConfig.ClientAppId;
            }

            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Configure Bot API Permissions");
                logger.LogInformation("Would configure Bot API permissions:");
                logger.LogInformation("  - Blueprint: {BlueprintId}", setupConfig.AgentBlueprintId);
                logger.LogInformation("  - Messaging Bot API: Authorization.ReadWrite, user_impersonation");
                logger.LogInformation("  - Observability API: user_impersonation");
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

        }, configOption, verboseOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Custom blueprint permissions subcommand
    /// </summary>
    private static Command CreateCustomSubcommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService)
    {
        var command = new Command("custom",
            "Configure custom resource OAuth2 grants and inheritable permissions\n" +
            "Minimum required permissions: Global Administrator\n\n" +
            "Prerequisites: Blueprint created (run 'a365 setup blueprint' first)\n");

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

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);

        command.SetHandler(async (config, verbose, dryRun) =>
        {
            var setupConfig = await configService.LoadAsync(config.FullName);

            if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
            {
                logger.LogError("Blueprint ID not found. Run 'a365 setup blueprint' first.");
                Environment.Exit(1);
            }

            // Configure GraphApiService with custom client app ID if available
            if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
            {
                graphApiService.CustomClientAppId = setupConfig.ClientAppId;
            }

            if (dryRun)
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
                return;
            }

            await ConfigureCustomPermissionsAsync(
                config.FullName,
                logger,
                configService,
                executor,
                graphApiService,
                blueprintService,
                setupConfig,
                false);

        }, configOption, verboseOption, dryRunOption);

        return command;
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
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("");
        logger.LogInformation("Configuring MCP server permissions...");
        logger.LogInformation("");

        try
        {
            // Read scopes from ToolingManifest.json
            var manifestPath = Path.Combine(setupConfig.DeploymentProjectPath ?? string.Empty, McpConstants.ToolingManifestFileName);
            var toolingScopes = await ManifestHelper.GetRequiredScopesAsync(manifestPath);

            var resourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(setupConfig.Environment);

            // Configure all permissions using unified method
            await SetupHelpers.EnsureResourcePermissionsAsync(
                graphApiService,
                blueprintService,
                setupConfig,
                resourceAppId,
                "Agent 365 Tools",
                toolingScopes,
                logger,
                addToRequiredResourceAccess: false,
                setInheritablePermissions: true,
                setupResults,
                cancellationToken);

            logger.LogInformation("");
            logger.LogInformation("MCP server permissions configured successfully");
            logger.LogInformation("");
            if (!iSetupAll)
            {
                logger.LogInformation("Next step: 'a365 setup permissions bot' to configure Bot API permissions");
            }

            // write changes to generated config
            await configService.SaveStateAsync(setupConfig);
            return true;
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
        logger.LogInformation("");
        logger.LogInformation("Configuring Messaging Bot API permissions...");
        logger.LogInformation("");

        try
        {
            // Configure Messaging Bot API permissions using unified method
            // Note: Messaging Bot API is a first-party Microsoft service with custom OAuth2 scopes
            // that are not published in the standard service principal permissions.
            // We skip addToRequiredResourceAccess because the scopes won't be found there.
            // The permissions appear in the portal via OAuth2 grants and inheritable permissions.
            await SetupHelpers.EnsureResourcePermissionsAsync(
                graphService,
                blueprintService,
                setupConfig,
                ConfigConstants.MessagingBotApiAppId,
                "Messaging Bot API",
                new[] { "Authorization.ReadWrite", "user_impersonation" },
                logger,
                addToRequiredResourceAccess: false,
                setInheritablePermissions: true,
                setupResults,
                cancellationToken);

            // Configure Observability API permissions using unified method
            // Note: Observability API is also a first-party Microsoft service
            await SetupHelpers.EnsureResourcePermissionsAsync(
                graphService,
                blueprintService,
                setupConfig,
                ConfigConstants.ObservabilityApiAppId,
                "Observability API",
                new[] { "user_impersonation" },
                logger,
                addToRequiredResourceAccess: false,
                setInheritablePermissions: true,
                setupResults,
                cancellationToken);

            // Configure Power Platform API permissions using unified method
            // Note: Using the MOS Power Platform API (8578e004-a5c6-46e7-913e-12f58912df43) which is
            // the Power Platform API for agent operations. This API exposes Connectivity.Connections.Read
            // for reading Power Platform connections.
            // Similar to Messaging Bot API, we skip addToRequiredResourceAccess because the scopes
            // won't be found in the standard service principal permissions.
            // The permissions appear in the portal via OAuth2 grants and inheritable permissions.
            await SetupHelpers.EnsureResourcePermissionsAsync(
                graphService,
                blueprintService,
                setupConfig,
                MosConstants.PowerPlatformApiResourceAppId,
                "Power Platform API",
                new[] { "Connectivity.Connections.Read" },
                logger,
                addToRequiredResourceAccess: false,
                setInheritablePermissions: true,
                setupResults,
                cancellationToken);

            // write changes to generated config
            await configService.SaveStateAsync(setupConfig);

            logger.LogInformation("");
            logger.LogInformation("Bot API permissions configured successfully");
            logger.LogInformation("");
            if (!iSetupAll)
            {
                logger.LogInformation("Next step: Deploy your agent (run 'a365 deploy' if hosting on Azure)");
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to configure Bot API permissions: {Message}", ex.Message);
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
    private static async Task RemoveStaleCustomPermissionsAsync(
        ILogger logger,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        Models.Agent365Config setupConfig,
        HashSet<string> desiredCustomIds,
        CancellationToken cancellationToken)
    {
        // Resource app IDs owned by standard setup subcommands — never remove these
        var protectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ConfigConstants.GetAgent365ToolsResourceAppId(setupConfig.Environment),
            ConfigConstants.MessagingBotApiAppId,
            ConfigConstants.ObservabilityApiAppId,
            MosConstants.PowerPlatformApiResourceAppId,
            AuthenticationConstants.MicrosoftGraphResourceAppId,
        };

        var requiredPermissions = new[] { "AgentIdentityBlueprint.UpdateAuthProperties.All", "Application.ReadWrite.All" };

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
                logger.LogInformation("No custom blueprint permissions configured.");
                await configService.SaveStateAsync(setupConfig);
                return true;
            }

            var hasValidationFailures = false;
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
                            // Fallback if lookup fails - use safe helper method
                            customPerm.ResourceName = CreateFallbackResourceName(customPerm.ResourceAppId);
                            logger.LogWarning("  - Could not resolve resource name, using fallback: {ResourceName}",
                                customPerm.ResourceName);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Fallback if lookup fails - use safe helper method
                        customPerm.ResourceName = CreateFallbackResourceName(customPerm.ResourceAppId);
                        logger.LogWarning(ex, "  - Failed to auto-resolve resource name: {Message}. Using fallback: {ResourceName}",
                            ex.Message, customPerm.ResourceName);
                    }
                }

                logger.LogInformation("Configuring {ResourceName} ({ResourceAppId})...",
                    customPerm.ResourceName, customPerm.ResourceAppId);

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

                // Use the same unified method as standard permissions
                // Note: Agent Blueprints don't support requiredResourceAccess via v1.0 API
                // (same limitation as CopilotStudio and MCP permissions)
                await SetupHelpers.EnsureResourcePermissionsAsync(
                    graphApiService,
                    blueprintService,
                    setupConfig,
                    customPerm.ResourceAppId,
                    customPerm.ResourceName,
                    customPerm.Scopes.ToArray(),
                    logger,
                    addToRequiredResourceAccess: false,  // Skip requiredResourceAccess - not supported for Agent Blueprints
                    setInheritablePermissions: true,      // Inheritable permissions work correctly
                    setupResults,
                    cancellationToken);

                logger.LogInformation("  - {ResourceName} configured successfully",
                    customPerm.ResourceName);
            }

            logger.LogInformation("");
            if (hasValidationFailures)
                logger.LogWarning("Custom blueprint permissions completed with validation failures — check errors above");
            else
                logger.LogInformation("Custom blueprint permissions configured successfully");
            logger.LogInformation("");

            // Save dynamic state changes to the generated config (CustomBlueprintPermissions is not persisted here)
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

            // Only log when handling the error here (standalone command)
            logger.LogError(ex, "Failed to configure custom blueprint permissions: {Message}", ex.Message);
            return false;
        }
    }
}
