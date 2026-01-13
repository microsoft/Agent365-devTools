// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Ensures oauth2PermissionGrant exists for the custom client application.
/// Validates that AgentIdentityBlueprint.ReadWrite.All permission is granted, which is required for creating and managing Agent Blueprints.
/// </summary>
public sealed class DelegatedConsentService
{
    private readonly ILogger<DelegatedConsentService> _logger;
    private readonly GraphApiService _graphService;

    // Constants
    private static readonly string[] PermissionGrantScopes = AuthenticationConstants.PermissionGrantAuthScopes;
    private const string TargetScope = "AgentIdentityBlueprint.ReadWrite.All";
    private const string AllPrincipalsConsentType = "AllPrincipals";

    public DelegatedConsentService(
        ILogger<DelegatedConsentService> logger,
        GraphApiService graphService)
    {
        _logger = logger;
        _graphService = graphService;
    }

    /// <summary>
    /// Ensures AgentIdentityBlueprint.ReadWrite.All permission is granted to the custom client application.
    /// Required for creating and managing Agent Blueprints.
    /// </summary>
    /// <param name="callingAppId">Application ID of the custom client app from configuration</param>
    /// <param name="tenantId">Tenant ID where the permission grant will be created</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if grant was created or updated successfully</returns>
    public async Task<bool> EnsureBlueprintPermissionGrantAsync(
        string callingAppId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("==> Ensuring AgentIdentityBlueprint.ReadWrite.All permission for custom client app");
            _logger.LogInformation("    Client App ID: {AppId}", callingAppId);
            _logger.LogInformation("    Tenant ID: {TenantId}", tenantId);
            _logger.LogInformation("    Required Scope: {Scope}", TargetScope);

            if (!Guid.TryParse(callingAppId, out _))
            {
                _logger.LogError("Invalid Calling App ID format: {AppId}", callingAppId);
                return false;
            }

            if (!Guid.TryParse(tenantId, out _))
            {
                _logger.LogError("Invalid Tenant ID format: {TenantId}", tenantId);
                return false;
            }

            // Prewarm delegated auth (device code) once; validates we can get a delegated token.
            var warmup = await _graphService.GraphGetAsync(
                tenantId,
                "/v1.0/me?$select=id",
                cancellationToken,
                scopes: new[] { "User.Read" });

            if (warmup == null)
            {
                _logger.LogError("Failed to authenticate to Microsoft Graph with delegated permissions (device code).");
                return false;
            }

            // 1) Ensure SP for the calling (custom client) app
            _logger.LogInformation("    Ensuring service principal for client app (appId: {AppId})", callingAppId);
            var clientSpObjectId = await _graphService.EnsureServicePrincipalForAppIdAsync(
                tenantId,
                callingAppId,
                cancellationToken,
                scopes: PermissionGrantScopes);

            if (string.IsNullOrWhiteSpace(clientSpObjectId))
            {
                _logger.LogError("Failed to ensure service principal for calling app");
                return false;
            }

            _logger.LogInformation("    Client Service Principal ID: {SpId}", clientSpObjectId);

            // 2) Ensure SP for Microsoft Graph resource app
            _logger.LogInformation("    Ensuring Microsoft Graph service principal");
            var graphSpObjectId = await _graphService.EnsureServicePrincipalForAppIdAsync(
                tenantId,
                AuthenticationConstants.MicrosoftGraphResourceAppId,
                cancellationToken,
                scopes: PermissionGrantScopes);

            if (string.IsNullOrWhiteSpace(graphSpObjectId))
            {
                _logger.LogError("Failed to ensure Microsoft Graph service principal");
                return false;
            }

            _logger.LogInformation("    Graph Service Principal ID: {SpId}", graphSpObjectId);

            // 3) Create or update the oauth2PermissionGrant (AllPrincipals) to include TargetScope
            _logger.LogInformation("    Creating/updating oauth2PermissionGrant (AllPrincipals) for scope: {Scope}", TargetScope);

            var ok = await _graphService.CreateOrUpdateOauth2PermissionGrantAsync(
                tenantId,
                clientSpObjectId,
                graphSpObjectId,
                new[] { TargetScope },
                cancellationToken,
                permissionGrantScopes: PermissionGrantScopes);

            if (!ok)
            {
                _logger.LogError("Failed to create/update oauth2PermissionGrant for scope: {Scope}", TargetScope);
                return false;
            }

            _logger.LogInformation("Successfully ensured grant for scope: {Scope}", TargetScope);
            _logger.LogInformation("    You can now create Agent Blueprints");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure AgentIdentityBlueprint.ReadWrite.All consent: {Message}", ex.Message);
            return false;
        }
    }
}
