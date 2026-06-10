// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Abstraction over MSAL token acquisition used by ArmApiService and GraphApiService.
/// Enables test substitution without triggering real interactive authentication.
/// Co-located with AuthenticationService per the related-interfaces convention.
/// </summary>
public interface IAuthenticationService
{
    Task<string> GetAccessTokenAsync(
        string resourceUrl,
        string? tenantId = null,
        bool forceRefresh = false,
        string? clientId = null,
        IEnumerable<string>? scopes = null,
        bool useInteractiveBrowser = true,
        string? userId = null,
        CancellationToken ct = default);

    Task<string?> ResolveLoginHintFromCacheAsync();

    Task ClearTokenCacheAsync();
}

/// <summary>
/// Service for handling authentication to Agent 365 Tools and Microsoft Graph API.
///
/// AUTHENTICATION STRATEGY:
/// - Uses interactive authentication by default (no device code flow)
/// - Typical user experience: 1-2 authentication prompts for entire CLI workflow
///
/// TOKEN CACHING:
/// - This service does NOT persist access tokens to disk itself.
/// - All token persistence is delegated to the OS-protected MSAL persistent cache
///   (<c>msal-token-cache</c>) managed by <see cref="MsalBrowserCredential"/>:
///   DPAPI-encrypted on Windows, Keychain on macOS, and a 0600 owner-only file on Linux.
/// - Cache Location: %LocalApplicationData%\Microsoft.Agents.A365.DevTools.Cli\msal-token-cache
/// - Silent re-acquisition (no prompt) is performed by MSAL using the cached refresh token.
///
/// AUTHENTICATION FLOW:
/// 1. Call into MSAL, which acquires silently from its persistent cache when possible.
/// 2. If silent acquisition is not possible: prompt for interactive authentication.
/// 3. MSAL persists the resulting refresh token in its OS-protected cache.
///
/// MULTI-COMMAND WORKFLOW:
/// - First command (e.g., 'setup all'): 1-2 authentication prompts.
/// - Subsequent commands: 0 prompts (MSAL acquires silently from its persistent cache).
/// </summary>
public class AuthenticationService : IAuthenticationService
{
    private readonly ILogger<AuthenticationService> _logger;
    // Stored so cache-clearing helpers can compute the MSAL cache path (and the legacy
    // auth-token.json path for migration cleanup) without a cross-class dependency on
    // MsalBrowserCredential's private static field.
    private readonly string _cacheDir;

    /// <summary>
    /// Legacy plaintext access-token cache file name. The CLI no longer writes this file —
    /// access tokens are now persisted only by the OS-protected MSAL cache. The name is
    /// retained solely so cache-clearing paths can best-effort delete any file left behind
    /// by an older CLI version (migration cleanup).
    /// </summary>
    private const string LegacyTokenCacheFileName = "auth-token.json";

    // Deduplicates the "Authentication context" audit line so it only logs when the
    // resolved user or tenant changes between token acquisitions.
    private readonly object _authContextLogLock = new();
    private string? _lastLoggedUser;
    private string? _lastLoggedTenant;

    public AuthenticationService(ILogger<AuthenticationService> logger)
    {
        _logger = logger;
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cacheDir = Path.Combine(appDataPath, AuthenticationConstants.ApplicationName);
        Directory.CreateDirectory(_cacheDir);

        // One-time migration cleanup: delete any plaintext auth-token.json written by an older CLI
        // version. Runs once per process. TEMPORARY — this call and DeleteLegacyTokenCache can be
        // removed a couple of releases after this ships, once upgraded installs have all run once.
        DeleteLegacyTokenCache();
    }

    /// <summary>
    /// Clears the persistent MSAL token cache. Callers invoke this after an operation that
    /// invalidates cached tokens — most commonly after adding an optional claim (like <c>wids</c>)
    /// to the client app registration, where existing access tokens lack the new claim and need
    /// to be re-issued. Silent re-acquisition on the next call (via WAM on Windows or refresh-token
    /// flow elsewhere) typically completes without re-prompting the user.
    /// </summary>
    public Task ClearTokenCacheAsync() => ClearMsalCacheAsync();

    /// <summary>
    /// Gets an access token for Agent 365, using cached token if valid or prompting for authentication
    /// </summary>
    /// <param name="resourceUrl">The resource URL to request a token for (e.g., https://agent365.svc.cloud.microsoft or environment-specific URL)</param>
    /// <param name="tenantId">Optional tenant ID for single-tenant authentication. If provided and cached token is for different tenant, forces re-authentication</param>
    /// <param name="forceRefresh">Force token refresh even if cached token is valid</param>
    /// <param name="clientId">Optional client ID for authentication. If not provided, uses PowerShell client ID</param>
    /// <param name="scopes">Optional explicit scopes to request. If not provided, uses .default scope pattern</param>
    public async Task<string> GetAccessTokenAsync(
        string resourceUrl,
        string? tenantId = null,
        bool forceRefresh = false,
        string? clientId = null,
        IEnumerable<string>? scopes = null,
        bool useInteractiveBrowser = true,
        string? userId = null,
        CancellationToken ct = default)
    {
        // Access tokens are no longer cached to disk by this service. Token persistence and
        // silent re-acquisition are delegated entirely to the OS-protected MSAL persistent cache
        // (managed by MsalBrowserCredential). When forceRefresh is requested, the underlying
        // credential is configured to bypass MSAL's silent cache and acquire a fresh token.
        _logger.LogDebug("Authentication required for Agent 365 Tools");
        var token = await AuthenticateInteractivelyAsync(resourceUrl, tenantId, clientId, scopes, useInteractiveBrowser, loginHint: userId, forceRefresh: forceRefresh, ct: ct);

        // Self-heal: validate the tid claim in the returned JWT against the requested tenant.
        // WAM may silently select a cached work account from a different tenant when multiple
        // Windows accounts are present (issue #430). On mismatch, clear the MSAL persistent cache
        // (and any legacy plaintext cache) to reset WAM's account selection, then retry once.
        // Only compare tid when the requested tenantId is a GUID — JWT tid claims are always
        // GUIDs, so comparison against a domain-form tenantId (e.g. contoso.onmicrosoft.com)
        // would always appear as a mismatch, causing unnecessary cache clears and retry loops.
        if (!string.IsNullOrWhiteSpace(tenantId) && Guid.TryParse(tenantId, out _))
        {
            var returnedTid = JwtHelper.TryDecodeClaim(token.AccessToken, "tid");
            if (!string.IsNullOrWhiteSpace(returnedTid) &&
                !string.Equals(returnedTid, tenantId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Authentication returned token for tenant {ReturnedTenant} but {RequestedTenant} is required. " +
                    "Clearing cached credentials and retrying...",
                    returnedTid, tenantId);
                await ClearMsalCacheAsync();
                // Retry once with the same parameters — MSAL disk cache is now empty so WAM
                // gets a clean slate and will either pick the correct account or prompt.
                token = await AuthenticateInteractivelyAsync(resourceUrl, tenantId, clientId, scopes, useInteractiveBrowser, loginHint: userId, forceRefresh: forceRefresh, ct: ct);
                var retryTid = JwtHelper.TryDecodeClaim(token.AccessToken, "tid");
                if (!string.IsNullOrWhiteSpace(retryTid) &&
                    !string.Equals(retryTid, tenantId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AzureAuthenticationException(
                        $"The account selected does not match the configured tenant ({tenantId}). " +
                        $"Ensure 'az login' targets the correct tenant, or select the correct account when prompted.");
                }
            }
        }

        // Validate the token identity: if a userId was requested, ensure the returned token is
        // actually for that user. WAM may return a guest/cross-app token for an account it
        // considers "equivalent" (same Microsoft account in a different tenant). We log the
        // mismatch for diagnostics but still return the token — it may be valid for this call.
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var returnedUpn = TryExtractUpnFromJwt(token.AccessToken);
            if (!string.IsNullOrWhiteSpace(returnedUpn) &&
                !string.Equals(returnedUpn, userId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(
                    "Authentication returned token for {ReturnedUser} but {RequestedUser} was requested.",
                    returnedUpn, userId);
            }
        }

        LogAuthenticationContext(token.AccessToken, token.TenantId, userId, resourceUrl);
        return token.AccessToken;
    }

    /// <summary>
    /// Authenticates user interactively using browser or device code flow
    /// </summary>
    /// <param name="resourceUrl">The resource URL to request a token for</param>
    /// <param name="tenantId">Optional tenant ID for single-tenant authentication. If null, uses common tenant</param>
    /// <param name="clientId">Optional client ID for authentication. If not provided, uses PowerShell client ID</param>
    /// <param name="explicitScopes">Optional explicit scopes to request. If not provided, uses .default scope pattern</param>
    /// <param name="useInteractiveBrowser">If true, uses browser authentication with redirect URI; if false, uses device code flow. Default is false for backward compatibility.</param>
    private async Task<TokenInfo> AuthenticateInteractivelyAsync(
        string resourceUrl,
        string? tenantId = null,
        string? clientId = null,
        IEnumerable<string>? explicitScopes = null,
        bool useInteractiveBrowser = false,
        string? loginHint = null,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        // Declare variables outside try block so they're available in catch for logging
        string effectiveTenantId = tenantId ?? "unknown";
        string effectiveClientId = clientId ?? "unknown";
        string[] scopes = Array.Empty<string>();
        
        try
        {
            // Use specific tenant ID if provided, otherwise use common tenant for multi-tenant apps
            effectiveTenantId = string.IsNullOrWhiteSpace(tenantId)
                ? AuthenticationConstants.CommonTenantId
                : tenantId;

            // Determine which scope to use based on the resource URL or App ID
            if (explicitScopes != null && explicitScopes.Any())
            {
                // Construct scope strings for the token request by prefixing with the resource App ID
                // This creates the format required by Azure AD for the TokenRequestContext: {resourceAppId}/{scope}
                // Example: "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1/McpServers.Mail.All"
                scopes = explicitScopes.Select(s => $"{resourceUrl}/{s}").ToArray();
                _logger.LogDebug("Using explicit scopes for authentication: {Scopes}", string.Join(", ", explicitScopes));
                _logger.LogDebug("Formatted as: {FormattedScopes}", string.Join(", ", scopes));
            }
            else
            {
                string scope;
                // Check if this is the production App ID
                if (resourceUrl == McpConstants.WorkIQToolsProdAppId)
                {
                    scope = $"{resourceUrl}/.default";
                    _logger.LogDebug("Authenticating to Agent 365 Tools");
                }
                // Check for Agent 365 endpoint URLs (legacy support)
                else if (resourceUrl.Contains("agent365", StringComparison.OrdinalIgnoreCase))
                {
                    // Use production App ID by default
                    // For non-production environments, users should provide the App ID directly via config
                    // or set environment variable A365_MCP_APP_ID (without environment suffix for backward compatibility)
                    var appId = Environment.GetEnvironmentVariable("A365_MCP_APP_ID") ?? McpConstants.WorkIQToolsProdAppId;

                    if (appId != McpConstants.WorkIQToolsProdAppId)
                    {
                        _logger.LogDebug("Using custom Agent 365 Tools App ID from A365_MCP_APP_ID environment variable");
                    }
                    else
                    {
                        _logger.LogDebug("Authenticating to Agent 365 Tools");
                    }

                    scope = $"{appId}/.default";
                }
                else
                {
                    // Default: use the resource as-is with /.default suffix (likely an App ID)
                    // This allows passing custom App IDs directly via config
                    scope = resourceUrl.EndsWith("/.default", StringComparison.OrdinalIgnoreCase)
                        ? resourceUrl
                        : $"{resourceUrl.TrimEnd('/')}/.default";
                    _logger.LogDebug("Using custom resource for authentication: {Resource}", resourceUrl);
                }
                scopes = [scope];
                _logger.LogDebug("Token scope: {Scope}", scope);
            }

            _logger.LogDebug("Authenticating for tenant: {TenantId}", effectiveTenantId);

            // Use provided client ID or default to PowerShell client ID
            effectiveClientId = string.IsNullOrWhiteSpace(clientId) 
                ? AuthenticationConstants.PowershellClientId 
                : clientId;

            TokenCredential credential;

            if (useInteractiveBrowser)
            {
                // Use MsalBrowserCredential which handles WAM on Windows and browser on other platforms
                _logger.LogDebug("Using interactive authentication (browser/WAM)...");

                credential = CreateBrowserCredential(effectiveClientId, effectiveTenantId, loginHint: loginHint, forceRefresh: forceRefresh);
            }
            else
            {
                // Device code flow - works in all environments including SSH/remote sessions
                _logger.LogDebug("Using device code authentication...");
                _logger.LogDebug("Please sign in with your Microsoft account");
                credential = CreateDeviceCodeCredential(effectiveClientId, effectiveTenantId);
            }

            var tokenRequestContext = new TokenRequestContext(scopes);
            AccessToken tokenResult;
            try
            {
                tokenResult = await credential.GetTokenAsync(tokenRequestContext, ct);
            }
            catch (MsalAuthenticationFailedException ex) when (useInteractiveBrowser && ex.InnerException is PlatformNotSupportedException)
            {
                _logger.LogWarning("Browser authentication is not supported on this platform, falling back to device code flow...");
                _logger.LogDebug("Using device code authentication...");
                _logger.LogDebug("Please sign in with your Microsoft account");
                var deviceCodeCredential = CreateDeviceCodeCredential(effectiveClientId, effectiveTenantId);
                tokenResult = await deviceCodeCredential.GetTokenAsync(tokenRequestContext, ct);
            }
            _logger.LogDebug("Authentication successful!");

            return new TokenInfo
            {
                AccessToken = tokenResult.Token,
                ExpiresOn = tokenResult.ExpiresOn.UtcDateTime,
                // Store the decoded JWT tid only when the requested tenantId is also a GUID.
                // If callers pass a domain name (e.g. contoso.onmicrosoft.com), storing the
                // GUID tid would cause the next cache-read comparison to always fail, forcing
                // re-authentication on every run.
                TenantId = Guid.TryParse(effectiveTenantId, out _)
                    ? JwtHelper.TryDecodeClaim(tokenResult.Token, "tid") ?? effectiveTenantId
                    : effectiveTenantId
            };
        }
        catch (MsalAuthenticationFailedException ex) when (ex.Message.Contains("code_expired") || ex.InnerException?.Message.Contains("code_expired") == true)
        {
            _logger.LogError("Device code expired - authentication not completed in time");
            throw new AzureAuthenticationException("Device code authentication timed out - please complete authentication promptly when retrying");
        }
        catch (MsalAuthenticationFailedException ex)
        {
            _logger.LogError("Interactive authentication failed: {Message}", ex.Message);
            _logger.LogError("Exception type: {Type}", ex.GetType().FullName);
            
            if (ex.InnerException != null)
            {
                _logger.LogError("Inner exception: {InnerMessage}", ex.InnerException.Message);
                _logger.LogError("Inner exception type: {InnerType}", ex.InnerException.GetType().FullName);
            }
            
            // Log more details for debugging
            _logger.LogError("Requested scopes: {Scopes}", string.Join(", ", scopes));
            _logger.LogError("Tenant ID: {TenantId}", effectiveTenantId);
            _logger.LogError("Client ID: {ClientId}", effectiveClientId);
            
            throw new AzureAuthenticationException($"Authentication failed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            // Honor cancellation — never wrap into AzureAuthenticationException. Otherwise
            // Ctrl+C is silently converted to an auth failure and callers either return null
            // or fall through to interactive prompts.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("Unexpected authentication error: {Message}", ex.Message);
            _logger.LogError("Exception type: {Type}", ex.GetType().FullName);
            
            if (ex.InnerException != null)
            {
                _logger.LogError("Inner exception: {InnerMessage}", ex.InnerException.Message);
            }
            
            throw new AzureAuthenticationException($"Unexpected authentication error: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets an access token with explicit scopes for MCP servers or other resources
    /// This is a convenience wrapper around GetAccessTokenAsync with scope validation
    /// </summary>
    /// <param name="resourceAppId">The resource application ID (e.g., Agent 365 Tools App ID)</param>
    /// <param name="scopes">Explicit list of scopes to request (e.g., ["McpServers.Mail.All", "McpServers.Calendar.All"])</param>
    /// <param name="tenantId">Optional tenant ID for single-tenant authentication</param>
    /// <param name="forceRefresh">Force token refresh even if cached token is valid</param>
    /// <param name="clientId">Optional client ID for authentication. If not provided, uses PowerShell client ID</param>
    /// <param name="userId">Optional UPN/email to pre-select the account for WAM and silent acquisition.
    /// When provided, WAM will target this identity instead of the first cached account.</param>
    /// <returns>Access token with the requested scopes</returns>
    public async Task<string> GetAccessTokenWithScopesAsync(
        string resourceAppId,
        IEnumerable<string> scopes,
        string? tenantId = null,
        bool forceRefresh = false,
        string? clientId = null,
        bool useInteractiveBrowser = true,
        string? userId = null)
    {
        if (string.IsNullOrWhiteSpace(resourceAppId))
            throw new ArgumentException("Resource App ID cannot be empty", nameof(resourceAppId));

        if (scopes == null || !scopes.Any())
            throw new ArgumentException("At least one scope must be specified", nameof(scopes));

        _logger.LogDebug("Requesting token for resource {ResourceAppId} with explicit scopes: {Scopes}",
            resourceAppId, string.Join(", ", scopes));

        // Delegate to the consolidated GetAccessTokenAsync method
        return await GetAccessTokenAsync(resourceAppId, tenantId, forceRefresh, clientId, scopes, useInteractiveBrowser, userId);
    }

    /// <summary>
    /// Gets an access token with scope resolution for MCP servers
    /// This method uses the .default scope pattern for backward compatibility
    /// For explicit scope control, use GetAccessTokenWithScopesAsync instead
    /// </summary>
    /// <param name="resourceUrl">The resource URL to request a token for</param>
    /// <param name="manifestPath">Optional path to ToolingManifest.json for MCP scope resolution</param>
    /// <param name="tenantId">Optional tenant ID for single-tenant authentication</param>
    /// <param name="forceRefresh">Force token refresh even if cached token is valid</param>
    public async Task<string> GetAccessTokenForMcpAsync(string resourceUrl, string? manifestPath = null, string? tenantId = null, bool forceRefresh = false)
    {
        var scopes = ResolveScopesForResource(resourceUrl, manifestPath);

        // For now, continue using the same authentication pattern but log the resolved scopes
        _logger.LogDebug("Resolved scopes for resource {ResourceUrl}: {Scopes}", resourceUrl, string.Join(", ", scopes));

        // Use the existing method for backward compatibility
        // For explicit scope control, callers should use GetAccessTokenWithScopesAsync
        var loginHint = await AzCliHelper.ResolveLoginHintAsync();
        return await GetAccessTokenAsync(resourceUrl, tenantId, forceRefresh, userId: loginHint);
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
        var scope = $"{McpConstants.WorkIQToolsProdAppId}/.default";
        var defaultScopes = new[] { scope };

        // If no manifest path provided, try to find it in current directory
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            var currentDir = Environment.CurrentDirectory;
            manifestPath = Path.Combine(currentDir, McpConstants.ToolingManifestFileName);

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
                _logger.LogDebug("Using MCP-specific scopes for {ResourceUrl}: {Scopes}",
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
            _logger.LogDebug("Validation check - Required scopes for {ResourceUrl}: {Scopes}",
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
    /// Creates a browser credential for interactive authentication.
    /// Protected virtual to allow substitution in tests.
    /// </summary>
    /// <param name="forceRefresh">When true, the credential bypasses MSAL's silent cache and
    /// acquires a fresh access token (used to honor the public forceRefresh contract now that
    /// the plaintext app-level cache has been removed).</param>
    protected virtual TokenCredential CreateBrowserCredential(string clientId, string tenantId, string? loginHint = null, bool forceRefresh = false)
        => new MsalBrowserCredential(clientId, tenantId, redirectUri: null, _logger, loginHint: loginHint, forceRefresh: forceRefresh);

    /// <summary>
    /// Creates a DeviceCodeCredential configured for interactive device code authentication.
    /// This flow works in all environments including SSH, remote sessions, and platforms where
    /// browser-based authentication is unavailable.
    /// Protected virtual to allow substitution in tests.
    /// </summary>
    protected virtual TokenCredential CreateDeviceCodeCredential(string clientId, string tenantId)
    {
        return new DeviceCodeCredential(new DeviceCodeCredentialOptions
        {
            TenantId = tenantId,
            ClientId = clientId,
            AuthorityHost = AzureAuthorityHosts.AzurePublicCloud,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = AuthenticationConstants.ApplicationName
            },
            DeviceCodeCallback = (code, cancellation) =>
            {
                _logger.LogInformation("");
                _logger.LogInformation("==========================================================================");
                _logger.LogInformation("To sign in, use a web browser to open the page:");
                _logger.LogInformation("    {VerificationUri}", code.VerificationUri);
                _logger.LogInformation("");
                _logger.LogInformation("And enter the code: {UserCode}", code.UserCode);
                _logger.LogInformation("==========================================================================");
                _logger.LogInformation("");
                return Task.CompletedTask;
            }
        });
    }

    /// <summary>
    /// Resolves the login hint (UPN) from the OS-protected MSAL persistent cache by reading the
    /// first cached account's username. Used to pre-select the correct account for WAM/MSAL when
    /// the Azure CLI is not available. Returns null if no account is cached or the lookup fails.
    /// </summary>
    public Task<string?> ResolveLoginHintFromCacheAsync()
        => MsalBrowserCredential.TryGetCachedAccountUsernameAsync(
            AuthenticationConstants.PowershellClientId, _logger);

    private static string? TryExtractUpnFromJwt(string? jwt)
    {
        // Try the UPN claim variants in order of specificity.
        // Delegates to JwtHelper.TryDecodeClaim for the shared Base64Url decode.
        return JwtHelper.TryDecodeClaim(jwt, "upn")
            ?? JwtHelper.TryDecodeClaim(jwt, "preferred_username")
            ?? JwtHelper.TryDecodeClaim(jwt, "unique_name");
    }

    /// <summary>
    /// Deletes the OS-protected MSAL persistent cache so the next acquisition starts from a clean
    /// account list. Non-fatal; errors are logged at Debug level. (Legacy plaintext-cache cleanup
    /// runs once at construction and is not repeated here.)
    /// </summary>
    private Task ClearMsalCacheAsync()
    {
        // MSAL persistent cache (WAM/browser) — the active token store.
        var msalCachePath = Path.Combine(_cacheDir, AuthenticationConstants.MsalCacheFileName);
        try
        {
            if (File.Exists(msalCachePath))
            {
                File.Delete(msalCachePath);
                _logger.LogDebug("Cleared MSAL token cache at {Path}", msalCachePath);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: a stale cache only means the user re-acquires sooner than expected,
            // not that anything breaks. Surface at Debug so the operator can see why if needed.
            _logger.LogDebug(ex, "Failed to clear MSAL token cache at {Path}: {Message}", msalCachePath, ex.Message);
        }


        return Task.CompletedTask;
    }

    private void LogAuthenticationContext(
        string accessToken,
        string? fallbackTenantId,
        string? fallbackUserId,
        string resourceUrl)
    {
        var user = TryExtractUpnFromJwt(accessToken) ?? fallbackUserId ?? "(unknown)";
        var tenant = JwtHelper.TryDecodeClaim(accessToken, "tid") ?? fallbackTenantId ?? "(unknown)";

        if (TryClaimContextChange(user, tenant))
        {
            _logger.LogInformation(
                "Authentication context: API calls will use user {User} in tenant {TenantId}",
                user,
                tenant);
        }

        _logger.LogDebug(
            "Resolved access token for {ResourceUrl} using user {User} in tenant {TenantId}",
            resourceUrl,
            user,
            tenant);
    }

    /// <summary>
    /// Records the current authentication user/tenant and returns whether it changed
    /// since the last logged context. Thread-safe.
    /// </summary>
    private bool TryClaimContextChange(string user, string tenant)
    {
        lock (_authContextLogLock)
        {
            var changed = !string.Equals(_lastLoggedUser, user, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_lastLoggedTenant, tenant, StringComparison.OrdinalIgnoreCase);

            if (changed)
            {
                _lastLoggedUser = user;
                _lastLoggedTenant = tenant;
            }

            return changed;
        }
    }

    /// <summary>
    /// Best-effort deletion of the legacy plaintext <c>auth-token.json</c> cache. The current CLI
    /// never writes this file; this exists solely to clean up artifacts from older versions.
    /// </summary>
    private void DeleteLegacyTokenCache()
    {
        var legacyPath = Path.Combine(_cacheDir, LegacyTokenCacheFileName);
        try
        {
            if (File.Exists(legacyPath))
            {
                File.Delete(legacyPath);
                _logger.LogDebug("Removed legacy plaintext token cache at {Path}", legacyPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to remove legacy token cache at {Path}: {Message}", legacyPath, ex.Message);
        }
    }

    /// <summary>
    /// Best-effort removal of the legacy plaintext token cache file. Retained as a public hook so
    /// callers (and tests) can clean up artifacts from older CLI versions. The current CLI never
    /// writes a plaintext token file — token persistence is owned by the MSAL persistent cache.
    /// </summary>
    public void ClearCache() => DeleteLegacyTokenCache();


    private class TokenInfo
    {
        public string AccessToken { get; set; } = string.Empty;
        public DateTime ExpiresOn { get; set; }
        public string? TenantId { get; set; }
    }
}
