// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Extensions.Logging;

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to acquire MOS token: {Message}", ex.Message);
            return null;
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
