// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Native C# service for acquiring MOS (Microsoft Office Store) tokens.
/// Replaces GetToken.ps1 PowerShell script.
/// </summary>
public class MosTokenService
{
    private readonly ILogger<MosTokenService> _logger;
    private readonly IConfigService _configService;
    private readonly string _cacheFilePath;

    public MosTokenService(ILogger<MosTokenService> logger, IConfigService configService)
    {
        _logger = logger;
        _configService = configService;
        
        // Store token cache in user's home directory for security
        // Avoid current directory which may have shared/inappropriate permissions
        var cacheDir = FileHelper.GetSecureCrossOsDirectory();
        _cacheFilePath = Path.Combine(cacheDir, "mos-token-cache.json");
    }

    /// <summary>
    /// Acquire MOS token for the specified environment.
    /// Uses MSAL.NET for interactive authentication with caching.
    /// </summary>
    public async Task<string?> AcquireTokenAsync(string environment, string? personalToken = null, CancellationToken cancellationToken = default)
    {
        environment = environment.ToLowerInvariant().Trim();

        // If personal token provided, use it directly (no caching)
        if (!string.IsNullOrWhiteSpace(personalToken))
        {
            _logger.LogInformation("Using provided personal MOS token override");
            return personalToken.Trim();
        }

        // Try cache first
        var cached = TryGetCachedToken(environment);
        if (cached.HasValue)
        {
            _logger.LogInformation("Using cached MOS token (valid until {Expiry:u})", cached.Value.Expiry);
            return cached.Value.Token;
        }

        // Load config to get tenant ID
        var setupConfig = await _configService.LoadAsync();
        if (setupConfig == null)
        {
            _logger.LogError("Configuration not found. Run 'a365 config init' first.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(setupConfig.TenantId))
        {
            _logger.LogError("TenantId not configured. Run 'a365 config init' first.");
            return null;
        }

        // Use Microsoft first-party client app for MOS token acquisition
        // This is required because MOS APIs only accept tokens from first-party apps
        var mosClientAppId = MosConstants.TpsAppServicesClientAppId;
        _logger.LogDebug("Using Microsoft first-party client app for MOS tokens: {ClientAppId}", mosClientAppId);

        // Get environment-specific configuration
        var config = GetEnvironmentConfig(environment, mosClientAppId, setupConfig.TenantId);
        if (config == null)
        {
            _logger.LogError("Unsupported MOS environment: {Environment}", environment);
            return null;
        }

        // Acquire new token using MSAL.NET with device code flow (no browser popup)
        try
        {
            _logger.LogInformation("Acquiring MOS token for environment: {Environment}", environment);
            _logger.LogInformation("Please follow the device code instructions...");

            var app = PublicClientApplicationBuilder
                .Create(config.ClientId)
                .WithAuthority(config.Authority)
                .WithRedirectUri(MosConstants.RedirectUri)
                .Build();

            var result = await app
                .AcquireTokenWithDeviceCode(new[] { config.Scope }, deviceCodeResult =>
                {
                    _logger.LogInformation("");
                    _logger.LogInformation("========================================================================");
                    _logger.LogInformation("To sign in, use a web browser to open the page:");
                    _logger.LogInformation("    {VerificationUri}", deviceCodeResult.VerificationUrl);
                    _logger.LogInformation("");
                    _logger.LogInformation("And enter the code: {UserCode}", deviceCodeResult.UserCode);
                    _logger.LogInformation("========================================================================");
                    _logger.LogInformation("");
                    return Task.CompletedTask;
                })
                .ExecuteAsync(cancellationToken);

            if (result?.AccessToken == null)
            {
                _logger.LogError("Failed to acquire MOS token");
                return null;
            }

            // Log the scopes in the token for debugging
            if (result.Scopes != null && result.Scopes.Any())
            {
                _logger.LogDebug("Token scopes: {Scopes}", string.Join(", ", result.Scopes));
            }
            else
            {
                _logger.LogWarning("Token has no scopes property");
            }

            // Cache the token
            var expiry = result.ExpiresOn.UtcDateTime;
            CacheToken(environment, result.AccessToken, expiry);

            _logger.LogInformation("MOS token acquired successfully (expires {Expiry:u})", expiry);
            return result.AccessToken;
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

    private (string Token, DateTime Expiry)? TryGetCachedToken(string environment)
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
                return null;

            var json = File.ReadAllText(_cacheFilePath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty(environment, out var envElement))
            {
                var token = envElement.TryGetProperty("token", out var t) ? t.GetString() : null;
                var expiryStr = envElement.TryGetProperty("expiry", out var e) ? e.GetString() : null;

                if (!string.IsNullOrWhiteSpace(token) && DateTime.TryParse(expiryStr, out var expiry))
                {
                    // Return cached token if valid for at least 2 more minutes
                    if (DateTime.UtcNow < expiry.AddMinutes(-2))
                    {
                        return (token, expiry);
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read token cache");
            return null;
        }
    }

    private void CacheToken(string environment, string token, DateTime expiry)
    {
        try
        {
            var cache = new Dictionary<string, object>();

            if (File.Exists(_cacheFilePath))
            {
                var json = File.ReadAllText(_cacheFilePath);
                cache = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();
            }

            cache[environment] = new
            {
                token,
                expiry = expiry.ToUniversalTime().ToString("o")
            };

            var updated = System.Text.Json.JsonSerializer.Serialize(cache, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cacheFilePath, updated);

            // Set file permissions to user-only on Unix systems
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    var fileInfo = new FileInfo(_cacheFilePath);
                    fileInfo.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    _logger.LogDebug("Set secure permissions (0600) on token cache file");
                }
                catch (Exception permEx)
                {
                    _logger.LogWarning(permEx, "Failed to set Unix file permissions on token cache");
                }
            }

            _logger.LogDebug("Token cached for environment: {Environment} at {Path}", environment, _cacheFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache token");
        }
    }

    private class MosEnvironmentConfig
    {
        public required string ClientId { get; init; }
        public required string Authority { get; init; }
        public required string Scope { get; init; }
    }
}
