// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Provides interactive authentication to Microsoft Graph using browser authentication.
/// Uses a custom client app registration created by the user in their tenant.
/// 
/// The key difference from Azure CLI authentication:
/// - Azure CLI tokens are delegated (user acting on behalf of themselves)
/// - This service gets application-level access through user consent
/// - Supports AgentApplication.Create application permission
/// 
/// PURE C# IMPLEMENTATION - NO POWERSHELL DEPENDENCIES
/// </summary>
public sealed class InteractiveGraphAuthService
{
    private readonly ILogger<InteractiveGraphAuthService> _logger;
    private readonly string _clientAppId;
    private GraphServiceClient? _cachedClient;
    private string? _cachedTenantId;

    // Scopes required for Agent Blueprint creation and inheritable permissions configuration
    private static readonly string[] RequiredScopes = new[]
    {
        "https://graph.microsoft.com/Application.ReadWrite.All",
        "https://graph.microsoft.com/AgentIdentityBlueprint.ReadWrite.All",
        "https://graph.microsoft.com/AgentIdentityBlueprint.UpdateAuthProperties.All",
        "https://graph.microsoft.com/User.Read"
    };

    public InteractiveGraphAuthService(
        ILogger<InteractiveGraphAuthService> logger,
        string clientAppId)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(clientAppId))
        {
            throw new ArgumentNullException(
                nameof(clientAppId),
                $"Client App ID is required. Configure clientAppId in a365.config.json. See {ConfigConstants.Agent365CliDocumentationUrl} for setup instructions.");
        }

        if (!Guid.TryParse(clientAppId, out _))
        {
            throw new ArgumentException(
                $"Client App ID must be a valid GUID format (received: {clientAppId})",
                nameof(clientAppId));
        }

        _clientAppId = clientAppId;
    }

    /// <summary>
    /// Gets an authenticated GraphServiceClient using interactive browser authentication.
    /// Caches the client instance to avoid repeated authentication prompts.
    /// </summary>
    public Task<GraphServiceClient> GetAuthenticatedGraphClientAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        // Return cached client if available for the same tenant
        if (_cachedClient != null && _cachedTenantId == tenantId)
        {
            _logger.LogDebug("Reusing cached Graph client for tenant {TenantId}", tenantId);
            return Task.FromResult(_cachedClient);
        }

        _logger.LogInformation("Attempting to authenticate to Microsoft Graph interactively...");
        _logger.LogInformation("This requires permissions defined in AuthenticationConstants.RequiredClientAppPermissions for Agent Blueprint operations.");
        _logger.LogInformation("");
        _logger.LogInformation("IMPORTANT: Interactive authentication is required.");
        _logger.LogInformation("Please sign in with an account that has Global Administrator or similar privileges.");
        _logger.LogInformation("");

        // Use browser authentication (MsalBrowserCredential handles WAM on Windows and browser on other platforms)
        try
        {
            // Pass null for redirectUri to let MsalBrowserCredential decide based on platform
            var browserCredential = new MsalBrowserCredential(
                _clientAppId,
                tenantId,
                redirectUri: null,  // Let MsalBrowserCredential use WAM on Windows
                _logger);

            _logger.LogInformation("Authenticating to Microsoft Graph...");
            _logger.LogInformation("IMPORTANT: You must grant consent for all required permissions.");
            _logger.LogInformation("Required permissions are defined in AuthenticationConstants.RequiredClientAppPermissions.");
            _logger.LogInformation($"See {ConfigConstants.Agent365CliDocumentationUrl} for the complete list.");
            _logger.LogInformation("");

            // Create GraphServiceClient with the credential
            var graphClient = new GraphServiceClient(browserCredential, RequiredScopes);

            _logger.LogInformation("Successfully authenticated to Microsoft Graph!");
            _logger.LogInformation("");

            // Cache the client for reuse
            _cachedClient = graphClient;
            _cachedTenantId = tenantId;

            return Task.FromResult(graphClient);
        }
        catch (MsalAuthenticationFailedException ex) when (ex.Message.Contains("invalid_grant"))
        {
            // Permissions issue
            ThrowInsufficientPermissionsException(ex);
            throw; // Unreachable but required for compiler
        }
        catch (MsalAuthenticationFailedException ex) when (
            ex.Message.Contains("localhost") ||
            ex.Message.Contains("connection") ||
            ex.Message.Contains("redirect_uri"))
        {
            // Infrastructure/connectivity issue
            _logger.LogError("Browser authentication failed due to connectivity issue: {Message}", ex.Message);
            throw new GraphApiException(
                "Browser authentication",
                $"Authentication failed due to connectivity issue: {ex.Message}. Please ensure you have network connectivity.",
                isPermissionIssue: false);
        }
        catch (Microsoft.Identity.Client.MsalServiceException ex) when (ex.ErrorCode == "access_denied")
        {
            _logger.LogError("Authentication was denied or cancelled");
            throw new GraphApiException(
                "Interactive browser authentication",
                "Authentication was denied or cancelled by the user",
                isPermissionIssue: false);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to authenticate to Microsoft Graph: {Message}", ex.Message);
            throw new GraphApiException(
                "Browser authentication",
                $"Authentication failed: {ex.Message}",
                isPermissionIssue: false);
        }
    }

    private void ThrowInsufficientPermissionsException(Exception innerException)
    {
        _logger.LogError("Authentication failed - insufficient permissions");
        throw new GraphApiException(
            "Graph authentication",
            "Insufficient permissions - you must be a Global Administrator or have all required permissions defined in AuthenticationConstants.RequiredClientAppPermissions",
            isPermissionIssue: true);
    }
}
