// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Resolves BYO MCP server resources in Entra and manages the delegated permission grants
/// that let agent identities call those servers.
/// </summary>
public class McpServerPermissionService
{
    private readonly GraphApiService _graphApiService;
    private readonly AgentBlueprintService _blueprintService;
    private readonly ILogger<McpServerPermissionService> _logger;

    /// <summary>
    /// Routes sign-in through the device code flow instead of the WAM broker, for terminals
    /// where WAM cannot present a dialog.
    /// </summary>
    public virtual bool UseDeviceCodeAuthentication
    {
        get => _graphApiService.UseDeviceCodeAuthentication;
        set => _graphApiService.UseDeviceCodeAuthentication = value;
    }

    public McpServerPermissionService(
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        ILogger<McpServerPermissionService> logger)
    {
        _graphApiService = graphApiService;
        _blueprintService = blueprintService;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the BYO application for an MCP server name and returns its service principal,
    /// which is the resourceId of the permission grant. Returns null when either the application
    /// or its service principal cannot be found.
    /// </summary>
    public virtual async Task<McpServerResource?> ResolveServerResourceAsync(
        string tenantId,
        string serverName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);

        var displayName = McpConstants.BuildByoAppDisplayName(serverName);

        var appIds = await _graphApiService.FindApplicationAppIdsByDisplayNameAsync(tenantId, displayName, ct);
        if (appIds.Count == 0)
        {
            // A failed sign-in also yields a null lookup result, which would otherwise be reported
            // as "application not found" and send the user off to create an app that may exist.
            if (string.IsNullOrWhiteSpace(await _graphApiService.GetGraphAccessTokenAsync(tenantId, ct: ct)))
            {
                _logger.LogError("Could not sign in to tenant {TenantId}, so '{DisplayName}' could not be looked up.", tenantId, displayName);
                return null;
            }

            _logger.LogError("No Entra application named '{DisplayName}' was found in tenant {TenantId}.", displayName, tenantId);
            return null;
        }

        // Display names are not unique, so granting against an arbitrary match could hand the
        // agent access to a different MCP server than the caller named.
        if (appIds.Count > 1)
        {
            _logger.LogError(
                "Tenant {TenantId} has {Count} applications named '{DisplayName}' ({AppIds}). Rename or remove the duplicates so the MCP server resolves to one application.",
                tenantId, appIds.Count, displayName, string.Join(", ", appIds));
            return null;
        }

        var appId = appIds[0];

        var spObjectId = await _graphApiService.LookupServicePrincipalByAppIdAsync(
            tenantId, appId, ct, AuthenticationConstants.RequiredPermissionGrantScopes);
        if (string.IsNullOrWhiteSpace(spObjectId))
        {
            _logger.LogError(
                "Application '{DisplayName}' ({AppId}) has no service principal in tenant {TenantId}. Permissions cannot be granted against it.",
                displayName, appId, tenantId);
            return null;
        }

        _logger.LogDebug("Resolved MCP server '{ServerName}' to app {AppId}, service principal {SpObjectId}.",
            serverName, appId, spObjectId);

        return new McpServerResource(serverName, displayName, appId, spObjectId);
    }

    /// <summary>
    /// Returns every agent instance linked to the blueprint together with whether it already
    /// holds <see cref="McpConstants.V2ScopeValue"/> against the given MCP server resource.
    /// </summary>
    public virtual async Task<IReadOnlyList<AgentInstancePermissionStatus>> GetAgentInstanceStatusesAsync(
        string tenantId,
        string blueprintId,
        string resourceSpObjectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(blueprintId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceSpObjectId);

        var instances = await _blueprintService.GetAgentInstancesForBlueprintAsync(tenantId, blueprintId, ct);

        var statuses = new List<AgentInstancePermissionStatus>(instances.Count);
        foreach (var instance in instances)
        {
            ct.ThrowIfCancellationRequested();

            var grants = await _graphApiService.TryGetOauth2PermissionGrantsAsync(tenantId, instance.IdentitySpId, ct);
            if (grants is null)
            {
                // An empty list is also what a failed read returns, and reporting that as "missing"
                // would let --yes grant on the strength of a lookup that never succeeded.
                throw new InvalidOperationException(
                    $"Could not read the existing permission grants for agent identity {instance.IdentitySpId}. " +
                    "The permission state is unknown, so no grants were made.");
            }

            var hasScope = grants.Any(g =>
                string.Equals(g.resourceId, resourceSpObjectId, StringComparison.OrdinalIgnoreCase) &&
                ScopeStringContains(g.scope, McpConstants.V2ScopeValue));

            statuses.Add(new AgentInstancePermissionStatus(instance.IdentitySpId, instance.DisplayName, hasScope));
        }

        return statuses;
    }

    /// <summary>
    /// Creates or updates the AllPrincipals grant that gives an agent identity
    /// <see cref="McpConstants.V2ScopeValue"/> on the given MCP server resource.
    /// </summary>
    public virtual async Task<bool> GrantServerScopeAsync(
        string tenantId,
        string agentServicePrincipalObjectId,
        string resourceSpObjectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentServicePrincipalObjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceSpObjectId);

        return await _graphApiService.CreateOrUpdateOauth2PermissionGrantAsync(
            tenantId,
            agentServicePrincipalObjectId,
            resourceSpObjectId,
            [McpConstants.V2ScopeValue],
            ct,
            AuthenticationConstants.RequiredPermissionGrantScopes);
    }

    /// <summary>
    /// Grant scope values are a single space-delimited string, so a substring match would report
    /// a false positive for any scope that merely shares a prefix.
    /// </summary>
    private static bool ScopeStringContains(string? scopeString, string scope) =>
        !string.IsNullOrWhiteSpace(scopeString) &&
        scopeString.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => string.Equals(s, scope, StringComparison.OrdinalIgnoreCase));
}
