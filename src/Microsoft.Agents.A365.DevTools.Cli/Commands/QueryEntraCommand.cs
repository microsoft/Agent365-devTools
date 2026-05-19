// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// QueryEntra command - Query Microsoft Entra ID for agent-related information
/// </summary>
public class QueryEntraCommand
{
    public static Command CreateCommand(
        ILogger<QueryEntraCommand> logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command("query-entra", "Query Microsoft Entra ID for agent information (scopes, permissions, consent status)");

        // Add subcommands for different query types
        command.AddCommand(CreateBlueprintScopesSubcommand(logger, configService, executor, graphApiService, blueprintService, resolver));
        command.AddCommand(CreateInstanceScopesSubcommand(logger, configService, executor, resolver));
        command.AddCommand(CreateInheritanceSubcommand(logger, configService, graphApiService, blueprintService, resolver));

        return command;
    }

    /// <summary>
    /// Create inheritance subcommand to verify that inheritable permissions on the blueprint
    /// use kind=allAllowed for both scopes and roles on every configured resource. This is the
    /// configuration-level check; it does not acquire a token or inspect runtime claims.
    /// </summary>
    private static Command CreateInheritanceSubcommand(
        ILogger<QueryEntraCommand> logger,
        IConfigService configService,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command("inheritance", "Verify the blueprint's inheritablePermissions are set to kind=allAllowed for both scopes and roles");

        var agentNameOption = new Option<string?>(
            ["--agent-name", "-n"],
            description: "Agent base name. When provided, no config file is required.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Overrides auto-detection. Use with --agent-name.");

        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var configFile = new FileInfo("a365.config.json");
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var ct = context.GetCancellationToken();
            try
            {
                // Read-only diagnostic. We pass isCleanupMode: true so the resolver also looks up
                // the AgentBlueprintId from Entra (its display name resolution path) when only
                // --agent-name is supplied. The flag name is misleading — it does NOT do any
                // cleanup-style mutation; it only enables the extra blueprint/registration ID
                // resolution that this command needs to query the blueprint.
                Agent365Config? setupConfig = resolver != null
                    ? await resolver.ResolveAsync(agentName, tenantIdFlag, configFile, isCleanupMode: true, ct)
                    : await LoadConfigAsync(configFile, logger, configService);

                if (setupConfig == null)
                {
                    logger.LogError("Failed to load configuration");
                    context.ExitCode = 1;
                    return;
                }

                if (string.IsNullOrEmpty(setupConfig.AgentBlueprintId))
                {
                    logger.LogError("Agent Blueprint ID not found in configuration. Run 'a365 setup blueprint' first.");
                    context.ExitCode = 1;
                    return;
                }

                if (string.IsNullOrEmpty(setupConfig.TenantId))
                {
                    logger.LogError("Tenant ID not found in configuration.");
                    context.ExitCode = 1;
                    return;
                }

                logger.LogInformation("Inheritable permissions for blueprint {BlueprintId}", setupConfig.AgentBlueprintId);
                logger.LogInformation("");

                var entries = await blueprintService.ListInheritablePermissionsAsync(
                    setupConfig.TenantId, setupConfig.AgentBlueprintId, requiredScopes: null, ct);

                if (entries.Count == 0)
                {
                    logger.LogWarning("No inheritable permissions configured for this blueprint.");
                    logger.LogInformation("Run 'a365 setup permissions' to configure them.");
                    context.ExitCode = 1;
                    return;
                }

                // Under kind=allAllowed the entry itself doesn't list scopes — what's actually inherited
                // is whatever is granted on the blueprint SP for that resource. Fetch those grants so the
                // operator can see the real permission names that agent identities will receive.
                var grantsByResource = await blueprintService.GetBlueprintSpGrantsAsync(
                    setupConfig.TenantId,
                    setupConfig.AgentBlueprintId,
                    entries.Select(e => e.ResourceAppId),
                    requiredScopes: null,
                    ct);

                // Effective inheritance requires BOTH the right config (kind=allAllowed) AND
                // permissions actually granted on the blueprint SP for the resource. Showing only
                // the config status is misleading: a kind=allAllowed entry with zero granted
                // permissions inherits nothing. Compute and surface effective state per resource.
                var effectiveCount = 0;
                foreach (var entry in entries)
                {
                    var resourceName = await ResolveResourceNameAsync(entry.ResourceAppId, graphApiService, setupConfig.TenantId, logger, ct);
                    logger.LogInformation("Resource: {ResourceName} ({ResourceAppId})", resourceName, entry.ResourceAppId);

                    var hasGrantInfo = grantsByResource.TryGetValue(entry.ResourceAppId, out var grants);
                    var hasDelegatedGrants = hasGrantInfo && grants.DelegatedScopes.Length > 0;
                    var hasAppRoleGrants = hasGrantInfo && grants.AppRoleNames.Length > 0;
                    var hasAnyGrants = hasDelegatedGrants || hasAppRoleGrants;

                    // Scopes line — OK only when kind=allAllowed AND something delegated is granted.
                    if (entry.ScopesAllAllowed && hasDelegatedGrants)
                        logger.LogInformation("  Scopes: OK   (kind=allAllowed, delegated permissions granted on blueprint SP)");
                    else if (entry.ScopesAllAllowed && !hasDelegatedGrants)
                        logger.LogWarning("  Scopes: WARN (kind=allAllowed BUT no delegated permissions granted on blueprint SP — inheritance has nothing to inherit)");
                    else
                        logger.LogWarning("  Scopes: WARN (kind is not allAllowed — legacy enumerated entry; re-run 'a365 setup permissions' to reconcile)");

                    // Roles line — OK only when kind=allAllowed AND something app-role-side is granted.
                    if (entry.RolesAllAllowed && hasAppRoleGrants)
                        logger.LogInformation("  Roles:  OK   (kind=allAllowed, app roles granted on blueprint SP)");
                    else if (entry.RolesAllAllowed && !hasAppRoleGrants)
                        logger.LogWarning("  Roles:  WARN (kind=allAllowed BUT no app roles granted on blueprint SP — inheritance has nothing to inherit)");
                    else
                        logger.LogWarning("  Roles:  WARN (kind is not allAllowed — app role inheritance is not active; re-run 'a365 setup permissions' to reconcile)");

                    // Always render the granted lists so the user can see exactly what's on the blueprint SP.
                    if (hasGrantInfo)
                    {
                        logger.LogInformation("    Granted delegated scopes: {Scopes}",
                            hasDelegatedGrants ? string.Join(", ", grants.DelegatedScopes) : "(none)");
                        logger.LogInformation("    Granted app roles:        {Roles}",
                            hasAppRoleGrants ? string.Join(", ", grants.AppRoleNames) : "(none)");
                    }
                    else
                    {
                        logger.LogInformation("    Granted permissions:      (resource service principal not found in tenant)");
                    }

                    // Effective inheritance per resource: kind=allAllowed on both AND at least one
                    // grant exists on the blueprint SP for that resource. Anything else is broken
                    // from the agent identity's perspective.
                    var effective = entry.ScopesAllAllowed && entry.RolesAllAllowed && hasAnyGrants;
                    if (effective)
                    {
                        logger.LogInformation("  Effective inheritance: OK");
                        effectiveCount++;
                    }
                    else if (entry.ScopesAllAllowed && entry.RolesAllAllowed && !hasAnyGrants)
                    {
                        logger.LogWarning("  Effective inheritance: NONE — kind is configured but blueprint SP has no granted permissions for this resource. Agent identities created from this blueprint will not inherit anything for this resource. Run 'a365 setup permissions' as a Global Administrator to grant on the blueprint SP.");
                    }
                    else
                    {
                        logger.LogWarning("  Effective inheritance: BROKEN — kind is not allAllowed for one or both sides. Re-run 'a365 setup permissions' to reconcile.");
                    }

                    logger.LogInformation("");
                }

                logger.LogInformation("Summary: {EffectiveCount} of {TotalCount} resource(s) have effective inheritance (kind=allAllowed on both sides AND at least one permission granted on the blueprint SP).",
                    effectiveCount, entries.Count);

                if (effectiveCount < entries.Count)
                {
                    logger.LogInformation("");
                    logger.LogInformation("To reconcile: run 'a365 setup permissions' as a Global Administrator. " +
                        "If you ran it already and grants are still missing, your token may be missing the 'wids' optional claim — run 'a365 setup requirements' to check.");
                    context.ExitCode = 1;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to query inheritable permissions: {Message}", ex.Message);
                context.ExitCode = 1;
            }
        });

        return command;
    }

    /// <summary>
    /// Create blueprint-scopes subcommand to query Entra ID for blueprint scopes and consent status
    /// </summary>
    private static Command CreateBlueprintScopesSubcommand(
        ILogger<QueryEntraCommand> logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command("blueprint-scopes", "List delegated and application permissions currently granted on the agent blueprint service principal (the view shown in the Entra portal 'API permissions' blade)");

        var agentNameOption = new Option<string?>(
            ["--agent-name", "-n"],
            description: "Agent base name. When provided, no config file is required.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Overrides auto-detection. Use with --agent-name.");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Enable verbose logging");

        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(verboseOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var configFile = new FileInfo("a365.config.json");
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            _ = context.ParseResult.GetValueForOption(verboseOption);
            var ct = context.GetCancellationToken();
            try
            {
                logger.LogInformation("Querying Entra ID for agent blueprint granted permissions...");

                Agent365Config? setupConfig;
                if (resolver != null)
                    setupConfig = await resolver.ResolveAsync(agentName, tenantIdFlag, configFile, isCleanupMode: true, ct);
                else
                    setupConfig = await LoadConfigAsync(configFile, logger, configService);
                if (setupConfig == null)
                {
                    logger.LogError("Failed to load configuration");
                    context.ExitCode = 1;
                    return;
                }

                if (string.IsNullOrEmpty(setupConfig.AgentBlueprintId))
                {
                    logger.LogError("Agent Blueprint ID not found in configuration. Please run 'a365 setup blueprint' first.");
                    logger.LogInformation("The blueprint must be created before you can query its scopes.");
                    context.ExitCode = 1;
                    return;
                }

                if (string.IsNullOrEmpty(setupConfig.TenantId))
                {
                    logger.LogError("Tenant ID not found in configuration.");
                    context.ExitCode = 1;
                    return;
                }

                logger.LogInformation("Agent Blueprint ID: {BlueprintId}", setupConfig.AgentBlueprintId);
                logger.LogInformation("");

                // Query Microsoft Graph for the permissions actually GRANTED on the blueprint
                // service principal — i.e. oauth2PermissionGrants (delegated) + appRoleAssignments
                // (application). This is what the Entra portal "API permissions" blade shows under
                // "granted for {tenant}", and it is the authoritative answer to "what permissions
                // does the blueprint currently hold?". Setup intentionally does NOT populate the
                // blueprint app's requiredResourceAccess (see BatchPermissionsOrchestrator), so
                // requiredResourceAccess would be empty even though grants exist.
                //
                // We seed the resource list from inheritablePermissions because that is the set
                // setup configures. Resources outside that set would not be touched by `a365 setup`
                // anyway, and aligning with `a365 query-entra inheritance` keeps both subcommands
                // showing the same resource universe.
                logger.LogInformation("Querying Microsoft Graph API for blueprint granted permissions...");

                var entries = await blueprintService.ListInheritablePermissionsAsync(
                    setupConfig.TenantId, setupConfig.AgentBlueprintId, requiredScopes: null, ct);

                if (entries.Count == 0)
                {
                    logger.LogWarning("No inheritable permissions configured for this blueprint - the blueprint has no resource list to enumerate.");
                    logger.LogInformation("Run 'a365 setup permissions' to configure them.");
                    context.ExitCode = 1;
                    return;
                }

                var grantsByResource = await blueprintService.GetBlueprintSpGrantsAsync(
                    setupConfig.TenantId,
                    setupConfig.AgentBlueprintId,
                    entries.Select(e => e.ResourceAppId),
                    requiredScopes: null,
                    ct);

                logger.LogInformation("Blueprint Granted Permissions:");
                logger.LogInformation("==============================");
                logger.LogInformation("");

                var totalDelegated = 0;
                var totalApplication = 0;
                var resourcesWithAnyPermission = 0;
                foreach (var entry in entries)
                {
                    var resourceName = await ResolveResourceNameAsync(entry.ResourceAppId, graphApiService, setupConfig.TenantId, logger, ct);
                    logger.LogInformation("Resource: {ResourceName} ({ResourceAppId})", resourceName, entry.ResourceAppId);

                    grantsByResource.TryGetValue(entry.ResourceAppId, out var grants);
                    var delegated = grants.DelegatedScopes ?? Array.Empty<string>();
                    var application = grants.AppRoleNames ?? Array.Empty<string>();

                    var delegatedSorted = delegated.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
                    var applicationSorted = application.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();

                    logger.LogInformation("  Delegated permissions ({Count}): {Names}",
                        delegatedSorted.Length,
                        delegatedSorted.Length > 0 ? string.Join(", ", delegatedSorted) : "(none)");
                    logger.LogInformation("  Application permissions ({Count}): {Names}",
                        applicationSorted.Length,
                        applicationSorted.Length > 0 ? string.Join(", ", applicationSorted) : "(none)");

                    totalDelegated += delegatedSorted.Length;
                    totalApplication += applicationSorted.Length;
                    if (delegatedSorted.Length > 0 || applicationSorted.Length > 0) resourcesWithAnyPermission++;
                    logger.LogInformation("");
                }

                logger.LogInformation("Summary: {WithGrants} of {Total} resource(s) have at least one granted permission on the blueprint SP. Total delegated: {Delegated}. Total application: {Application}.",
                    resourcesWithAnyPermission, entries.Count, totalDelegated, totalApplication);
                logger.LogInformation("");
                logger.LogInformation("To see whether agent identities created from this blueprint will inherit these permissions, run: a365 query-entra inheritance");
                logger.LogInformation("To manage blueprint permissions, visit:");
                logger.LogInformation("https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/{AppId}", setupConfig.AgentBlueprintId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to query blueprint granted permissions: {Message}", ex.Message);
                context.ExitCode = 1;
            }
        });

        return command;
    }

    /// <summary>
    /// Create instance-scopes subcommand to query Entra ID for instance scopes and consent status
    /// </summary>
    private static Command CreateInstanceScopesSubcommand(
        ILogger<QueryEntraCommand> logger,
        IConfigService configService,
        CommandExecutor executor,
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command("instance-scopes", "List configured scopes and consent status for the agent instance");

        var agentNameOption = new Option<string?>(
            ["--agent-name", "-n"],
            description: "Agent base name. When provided, no config file is required.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Overrides auto-detection. Use with --agent-name.");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Enable verbose logging");

        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(verboseOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var configFile = new FileInfo("a365.config.json");
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            _ = context.ParseResult.GetValueForOption(verboseOption);
            var ct = context.GetCancellationToken();
            try
            {
                logger.LogInformation("Querying Entra ID for agent instance scopes and consent status...");

                Agent365Config? instanceConfig;
                if (resolver != null)
                    instanceConfig = await resolver.ResolveAsync(agentName, tenantIdFlag, configFile, isCleanupMode: true, ct);
                else
                    instanceConfig = await LoadConfigAsync(configFile, logger, configService);
                if (instanceConfig == null)
                {
                    logger.LogError("Failed to load configuration");
                    context.ExitCode = 1;
                    return;
                }

                // Check for agent identity (could be AgentBlueprintId or specific instance identity)
                string? agenticAppId = null;
                string identityType = "";

                if (!string.IsNullOrEmpty(instanceConfig.AgenticAppId))
                {
                    agenticAppId = instanceConfig.AgenticAppId;
                    identityType = "Agent Identity";
                }
                else if (!string.IsNullOrEmpty(instanceConfig.AgentBlueprintId))
                {
                    agenticAppId = instanceConfig.AgentBlueprintId;
                    identityType = "Agent Blueprint";
                }
                else
                {
                    logger.LogError("No agent identity found in configuration. Please create an agent instance first.");
                    logger.LogInformation("An agent identity must be created before you can query OAuth2 grants.");
                    context.ExitCode = 1;
                    return;
                }

                logger.LogInformation("{IdentityType} ID: {IdentityId}", identityType, agenticAppId);
                logger.LogInformation("");

                // Query Entra ID for the agent identity and OAuth2 grants
                logger.LogInformation("Querying Microsoft Entra ID for agent identity and OAuth2 grants...");
                
                // Get the service principal details for this application  
                var spResult = await executor.ExecuteAsync("az", 
                    $"ad sp list --filter \"appId eq '{agenticAppId}'\" --query \"[].{{objectId:id,appId:appId,displayName:displayName}}\" --output json");

                if (!spResult.Success)
                {
                    logger.LogError("Failed to query service principal: {Error}", spResult.StandardError);
                    logger.LogInformation("Make sure you are logged in with 'az login' and have permission to read the application.");
                    context.ExitCode = 1;
                    return;
                }

                using var spDoc = JsonDocument.Parse(spResult.StandardOutput);
                
                if (spDoc.RootElement.ValueKind != JsonValueKind.Array || spDoc.RootElement.GetArrayLength() == 0)
                {
                    logger.LogWarning("No service principal found for this application. The app may not be installed in this tenant.");
                    context.ExitCode = 1;
                    return;
                }
                
                var spElement = spDoc.RootElement[0]; // Get the first (and only) service principal
                var displayName = spElement.TryGetProperty("displayName", out var nameElement) ? nameElement.GetString() : "Unknown";
                var appId = spElement.TryGetProperty("appId", out var appIdElement) ? appIdElement.GetString() : agenticAppId;
                
                logger.LogInformation("Application: {DisplayName}", displayName);
                logger.LogInformation("App ID: {AppId}", appId);
                
                if (!string.IsNullOrEmpty(instanceConfig.AgentUserPrincipalName))
                {
                    logger.LogInformation("Agent User: {AgentUserPrincipalName}", instanceConfig.AgentUserPrincipalName);
                }
                logger.LogInformation("");

                // Query OAuth2 permission grants for this service principal
                logger.LogInformation("OAuth2 Permission Grants (Admin Consented):");
                logger.LogInformation("============================================");
                
                // Use Microsoft Graph API through Azure CLI to get OAuth2 permission grants
                var grantsResult = await executor.ExecuteAsync("az",
                    $"rest --method GET --url \"https://graph.microsoft.com/v1.0/oauth2PermissionGrants?$filter=clientId eq '{agenticAppId}'\" --output json");

                // Distinguish "API call failed" (can't read) from "API succeeded but returned no grants".
                // Non-admin developers lack DelegatedPermissionGrant.Read.All and always get a failure here —
                // claiming "admin consent has not been granted" is a false negative in that case.
                bool grantsReadable = grantsResult.Success && !string.IsNullOrWhiteSpace(grantsResult.StandardOutput);

                bool hasGrants = false;
                if (grantsReadable)
                {
                    try
                    {
                        using var grantsDoc = JsonDocument.Parse(grantsResult.StandardOutput);
                        if (grantsDoc.RootElement.TryGetProperty("value", out var valueElement) &&
                            valueElement.ValueKind == JsonValueKind.Array && valueElement.GetArrayLength() > 0)
                        {
                            hasGrants = true;
                            
                            foreach (var grantElement in valueElement.EnumerateArray())
                            {
                                var scope = grantElement.TryGetProperty("scope", out var scopeElement) ? scopeElement.GetString() : "Unknown";
                                var resourceId = grantElement.TryGetProperty("resourceId", out var resourceIdElement) ? resourceIdElement.GetString() : "Unknown";
                                
                                // Get the resource display name using Graph API
                                var resourceResult = await executor.ExecuteAsync("az", 
                                    $"rest --method GET --url \"https://graph.microsoft.com/v1.0/servicePrincipals/{resourceId}?$select=displayName,appId\" --output json");
                                
                                string resourceName = "Unknown Resource";
                                string resourceAppId = "Unknown";
                                
                                if (resourceResult.Success)
                                {
                                    try
                                    {
                                        using var resourceDoc = JsonDocument.Parse(resourceResult.StandardOutput);
                                        resourceName = resourceDoc.RootElement.TryGetProperty("displayName", out var resNameElement) ? resNameElement.GetString() ?? "Unknown" : "Unknown";
                                        resourceAppId = resourceDoc.RootElement.TryGetProperty("appId", out var resAppIdElement) ? resAppIdElement.GetString() ?? resourceAppId : resourceAppId;
                                        
                                        // Prefer the well-known name when one is registered; otherwise keep
                                        // the displayName already fetched from Graph above.
                                        var wellKnownName = GetWellKnownResourceName(resourceAppId);
                                        if (!string.IsNullOrWhiteSpace(wellKnownName))
                                        {
                                            resourceName = wellKnownName;
                                        }
                                    }
                                    catch
                                    {
                                        // Use fallback if parsing fails
                                    }
                                }
                                
                                logger.LogInformation("Resource: {ResourceName}", resourceName);
                                if (!string.IsNullOrWhiteSpace(scope))
                                {
                                    var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                    foreach (var individualScope in scopes)
                                    {
                                        logger.LogInformation("  {Scope}", individualScope);
                                    }
                                }
                                else
                                {
                                    logger.LogInformation("    No specific scopes granted");
                                }
                                logger.LogInformation("");
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        logger.LogWarning("Failed to parse OAuth2 grants response: {Error}", ex.Message);
                    }
                }

                if (!hasGrants)
                {
                    if (!grantsReadable)
                    {
                        logger.LogInformation("    Cannot read tenant-wide OAuth2 permission grants from the current credentials.");
                        logger.LogInformation("    (Reading grants requires the admin-only DelegatedPermissionGrant.Read.All scope; the other information shown above does not.)");
                        logger.LogInformation("    To verify consent status, sign in as a tenant administrator and re-run, or inspect the app in the Entra portal:");
                        logger.LogInformation("    https://portal.azure.com -> Entra ID -> App registrations -> {DisplayName} -> API permissions", displayName);
                    }
                    else
                    {
                        logger.LogInformation("    No OAuth2 permission grants found");
                        logger.LogInformation("    This means admin consent has not been granted for any API permissions");
                        logger.LogInformation("");
                        logger.LogInformation("To grant admin consent:");
                        logger.LogInformation("  1. Visit the Azure portal: https://portal.azure.com");
                        logger.LogInformation("  2. Go to Entra ID > App registrations");
                        logger.LogInformation("  3. Find your application: {DisplayName}", displayName);
                        logger.LogInformation("  4. Go to API permissions and click 'Grant admin consent'");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to query instance scopes: {Message}", ex.Message);
                context.ExitCode = 1;
            }
        });

        return command;
    }

    /// <summary>
    /// Load configuration from file using the config service
    /// </summary>
    private static async Task<Agent365Config?> LoadConfigAsync(
        FileInfo config, 
        ILogger<QueryEntraCommand> logger, 
        IConfigService configService)
    {
        try
        {
            return await configService.LoadAsync(config.FullName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load configuration from {Path}: {Message}", config.FullName, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Returns a stable, hard-coded display name for known resource app IDs. Returns null for any
    /// app ID not in this list — callers should fall back to a live Entra lookup via
    /// <see cref="ResolveResourceNameAsync"/>. Kept in sync with the resources the CLI configures
    /// on agent blueprints (Observability, Power Platform, Messaging Bot, Microsoft Graph, AAD).
    /// </summary>
    private static string? GetWellKnownResourceName(string? resourceAppId)
    {
        return resourceAppId switch
        {
            null or "" => null,
            AuthenticationConstants.MicrosoftGraphResourceAppId => "Microsoft Graph",
            ConfigConstants.MessagingBotApiAppId => "Messaging Bot API",
            ConfigConstants.ObservabilityApiAppId => "Observability API",
            PowerPlatformConstants.PowerPlatformApiResourceAppId => "Power Platform API",
            "00000002-0000-0000-c000-000000000000" => "Azure Active Directory Graph",
            "797f4846-ba00-4fd7-ba43-dac1f8f63013" => "Azure Service Management",
            "00000001-0000-0000-c000-000000000000" => "Azure ESTS Service",
            _ => null
        };
    }

    /// <summary>
    /// Resolves a friendly resource name for display. Checks the well-known list first (no Graph
    /// call), then falls back to fetching the service principal's <c>displayName</c> from Entra.
    /// If the resource SP isn't in the tenant or Graph fails, returns "Unknown Resource" so the
    /// caller can render a meaningful row alongside the GUID (which is logged separately).
    /// </summary>
    private static async Task<string> ResolveResourceNameAsync(
        string? resourceAppId,
        GraphApiService graphService,
        string tenantId,
        ILogger logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(resourceAppId)) return "Unknown Resource";

        var wellKnown = GetWellKnownResourceName(resourceAppId);
        if (!string.IsNullOrWhiteSpace(wellKnown)) return wellKnown!;

        try
        {
            var displayName = await graphService.GetServicePrincipalDisplayNameByAppIdAsync(tenantId, resourceAppId, ct);
            if (!string.IsNullOrWhiteSpace(displayName)) return displayName!;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to resolve resource name for {AppId} via Graph; falling back to 'Unknown Resource'.", resourceAppId);
        }

        return "Unknown Resource";
    }
}