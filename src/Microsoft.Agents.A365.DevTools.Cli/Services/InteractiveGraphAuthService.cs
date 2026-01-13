// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;

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

        _logger.LogInformation("Attempting to authenticate to Microsoft Graph using device code flow...");
        _logger.LogInformation("This requires permissions defined in AuthenticationConstants.RequiredClientAppPermissions for Agent Blueprint operations.");
        _logger.LogInformation("");
        _logger.LogInformation("IMPORTANT: Please follow the device code instructions.");
        _logger.LogInformation("Sign in with an account that has Global Administrator or similar privileges.");
        _logger.LogInformation("");

        // ALWAYS use device code flow for CLI-friendly authentication (no browser popups)
        GraphServiceClient? graphClient = null;

        try
        {
            var deviceCodeCredential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
            {
                TenantId = tenantId,
                ClientId = _clientAppId,
                AuthorityHost = AzureAuthorityHosts.AzurePublicCloud,
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions
                {
                    Name = AuthenticationConstants.ApplicationName
                },
                DeviceCodeCallback = (code, cancellation) =>
                {
                    _logger.LogInformation("");
                    _logger.LogInformation("=============================================================");
                    _logger.LogInformation("DEVICE CODE AUTHENTICATION");
                    _logger.LogInformation("=============================================================");
                    _logger.LogInformation("");
                    _logger.LogInformation("To sign in, use a web browser to open the page:");
                    _logger.LogInformation("    {0}", code.VerificationUri);
                    _logger.LogInformation("");
                    _logger.LogInformation("And enter the code:");
                    _logger.LogInformation("    {0}", code.UserCode);
                    _logger.LogInformation("");
                    _logger.LogInformation("=============================================================");
                    _logger.LogInformation("");
                    return Task.CompletedTask;
                }
            });

            graphClient = new GraphServiceClient(deviceCodeCredential, RequiredScopes);

            _logger.LogInformation("Successfully authenticated to Microsoft Graph!");
            _logger.LogInformation("");

            // Cache the client for reuse
            _cachedClient = graphClient;
            _cachedTenantId = tenantId;

            return Task.FromResult(graphClient);
        }
        catch (Azure.Identity.AuthenticationFailedException ex) when (ex.Message.Contains("invalid_grant"))
        {
            // Permissions issue in device code flow
            ThrowInsufficientPermissionsException(ex);
            throw; // Unreachable but required for compiler
        }
        catch (Exception ex)
        {
            _logger.LogError("Device code authentication failed: {Message}", ex.Message);
            throw new GraphApiException(
                "Device code authentication",
                $"Authentication failed: {ex.Message}. Ensure you have required permissions and completed authentication flow.");
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
