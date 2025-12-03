// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for handling authentication to Agent 365 Tools
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
    /// Gets an access token for Agent 365, using cached token if valid or prompting for authentication
    /// </summary>
    /// <param name="resourceUrl">The resource URL to request a token for (e.g., https://agent365.svc.cloud.microsoft or environment-specific URL)</param>
    /// <param name="tenantId">Optional tenant ID for single-tenant authentication. If provided and cached token is for different tenant, forces re-authentication</param>
    /// <param name="forceRefresh">Force token refresh even if cached token is valid</param>
    public async Task<string> GetAccessTokenAsync(string resourceUrl, string? tenantId = null, bool forceRefresh = false)
    {
        // Try to load cached token for this resourceUrl
        if (!forceRefresh && File.Exists(_tokenCachePath))
        {
            try
            {
                var cachedToken = await LoadCachedTokenAsync(resourceUrl);
                if (cachedToken != null && !IsTokenExpired(cachedToken))
                {
                    // If tenant ID is specified, validate that cached token is for the correct tenant
                    if (!string.IsNullOrWhiteSpace(tenantId))
                    {
                        if (string.IsNullOrWhiteSpace(cachedToken.TenantId))
                        {
                            _logger.LogWarning("Cached token does not have tenant information. Re-authenticating with tenant {TenantId}...", tenantId);
                            // Fall through to re-authenticate
                        }
                        else if (!string.Equals(cachedToken.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("Cached token is for tenant {CachedTenant} but requested tenant is {RequestedTenant}. Re-authenticating...",
                                cachedToken.TenantId, tenantId);
                            // Fall through to re-authenticate
                        }
                        else
                        {
                            _logger.LogInformation("Using cached authentication token for {ResourceUrl} (tenant: {TenantId})",
                                resourceUrl, tenantId);
                            return cachedToken.AccessToken;
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Using cached authentication token for {ResourceUrl}", resourceUrl);
                        return cachedToken.AccessToken;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load cached token, will re-authenticate");
            }
        }

        // Authenticate interactively with specific tenant
        _logger.LogInformation("Authentication required for Agent 365 Tools");
        var token = await AuthenticateInteractivelyAsync(resourceUrl, tenantId);

        // Cache the token for this resourceUrl
        await CacheTokenAsync(resourceUrl, token);

        return token.AccessToken;
    }

    /// <summary>
    /// Authenticates user interactively using device code flow or browser
    /// </summary>
    /// <param name="resourceUrl">The resource URL to request a token for</param>
    /// <param name="tenantId">Optional tenant ID for single-tenant authentication. If null, uses common tenant</param>
    private async Task<TokenInfo> AuthenticateInteractivelyAsync(string resourceUrl, string? tenantId = null)
    {
        try
        {
            // Use specific tenant ID if provided, otherwise use common tenant for multi-tenant apps
            var effectiveTenantId = string.IsNullOrWhiteSpace(tenantId)
                ? AuthenticationConstants.CommonTenantId
                : tenantId;

            // Determine which scope to use based on the resource URL or App ID
            string scope;
            string environmentName;

            // Check if this is the production App ID
            if (resourceUrl == McpConstants.Agent365ToolsProdAppId)
            {
                scope = $"{resourceUrl}/.default";
                environmentName = "PRODUCTION";
                _logger.LogInformation("Using Agent 365 Tools (PRODUCTION) for authentication");
            }
            // Check for Agent 365 endpoint URLs (legacy support)
            else if (resourceUrl.Contains("agent365", StringComparison.OrdinalIgnoreCase))
            {
                // Use production App ID by default
                // For non-production environments, users should provide the App ID directly via config
                // or set environment variable A365_MCP_APP_ID (without environment suffix for backward compatibility)
                var appId = Environment.GetEnvironmentVariable("A365_MCP_APP_ID") ?? McpConstants.Agent365ToolsProdAppId;
                
                if (appId != McpConstants.Agent365ToolsProdAppId)
                {
                    environmentName = "CUSTOM";
                    _logger.LogInformation("Using custom Agent 365 Tools App ID from A365_MCP_APP_ID environment variable");
                }
                else
                {
                    environmentName = "PRODUCTION";
                    _logger.LogInformation("Using Agent 365 Tools (PRODUCTION) App ID for endpoint URL");
                }

                scope = $"{appId}/.default";
            }
            else
            {
                // Default: use the resource as-is with /.default suffix (likely an App ID)
                // This allows passing custom App IDs directly via config
                scope = resourceUrl.EndsWith("/.default", StringComparison.OrdinalIgnoreCase)
                    ? resourceUrl
                    : $"{resourceUrl}/.default";
                environmentName = "CUSTOM";
                _logger.LogInformation("Using custom resource for authentication: {Resource}", resourceUrl);
            }

            _logger.LogInformation("Token scope: {Scope}", scope);
            _logger.LogInformation("Authenticating for tenant: {TenantId}", effectiveTenantId);

            // For Power Platform API authentication, use device code flow to avoid URL length issues
            // InteractiveBrowserCredential with Power Platform scopes can create URLs that exceed browser limits
            _logger.LogInformation("Opening browser for authentication ({Environment} environment)...", environmentName);
            _logger.LogInformation("Please sign in with your Microsoft account");

            TokenCredential credential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
            {
                TenantId = effectiveTenantId,
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
                ExpiresOn = tokenResult.ExpiresOn.UtcDateTime,
                TenantId = effectiveTenantId
            };
        }
        catch (AuthenticationFailedException ex) when (ex.Message.Contains("code_expired") || ex.InnerException?.Message.Contains("code_expired") == true)
        {
            _logger.LogError("Device code expired - authentication not completed in time");
            throw new AzureAuthenticationException("Device code authentication timed out - please complete authentication promptly when retrying");
        }
        catch (AuthenticationFailedException ex)
        {
            _logger.LogError("Interactive authentication failed: {Message}", ex.Message);
            throw new AzureAuthenticationException($"Authentication failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError("Unexpected authentication error: {Message}", ex.Message);
            throw new AzureAuthenticationException($"Unexpected authentication error: {ex.Message}");
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
    /// <param name="tenantId">Optional tenant ID for single-tenant authentication</param>
    /// <param name="forceRefresh">Force token refresh even if cached token is valid</param>
    public async Task<string> GetAccessTokenForMcpAsync(string resourceUrl, string? manifestPath = null, string? tenantId = null, bool forceRefresh = false)
    {
        var scopes = ResolveScopesForResource(resourceUrl, manifestPath);

        // For now, continue using the same authentication pattern but log the resolved scopes
        _logger.LogInformation("Resolved scopes for resource {ResourceUrl}: {Scopes}", resourceUrl, string.Join(", ", scopes));

        // Use the existing method for backward compatibility
        // In the future, this could use the specific scopes for targeted authentication
        return await GetAccessTokenAsync(resourceUrl, tenantId, forceRefresh);
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
        public string? TenantId { get; set; }
    }

    private class TokenCache
    {
        public Dictionary<string, TokenInfo> Tokens { get; set; } = new();
    }
}