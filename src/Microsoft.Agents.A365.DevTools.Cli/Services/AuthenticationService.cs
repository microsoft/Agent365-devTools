// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for handling authentication to Agent365 Agent 365 Tools
/// </summary>
public class AuthenticationService
{
    private readonly ILogger<AuthenticationService> _logger;
    private readonly string _tokenCachePath;

    public AuthenticationService(ILogger<AuthenticationService> logger)
    {
        _logger = logger;
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cacheDir = Path.Combine(appDataPath, AuthenticationConstants.ApplicationName);
        Directory.CreateDirectory(cacheDir);
        _tokenCachePath = Path.Combine(cacheDir, AuthenticationConstants.TokenCacheFileName);
    }

    /// <summary>
    /// Gets an access token for Agent365, using cached token if valid or prompting for authentication
    /// </summary>
    /// <param name="resourceUrl">The resource URL to request a token for (e.g., https://test.agent365.svc.cloud.microsoft)</param>
    /// <param name="forceRefresh">Force token refresh even if cached token is valid</param>
    public async Task<string> GetAccessTokenAsync(string resourceUrl, bool forceRefresh = false)
    {
        // Try to load cached token for this resourceUrl
        if (!forceRefresh && File.Exists(_tokenCachePath))
        {
            try
            {
                var cachedToken = await LoadCachedTokenAsync(resourceUrl);
                if (cachedToken != null && !IsTokenExpired(cachedToken))
                {
                    _logger.LogInformation("Using cached authentication token for {ResourceUrl}", resourceUrl);
                    return cachedToken.AccessToken;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load cached token, will re-authenticate");
            }
        }

        // Authenticate interactively
        _logger.LogInformation("Authentication required for Agent365 Agent 365 Tools");
        var token = await AuthenticateInteractivelyAsync(resourceUrl);

        // Cache the token for this resourceUrl
        await CacheTokenAsync(resourceUrl, token);

        return token.AccessToken;
    }

    /// <summary>
    /// Authenticates user interactively using device code flow or browser
    /// </summary>
    /// <param name="resourceUrl">The resource URL to request a token for</param>
    private async Task<TokenInfo> AuthenticateInteractivelyAsync(string resourceUrl)
    {
        try
        {
            // Determine which scope to use based on the resource URL or App ID
            string scope;
            string environmentName;

            // Agent 365 Tools App IDs for different environments
            const string Agent365ToolsTestAppId = "05879165-0320-489e-b644-f72b33f3edf0";
            const string Agent365ToolsPreprodAppId = "4585d2c8-61e2-4f6a-a2a5-707519abf91c";
            const string Agent365ToolsProdAppId = "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1";

            // Check for Agent365 Agent 365 Tools App IDs
            if (resourceUrl == Agent365ToolsTestAppId)
            {
                scope = $"{resourceUrl}/.default";
                environmentName = "TEST";
                _logger.LogInformation("Using Agent365 Agent 365 Tools (TEST) for authentication");
            }
            else if (resourceUrl == Agent365ToolsPreprodAppId)
            {
                scope = $"{resourceUrl}/.default";
                environmentName = "PREPROD";
                _logger.LogInformation("Using Agent365 Agent 365 Tools (PREPROD) for authentication");
            }
            else if (resourceUrl == Agent365ToolsProdAppId)
            {
                scope = $"{resourceUrl}/.default";
                environmentName = "PRODUCTION";
                _logger.LogInformation("Using Agent365 Agent 365 Tools (PRODUCTION) for authentication");
            }
            // Check for Agent365 endpoint URLs (legacy support)
            else if (resourceUrl.Contains("agent365", StringComparison.OrdinalIgnoreCase))
            {
                // Determine App ID from endpoint URL
                string appId;
                if (resourceUrl.Contains("preprod", StringComparison.OrdinalIgnoreCase))
                {
                    appId = Agent365ToolsPreprodAppId;
                    environmentName = "PREPROD";
                }
                else if (resourceUrl.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                         resourceUrl.Contains("dev", StringComparison.OrdinalIgnoreCase))
                {
                    appId = Agent365ToolsTestAppId;
                    environmentName = "TEST";
                }
                else
                {
                    appId = Agent365ToolsProdAppId;
                    environmentName = "PRODUCTION";
                }

                scope = $"{appId}/.default";
                _logger.LogInformation("Using Agent365 Agent 365 Tools App ID for endpoint URL ({Environment})", environmentName);
            }
            else
            {
                // Default: use the resource as-is with /.default suffix (likely an App ID)
                scope = resourceUrl.EndsWith("/.default", StringComparison.OrdinalIgnoreCase)
                    ? resourceUrl
                    : $"{resourceUrl}/.default";
                environmentName = "CUSTOM";
                _logger.LogInformation("Using custom resource for authentication: {Resource}", resourceUrl);
            }

            _logger.LogInformation("Token scope: {Scope}", scope);

            // For Power Platform API authentication, use device code flow to avoid URL length issues
            // InteractiveBrowserCredential with Power Platform scopes can create URLs that exceed browser limits
            _logger.LogInformation("Opening browser for authentication ({Environment} environment)...", environmentName);
            _logger.LogInformation("Please sign in with your Microsoft account");

            TokenCredential credential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
            {
                TenantId = AuthenticationConstants.CommonTenantId,
                ClientId = AuthenticationConstants.PowershellClientId,
                DeviceCodeCallback = (code, cancellation) =>
                {
                    Console.WriteLine();
                    Console.WriteLine("==========================================================================");
                    Console.WriteLine($"To sign in, use a web browser to open the page:");
                    Console.WriteLine($"    {code.VerificationUri}");
                    Console.WriteLine();
                    Console.WriteLine($"And enter the code: {code.UserCode}");
                    Console.WriteLine("==========================================================================");
                    Console.WriteLine();
                    return Task.CompletedTask;
                }
            });

            string[] scopes = new[] { scope };
            _logger.LogInformation("Requesting token with scope: {Scope}", scope);

            var tokenRequestContext = new TokenRequestContext(scopes);
            var tokenResult = await credential.GetTokenAsync(tokenRequestContext, default);

            _logger.LogInformation("Authentication successful!");

            return new TokenInfo
            {
                AccessToken = tokenResult.Token,
                ExpiresOn = tokenResult.ExpiresOn.UtcDateTime
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Interactive authentication failed");
            throw new InvalidOperationException("Failed to authenticate. Please ensure you're logged in with your Microsoft account.", ex);
        }
    }

    /// <summary>
    /// Loads cached token for a specific resourceUrl from disk
    /// </summary>
    private async Task<TokenInfo?> LoadCachedTokenAsync(string resourceUrl)
    {
        if (!File.Exists(_tokenCachePath))
            return null;

        var json = await File.ReadAllTextAsync(_tokenCachePath);
        var cache = JsonSerializer.Deserialize<TokenCache>(json) ?? new TokenCache();
        cache.Tokens.TryGetValue(resourceUrl, out var token);
        return token;
    }

    /// <summary>
    /// Caches token for a specific resourceUrl to disk
    /// </summary>
    private async Task CacheTokenAsync(string resourceUrl, TokenInfo token)
    {
        TokenCache cache;
        if (File.Exists(_tokenCachePath))
        {
            var json = await File.ReadAllTextAsync(_tokenCachePath);
            cache = JsonSerializer.Deserialize<TokenCache>(json) ?? new TokenCache();
        }
        else
        {
            cache = new TokenCache();
        }

        cache.Tokens[resourceUrl] = token;
        var updatedJson = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_tokenCachePath, updatedJson);
        _logger.LogInformation("Authentication token cached for {ResourceUrl} at: {Path}", resourceUrl, _tokenCachePath);
    }

    /// <summary>
    /// Checks if token is expired (with buffer to prevent using tokens that expire during a request)
    /// </summary>
    private bool IsTokenExpired(TokenInfo token)
    {
        return token.ExpiresOn <= DateTime.UtcNow.AddMinutes(AuthenticationConstants.TokenExpirationBufferMinutes);
    }

    /// <summary>
    /// Gets an access token with scope resolution for MCP servers
    /// </summary>
    /// <param name="resourceUrl">The resource URL to request a token for</param>
    /// <param name="manifestPath">Optional path to ToolingManifest.json for MCP scope resolution</param>
    /// <param name="forceRefresh">Force token refresh even if cached token is valid</param>
    public async Task<string> GetAccessTokenForMcpAsync(string resourceUrl, string? manifestPath = null, bool forceRefresh = false)
    {
        var scopes = ResolveScopesForResource(resourceUrl, manifestPath);

        // For now, continue using the same authentication pattern but log the resolved scopes
        _logger.LogInformation("Resolved scopes for resource {ResourceUrl}: {Scopes}", resourceUrl, string.Join(", ", scopes));

        // Use the existing method for backward compatibility
        // In the future, this could use the specific scopes for targeted authentication
        return await GetAccessTokenAsync(resourceUrl, forceRefresh);
    }

    /// <summary>
    /// Resolves the appropriate authentication scopes based on resource URL and MCP manifest
    /// </summary>
    /// <param name="resourceUrl">The resource URL being accessed</param>
    /// <param name="manifestPath">Optional path to ToolingManifest.json</param>
    /// <returns>Array of scope strings to request for authentication</returns>
    public string[] ResolveScopesForResource(string resourceUrl, string? manifestPath = null)
    {
        // Default to Agent 365 Tools resource app ID scope for backward compatibility
        var scope = $"{McpConstants.Agent365ToolsProdAppId}/.default";
        var defaultScopes = new[] { scope };

        // If no manifest path provided, try to find it in current directory
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            var currentDir = Environment.CurrentDirectory;
            manifestPath = Path.Combine(currentDir, "ToolingManifest.json");

            if (!File.Exists(manifestPath))
            {
                _logger.LogDebug("No ToolingManifest.json found, using default Agent 365 Tools resource app ID scope");
                return defaultScopes;
            }
        }

        // Try to read MCP manifest and find relevant scopes
        try
        {
            if (!File.Exists(manifestPath))
            {
                _logger.LogDebug("ToolingManifest.json not found at {Path}, using default scope", manifestPath);
                return defaultScopes;
            }

            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<ToolingManifest>(manifestJson);

            if (manifest?.McpServers == null || manifest.McpServers.Length == 0)
            {
                _logger.LogDebug("No MCP servers found in manifest, using default scope");
                return defaultScopes;
            }

            // Look for MCP servers that match the resource URL
            var relevantScopes = new List<string>();

            foreach (var server in manifest.McpServers)
            {
                // Check if this server's URL matches the resource URL being accessed
                if (!string.IsNullOrWhiteSpace(server.Url))
                {
                    try
                    {
                        var serverUri = new Uri(server.Url);
                        var resourceUri = new Uri(resourceUrl);

                        // Match by host (domain)
                        if (string.Equals(serverUri.Host, resourceUri.Host, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(server.Scope))
                            {
                                relevantScopes.Add(server.Scope);
                                _logger.LogDebug("Found matching MCP server {ServerName} with scope: {Scope}",
                                    server.McpServerName, server.Scope);
                            }
                        }
                    }
                    catch (UriFormatException ex)
                    {
                        _logger.LogWarning("Invalid URL format for MCP server {ServerName}: {Url} - {Error}",
                            server.McpServerName, server.Url, ex.Message);
                    }
                }
            }

            // If we found relevant scopes, use them; otherwise use default
            if (relevantScopes.Count > 0)
            {
                var uniqueScopes = relevantScopes.Distinct().ToArray();
                _logger.LogInformation("Using MCP-specific scopes for {ResourceUrl}: {Scopes}",
                    resourceUrl, string.Join(", ", uniqueScopes));
                return uniqueScopes;
            }

        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve MCP scopes from manifest, using default scope");
        }

        _logger.LogDebug("No matching MCP servers found, using default Power Platform API scope");
        return defaultScopes;
    }

    /// <summary>
    /// Validates that the current authentication token has the required scopes for an MCP server
    /// </summary>
    /// <param name="resourceUrl">The resource URL being accessed</param>
    /// <param name="manifestPath">Optional path to ToolingManifest.json</param>
    /// <returns>True if authentication should work, false if re-authentication may be needed</returns>
    public bool ValidateScopesForResource(string resourceUrl, string? manifestPath = null)
    {
        try
        {
            var requiredScopes = ResolveScopesForResource(resourceUrl, manifestPath);

            // For now, this is a basic validation - in a full implementation,
            // we would decode the JWT token and check the scopes claim
            _logger.LogInformation("Validation check - Required scopes for {ResourceUrl}: {Scopes}",
                resourceUrl, string.Join(", ", requiredScopes));

            // Return true for now since we're using the Power Platform API scope pattern
            // which provides broad access through the api://appid/.default pattern
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate scopes for resource {ResourceUrl}", resourceUrl);
            return false;
        }
    }

    /// <summary>
    /// Clears cached authentication token(s)
    /// </summary>
    public void ClearCache()
    {
        if (File.Exists(_tokenCachePath))
        {
            File.Delete(_tokenCachePath);
            _logger.LogInformation("Authentication cache cleared");
        }
    }

    private class TokenInfo
    {
        public string AccessToken { get; set; } = string.Empty;
        public DateTime ExpiresOn { get; set; }
    }

    private class TokenCache
    {
        public Dictionary<string, TokenInfo> Tokens { get; set; } = new();
    }
}