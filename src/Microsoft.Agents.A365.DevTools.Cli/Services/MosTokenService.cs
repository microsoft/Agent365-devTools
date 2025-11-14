// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

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
    private readonly string _cacheFilePath;

    public MosTokenService(ILogger<MosTokenService> logger)
    {
        _logger = logger;
        _cacheFilePath = Path.Combine(Environment.CurrentDirectory, ".mos-token-cache.json");
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

        // Get environment-specific configuration
        var config = GetEnvironmentConfig(environment);
        if (config == null)
        {
            _logger.LogError("Unsupported MOS environment: {Environment}", environment);
            return null;
        }

        // Acquire new token using MSAL.NET
        try
        {
            _logger.LogInformation("Acquiring MOS token for environment: {Environment}", environment);
            _logger.LogInformation("A browser window will open for authentication...");

            var app = PublicClientApplicationBuilder
                .Create(config.ClientId)
                .WithAuthority(config.Authority)
                .WithRedirectUri("http://localhost")
                .Build();

            var result = await app
                .AcquireTokenInteractive(new[] { config.Scope })
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(cancellationToken);

            if (result?.AccessToken == null)
            {
                _logger.LogError("Failed to acquire MOS token");
                return null;
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

    private MosEnvironmentConfig? GetEnvironmentConfig(string environment)
    {
        return environment switch
        {
            "prod" => new MosEnvironmentConfig
            {
                ClientId = "caef0b02-8d39-46ab-b28c-f517033d8a21", // TPS Test Client
                Authority = "https://login.microsoftonline.com/common",
                Scope = "https://titles.prod.mos.microsoft.com/.default"
            },
            "sdf" => new MosEnvironmentConfig
            {
                ClientId = "caef0b02-8d39-46ab-b28c-f517033d8a21",
                Authority = "https://login.microsoftonline.com/common",
                Scope = "https://titles.sdf.mos.microsoft.com/.default"
            },
            "test" => new MosEnvironmentConfig
            {
                ClientId = "caef0b02-8d39-46ab-b28c-f517033d8a21",
                Authority = "https://login.microsoftonline.com/common",
                Scope = "https://testappservices.mos.microsoft.com/.default"
            },
            "gccm" => new MosEnvironmentConfig
            {
                ClientId = "caef0b02-8d39-46ab-b28c-f517033d8a21",
                Authority = "https://login.microsoftonline.com/common",
                Scope = "https://titles.gccm.mos.microsoft.com/.default"
            },
            "gcch" => new MosEnvironmentConfig
            {
                ClientId = "90ee8804-635f-435e-9dbf-cafc46ee769f",
                Authority = "https://login.microsoftonline.us/common",
                Scope = "https://titles.gcch.mos.svc.usgovcloud.microsoft/.default"
            },
            "dod" => new MosEnvironmentConfig
            {
                ClientId = "90ee8804-635f-435e-9dbf-cafc46ee769f",
                Authority = "https://login.microsoftonline.us/common",
                Scope = "https://titles.dod.mos.svc.usgovcloud.microsoft/.default"
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

            _logger.LogDebug("Token cached for environment: {Environment}", environment);
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
