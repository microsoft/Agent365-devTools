// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Native C# service for acquiring MOS (Microsoft Office Store) tokens.
/// Delegates to <see cref="MsalBrowserCredential"/> for interactive authentication
/// with automatic device code fallback, leveraging MSAL's built-in token cache.
/// </summary>
public class MosTokenService
{
    private readonly ILogger<MosTokenService> _logger;
    private readonly IConfigService _configService;

    public MosTokenService(ILogger<MosTokenService> logger, IConfigService configService)
    {
        _logger = logger;
        _configService = configService;
    }

    /// <summary>
    /// Acquire MOS token for the specified environment.
    /// Uses <see cref="MsalBrowserCredential"/> for interactive authentication with caching.
    /// </summary>
    public async Task<string?> AcquireTokenAsync(string environment, string? personalToken = null, CancellationToken cancellationToken = default)
    {
        environment = environment.ToLowerInvariant().Trim();

        if (!string.IsNullOrWhiteSpace(personalToken))
        {
            _logger.LogInformation("Using provided personal MOS token override");
            return personalToken.Trim();
        }

        var setupConfig = await _configService.LoadAsync();
        if (setupConfig is null)
        {
            _logger.LogError("Configuration not found. Run 'a365 config init' first.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(setupConfig.TenantId))
        {
            _logger.LogError("TenantId not configured. Run 'a365 config init' first.");
            return null;
        }

        var config = GetEnvironmentConfig(environment, MosConstants.TpsAppServicesClientAppId, setupConfig.TenantId);
        if (config is null)
        {
            _logger.LogError("Unsupported MOS environment: {Environment}", environment);
            return null;
        }

        try
        {
            _logger.LogInformation("Acquiring MOS token for environment: {Environment}", environment);

            // useWam: false because TpsAppServicesClientAppId is a Microsoft first-party app.
            // WAM would override the redirect URI to the WAM broker format, which is not
            // registered for this app. The original flow used a system browser redirect.
            var credential = new MsalBrowserCredential(
                config.ClientId,
                setupConfig.TenantId,
                redirectUri: MosConstants.RedirectUri,
                logger: _logger,
                useWam: false,
                authority: config.Authority);

            var tokenRequestContext = new TokenRequestContext(new[] { config.Scope });
            var token = await credential.GetTokenAsync(tokenRequestContext, cancellationToken);

            _logger.LogInformation("MOS token acquired successfully (expires {Expiry:u})", token.ExpiresOn.UtcDateTime);
            return token.Token;
        }
        catch (MsalAuthenticationFailedException ex)
        {
            if (ex.InnerException is MsalServiceException msalEx)
            {
                LogMsalServiceError(msalEx, config.ClientId);
            }
            else
            {
                _logger.LogError("Failed to acquire MOS token: {Message}", ex.Message);
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to acquire MOS token: {Message}", ex.Message);
            return null;
        }
    }

    private void LogMsalServiceError(MsalServiceException ex, string clientAppId)
    {
        if (ex.ErrorCode == "invalid_client" && ex.Message.Contains("AADSTS650052"))
        {
            _logger.LogError("MOS token acquisition failed: Missing service principal or admin consent (Error: {ErrorCode})", ex.ErrorCode);
            _logger.LogInformation("");
            _logger.LogInformation("The MOS service principals exist, but admin consent may not be granted.");
            _logger.LogInformation("Grant admin consent at:");
            _logger.LogInformation("  {PortalUrl}", MosConstants.GetApiPermissionsPortalUrl(clientAppId));
            _logger.LogInformation("");
            _logger.LogInformation("Or authenticate interactively and consent when prompted.");
            _logger.LogInformation("");
        }
        else if (ex.ErrorCode == "unauthorized_client" && ex.Message.Contains("AADSTS50194"))
        {
            _logger.LogError("MOS token acquisition failed: Single-tenant app cannot use /common endpoint (Error: {ErrorCode})", ex.ErrorCode);
            _logger.LogInformation("");
            _logger.LogInformation("AADSTS50194: The application is configured as single-tenant but is trying to use the /common authority.");
            _logger.LogInformation("This should be automatically handled by using tenant-specific authority URLs.");
            _logger.LogInformation("");
            _logger.LogInformation("If this error persists:");
            _logger.LogInformation("1. Verify your app registration is configured correctly in Azure Portal");
            _logger.LogInformation("2. Check that tenantId in a365.config.json matches your app's home tenant");
            _logger.LogInformation("3. Ensure the app's 'Supported account types' setting matches your use case");
            _logger.LogInformation("");
        }
        else if (ex.ErrorCode == "invalid_grant")
        {
            _logger.LogError("MOS token acquisition failed: Invalid or expired credentials (Error: {ErrorCode})", ex.ErrorCode);
            _logger.LogInformation("");
            _logger.LogInformation("The authentication failed due to invalid credentials or expired tokens.");
            _logger.LogInformation("Re-run the command to re-authenticate.");
            _logger.LogInformation("");
        }
        else
        {
            _logger.LogError("MOS token acquisition failed with MSAL error");
            _logger.LogError("Error Code: {ErrorCode}", ex.ErrorCode);
            _logger.LogError("Error Message: {Message}", ex.Message);
            _logger.LogInformation("");
            _logger.LogInformation("Authentication failed. Common issues:");
            _logger.LogInformation("1. Missing admin consent - Grant at:");
            _logger.LogInformation("   {PortalUrl}", MosConstants.GetApiPermissionsPortalUrl(clientAppId));
            _logger.LogInformation("2. Insufficient permissions - Verify required API permissions are configured");
            _logger.LogInformation("3. Tenant configuration - Ensure app registration matches your tenant setup");
            _logger.LogInformation("");
            _logger.LogInformation("For detailed troubleshooting, search for error code: {ErrorCode}", ex.ErrorCode);
            _logger.LogInformation("");
        }
    }

    private MosEnvironmentConfig? GetEnvironmentConfig(string environment, string clientAppId, string tenantId)
    {
        // Use tenant-specific authority to support single-tenant apps (AADSTS50194 fix)
        var commercialAuthority = $"https://login.microsoftonline.com/{tenantId}";
        var governmentAuthority = $"https://login.microsoftonline.us/{tenantId}";

        return environment switch
        {
            "prod" => new MosEnvironmentConfig
            {
                ClientId = clientAppId,
                Authority = commercialAuthority,
                Scope = MosConstants.Environments.ProdScope
            },
            "sdf" => new MosEnvironmentConfig
            {
                ClientId = clientAppId,
                Authority = commercialAuthority,
                Scope = MosConstants.Environments.SdfScope
            },
            "test" => new MosEnvironmentConfig
            {
                ClientId = clientAppId,
                Authority = commercialAuthority,
                Scope = MosConstants.Environments.TestScope
            },
            "gccm" => new MosEnvironmentConfig
            {
                ClientId = clientAppId,
                Authority = commercialAuthority,
                Scope = MosConstants.Environments.GccmScope
            },
            "gcch" => new MosEnvironmentConfig
            {
                ClientId = clientAppId,
                Authority = governmentAuthority,
                Scope = MosConstants.Environments.GcchScope
            },
            "dod" => new MosEnvironmentConfig
            {
                ClientId = clientAppId,
                Authority = governmentAuthority,
                Scope = MosConstants.Environments.DodScope
            },
            _ => null
        };
    }

    private class MosEnvironmentConfig
    {
        public required string ClientId { get; init; }
        public required string Authority { get; init; }
        public required string Scope { get; init; }
    }
}
