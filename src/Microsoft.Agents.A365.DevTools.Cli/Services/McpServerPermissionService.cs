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
    /// where WAM cannot present a dialog. Held here rather than on the shared
    /// <see cref="GraphApiService"/> singleton so it cannot affect unrelated Graph calls, and
    /// passed explicitly on every call this service makes.
    /// </summary>
    public virtual bool UseDeviceCodeAuthentication { get; set; }

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
    /// <param name="selectAppId">
    /// Invoked when more than one application shares the display name, to choose between them.
    /// Returning null aborts. When not supplied, an ambiguous name is an error: display names are
    /// not unique, so picking one unattended could grant against a different MCP server.
    /// </param>
    public virtual async Task<McpServerResource?> ResolveServerResourceAsync(
        string tenantId,
        string serverName,
        CancellationToken ct = default,
        Func<IReadOnlyList<string>, string?>? selectAppId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);

        var displayName = McpConstants.BuildByoAppDisplayName(serverName);

        var lookup = await _graphApiService.TryFindApplicationAppIdsByDisplayNameAsync(
            tenantId, displayName, ct, UseDeviceCodeAuthentication);
        var appIds = lookup.AppIds;
        if (appIds is null)
        {
            // A failed read is not an absent application — reporting "not found" would send the
            // user off to create an application that may already exist (issue #500).
            if (lookup.SignInFailed)
            {
                _logger.LogError("Could not sign in to tenant {TenantId}, so '{DisplayName}' could not be looked up.", tenantId, displayName);
            }
            else
            {
                _logger.LogError(
                    "Could not read application registrations in tenant {TenantId}, so '{DisplayName}' could not be looked up. " +
                    "This usually means the signed-in account lacks permission to read applications.",
                    tenantId, displayName);
            }

            return null;
        }

        if (appIds.Count == 0)
        {
            _logger.LogError("No Entra application named '{DisplayName}' was found in tenant {TenantId}.", displayName, tenantId);
            return null;
        }

        // Display names are not unique, so granting against an arbitrary match could hand the
        // agent access to a different MCP server than the caller named. Every match is listed for
        // the caller to choose from; with nobody to ask, this is an error rather than a guess.
        string appId;
        if (appIds.Count > 1)
        {
            if (selectAppId is null)
            {
                _logger.LogError(
                    "Tenant {TenantId} has {Count} applications named '{DisplayName}' ({AppIds}). Rename or remove the duplicates so the MCP server resolves to one application.",
                    tenantId, appIds.Count, displayName, string.Join(", ", appIds));
                return null;
            }

            var chosen = selectAppId(appIds);
            if (chosen is null)
            {
                return null;
            }

            appId = chosen;
        }
        else
        {
            appId = appIds[0];
        }

        var spObjectId = await _graphApiService.LookupServicePrincipalByAppIdAsync(
            tenantId, appId, ct, AuthenticationConstants.RequiredPermissionGrantScopes, UseDeviceCodeAuthentication);
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

        var instances = await _blueprintService.GetAgentInstancesForBlueprintAsync(
            tenantId, blueprintId, ct, UseDeviceCodeAuthentication);

        var statuses = new List<AgentInstancePermissionStatus>(instances.Count);
        foreach (var instance in instances)
        {
            ct.ThrowIfCancellationRequested();

            var grants = await _graphApiService.TryGetOauth2PermissionGrantsAsync(
                tenantId, instance.IdentitySpId, ct, UseDeviceCodeAuthentication);
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
                string.Equals(g.consentType, "AllPrincipals", StringComparison.OrdinalIgnoreCase) &&
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
            AuthenticationConstants.RequiredPermissionGrantScopes,
            // Never patch a user-scoped grant in place of the tenant-wide one this command
            // reports on, or the agent would still be missing the permission (issue #500).
            requireMatchingConsentType: true,
            UseDeviceCodeAuthentication,
            // A failed read is not "no grant" — creating from unknown state can report success
            // on "Permission entry already exists" without merging the scope (issue #500).
            abortWhenLookupFails: true);
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
