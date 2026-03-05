// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
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
    private readonly Func<string, string, TokenCredential>? _credentialFactory;
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
        string clientAppId,
        Func<string, string, TokenCredential>? credentialFactory = null)
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
        _credentialFactory = credentialFactory;
    }

    /// <summary>
    /// Gets an authenticated GraphServiceClient using interactive browser authentication.
    /// Caches the client instance to avoid repeated authentication prompts.
    ///
    /// NOTE: GraphServiceClient acquires tokens lazily (on first API call). To surface
    /// authentication failures early and ensure the "success" log is accurate, this method
    /// eagerly acquires a token before constructing the client.
    /// </summary>
    public async Task<GraphServiceClient> GetAuthenticatedGraphClientAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        // Return cached client if available for the same tenant
        if (_cachedClient != null && _cachedTenantId == tenantId)
        {
            _logger.LogDebug("Reusing cached Graph client for tenant {TenantId}", tenantId);
            return _cachedClient;
        }

        _logger.LogInformation("Attempting to authenticate to Microsoft Graph interactively...");
        _logger.LogInformation("This requires permissions defined in AuthenticationConstants.RequiredClientAppPermissions for Agent Blueprint operations.");
        _logger.LogInformation("");
        _logger.LogInformation("IMPORTANT: Interactive authentication is required.");
        _logger.LogInformation("Please sign in with an account that has Global Administrator or similar privileges.");
        _logger.LogInformation("");

        _logger.LogInformation("Authenticating to Microsoft Graph...");
        _logger.LogInformation("IMPORTANT: You must grant consent for all required permissions.");
        _logger.LogInformation("Required permissions are defined in AuthenticationConstants.RequiredClientAppPermissions.");
        _logger.LogInformation($"See {ConfigConstants.Agent365CliDocumentationUrl} for the complete list.");
        _logger.LogInformation("");

        // Eagerly acquire a token so authentication failures are detected here rather than
        // surfacing later from inside GraphServiceClient's lazy token acquisition.
        // Resolve credential inside try/catch so factory exceptions are wrapped consistently.
        var tokenContext = new TokenRequestContext(RequiredScopes);
        TokenCredential? credential = null;
        try
        {
            // Resolve credential: use injected factory (for tests) or default MsalBrowserCredential
            credential = _credentialFactory?.Invoke(_clientAppId, tenantId)
                ?? new MsalBrowserCredential(_clientAppId, tenantId, redirectUri: null, _logger);

            await credential.GetTokenAsync(tokenContext, cancellationToken);
        }
        catch (MsalAuthenticationFailedException ex) when (ex.Message.Contains("invalid_grant", StringComparison.Ordinal))
        {
            ThrowInsufficientPermissionsException(ex);
            throw; // Unreachable but required for compiler
        }
        catch (MsalAuthenticationFailedException ex) when (
            ex.Message.Contains("localhost", StringComparison.Ordinal) ||
            ex.Message.Contains("connection", StringComparison.Ordinal) ||
            ex.Message.Contains("redirect_uri", StringComparison.Ordinal))
        {
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

        // Token acquired successfully — log and construct the client.
        // MsalBrowserCredential caches the MSAL account, so subsequent GetTokenAsync calls
        // from GraphServiceClient will hit the silent cache without re-prompting.
        _logger.LogInformation("Successfully authenticated to Microsoft Graph!");
        _logger.LogInformation("");

        var graphClient = new GraphServiceClient(credential!, RequiredScopes);
        _cachedClient = graphClient;
        _cachedTenantId = tenantId;

        return graphClient;
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
