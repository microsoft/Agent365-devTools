// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.DevelopSubcommands;

/// <summary>
/// Executor for the add-byo-scopes subcommand.
/// Grants oauth2PermissionGrants so agent instances can access BYO MCP server PPMI apps.
/// </summary>
internal sealed class AddByoScopesExecutor
{
    private readonly ILogger _logger;
    private readonly IAgent365ToolingService _toolingService;
    private readonly AgentBlueprintService _blueprintService;
    private readonly GraphApiService _graphApiService;

    private const string AllPrincipalsConsentType = "AllPrincipals";
    private const string DefaultScope = "user_impersonation";

    public AddByoScopesExecutor(
        ILogger logger,
        IAgent365ToolingService toolingService,
        AgentBlueprintService blueprintService,
        GraphApiService graphApiService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _toolingService = toolingService ?? throw new ArgumentNullException(nameof(toolingService));
        _blueprintService = blueprintService ?? throw new ArgumentNullException(nameof(blueprintService));
        _graphApiService = graphApiService ?? throw new ArgumentNullException(nameof(graphApiService));
    }

    /// <summary>
    /// Executes the add-byo-scopes command.
    /// </summary>
    public async Task<bool> ExecuteAsync(
        string serverNamesRaw,
        string? blueprintId,
        string? agentInstancesRaw,
        string? tenantId,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        // Validate at least one of --blueprint-id or --agent-instances
        if (string.IsNullOrWhiteSpace(blueprintId) && string.IsNullOrWhiteSpace(agentInstancesRaw))
        {
            _logger.LogError("At least one of --blueprint-id or --agent-instances is required");
            return false;
        }

        if (string.IsNullOrWhiteSpace(serverNamesRaw))
        {
            _logger.LogError("--server-names is required");
            return false;
        }

        // Parse server names
        var serverNames = serverNamesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (serverNames.Length == 0)
        {
            _logger.LogError("No valid server names provided");
            return false;
        }

        // Validate GUID formats
        if (!string.IsNullOrWhiteSpace(blueprintId) && !Guid.TryParse(blueprintId, out _))
        {
            _logger.LogError("Invalid blueprint ID format: {BlueprintId}. Must be a valid GUID.", blueprintId);
            return false;
        }

        // Resolve tenant ID
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            tenantId = await ResolveTenantIdAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                _logger.LogError("Could not determine tenant ID. Provide --tenant-id or sign in with 'az login'.");
                return false;
            }
        }
        else if (!Guid.TryParse(tenantId, out _))
        {
            _logger.LogError("Invalid tenant ID format: {TenantId}. Must be a valid GUID.", tenantId);
            return false;
        }

        // Resolve agent instances
        var instanceSpIds = await ResolveAgentInstancesAsync(blueprintId, agentInstancesRaw, tenantId, cancellationToken);
        if (instanceSpIds == null || instanceSpIds.Count == 0)
        {
            _logger.LogError("No agent instances resolved. Verify your --blueprint-id or --agent-instances values.");
            return false;
        }

        _logger.LogInformation("Resolved {Count} agent instance(s)", instanceSpIds.Count);

        if (dryRun)
        {
            _logger.LogInformation("[DRY RUN] Would create oauth2PermissionGrants for:");
            foreach (var serverName in serverNames)
            {
                _logger.LogInformation("  Server: {ServerName}", serverName);
            }
            _logger.LogInformation("  Against {Count} agent instance(s):", instanceSpIds.Count);
            foreach (var spId in instanceSpIds)
            {
                _logger.LogInformation("    {SpId}", spId);
            }
            return true;
        }

        // Process each server
        var totalGranted = 0;
        var totalSkipped = 0;
        var totalFailed = 0;

        foreach (var serverName in serverNames)
        {
            _logger.LogInformation("Processing server: {ServerName}...", serverName);

            // Step 1: Get the PPMI app ID from MCP Platform
            var appIdResponse = await _toolingService.GetMcpServerAppIdByNameAsync(serverName, cancellationToken);
            if (appIdResponse == null || string.IsNullOrWhiteSpace(appIdResponse.McpServerAppId))
            {
                _logger.LogError("Failed to get app ID for server '{ServerName}'. Skipping.", serverName);
                totalFailed += instanceSpIds.Count;
                continue;
            }

            var mcpServerAppId = appIdResponse.McpServerAppId;
            _logger.LogDebug("MCP server '{ServerName}' PPMI app ID: {AppId}", serverName, mcpServerAppId);

            // Step 2: Resolve the PPMI app's service principal in the tenant
            var ppmiSpId = await ResolveServicePrincipalIdAsync(tenantId, mcpServerAppId, cancellationToken);
            if (string.IsNullOrWhiteSpace(ppmiSpId))
            {
                _logger.LogError("Could not find service principal for PPMI app {AppId} in tenant {TenantId}. Skipping server '{ServerName}'.",
                    mcpServerAppId, tenantId, serverName);
                totalFailed += instanceSpIds.Count;
                continue;
            }

            _logger.LogDebug("PPMI service principal ID: {SpId}", ppmiSpId);

            // Step 3: Create/update oauth2PermissionGrant for each agent instance
            foreach (var instanceSpId in instanceSpIds)
            {
                var result = await UpsertDelegatedGrantAsync(tenantId, instanceSpId, ppmiSpId, DefaultScope, cancellationToken);
                switch (result)
                {
                    case GrantResult.Created:
                    case GrantResult.Updated:
                        totalGranted++;
                        _logger.LogInformation("  Granted scope '{Scope}' on server '{ServerName}' to instance {InstanceSpId}",
                            DefaultScope, serverName, instanceSpId);
                        break;
                    case GrantResult.AlreadyExists:
                        totalSkipped++;
                        _logger.LogDebug("  Scope '{Scope}' already granted on server '{ServerName}' to instance {InstanceSpId}",
                            DefaultScope, serverName, instanceSpId);
                        break;
                    case GrantResult.Failed:
                        totalFailed++;
                        _logger.LogError("  Failed to grant scope on server '{ServerName}' to instance {InstanceSpId}",
                            serverName, instanceSpId);
                        break;
                }
            }
        }

        // Summary
        _logger.LogInformation("");
        _logger.LogInformation("Summary: {Granted} granted, {Skipped} already existed, {Failed} failed",
            totalGranted, totalSkipped, totalFailed);

        return totalFailed == 0;
    }

    private async Task<List<string>?> ResolveAgentInstancesAsync(
        string? blueprintId,
        string? agentInstancesRaw,
        string tenantId,
        CancellationToken cancellationToken)
    {
        List<string>? blueprintInstances = null;
        HashSet<string>? filterSet = null;

        // Parse explicit agent instance list
        if (!string.IsNullOrWhiteSpace(agentInstancesRaw))
        {
            var parsed = agentInstancesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var id in parsed)
            {
                if (!Guid.TryParse(id, out _))
                {
                    _logger.LogError("Invalid agent instance SP ID format: {Id}. Must be a valid GUID.", id);
                    return null;
                }
            }
            filterSet = new HashSet<string>(parsed, StringComparer.OrdinalIgnoreCase);
        }

        // Resolve from blueprint if provided
        if (!string.IsNullOrWhiteSpace(blueprintId))
        {
            try
            {
                var instances = await _blueprintService.GetAgentInstancesForBlueprintAsync(tenantId, blueprintId, cancellationToken);
                blueprintInstances = instances.Select(i => i.IdentitySpId).ToList();
                _logger.LogDebug("Blueprint {BlueprintId} has {Count} instance(s)", blueprintId, blueprintInstances.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve agent instances for blueprint {BlueprintId}", blueprintId);
                return null;
            }
        }

        // Determine final list
        if (blueprintInstances != null && filterSet != null)
        {
            // Both: filter blueprint instances by the explicit list
            return blueprintInstances.Where(id => filterSet.Contains(id)).ToList();
        }
        else if (blueprintInstances != null)
        {
            return blueprintInstances;
        }
        else if (filterSet != null)
        {
            return filterSet.ToList();
        }

        return null;
    }

    private async Task<string?> ResolveServicePrincipalIdAsync(
        string tenantId,
        string appId,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = $"/v1.0/servicePrincipals?$filter=appId eq '{appId}'&$select=id";
            using var doc = await _graphApiService.GraphGetAsync(tenantId, path, cancellationToken);

            if (doc == null)
            {
                _logger.LogDebug("Graph query for SP with appId {AppId} returned null", appId);
                return null;
            }

            if (doc.RootElement.TryGetProperty("value", out var value) &&
                value.ValueKind == JsonValueKind.Array &&
                value.GetArrayLength() > 0)
            {
                var first = value[0];
                if (first.TryGetProperty("id", out var idProp))
                {
                    return idProp.GetString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve service principal for appId {AppId}", appId);
            return null;
        }
    }

    private enum GrantResult { Created, Updated, AlreadyExists, Failed }

    private async Task<GrantResult> UpsertDelegatedGrantAsync(
        string tenantId,
        string clientSpId,
        string resourceSpId,
        string scope,
        CancellationToken cancellationToken)
    {
        try
        {
            var graphToken = await _graphApiService.GetGraphAccessTokenAsync(tenantId, ct: cancellationToken);
            if (string.IsNullOrWhiteSpace(graphToken))
            {
                _logger.LogError("Failed to acquire Graph API access token");
                return GrantResult.Failed;
            }

            using var httpClient = Services.Internal.HttpClientFactory.CreateAuthenticatedClient(graphToken);

            // Check existing grants
            var filter = $"clientId eq '{clientSpId}' and resourceId eq '{resourceSpId}' and consentType eq '{AllPrincipalsConsentType}'";
            var getUrl = $"{GraphApiConstants.BaseUrl}/v1.0/oauth2PermissionGrants?$filter={Uri.EscapeDataString(filter)}";

            using var getResponse = await httpClient.GetAsync(getUrl, cancellationToken);
            if (!getResponse.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to query existing grants: {Status}", getResponse.StatusCode);
                return GrantResult.Failed;
            }

            var getJson = await getResponse.Content.ReadAsStringAsync(cancellationToken);
            using var getDoc = JsonDocument.Parse(getJson);

            if (getDoc.RootElement.TryGetProperty("value", out var grants) &&
                grants.ValueKind == JsonValueKind.Array &&
                grants.GetArrayLength() > 0)
            {
                // Grant exists: check if scope is already present
                var existing = grants[0];
                var grantId = existing.GetProperty("id").GetString();
                var existingScope = existing.TryGetProperty("scope", out var s) ? s.GetString() ?? "" : "";
                var existingScopes = existingScope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

                if (existingScopes.Contains(scope))
                {
                    return GrantResult.AlreadyExists;
                }

                // Add scope via PATCH
                existingScopes.Add(scope);
                var newScope = string.Join(' ', existingScopes.OrderBy(x => x));
                var patchUrl = $"{GraphApiConstants.BaseUrl}/v1.0/oauth2PermissionGrants/{grantId}";
                var patchBody = new { scope = newScope };

                using var patchResponse = await httpClient.PatchAsync(
                    patchUrl,
                    new StringContent(JsonSerializer.Serialize(patchBody), Encoding.UTF8, "application/json"),
                    cancellationToken);

                if (!patchResponse.IsSuccessStatusCode)
                {
                    var error = await patchResponse.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("Failed to update grant {GrantId}: {Error}", grantId, error);
                    return GrantResult.Failed;
                }

                return GrantResult.Updated;
            }

            // Create new grant
            var createUrl = $"{GraphApiConstants.BaseUrl}/v1.0/oauth2PermissionGrants";
            var createBody = new
            {
                clientId = clientSpId,
                consentType = AllPrincipalsConsentType,
                resourceId = resourceSpId,
                scope = scope
            };

            using var createResponse = await httpClient.PostAsync(
                createUrl,
                new StringContent(JsonSerializer.Serialize(createBody), Encoding.UTF8, "application/json"),
                cancellationToken);

            if (!createResponse.IsSuccessStatusCode)
            {
                var error = await createResponse.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to create grant: {Error}", error);
                return GrantResult.Failed;
            }

            return GrantResult.Created;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception upserting delegated grant for client {ClientSpId} -> resource {ResourceSpId}", clientSpId, resourceSpId);
            return GrantResult.Failed;
        }
    }

    private static async Task<string?> ResolveTenantIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows);
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "az",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (isWindows)
            {
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add("az");
            }
            startInfo.ArgumentList.Add("account");
            startInfo.ArgumentList.Add("show");
            startInfo.ArgumentList.Add("--query");
            startInfo.ArgumentList.Add("tenantId");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("tsv");

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null) return null;

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                var tenantId = outputTask.Result.Trim();
                if (!string.IsNullOrWhiteSpace(tenantId))
                    return tenantId;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Non-fatal
        }

        return null;
    }
}
