// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for managing Microsoft Graph API permissions and registrations
/// </summary>
public class GraphApiService
{
    private readonly ILogger<GraphApiService> _logger;
    private readonly CommandExecutor _executor;
    private readonly HttpClient _httpClient;
    private readonly IMicrosoftGraphTokenProvider? _tokenProvider;
    private readonly IAuthenticationService _authService;
    private readonly RetryHelper _retryHelper;
    private string _graphBaseUrl;

    // Login hint resolved once per GraphApiService instance.
    // Used to direct MSAL/WAM to the correct identity, preventing the Windows default
    // account (WAM) or a stale cached MSAL account from being used instead.
    // Resolved from az CLI if available, otherwise from the AuthenticationService token cache.
    private string? _loginHint;
    private bool _loginHintResolved;

    // Resolver delegate for the login hint.
    // Defaults to az CLI first, then AuthenticationService JWT cache as fallback.
    // Injectable via constructor so unit tests can bypass the real az process.
    private readonly Func<Task<string?>> _loginHintResolver;

    /// <summary>
    /// Optional custom client app ID to use for authentication with Microsoft Graph PowerShell.
    /// When set, this will be passed to Connect-MgGraph -ClientId parameter.
    /// </summary>
    public string? CustomClientAppId { get; set; }

    /// <summary>
    /// Override the Microsoft Graph base URL for sovereign / government cloud tenants.
    /// Defaults to <see cref="GraphApiConstants.BaseUrl"/> (commercial cloud).
    /// Set this after construction when the config is available (e.g. from Agent365Config.GraphBaseUrl).
    /// </summary>
    public string GraphBaseUrl
    {
        get => _graphBaseUrl;
        set => _graphBaseUrl = string.IsNullOrWhiteSpace(value) ? GraphApiConstants.BaseUrl : value;
    }

    // Lightweight wrapper to surface HTTP status, reason and body to callers
    public record GraphResponse
    {
        public bool IsSuccess { get; init; }
        public int StatusCode { get; init; }
        public string ReasonPhrase { get; init; } = string.Empty;
        public string Body { get; init; } = string.Empty;
        public JsonDocument? Json { get; init; }
    }

    // Allow injecting a custom HttpMessageHandler for unit testing.
    // loginHintResolver: optional override for login-hint resolution.
    // Pass () => Task.FromResult<string?>(null) in unit tests to skip login-hint resolution.
    public GraphApiService(ILogger<GraphApiService> logger, CommandExecutor executor, IAuthenticationService authService, HttpMessageHandler? handler = null, IMicrosoftGraphTokenProvider? tokenProvider = null, Func<Task<string?>>? loginHintResolver = null, string? graphBaseUrl = null, RetryHelper? retryHelper = null)
    {
        _logger = logger;
        _executor = executor;
        _authService = authService;
        _httpClient = handler != null ? new HttpClient(handler) : HttpClientFactory.CreateAuthenticatedClient();
        _tokenProvider = tokenProvider;
        _retryHelper = retryHelper ?? new RetryHelper(_logger);
        // Default: try az CLI first (if present), fall back to JWT cache in AuthenticationService.
        _loginHintResolver = loginHintResolver ?? (() => ResolveLoginHintWithFallbackAsync(authService));
        _graphBaseUrl = string.IsNullOrWhiteSpace(graphBaseUrl) ? GraphApiConstants.BaseUrl : graphBaseUrl;
    }

    // Parameterless constructor to ease test mocking/substitution frameworks which may
    // require creating proxy instances without providing constructor arguments.
    public GraphApiService()
        : this(NullLogger<GraphApiService>.Instance, new CommandExecutor(NullLogger<CommandExecutor>.Instance), new AuthenticationService(NullLogger<AuthenticationService>.Instance), null, null, null)
    {
    }

    // Convenience constructors for tests. Castle DynamicProxy (used by NSubstitute) resolves
    // constructors by exact argument count — it does not handle C# optional parameters.
    // Both forms must exist as separate constructors so Castle can proxy either call site.
    //
    // 2-arg form: used by tests that do not need loginHintResolver control.
    public GraphApiService(ILogger<GraphApiService> logger, CommandExecutor executor)
        : this(logger ?? NullLogger<GraphApiService>.Instance, executor ?? throw new ArgumentNullException(nameof(executor)), new AuthenticationService(NullLogger<AuthenticationService>.Instance), null, null, null)
    {
    }

    // 3-arg form: used by partial-mock tests that inject loginHintResolver to prevent
    // a real az account show subprocess. Pass () => Task.FromResult<string?>(null).
    public GraphApiService(ILogger<GraphApiService> logger, CommandExecutor executor, Func<Task<string?>>? loginHintResolver)
        : this(logger ?? NullLogger<GraphApiService>.Instance, executor ?? throw new ArgumentNullException(nameof(executor)), new AuthenticationService(NullLogger<AuthenticationService>.Instance), null, null, loginHintResolver)
    {
    }

    private static async Task<string?> ResolveLoginHintWithFallbackAsync(IAuthenticationService authService)
    {
        // Try az CLI first — most reliable when the user has run 'az login'.
        var hint = await AzCliHelper.ResolveLoginHintAsync();
        if (!string.IsNullOrWhiteSpace(hint))
            return hint;
        // Fall back to the UPN embedded in a previously cached MSAL JWT.
        return await authService.ResolveLoginHintFromCacheAsync();
    }

    /// <summary>
    /// Acquires an access token for Microsoft Graph API via MSAL (WAM on Windows,
    /// browser/device-code on macOS/Linux). Token is cached persistently by
    /// AuthenticationService — no az CLI subprocess involved.
    /// </summary>
    public virtual async Task<string?> GetGraphAccessTokenAsync(string tenantId, bool forceRefresh = false, CancellationToken ct = default)
    {
        _logger.LogDebug("Acquiring Graph API access token for tenant {TenantId}", tenantId);
        try
        {
            var resource = GraphApiConstants.GetResource(_graphBaseUrl);
            var loginHint = await _loginHintResolver();
            var token = await _authService.GetAccessTokenAsync(resource, tenantId, forceRefresh: forceRefresh, userId: loginHint);
            if (!string.IsNullOrWhiteSpace(token))
            {
                _logger.LogDebug("Graph API access token acquired successfully");
                return token;
            }
            _logger.LogError("Failed to acquire Graph API access token for tenant {TenantId}", tenantId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring Graph API access token");
            return null;
        }
    }


    private async Task<bool> EnsureGraphHeadersAsync(string tenantId, bool forceRefresh = false, IEnumerable<string>? scopes = null, CancellationToken ct = default)
    {
        // Authentication Strategy:
        // 1. If specific scopes required AND token provider configured: Use MSAL with delegated scopes (WAM/browser/device-code)
        // 2. Otherwise: Use MSAL via AuthenticationService (WAM/browser/device-code, persistent cache)
        // All paths go through MSAL — no az CLI subprocess involved.

        string? token;

        if (scopes != null && _tokenProvider != null)
        {
            // Use token provider with delegated scopes (interactive browser auth with caching)
            _logger.LogDebug("Acquiring Graph token with specific scopes via token provider: {Scopes}", string.Join(", ", scopes));
            var loginHint = await ResolveLoginHintAsync();
            token = await _tokenProvider.GetMgGraphAccessTokenAsync(tenantId, scopes, false, CustomClientAppId, ct, loginHint, forceRefresh);

            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogError("Failed to acquire Graph token with scopes: {Scopes}", string.Join(", ", scopes));
                return false;
            }

            _logger.LogDebug("Successfully acquired Graph token with specific scopes (cached or new)");
        }
        else if (scopes != null && _tokenProvider == null)
        {
            // Scopes required but no token provider - this is a configuration issue
            _logger.LogError("Token provider is not configured, but specific scopes are required: {Scopes}", string.Join(", ", scopes));
            return false;
        }
        else
        {
            // Default path: acquire via AuthenticationService (MSAL, persistent disk cache).
            token = await GetGraphAccessTokenAsync(tenantId, forceRefresh: forceRefresh, ct: ct);

            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogError("Failed to acquire Graph token. Sign-in will be prompted on the next attempt.");
                return false;
            }
        }

        // Remove all newline characters and trim whitespace to prevent header validation errors
        token = token.ReplaceLineEndings(string.Empty).Trim();

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // NOTE: Do NOT add "ConsistencyLevel: eventual" header here.
        // This header is only required for advanced Graph query capabilities ($count, $search, certain $filter operations).
        // For simple queries like service principal lookups, this header is not needed and causes HTTP 400 errors.
        // See: https://learn.microsoft.com/en-us/graph/aad-advanced-queries

        return true;
    }

    /// <summary>
    /// Returns the object ID of the currently signed-in user via GET /v1.0/me.
    /// Replaces 'az ad signed-in-user show --query id -o tsv' (~30s) with a Graph HTTP call (~200ms).
    /// Returns null if the call fails (caller should fall back to az CLI).
    /// </summary>
    public virtual async Task<string?> GetCurrentUserObjectIdAsync(string tenantId, CancellationToken ct = default)
    {
        using var doc = await GraphGetAsync(tenantId, "/v1.0/me?$select=id", ct);
        if (doc == null) return null;
        return doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
    }

    /// <summary>
    /// Checks whether a service principal with the given object ID exists in the tenant.
    /// Replaces 'az ad sp show --id {principalId}' (~30s) with a Graph HTTP call (~200ms).
    /// Used for MSI propagation polling — returns true when the SP is visible in the tenant.
    /// </summary>
    public virtual async Task<bool> ServicePrincipalExistsAsync(string tenantId, string principalId, CancellationToken ct = default)
    {
        using var doc = await GraphGetAsync(tenantId, $"/v1.0/servicePrincipals/{principalId}?$select=id", ct);
        return doc != null;
    }

    /// <summary>
    /// Executes a GET request to Microsoft Graph API.
    /// Virtual to allow mocking in unit tests using Moq.
    /// </summary>
    public virtual async Task<JsonDocument?> GraphGetAsync(string tenantId, string relativePath, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, scopes: scopes, ct: ct)) return null;
        var url = GraphApiConstants.BuildUrl(_graphBaseUrl, relativePath);
        try
        {
            using var resp = await _retryHelper.ExecuteWithRetryAsync(
                ct => _httpClient.GetAsync(url, ct), cancellationToken: ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errorBody = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogDebug("Graph GET {Url} failed {Code} {Reason}: {Body}", url, (int)resp.StatusCode, resp.ReasonPhrase, errorBody);
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (NetworkHelper.IsConnectionResetByProxy(ex))
                _logger.LogWarning(NetworkHelper.ConnectionResetWarning);
            else
                _logger.LogDebug(ex, "Graph GET {Url} failed with exception", url);
            return null;
        }
    }

    /// <summary>
    /// GET from Graph and always return HTTP response details (status, body, parsed JSON).
    /// Use this instead of GraphGetAsync when the caller needs to distinguish auth failures
    /// (401) from transient server errors (503, 429, network exceptions).
    /// </summary>
    public virtual async Task<GraphResponse> GraphGetWithResponseAsync(string tenantId, string relativePath, bool forceRefresh = false, IEnumerable<string>? scopes = null, CancellationToken ct = default)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, forceRefresh: forceRefresh, scopes: scopes, ct: ct))
            return new GraphResponse { IsSuccess = false, StatusCode = 0, ReasonPhrase = "NoAuth", Body = "Failed to acquire token" };

        var url = GraphApiConstants.BuildUrl(_graphBaseUrl, relativePath);

        try
        {
            return await _retryHelper.ExecuteWithRetryAsync(async (token) =>
            {
                using var resp = await _httpClient.GetAsync(url, token);
                var body = await resp.Content.ReadAsStringAsync(token);

                JsonDocument? json = null;
                if (resp.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body))
                {
                    try { json = JsonDocument.Parse(body); } catch { /* ignore parse errors */ }
                }

                if (!resp.IsSuccessStatusCode)
                    _logger.LogDebug("Graph GET {Url} failed {Code} {Reason}: {Body}", url, (int)resp.StatusCode, resp.ReasonPhrase, body);

                return new GraphResponse
                {
                    IsSuccess = resp.IsSuccessStatusCode,
                    StatusCode = (int)resp.StatusCode,
                    ReasonPhrase = resp.ReasonPhrase ?? string.Empty,
                    Body = body ?? string.Empty,
                    Json = json
                };
            }, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            if (NetworkHelper.IsConnectionResetByProxy(ex))
                _logger.LogWarning(NetworkHelper.ConnectionResetWarning);
            else
                _logger.LogDebug(ex, "Graph GET {Url} threw an exception", url);
            return new GraphResponse { IsSuccess = false, StatusCode = 0, ReasonPhrase = ex.Message, Body = string.Empty };
        }
    }

    public virtual async Task<JsonDocument?> GraphPostAsync(string tenantId, string relativePath, object payload, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, scopes: scopes, ct: ct)) return null;
        var url = GraphApiConstants.BuildUrl(_graphBaseUrl, relativePath);
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            using var resp = await _httpClient.PostAsync(url, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errorMessage = TryExtractGraphErrorMessage(body);
                if (errorMessage != null)
                    _logger.LogWarning("Graph POST {Url} failed: {ErrorMessage}", url, errorMessage);
                else
                    _logger.LogWarning("Graph POST {Url} failed {Code} {Reason}", url, (int)resp.StatusCode, resp.ReasonPhrase);
                _logger.LogDebug("Graph POST response body: {Body}", body);
                return null;
            }

            return string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (NetworkHelper.IsConnectionResetByProxy(ex))
                _logger.LogWarning(NetworkHelper.ConnectionResetWarning);
            else
                _logger.LogDebug(ex, "Graph POST {Url} failed with exception", url);
            throw;
        }
    }

    /// <summary>
    /// POST to Graph but always return HTTP response details (status, body, parsed JSON)
    /// </summary>
    public virtual async Task<GraphResponse> GraphPostWithResponseAsync(string tenantId, string relativePath, object payload, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, scopes: scopes, ct: ct))
        {
            return new GraphResponse { IsSuccess = false, StatusCode = 0, ReasonPhrase = "NoAuth", Body = "Failed to acquire token" };
        }

        var url = GraphApiConstants.BuildUrl(_graphBaseUrl, relativePath);

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            using var resp = await _httpClient.PostAsync(url, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            JsonDocument? json = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try { json = JsonDocument.Parse(body); } catch { /* ignore parse errors */ }
            }

            return new GraphResponse
            {
                IsSuccess = resp.IsSuccessStatusCode,
                StatusCode = (int)resp.StatusCode,
                ReasonPhrase = resp.ReasonPhrase ?? string.Empty,
                Body = body ?? string.Empty,
                Json = json
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (NetworkHelper.IsConnectionResetByProxy(ex))
                _logger.LogWarning(NetworkHelper.ConnectionResetWarning);
            else
                _logger.LogDebug(ex, "Graph POST {Url} failed with exception", url);
            throw;
        }
    }

    /// <summary>
    /// Executes a PATCH request to Microsoft Graph API.
    /// Virtual to allow mocking in unit tests using Moq.
    /// </summary>
    public virtual async Task<bool> GraphPatchAsync(string tenantId, string relativePath, object payload, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, scopes: scopes, ct: ct)) return false;
        var url = GraphApiConstants.BuildUrl(_graphBaseUrl, relativePath);
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod("PATCH"), url) { Content = content };
            using var resp = await _httpClient.SendAsync(request, ct);

            // Many PATCH calls return 204 NoContent on success
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var errorMessage = TryExtractGraphErrorMessage(body);
                if (errorMessage != null)
                    _logger.LogError("Graph PATCH {Url} failed: {ErrorMessage}", url, errorMessage);
                else
                    _logger.LogError("Graph PATCH {Url} failed {Code} {Reason}", url, (int)resp.StatusCode, resp.ReasonPhrase);
                _logger.LogDebug("Graph PATCH response body: {Body}", body);
            }

            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (NetworkHelper.IsConnectionResetByProxy(ex))
                _logger.LogWarning(NetworkHelper.ConnectionResetWarning);
            else
                _logger.LogDebug(ex, "Graph PATCH {Url} failed with exception", url);
            throw;
        }
    }

    public async Task<bool> GraphDeleteAsync(
        string tenantId,
        string relativePath,
        CancellationToken ct = default,
        bool treatNotFoundAsSuccess = true,
        IEnumerable<string>? scopes = null)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, scopes: scopes, ct: ct)) return false;

        var url = GraphApiConstants.BuildUrl(_graphBaseUrl, relativePath);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, url);
            using var resp = await _httpClient.SendAsync(req, ct);

            // 404 can be considered success for idempotent deletes
            if (treatNotFoundAsSuccess && (int)resp.StatusCode == 404) return true;

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var errorMessage = TryExtractGraphErrorMessage(body);
                if (errorMessage != null)
                    _logger.LogError("Graph DELETE {Url} failed: {ErrorMessage}", url, errorMessage);
                else
                    _logger.LogError("Graph DELETE {Url} failed {Code} {Reason}", url, (int)resp.StatusCode, resp.ReasonPhrase);
                _logger.LogDebug("Graph DELETE response body: {Body}", body);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (NetworkHelper.IsConnectionResetByProxy(ex))
                _logger.LogWarning(NetworkHelper.ConnectionResetWarning);
            else
                _logger.LogDebug(ex, "Graph DELETE {Url} failed with exception", url);
            throw;
        }
    }

    /// <summary>
    /// Looks up a service principal by its application (client) ID.
    /// Virtual to allow mocking in unit tests using Moq.
    /// </summary>
    public virtual async Task<string?> LookupServicePrincipalByAppIdAsync(
        string tenantId, string appId, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        // $filter=appId eq is "Default+Advanced" per Graph docs -� no ConsistencyLevel header required.
        // The token must have Application.Read.All; pass scopes to ensure MSAL token is used when needed.
        using var doc = await GraphGetAsync(
            tenantId,
            $"/v1.0/servicePrincipals?$filter=appId eq '{appId}'&$select=id",
            ct,
            scopes);
        if (doc == null) return null;
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.GetArrayLength() == 0) return null;
        return value[0].GetProperty("id").GetString();
    }

    /// <summary>
    /// Looks up the display name of a service principal by its application ID.
    /// Returns null if the service principal is not found.
    /// Virtual to allow substitution in unit tests using NSubstitute.
    /// </summary>
    public virtual async Task<string?> GetServicePrincipalDisplayNameAsync(
        string tenantId, string appId, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        // Validate GUID format to prevent OData injection
        if (!Guid.TryParse(appId, out var validGuid))
        {
            _logger.LogWarning("Invalid appId format for service principal lookup: {AppId}", appId);
            return null;
        }

        // Use validated GUID in normalized format to prevent OData injection
        using var doc = await GraphGetAsync(tenantId, $"/v1.0/servicePrincipals?$filter=appId eq '{validGuid:D}'&$select=displayName", ct, scopes);
        if (doc == null) return null;
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.GetArrayLength() == 0) return null;
        if (!value[0].TryGetProperty("displayName", out var displayName)) return null;
        return displayName.GetString();
    }

    /// <summary>
    /// Ensures a service principal exists for the given application ID.
    /// Creates the service principal if it doesn't already exist.
    /// Virtual to allow mocking in unit tests using Moq.
    /// </summary>
    public virtual async Task<string> EnsureServicePrincipalForAppIdAsync(
        string tenantId, string appId, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        // Try existing
        var spId = await LookupServicePrincipalByAppIdAsync(tenantId, appId, ct, scopes);
        if (!string.IsNullOrWhiteSpace(spId)) return spId!;

        // Create SP for this application
        var created = await GraphPostAsync(tenantId, "/v1.0/servicePrincipals", new { appId }, ct, scopes);
        if (created == null || !created.RootElement.TryGetProperty("id", out var idProp))
            throw new InvalidOperationException($"Failed to create servicePrincipal for appId {appId}");

        return idProp.GetString()!;
    }

    public async Task<bool> CreateOrUpdateOauth2PermissionGrantAsync(
        string tenantId,
        string clientSpObjectId,
        string resourceSpObjectId,
        IEnumerable<string> scopes,
        CancellationToken ct = default,
        IEnumerable<string>? permissionGrantScopes = null)
    {
        var desiredScopeString = string.Join(' ', scopes);

        // Read existing — extract string values immediately so JsonDocument can be disposed
        string? existingId = null;
        string existingScopes = "";

        using (var listDoc = await GraphGetAsync(
            tenantId,
            $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{clientSpObjectId}' and resourceId eq '{resourceSpObjectId}'",
            ct,
            permissionGrantScopes))
        {
            if (listDoc?.RootElement.TryGetProperty("value", out var arr) == true && arr.GetArrayLength() > 0)
            {
                var grant = arr[0];
                existingId = grant.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                existingScopes = grant.TryGetProperty("scope", out var scopeProp) ? scopeProp.GetString() ?? "" : "";
            }
        }

        if (string.IsNullOrWhiteSpace(existingId))
        {
            // AllPrincipals (tenant-wide) grants require Global Administrator.
            // Only called from admin paths (setup admin or setup all run by GA).
            var payload = new
            {
                clientId = clientSpObjectId,
                consentType = "AllPrincipals",
                resourceId = resourceSpObjectId,
                scope = desiredScopeString
            };

            _logger.LogDebug("Graph POST /v1.0/oauth2PermissionGrants body: {Body}", JsonSerializer.Serialize(payload));

            // A freshly-created service principal may not yet be visible to the
            // oauth2PermissionGrants replica (Directory_ObjectNotFound). Retry with
            // exponential back-off so the command is self-healing without user intervention.
            const int maxRetries = 8;
            const int baseDelaySeconds = 5;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                var grantResponse = await GraphPostWithResponseAsync(tenantId, "/v1.0/oauth2PermissionGrants", payload, ct, permissionGrantScopes);
                // Dispose the error JSON immediately — only IsSuccess and Body are needed below.
                grantResponse.Json?.Dispose();

                if (grantResponse.IsSuccess)
                    return true;

                if (!grantResponse.Body.Contains("Directory_ObjectNotFound", StringComparison.OrdinalIgnoreCase))
                    return false; // non-transient error, do not retry

                if (attempt < maxRetries - 1)
                {
                    var delaySecs = (int)Math.Min(baseDelaySeconds * Math.Pow(2, attempt), 60);
                    _logger.LogWarning(
                        "Service principal not yet replicated to grants endpoint — retrying in {Delay}s (attempt {Attempt}/{Max})...",
                        delaySecs, attempt + 1, maxRetries - 1);
                    await Task.Delay(TimeSpan.FromSeconds(delaySecs), ct);
                }
            }

            _logger.LogWarning(
                "OAuth2 permission grant failed after {MaxRetries} retries — service principal may still be propagating. " +
                "Re-run 'a365 setup admin' to retry.",
                maxRetries);
            return false;
        }

        // Merge scopes if needed
        var currentSet = new HashSet<string>(existingScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var desiredSet = new HashSet<string>(desiredScopeString.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        if (desiredSet.IsSubsetOf(currentSet)) return true; // already satisfied

        currentSet.UnionWith(desiredSet);
        var merged = string.Join(' ', currentSet);

        return await GraphPatchAsync(tenantId, $"/v1.0/oauth2PermissionGrants/{existingId}", new { scope = merged }, ct, permissionGrantScopes);
    }

    /// <summary>
    /// Checks if the current user has sufficient privileges to create service principals.
    /// Virtual to allow mocking in unit tests using Moq.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if user has required roles, false otherwise</returns>
    public virtual async Task<(bool hasPrivileges, List<string> roles)> CheckServicePrincipalCreationPrivilegesAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        try
        {
            _logger.LogDebug("Checking user's directory roles for service principal creation privileges");

            var token = await GetGraphAccessTokenAsync(tenantId, ct: ct);
            if (token == null)
            {
                _logger.LogWarning("Could not acquire Graph token to check privileges");
                return (false, new List<string>());
            }

            // Trim token to remove any newline characters that may cause header validation errors
            token = token.Trim();

            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{_graphBaseUrl}/v1.0/me/memberOf/microsoft.graph.directoryRole");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Could not retrieve user's directory roles: {Status}", response.StatusCode);
                return (false, new List<string>());
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);

            var roles = new List<string>();
            if (doc.RootElement.TryGetProperty("value", out var rolesArray))
            {
                roles = rolesArray.EnumerateArray()
                    .Where(role => role.TryGetProperty("displayName", out var displayName))
                    .Select(role => role.GetProperty("displayName").GetString())
                    .Where(roleName => !string.IsNullOrEmpty(roleName))
                    .ToList()!;
            }

            _logger.LogDebug("User has {Count} directory roles", roles.Count);

            // Check for required roles
            var requiredRoles = new[]
            {
                "Application Administrator",
                "Cloud Application Administrator",
                "Global Administrator"
            };

            var hasRequiredRole = roles.Any(r => requiredRoles.Contains(r, StringComparer.OrdinalIgnoreCase));

            if (hasRequiredRole)
            {
                _logger.LogDebug("User has sufficient privileges for service principal creation");
            }
            else
            {
                _logger.LogDebug("User does not have required roles for service principal creation. Roles: {Roles}",
                    string.Join(", ", roles));
            }

            return (hasRequiredRole, roles);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check service principal creation privileges: {Message}", ex.Message);
            return (false, new List<string>());
        }
    }

    /// <summary>
    /// Checks if a user is an owner of an application (read-only validation).
    /// Does not attempt to add the user as owner, only verifies ownership.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="applicationObjectId">The application object ID (not the client/app ID)</param>
    /// <param name="userObjectId">The user's object ID to check. If null, uses the current authenticated user.</param>
    /// <param name="ct">Cancellation token</param>
    /// <param name="scopes">OAuth2 scopes for elevated permissions (e.g., Application.ReadWrite.All, Directory.ReadWrite.All)</param>
    /// <returns>True if the user is an owner, false otherwise</returns>
    public virtual async Task<bool> IsApplicationOwnerAsync(
        string tenantId,
        string applicationObjectId,
        string? userObjectId = null,
        CancellationToken ct = default,
        IEnumerable<string>? scopes = null)
    {
        try
        {
            // Get current user's object ID if not provided
            if (string.IsNullOrWhiteSpace(userObjectId))
            {
                if (!await EnsureGraphHeadersAsync(tenantId, scopes: scopes, ct: ct))
                {
                    _logger.LogWarning("Could not acquire Graph token to check application owner");
                    return false;
                }

                using var meRequest = new HttpRequestMessage(HttpMethod.Get,
                    $"{_graphBaseUrl}/v1.0/me?$select=id");
                meRequest.Headers.Authorization = _httpClient.DefaultRequestHeaders.Authorization;

                using var meResponse = await _httpClient.SendAsync(meRequest, ct);
                if (!meResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Could not retrieve current user's ID: {Status}", meResponse.StatusCode);
                    return false;
                }

                var meJson = await meResponse.Content.ReadAsStringAsync(ct);
                using var meDoc = JsonDocument.Parse(meJson);

                if (!meDoc.RootElement.TryGetProperty("id", out var idElement))
                {
                    _logger.LogWarning("Could not extract user ID from Graph response");
                    return false;
                }

                userObjectId = idElement.GetString();
            }

            if (string.IsNullOrWhiteSpace(userObjectId))
            {
                _logger.LogWarning("User object ID is empty, cannot check owner");
                return false;
            }

            // Check if user is an owner
            _logger.LogDebug("Checking if user {UserId} is an owner of application {AppObjectId}", userObjectId, applicationObjectId);

            var ownersDoc = await GraphGetAsync(tenantId, $"/v1.0/applications/{applicationObjectId}/owners?$select=id", ct, scopes);
            if (ownersDoc != null && ownersDoc.RootElement.TryGetProperty("value", out var ownersArray))
            {
                var isOwner = ownersArray.EnumerateArray()
                    .Where(owner => owner.TryGetProperty("id", out var ownerId))
                    .Any(owner => string.Equals(owner.GetProperty("id").GetString(), userObjectId, StringComparison.OrdinalIgnoreCase));

                return isOwner;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking if user is owner of application: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Checks whether the currently signed-in user holds the Global Administrator role,
    /// which is required to grant tenant-wide admin consent interactively.
    /// Uses only <see cref="AuthenticationConstants.UserReadScope"/> — works for both admin and non-admin users.
    /// Returns <see cref="Models.RoleCheckResult.Unknown"/> (non-blocking) if the check cannot be completed.
    /// </summary>
    public virtual async Task<Models.RoleCheckResult> IsCurrentUserAdminAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        return await CheckDirectoryRoleAsync(tenantId, AuthenticationConstants.GlobalAdminRoleTemplateId, ct);
    }

    /// <summary>
    /// Checks whether the currently signed-in user holds the Agent ID Administrator role,
    /// which is required to create or update inheritable permissions on agent blueprints.
    /// Uses only <see cref="AuthenticationConstants.UserReadScope"/> — works for both admin and non-admin users.
    /// Returns <see cref="Models.RoleCheckResult.Unknown"/> (non-blocking) if the check cannot be completed.
    /// </summary>
    public virtual async Task<Models.RoleCheckResult> IsCurrentUserAgentIdAdminAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        return await CheckDirectoryRoleAsync(tenantId, AuthenticationConstants.AgentIdAdminRoleTemplateId, ct);
    }

    /// <summary>
    /// Returns <see cref="Models.RoleCheckResult.HasRole"/> if the role is confirmed active,
    /// <see cref="Models.RoleCheckResult.DoesNotHaveRole"/> if confirmed absent, or
    /// <see cref="Models.RoleCheckResult.Unknown"/> if the check itself failed (e.g. network error,
    /// throttling, auth failure) — in which case the caller should attempt the operation
    /// anyway and let the API surface the real error.
    /// Queries /me/transitiveMemberOf/microsoft.graph.directoryRole, which requires only
    /// User.Read and succeeds for both admin and non-admin users.
    /// Note: PIM-eligible-but-not-activated assignments are not considered active.
    /// </summary>
    private async Task<Models.RoleCheckResult> CheckDirectoryRoleAsync(string tenantId, string roleTemplateId, CancellationToken ct)
    {
        try
        {
            // /me/transitiveMemberOf is a directory query — Directory.Read.All is required.
            // User.Read is insufficient and would return Unknown for most users.
            IEnumerable<string>? scopes = _tokenProvider != null
                ? [AuthenticationConstants.DirectoryReadAllScope]
                : null;

            string? nextUrl = "/v1.0/me/transitiveMemberOf/microsoft.graph.directoryRole?$select=roleTemplateId";

            while (nextUrl != null)
            {
                using var doc = await GraphGetAsync(tenantId, nextUrl, ct, scopes);

                if (doc == null)
                    return Models.RoleCheckResult.Unknown;

                if (!doc.RootElement.TryGetProperty("value", out var roles))
                {
                    _logger.LogWarning("Unexpected Graph response shape — 'value' property missing from transitiveMemberOf response.");
                    return Models.RoleCheckResult.Unknown;
                }

                if (roles.EnumerateArray().Any(r =>
                        r.TryGetProperty("roleTemplateId", out var id) &&
                        string.Equals(id.GetString(), roleTemplateId, StringComparison.OrdinalIgnoreCase)))
                    return Models.RoleCheckResult.HasRole;

                nextUrl = doc.RootElement.TryGetProperty("@odata.nextLink", out var nextLink)
                    ? nextLink.GetString()
                    : null;
            }

            return Models.RoleCheckResult.DoesNotHaveRole;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Role check for {TemplateId} failed — will attempt operation anyway: {Message}",
                roleTemplateId, ex.Message);
            return Models.RoleCheckResult.Unknown;
        }
    }

    /// <summary>
    /// Resolves the Azure CLI login hint once per instance from 'az account show'.
    /// The hint is passed to MSAL so that WAM and silent auth target the correct
    /// Azure CLI identity instead of the Windows default account.
    /// Returns null if az account show fails or the user field is absent.
    /// </summary>
    private async Task<string?> ResolveLoginHintAsync()
    {
        if (_loginHintResolved)
            return _loginHint;

        _loginHintResolved = true;
        _loginHint = await _loginHintResolver();
        return _loginHint;
    }

    /// <summary>
    /// Registers an agent instance in the Microsoft Agent Registry via
    /// POST /beta/agentRegistry/agentInstances.
    /// Requires the caller to hold the "Agent Registry Administrator" Entra role
    /// and have consented to the AgentInstance.ReadWrite.All delegated scope.
    /// Returns the new agent instance ID, or null on failure.
    /// </summary>
    public virtual async Task<string?> RegisterAgentInstanceAsync(
        string tenantId,
        string displayName,
        string? agentBlueprintId,
        CancellationToken ct = default)
    {
        // Resolve the current user's object ID so we can populate ownerIds (required field).
        using var meDoc = await GraphGetAsync(tenantId, "/v1.0/me?$select=id", ct);
        if (meDoc == null)
        {
            _logger.LogError("Failed to retrieve current user ID from Microsoft Graph.");
            return null;
        }

        if (!meDoc.RootElement.TryGetProperty("id", out var userIdProp))
        {
            _logger.LogError("Current user ID not found in Graph /me response.");
            return null;
        }

        var currentUserId = userIdProp.GetString();

        var payload = new Dictionary<string, object?>
        {
            ["ownerIds"] = new[] { currentUserId },
            ["displayName"] = displayName
        };

        if (!string.IsNullOrWhiteSpace(agentBlueprintId))
            payload["agentIdentityBlueprintId"] = agentBlueprintId;

        _logger.LogInformation("POST https://graph.microsoft.com/beta/agentRegistry/agentInstances");
        _logger.LogInformation("Body: {{\"ownerIds\":[\"{UserId}\"],\"displayName\":\"{DisplayName}\",\"agentIdentityBlueprintId\":\"{BlueprintId}\"}}",
            currentUserId, displayName, agentBlueprintId ?? "(none)");

        // AgentInstance.ReadWrite.All is a user-delegated scope (no admin consent required).
        // We must request it explicitly so EnsureGraphHeadersAsync uses the MSAL path with the
        // custom client app — that app already has AgentInstance.ReadWrite.All consented via
        // RequiredClientAppPermissions. Using the az CLI token (no scope) would require the
        // scope to be consented on the Azure CLI app instead, which is not the expected setup.
        IEnumerable<string>? registrationScopes = _tokenProvider != null
            ? [Constants.AuthenticationConstants.AgentInstanceReadWriteAllScope]
            : null;

        var firstResponse = await GraphPostWithResponseAsync(tenantId, "/beta/agentRegistry/agentInstances", payload, ct, registrationScopes);

        if (firstResponse.IsSuccess)
        {
            var instanceId = ExtractAgentInstanceId(firstResponse);
            firstResponse.Json?.Dispose();
            if (instanceId == null)
                _logger.LogError("Agent instance created but response did not contain an 'id' field.");
            return instanceId;
        }

        var firstStatusCode = firstResponse.StatusCode;
        var firstBody = firstResponse.Body;
        firstResponse.Json?.Dispose();

        // On auth failure (0 = token acquisition failed): no point retrying.
        if (firstStatusCode == 0)
        {
            _logger.LogError("Failed to acquire an access token for the agent registry request. Ensure 'az login' is completed.");
            return null;
        }

        // On non-403: log the status and body so the caller has something to act on.
        if (firstStatusCode != 403)
        {
            _logger.LogError("Agent registry POST failed with HTTP {StatusCode}. Body: {Body}", firstStatusCode, firstBody);
            return null;
        }

        // On 403: the 'Agent Registry Administrator' role may not have propagated yet. Retry once.
        _logger.LogInformation("403 from agent registry — 'Agent Registry Administrator' role may not have propagated yet. Retrying once...");

        var retryResponse = await GraphPostWithResponseAsync(tenantId, "/beta/agentRegistry/agentInstances", payload, ct, registrationScopes);

        if (retryResponse.IsSuccess)
        {
            _logger.LogInformation("Agent instance registration succeeded on retry.");
            var instanceId = ExtractAgentInstanceId(retryResponse);
            retryResponse.Json?.Dispose();
            if (instanceId == null)
                _logger.LogError("Agent instance created but retry response did not contain an 'id' field.");
            return instanceId;
        }

        var retryStatusCode = retryResponse.StatusCode;
        retryResponse.Json?.Dispose();

        if (retryStatusCode == 403)
        {
            _logger.LogError(
                "Still 403 after retry. Ensure the 'Agent Registry Administrator' role is " +
                "assigned in Entra ID for the account running the CLI. " +
                "If the role was recently assigned, wait 5-15 minutes for propagation and retry.");
        }
        else if (retryStatusCode == 0)
        {
            _logger.LogError("Token re-acquisition failed on retry. Ensure 'az login' is still valid.");
        }
        else
        {
            _logger.LogError("Agent registry POST failed on retry with HTTP {StatusCode}.", retryStatusCode);
        }

        return null;
    }

    /// <summary>
    /// Registers an agent instance via the AgentX Agent Registration API V2
    /// (POST https://agentxppe.microsoft.com/api/a365/agents/registration).
    /// Acquires a bearer token via the token provider (delegated, AgentX.Access scope) when
    /// configured, or falls back to az CLI for the AgentX resource.
    /// Returns the new agent instance ID on success (202 Accepted), or null on failure.
    /// </summary>
    public virtual async Task<string?> RegisterAgentInstanceAsyncV2(
        string tenantId,
        string displayName,
        string? description,
        string? blueprintId,
        string? agentIdentityId,
        string? clientAppId,
        CancellationToken ct = default)
    {
        // Resolve current user ID from Graph (needed for ownerIds and createdBy).
        var currentUserId = await GetCurrentUserObjectIdAsync(tenantId, ct);
        if (string.IsNullOrWhiteSpace(currentUserId))
        {
            _logger.LogError("Failed to retrieve current user ID — required for agent registration V2.");
            return null;
        }

        // Acquire AgentX token — prefer delegated (token provider) over az CLI.
        string? token = null;

        var agentXScopes = new[] { Constants.AuthenticationConstants.AgentXAccessScope };

        if (_tokenProvider != null)
        {
            var loginHint = await ResolveLoginHintAsync();
            token = await _tokenProvider.GetMgGraphAccessTokenAsync(
                tenantId, agentXScopes, false, CustomClientAppId, ct, loginHint);

            if (string.IsNullOrWhiteSpace(token))
                _logger.LogWarning("Delegated token acquisition for AgentX failed — falling back to az CLI.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            var loginHint2 = await ResolveLoginHintAsync();
            token = await _authService.GetAccessTokenAsync(
                Constants.AuthenticationConstants.AgentXResource, tenantId, userId: loginHint2);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogError("Failed to acquire AgentX access token. Ensure the custom app has 'AgentX.Access' consented and authentication is completed.");
            return null;
        }

        token = token.ReplaceLineEndings(string.Empty).Trim();

        var now = DateTimeOffset.UtcNow.ToString("o");
        var payload = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["displayName"] = displayName,
            ["ownerIds"] = new[] { currentUserId },
            ["createdBy"] = currentUserId,
            ["createdDateTime"] = now,
            ["lastModifiedDateTime"] = now,
        };

        if (!string.IsNullOrWhiteSpace(description))
            payload["description"] = description;
        if (!string.IsNullOrWhiteSpace(blueprintId))
            payload["agentIdentityBlueprintId"] = blueprintId;
        // sourceAgentId is required by the contract. Use agentIdentityId when available,
        // fall back to blueprintId as the stable external identifier.
        payload["sourceAgentId"] = !string.IsNullOrWhiteSpace(agentIdentityId) ? agentIdentityId : blueprintId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(agentIdentityId))
            payload["agentIdentityId"] = agentIdentityId;
        // managedBy must be the AgentX service app ID, not the CLI client app ID.
        // Using the CLI client app ID causes 424 "You do not have permission to create
        // an agent registration managed by another AppId."
        payload["managedBy"] = "59eca866-2f46-40b8-96ff-63f663121ef9";

        var json = JsonSerializer.Serialize(payload);
        var url = $"{Constants.AuthenticationConstants.AgentXBaseUrl}/api/a365/agents/registration";

        _logger.LogInformation("POST {Url} (AgentX V2)", url);
        _logger.LogInformation("Body: {Body}", json);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            _logger.LogInformation("AgentX V2 response: {StatusCode} {Reason}", (int)response.StatusCode, response.ReasonPhrase);

            if ((int)response.StatusCode == 202 || response.IsSuccessStatusCode)
            {
                _logger.LogDebug("AgentX V2 response body: {Body}", body);

                // Extract the registration ID from the 202 response body, or fall back to our generated ID.
                string? registrationId = null;
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("id", out var idProp))
                            registrationId = idProp.GetString();
                    }
                    catch (JsonException) { }
                }
                registrationId ??= payload["id"]?.ToString();

                // 202 = accepted for async processing. Poll GET up to 3 times (10s apart)
                // to confirm the registration is fully committed and visible in MAC.
                if (!string.IsNullOrWhiteSpace(registrationId))
                {
                    var getUrl = $"{Constants.AuthenticationConstants.AgentXBaseUrl}/api/a365/agents/registration/{registrationId}";
                    const int maxAttempts = 3;
                    const int delaySeconds = 10;
                    bool confirmed = false;

                    for (int attempt = 1; attempt <= maxAttempts && !confirmed; attempt++)
                    {
                        try
                        {
                            _logger.LogInformation("GET {Url} (status check {Attempt}/{Max})", getUrl, attempt, maxAttempts);
                            using var getRequest = new HttpRequestMessage(HttpMethod.Get, getUrl);
                            getRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                            using var getResponse = await _httpClient.SendAsync(getRequest, ct);
                            _logger.LogInformation("AgentX GET response: {StatusCode} {Reason}", (int)getResponse.StatusCode, getResponse.ReasonPhrase);

                            if (getResponse.IsSuccessStatusCode)
                            {
                                confirmed = true;
                                _logger.LogInformation("Agent registration confirmed visible (attempt {Attempt})", attempt);
                            }
                            else if (attempt < maxAttempts)
                            {
                                _logger.LogDebug("Not yet visible (HTTP {StatusCode}), retrying in {Delay}s...", (int)getResponse.StatusCode, delaySeconds);
                                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                            }
                            else
                            {
                                _logger.LogWarning("Agent registration accepted but not yet visible after {Total}s — it may appear in MAC shortly.", maxAttempts * delaySeconds);
                            }
                        }
                        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                        {
                            _logger.LogDebug("AgentX GET status check attempt {Attempt} failed (non-fatal): {Message}", attempt, ex.Message);
                            break;
                        }
                    }
                }

                return registrationId;
            }

            _logger.LogError("AgentX agent registration failed with HTTP {StatusCode}. Body: {Body}", (int)response.StatusCode, body);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "AgentX agent registration request failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Deletes an agent registration via the AgentX Agent Registration API V2
    /// (DELETE https://agentxppe.microsoft.com/api/a365/agents/registration/{id}).
    /// Returns true on success (204) or if the registration was already deleted (404).
    /// </summary>
    public virtual async Task<bool> DeleteAgentRegistrationAsync(
        string tenantId,
        string registrationId,
        CancellationToken ct = default)
    {
        // Acquire AgentX token — prefer delegated (token provider) over az CLI.
        string? token = null;
        var agentXScopes = new[] { Constants.AuthenticationConstants.AgentXAccessScope };

        if (_tokenProvider != null)
        {
            var loginHint = await ResolveLoginHintAsync();
            token = await _tokenProvider.GetMgGraphAccessTokenAsync(
                tenantId, agentXScopes, false, CustomClientAppId, ct, loginHint);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            var loginHint2 = await ResolveLoginHintAsync();
            token = await _authService.GetAccessTokenAsync(
                Constants.AuthenticationConstants.AgentXResource, tenantId, userId: loginHint2);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogError("Failed to acquire AgentX access token for delete.");
            return false;
        }

        token = token.ReplaceLineEndings(string.Empty).Trim();
        var url = $"{Constants.AuthenticationConstants.AgentXBaseUrl}/api/a365/agents/registration/{registrationId}";
        _logger.LogInformation("DELETE {Url} (AgentX V2)", url);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await _httpClient.SendAsync(request, ct);
            _logger.LogInformation("AgentX delete response: {StatusCode} {Reason}", (int)response.StatusCode, response.ReasonPhrase);

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent ||
                response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return true;

            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("AgentX agent delete failed with HTTP {StatusCode}. Body: {Body}", (int)response.StatusCode, body);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "AgentX agent delete request failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Deletes an agent instance from the Microsoft Agent Registry via
    /// DELETE /beta/agentRegistry/agentInstances/{instanceId}.
    /// Requires AgentInstance.ReadWrite.All delegated scope.
    /// Returns true on success or if the instance was already deleted (404).
    /// </summary>
    public virtual async Task<bool> DeleteAgentInstanceAsync(
        string tenantId,
        string instanceId,
        CancellationToken ct = default)
    {
        IEnumerable<string>? scopes = _tokenProvider != null
            ? [Constants.AuthenticationConstants.AgentInstanceReadWriteAllScope]
            : null;

        _logger.LogInformation("DELETE https://graph.microsoft.com/beta/agentRegistry/agentInstances/{InstanceId}", instanceId);

        return await GraphDeleteAsync(
            tenantId,
            $"/beta/agentRegistry/agentInstances/{instanceId}",
            ct,
            treatNotFoundAsSuccess: true,
            scopes: scopes);
    }

    /// <summary>
    /// Acquires an access token for the blueprint application using the OAuth 2.0 client credentials flow.
    /// Used by <see cref="CreateAgentIdentityAsync"/> to authenticate as the blueprint application itself.
    /// </summary>
    public virtual async Task<string?> GetBlueprintAccessTokenAsync(
        string tenantId,
        string clientId,
        string clientSecret,
        CancellationToken ct,
        string? correlationId = null)
    {
        var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? HttpClientFactory.GenerateCorrelationId()
            : correlationId;

        try
        {
            _logger.LogDebug("Acquiring blueprint access token via client credentials (CorrelationId: {Id})", effectiveCorrelationId);

            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(correlationId: effectiveCorrelationId);
            var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

            const int maxRetries = 5;
            const int baseDelaySeconds = 5;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                // FormUrlEncodedContent is a one-shot stream — must be recreated per attempt.
                using var requestBody = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", clientId),
                    new KeyValuePair<string, string>("client_secret", clientSecret),
                    new KeyValuePair<string, string>("scope", "https://graph.microsoft.com/.default"),
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                });

                using var response = await httpClient.PostAsync(tokenEndpoint, requestBody, ct);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync(ct);
                    using var tokenDoc = JsonDocument.Parse(responseContent);
                    return tokenDoc.RootElement.GetProperty("access_token").GetString();
                }

                var errorContent = await response.Content.ReadAsStringAsync(ct);

                // AADSTS7000215 means the credential exists in AAD but is not yet visible on this
                // replica — same eventual consistency window as object replication. Retry with backoff.
                var isCredentialPropagationLag = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    && errorContent.Contains("AADSTS7000215", StringComparison.OrdinalIgnoreCase);

                if (!isCredentialPropagationLag || attempt == maxRetries - 1)
                {
                    _logger.LogError("Failed to acquire blueprint access token: {Status} - {Error}",
                        response.StatusCode, errorContent);
                    if (errorContent.Contains("invalid_client", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError("Invalid client credentials — verify the blueprint client secret in a365.generated.config.json is correct and not expired.");
                    }
                    return null;
                }

                var delaySecs = Math.Min(baseDelaySeconds * (int)Math.Pow(2, attempt), 60);
                _logger.LogInformation(
                    "Blueprint credential not yet propagated (AADSTS7000215) — retrying in {Delay}s (attempt {Attempt} of {Max})...",
                    delaySecs, attempt + 1, maxRetries - 1);
                await Task.Delay(TimeSpan.FromSeconds(delaySecs), ct);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception acquiring blueprint access token: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Creates an Agent Identity in the tenant using the delegated flow.
    /// Authenticates as the calling user with <c>AgentIdentity.Create.All</c> scope and calls
    /// POST /beta/servicePrincipals/Microsoft.Graph.AgentIdentity with agentIdentityBlueprintId.
    /// Requires Agent ID Administrator, Agent ID Developer, or Global Administrator role.
    /// No blueprint client secret required — preferred over the client-credentials path when possible.
    /// </summary>
    /// <returns>The agent identity ID on success, null on failure.</returns>
    public virtual async Task<string?> CreateAgentIdentityDelegatedAsync(
        string tenantId,
        string blueprintId,
        string displayName,
        CancellationToken ct)
    {
        var correlationId = HttpClientFactory.GenerateCorrelationId();
        _logger.LogDebug("Creating agent identity via delegated flow (CorrelationId: {Id})", correlationId);

        string? currentUserId = null;
        try
        {
            currentUserId = await GetCurrentUserObjectIdAsync(tenantId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve current user ID (non-fatal): {Message}", ex.Message);
        }

        var scopes = new[] { Constants.AuthenticationConstants.AgentIdentityCreateAllScope };

        // Log the token claims once here so it's easy to correlate with the 403 if it fails.
        if (_tokenProvider != null)
        {
            try
            {
                var loginHint = await ResolveLoginHintAsync();
                var previewToken = await _tokenProvider.GetMgGraphAccessTokenAsync(
                    tenantId, scopes, false, CustomClientAppId, ct, loginHint);
                if (!string.IsNullOrWhiteSpace(previewToken))
                {
                    var scp = TryDecodeTokenClaim(previewToken, "scp");
                    var upn = TryDecodeTokenClaim(previewToken, "upn") ?? TryDecodeTokenClaim(previewToken, "unique_name");
                    _logger.LogInformation("Agent identity token scp : {Scp}", scp ?? "(missing)");
                    _logger.LogInformation("Agent identity token upn : {Upn}", upn ?? "(missing)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not preview token claims (non-fatal)");
            }
        }

        try
        {
            var body = new JsonObject
            {
                ["displayName"] = displayName,
                ["agentIdentityBlueprintId"] = blueprintId,
            };

            if (!string.IsNullOrWhiteSpace(currentUserId))
            {
                body["sponsors@odata.bind"] = new JsonArray
                {
                    $"https://graph.microsoft.com/v1.0/users/{currentUserId}"
                };
                body["owners@odata.bind"] = new JsonArray
                {
                    $"https://graph.microsoft.com/v1.0/users/{currentUserId}"
                };
            }

            _logger.LogInformation("POST https://graph.microsoft.com/beta/servicePrincipals/Microsoft.Graph.AgentIdentity (delegated)");
            _logger.LogInformation("Body: {Body}", body.ToJsonString());

            // Use GraphPostWithResponseAsync so we can log the full error body on failure.
            var postResult = await GraphPostWithResponseAsync(
                tenantId,
                "/beta/servicePrincipals/Microsoft.Graph.AgentIdentity",
                body,
                ct,
                scopes: scopes);

            if (!postResult.IsSuccess)
            {
                _logger.LogWarning("Graph POST /beta/servicePrincipals/Microsoft.Graph.AgentIdentity failed: HTTP {Status} {Reason}",
                    postResult.StatusCode, postResult.ReasonPhrase);
                _logger.LogInformation("Error response body: {Body}", postResult.Body);
                postResult.Json?.Dispose();
                return null;
            }

            if (postResult.Json == null)
            {
                _logger.LogDebug("Delegated agent identity creation returned null — will fall back to client credentials if available.");
                return null;
            }

            using var doc = postResult.Json;

            var id = doc.RootElement.GetProperty("id").GetString();
            _logger.LogInformation("Agent identity created via delegated flow (ID: {Id})", id);
            return id;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Delegated agent identity creation failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Creates an Agent Identity in the tenant by instantiating the blueprint.
    /// Authenticates as the blueprint application (client credentials), then calls
    /// POST /beta/serviceprincipals/Microsoft.Graph.AgentIdentity.
    /// Saves the returned identity ID as <c>AgenticAppId</c> in the config.
    /// </summary>
    /// <returns>The agent identity ID on success, null on failure.</returns>
    public virtual async Task<string?> CreateAgentIdentityAsync(
        string tenantId,
        string blueprintId,
        string blueprintClientSecret,
        string displayName,
        CancellationToken ct)
    {
        var correlationId = HttpClientFactory.GenerateCorrelationId();

        if (string.IsNullOrWhiteSpace(blueprintClientSecret))
        {
            _logger.LogError("Blueprint client secret is required to create agent identity. " +
                "Ensure blueprint setup completed successfully.");
            return null;
        }

        var appToken = await GetBlueprintAccessTokenAsync(
            tenantId, blueprintId, blueprintClientSecret, ct, correlationId);

        if (string.IsNullOrWhiteSpace(appToken))
        {
            _logger.LogError("Failed to acquire blueprint access token for agent identity creation.");
            return null;
        }

        // Optionally include the current user as sponsor.
        string? currentUserId = null;
        try
        {
            currentUserId = await GetCurrentUserObjectIdAsync(tenantId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve current user ID for sponsor (non-fatal): {Message}", ex.Message);
        }

        try
        {
            _logger.LogDebug("Creating agent identity (CorrelationId: {Id})", correlationId);

            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(appToken, correlationId: correlationId);

            var body = new JsonObject
            {
                ["displayName"] = displayName,
                ["agentAppId"] = blueprintId,
            };

            if (!string.IsNullOrWhiteSpace(currentUserId))
            {
                body["sponsors@odata.bind"] = new JsonArray
                {
                    $"https://graph.microsoft.com/v1.0/users/{currentUserId}"
                };
            }

            using var content = new StringContent(
                body.ToJsonString(),
                System.Text.Encoding.UTF8,
                "application/json");

            using var response = await httpClient.PostAsync(
                "https://graph.microsoft.com/beta/serviceprincipals/Microsoft.Graph.AgentIdentity",
                content,
                ct);

            // Some tenants reject sponsor binding — retry without it.
            if (!response.IsSuccessStatusCode && response.StatusCode == System.Net.HttpStatusCode.BadRequest
                && !string.IsNullOrWhiteSpace(currentUserId))
            {
                _logger.LogDebug("Agent identity creation with sponsor failed (400); retrying without sponsor.");
                body.Remove("sponsors@odata.bind");
                using var content2 = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                using var response2 = await httpClient.PostAsync(
                    "https://graph.microsoft.com/beta/serviceprincipals/Microsoft.Graph.AgentIdentity",
                    content2,
                    ct);

                if (!response2.IsSuccessStatusCode)
                {
                    var err = await response2.Content.ReadAsStringAsync(ct);
                    _logger.LogError("Failed to create agent identity: {Status} - {Error}", response2.StatusCode, err);
                    return null;
                }

                var json2 = await response2.Content.ReadAsStringAsync(ct);
                using var doc2 = JsonDocument.Parse(json2);
                var id2 = doc2.RootElement.GetProperty("id").GetString();
                _logger.LogInformation("Agent identity created (ID: {Id})", id2);
                return id2;
            }

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Failed to create agent identity: {Status} - {Error}", response.StatusCode, err);
                if (err.Contains("Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("calling identity type", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Authorization denied. Ensure the blueprint application has " +
                        "Application.ReadWrite.All and AgentIdentity.Create.OwnedBy application permissions.");
                }
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("id").GetString();
            _logger.LogInformation("Agent identity created (ID: {Id})", id);
            return id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create agent identity: {Message}", ex.Message);
            return null;
        }
    }

    private static string? ExtractAgentInstanceId(GraphResponse response)
    {
        if (response.Json == null) return null;
        if (!response.Json.RootElement.TryGetProperty("id", out var idProp))
            return null;
        return idProp.GetString();
    }

    /// <summary>
    /// Decodes a JWT payload and returns the value of the specified claim.
    /// Used for debug logging only — never log the full token.
    /// Returns null if the token cannot be decoded or the claim is absent.
    /// </summary>
    private static string? TryDecodeTokenClaim(string token, string claimName)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1];
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(claimName, out var claim) ? claim.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to extract a human-readable error message from a Graph API JSON error response body.
    /// Returns null if the body cannot be parsed or does not contain an error message.
    /// </summary>
    private static string? TryExtractGraphErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var msg))
            {
                return msg.GetString();
            }
        }
        catch { /* ignore parse errors */ }
        return null;
    }
}
