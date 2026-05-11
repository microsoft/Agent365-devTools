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

    // Delay before retrying a 403 from the agent registry (role propagation lag).
    // Injectable so unit tests can pass TimeSpan.Zero and avoid the real 30s wait.
    private readonly TimeSpan _agentRegistryRetryDelay;

    // Graph path for the copilot agent registrations endpoint.
    // Both RegisterAgentInstanceAsyncV2 and DeleteAgentRegistrationAsync use this path.
    private const string AgentRegistrationsPath = "/beta/copilot/agentRegistrations";

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
    public GraphApiService(ILogger<GraphApiService> logger, CommandExecutor executor, IAuthenticationService authService, HttpMessageHandler? handler = null, IMicrosoftGraphTokenProvider? tokenProvider = null, Func<Task<string?>>? loginHintResolver = null, string? graphBaseUrl = null, RetryHelper? retryHelper = null, TimeSpan? agentRegistryRetryDelay = null)
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
        _agentRegistryRetryDelay = agentRegistryRetryDelay ?? TimeSpan.FromSeconds(30);
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
            var token = await _authService.GetAccessTokenAsync(resource, tenantId, forceRefresh: forceRefresh, userId: loginHint, ct: ct);
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

        bool hasScopes = scopes?.Any() == true;
        if (hasScopes && _tokenProvider != null)
        {
            // Use token provider with delegated scopes (interactive browser auth with caching)
            _logger.LogDebug("Acquiring Graph token with specific scopes via token provider: {Scopes}", string.Join(", ", scopes!));
            var loginHint = await ResolveLoginHintAsync();
            token = await _tokenProvider.GetMgGraphAccessTokenAsync(tenantId, scopes!, false, CustomClientAppId, ct, loginHint, forceRefresh);

            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogError("Failed to acquire Graph token with scopes: {Scopes}", string.Join(", ", scopes!));
                return false;
            }

            _logger.LogDebug("Successfully acquired Graph token with specific scopes (cached or new)");
        }
        else if (hasScopes && _tokenProvider == null)
        {
            // Scopes required but no token provider - this is a configuration issue
            _logger.LogError("Token provider is not configured, but specific scopes are required: {Scopes}", string.Join(", ", scopes!));
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
    /// Returns the set of delegated scope value names (e.g. "Agent365.Observability.OtelWrite")
    /// that are published by the service principal's resource app manifest.
    /// Used to filter permission grant calls to only include scopes that exist in the tenant.
    /// Returns an empty set if the call fails or the SP exposes no delegated scopes.
    /// </summary>
    public virtual async Task<HashSet<string>> GetAvailableScopeNamesAsync(
        string tenantId, string spObjectId, CancellationToken ct = default)
    {
        using var doc = await GraphGetAsync(tenantId, $"/v1.0/servicePrincipals/{spObjectId}?$select=oauth2PermissionScopes", ct);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc?.RootElement.TryGetProperty("oauth2PermissionScopes", out var arr) == true)
        {
            foreach (var scope in arr.EnumerateArray())
            {
                if (scope.TryGetProperty("value", out var val) && val.GetString() is string name)
                    result.Add(name);
            }
        }
        return result;
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

    public virtual async Task<JsonDocument?> GraphPostAsync(string tenantId, string relativePath, object payload, CancellationToken ct = default, IEnumerable<string>? scopes = null, bool logWarningOnFailure = true)
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
                if (logWarningOnFailure)
                {
                    if (errorMessage != null)
                        _logger.LogWarning("Graph POST {Url} failed: {ErrorMessage}", url, errorMessage);
                    else
                        _logger.LogWarning("Graph POST {Url} failed {Code} {Reason}", url, (int)resp.StatusCode, resp.ReasonPhrase);
                }
                else
                {
                    if (errorMessage != null)
                        _logger.LogDebug("Graph POST {Url} failed: {ErrorMessage}", url, errorMessage);
                    else
                        _logger.LogDebug("Graph POST {Url} failed {Code} {Reason}", url, (int)resp.StatusCode, resp.ReasonPhrase);
                }
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
    public virtual async Task<GraphResponse> GraphPostWithResponseAsync(string tenantId, string relativePath, object payload, CancellationToken ct = default, IEnumerable<string>? scopes = null, bool forceRefresh = false)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, forceRefresh: forceRefresh, scopes: scopes, ct: ct))
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
                if (!string.IsNullOrWhiteSpace(errorMessage))
                    _logger.LogError("Graph DELETE {Url} failed {Code}: {ErrorMessage}", url, (int)resp.StatusCode, errorMessage);
                else
                    _logger.LogError("Graph DELETE {Url} failed {Code} {Reason}: {Body}", url, (int)resp.StatusCode, resp.ReasonPhrase, body);
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
    /// Checks whether an Entra application with the given appId exists in the tenant.
    /// Uses the default az CLI token — does not require CustomClientAppId to be set.
    /// Returns false on any error so callers can fall back gracefully.
    /// Virtual to allow mocking in unit tests.
    /// </summary>
    public virtual async Task<bool> ApplicationExistsByAppIdAsync(
        string tenantId, string appId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(appId, out var validGuid)) return false;

        using var doc = await GraphGetAsync(
            tenantId,
            $"/v1.0/applications?$filter=appId eq '{validGuid:D}'&$select=appId&$top=1",
            ct);
        if (doc == null) return false;
        return doc.RootElement.TryGetProperty("value", out var value) && value.GetArrayLength() > 0;
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
    /// Finds an application's appId by its display name using a Graph advanced query.
    /// Uses ConsistencyLevel: eventual (required for string filter on displayName).
    /// Returns null if not found or on error. Does not require CustomClientAppId — uses the
    /// default auth token path so it can be called before the client app is resolved.
    /// </summary>
    public virtual async Task<string?> FindApplicationByDisplayNameAsync(
        string tenantId, string displayName, CancellationToken ct = default)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, ct: ct)) return null;

        // OData requires single quotes to be escaped by doubling them: ' → ''
        var escaped = displayName.Replace("'", "''", StringComparison.Ordinal);
        var url = GraphApiConstants.BuildUrl(_graphBaseUrl,
            $"/v1.0/applications?$filter=displayName eq '{escaped}'&$select=appId&$top=1&$count=true");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Copy auth header set by EnsureGraphHeadersAsync onto the shared _httpClient
            if (_httpClient.DefaultRequestHeaders.Authorization is { } auth)
                request.Headers.Authorization = auth;
            // Required for advanced query filters (displayName eq)
            request.Headers.TryAddWithoutValidation("ConsistencyLevel", "eventual");

            using var resp = await _httpClient.SendAsync(request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("FindApplicationByDisplayName {Name} failed {Code}", displayName, (int)resp.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("value", out var value) || value.GetArrayLength() == 0)
                return null;

            return value[0].TryGetProperty("appId", out var appId) ? appId.GetString() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to find application by display name {Name}", displayName);
            return null;
        }
    }

    /// <summary>
    /// Ensures a service principal exists for the given application ID.
    /// Creates the service principal if it doesn't already exist.
    /// Returns null if the SP could not be found or created (e.g. insufficient privileges).
    /// Virtual to allow mocking in unit tests using Moq.
    /// </summary>
    public virtual async Task<string?> EnsureServicePrincipalForAppIdAsync(
        string tenantId, string appId, CancellationToken ct = default, IEnumerable<string>? scopes = null,
        bool logWarningOnCreateFailure = true)
    {
        // Try existing
        var spId = await LookupServicePrincipalByAppIdAsync(tenantId, appId, ct, scopes);
        if (!string.IsNullOrWhiteSpace(spId)) return spId;

        // Create SP for this application (suppresses warning log when logWarningOnCreateFailure is false)
        var created = await GraphPostAsync(tenantId, "/v1.0/servicePrincipals", new { appId }, ct, scopes,
            logWarningOnFailure: logWarningOnCreateFailure);
        if (created == null || !created.RootElement.TryGetProperty("id", out var idProp))
            return null;

        return idProp.GetString();
    }

    /// <summary>
    /// Creates a new Entra app registration (public client) and its service principal for the CLI client app.
    /// Used by setup requirements when the well-known CLI client app is not found in a new tenant.
    /// Returns (appId, spObjectId), or (null, null) on failure.
    /// </summary>
    public virtual async Task<(string? appId, string? spId)> CreateCliClientAppAsync(
        string tenantId, string displayName, CancellationToken ct = default)
    {
        var body = new
        {
            displayName,
            signInAudience = "AzureADMyOrg",
            isFallbackPublicClient = true,
            publicClient = new { redirectUris = AuthenticationConstants.RequiredRedirectUris }
        };

        using var appDoc = await GraphPostAsync(tenantId, "/v1.0/applications", body, ct);
        if (appDoc == null
            || !appDoc.RootElement.TryGetProperty("appId", out var appIdProp)
            || !appDoc.RootElement.TryGetProperty("id", out var objIdProp))
        {
            _logger.LogError("Failed to create app registration in tenant {TenantId}.", tenantId);
            return (null, null);
        }

        var appId = appIdProp.GetString();
        var objectId = objIdProp.GetString();
        if (string.IsNullOrWhiteSpace(appId)) return (null, null);

        // Patch in the WAM broker redirect URI — requires the appId to be known first.
        if (!string.IsNullOrWhiteSpace(objectId))
        {
            var allUris = AuthenticationConstants.GetRequiredRedirectUris(appId);
            var redirectUrisPatched = await GraphPatchAsync(tenantId, $"/v1.0/applications/{objectId}",
                new { publicClient = new { redirectUris = allUris } }, ct);
            if (!redirectUrisPatched)
                _logger.LogError(
                    "App created ({AppId}) in tenant {TenantId}, but patching redirect URIs failed for application object {ObjectId}. " +
                    "The app registration may be missing required redirect URIs and authentication may fail until they are added.",
                    appId, tenantId, objectId);
        }

        var spId = await EnsureServicePrincipalForAppIdAsync(tenantId, appId, ct);
        if (string.IsNullOrWhiteSpace(spId))
            _logger.LogWarning("App created ({AppId}) but service principal creation failed — admin consent may fail until the SP exists.", appId);

        return (appId, spId);
    }

    public virtual async Task<bool> CreateOrUpdateOauth2PermissionGrantAsync(
        string tenantId,
        string clientSpObjectId,
        string resourceSpObjectId,
        IEnumerable<string> scopes,
        CancellationToken ct = default,
        IEnumerable<string>? permissionGrantScopes = null)
    {
        return await CreateOrUpdateOauth2PermissionGrantCoreAsync(
            tenantId,
            clientSpObjectId,
            resourceSpObjectId,
            principalId: null,
            consentType: "AllPrincipals",
            scopes,
            ct,
            permissionGrantScopes);
    }

    /// <summary>
    /// Creates or updates an oauth2PermissionGrant with consentType=Principal, scoped to a
    /// specific principal (service principal). This is used when admin consent has not been
    /// granted on the blueprint, so permissions must be granted directly to the agent identity.
    /// </summary>
    /// <param name="tenantId">Azure AD tenant ID</param>
    /// <param name="clientSpObjectId">Object ID of the client service principal (agent identity)</param>
    /// <param name="resourceSpObjectId">Object ID of the resource service principal (e.g. Microsoft Graph)</param>
    /// <param name="principalId">Object ID of the principal (the agent identity SP) to scope the grant to</param>
    /// <param name="scopes">Scopes to grant</param>
    /// <param name="ct">Cancellation token</param>
    /// <param name="permissionGrantScopes">Optional MSAL scopes for token acquisition</param>
    /// <returns>True on success</returns>
    public async Task<bool> CreatePrincipalOauth2PermissionGrantAsync(
        string tenantId,
        string clientSpObjectId,
        string resourceSpObjectId,
        string principalId,
        IEnumerable<string> scopes,
        CancellationToken ct = default,
        IEnumerable<string>? permissionGrantScopes = null)
    {
        return await CreateOrUpdateOauth2PermissionGrantCoreAsync(
            tenantId,
            clientSpObjectId,
            resourceSpObjectId,
            principalId,
            consentType: "Principal",
            scopes,
            ct,
            permissionGrantScopes);
    }

    /// <summary>
    /// Shared implementation for creating or updating an oauth2PermissionGrant.
    /// Both AllPrincipals (tenant-wide) and Principal (scoped) consent types use the same
    /// query → create-or-merge flow. The only differences are the OData filter, the payload
    /// shape (Principal includes principalId), and the in-code matching for Principal grants.
    /// </summary>
    private async Task<bool> CreateOrUpdateOauth2PermissionGrantCoreAsync(
        string tenantId,
        string clientSpObjectId,
        string resourceSpObjectId,
        string? principalId,
        string consentType,
        IEnumerable<string> scopes,
        CancellationToken ct,
        IEnumerable<string>? permissionGrantScopes)
    {
        var desiredScopeString = string.Join(' ', scopes);
        var isPrincipal = string.Equals(consentType, "Principal", StringComparison.OrdinalIgnoreCase);

        // Read existing — extract string values immediately so JsonDocument can be disposed.
        // AllPrincipals grants can filter by clientId+resourceId server-side.
        // Principal grants must filter by clientId only, then match resourceId/consentType/principalId in code
        // because the Graph API oauth2PermissionGrants endpoint has limited $filter support.
        string? existingId = null;
        string existingScopes = "";

        var existingFilter = principalId is not null
            ? $"clientId eq '{clientSpObjectId}' and resourceId eq '{resourceSpObjectId}' and consentType eq 'Principal' and principalId eq '{principalId}'"
            : $"clientId eq '{clientSpObjectId}' and resourceId eq '{resourceSpObjectId}'";

        using (var listDoc = await GraphGetAsync(
            tenantId,
            $"/v1.0/oauth2PermissionGrants?$filter={existingFilter}",
            ct,
            permissionGrantScopes))
        {
            if (listDoc?.RootElement.TryGetProperty("value", out var arr) == true)
            {
                if (isPrincipal)
                {
                    // Principal grants: match resourceId, consentType, and principalId in code.
                    foreach (var grant in arr.EnumerateArray())
                    {
                        var grantResourceId = grant.TryGetProperty("resourceId", out var rid) ? rid.GetString() : null;
                        var grantConsentType = grant.TryGetProperty("consentType", out var ctp) ? ctp.GetString() : null;
                        var grantPrincipalId = grant.TryGetProperty("principalId", out var pid) ? pid.GetString() : null;

                        if (string.Equals(grantResourceId, resourceSpObjectId, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(grantConsentType, "Principal", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(grantPrincipalId, principalId, StringComparison.OrdinalIgnoreCase))
                        {
                            existingId = grant.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                            existingScopes = grant.TryGetProperty("scope", out var scopeProp) ? scopeProp.GetString() ?? "" : "";
                            break;
                        }
                    }
                }
                else if (arr.GetArrayLength() > 0)
                {
                    // AllPrincipals grants: the server-side filter is precise enough.
                    var grant = arr[0];
                    existingId = grant.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    existingScopes = grant.TryGetProperty("scope", out var scopeProp) ? scopeProp.GetString() ?? "" : "";
                }
            }
        }

        if (string.IsNullOrWhiteSpace(existingId))
        {
            // Principal grants can be created by the developer for their own account.
            // AllPrincipals (tenant-wide) grants require Global Administrator.
            object payload = principalId is not null
                ? new { clientId = clientSpObjectId, consentType = "Principal", principalId, resourceId = resourceSpObjectId, scope = desiredScopeString }
                : new { clientId = clientSpObjectId, consentType = "AllPrincipals", resourceId = resourceSpObjectId, scope = desiredScopeString };

            _logger.LogDebug("Graph POST /v1.0/oauth2PermissionGrants ({ConsentType}) body: {Body}", consentType, JsonSerializer.Serialize(payload));

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

                // "Permission entry already exists" means the grant is already in place — treat as success.
                if (grantResponse.Body.Contains("Permission entry already exists", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug(
                        "OAuth2 permission grant already exists for resource {ResourceSpId} — treating as success (idempotent).",
                        resourceSpObjectId);
                    return true;
                }

                if (!grantResponse.Body.Contains("Directory_ObjectNotFound", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "OAuth2 permission grant failed (non-transient) for resource {ResourceSpId} with scopes [{Scopes}]. Graph response: {Body}",
                        resourceSpObjectId, desiredScopeString, grantResponse.Body);
                    return false; // non-transient error, do not retry
                }

                if (attempt < maxRetries - 1)
                {
                    var delaySecs = (int)Math.Min(baseDelaySeconds * Math.Pow(2, attempt), 60);
                    _logger.LogDebug(
                        "Service principal not yet replicated to grants endpoint — retrying in {Delay}s (attempt {Attempt}/{Max})...",
                        delaySecs, attempt + 1, maxRetries);
                    await Task.Delay(TimeSpan.FromSeconds(delaySecs), ct);
                }
            }

            _logger.LogWarning(
                "OAuth2 permission grant ({ConsentType}) failed after {MaxRetries} retries — service principal may still be propagating. " +
                "Re-run the command to retry.",
                consentType, maxRetries);
            return false;
        }

        // Merge scopes if needed
        var currentSet = new HashSet<string>(existingScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var desiredSet = new HashSet<string>(desiredScopeString.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        if (desiredSet.IsSubsetOf(currentSet)) return true;

        currentSet.UnionWith(desiredSet);
        var merged = string.Join(' ', currentSet);

        return await GraphPatchAsync(tenantId, $"/v1.0/oauth2PermissionGrants/{existingId}", new { scope = merged }, ct, permissionGrantScopes);
    }

    /// <summary>
    /// Retrieves the oauth2PermissionGrants (admin consent) for a given service principal.
    /// Used to check whether admin consent has been granted on the blueprint.
    /// </summary>
    /// <param name="tenantId">Azure AD tenant ID</param>
    /// <param name="clientSpObjectId">Object ID of the service principal to check grants for</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of grants with their scope strings and consent types, or empty list on failure</returns>
    public virtual async Task<List<(string resourceId, string scope, string consentType)>> GetOauth2PermissionGrantsAsync(
        string tenantId,
        string clientSpObjectId,
        CancellationToken ct = default)
    {
        var grants = new List<(string resourceId, string scope, string consentType)>();

        using var doc = await GraphGetAsync(
            tenantId,
            $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{clientSpObjectId}'",
            ct);

        if (doc == null) return grants;

        if (doc.RootElement.TryGetProperty("value", out var arr))
        {
            foreach (var grant in arr.EnumerateArray())
            {
                var resourceId = grant.TryGetProperty("resourceId", out var rid) ? rid.GetString() ?? "" : "";
                var scope = grant.TryGetProperty("scope", out var s) ? s.GetString() ?? "" : "";
                var consentType = grant.TryGetProperty("consentType", out var ct2) ? ct2.GetString() ?? "" : "";
                grants.Add((resourceId, scope, consentType));
            }
        }

        return grants;
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
            using var doc = JsonDocument.Parse(json);

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
    /// <returns>true if confirmed owner, false if confirmed non-owner, null if indeterminate (token failure or Graph error)</returns>
    public virtual async Task<bool?> IsApplicationOwnerAsync(
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
                    return null;
                }

                using var meRequest = new HttpRequestMessage(HttpMethod.Get,
                    $"{_graphBaseUrl}/v1.0/me?$select=id");
                meRequest.Headers.Authorization = _httpClient.DefaultRequestHeaders.Authorization;

                using var meResponse = await _httpClient.SendAsync(meRequest, ct);
                if (!meResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Could not retrieve current user's ID: {Status}", meResponse.StatusCode);
                    return null;
                }

                var meJson = await meResponse.Content.ReadAsStringAsync(ct);
                using var meDoc = JsonDocument.Parse(meJson);

                if (!meDoc.RootElement.TryGetProperty("id", out var idElement))
                {
                    _logger.LogWarning("Could not extract user ID from Graph response");
                    return null;
                }

                userObjectId = idElement.GetString();
            }

            if (string.IsNullOrWhiteSpace(userObjectId))
            {
                _logger.LogWarning("User object ID is empty, cannot check owner");
                return null;
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

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking if user is owner of application: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Checks whether the currently signed-in user holds the Global Administrator role,
    /// which is required to grant tenant-wide admin consent interactively.
    /// Role detection is performed by decoding the <c>wids</c> claim from the MSAL access token
    /// (see <see cref="CheckDirectoryRoleAsync"/>), so this does not call Graph and works without
    /// any directory-read scope. Returns <see cref="Models.RoleCheckResult.Unknown"/> (non-blocking)
    /// when the claim is absent or token acquisition fails.
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
    /// Role detection is performed by decoding the <c>wids</c> claim from the MSAL access token
    /// (see <see cref="CheckDirectoryRoleAsync"/>), so this does not call Graph and works without
    /// any directory-read scope. Returns <see cref="Models.RoleCheckResult.Unknown"/> (non-blocking)
    /// when the claim is absent or token acquisition fails.
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
    /// <see cref="Models.RoleCheckResult.Unknown"/> if the check could not be completed — in
    /// which case the caller should attempt the operation anyway and let the API surface the
    /// real error.
    ///
    /// Implementation decodes the <c>wids</c> claim from the MSAL access token (no Graph call).
    /// <c>wids</c> is a JWT array of role template GUIDs for the directly-assigned directory roles
    /// the user holds. The optional claim must be configured on the app registration's access
    /// token; when absent we return <see cref="Models.RoleCheckResult.Unknown"/>.
    ///
    /// Limitations:
    ///   - Only directly-assigned roles appear in <c>wids</c>. Roles assigned via Entra
    ///     role-assignable groups are NOT reflected and will return DoesNotHaveRole.
    ///   - PIM-eligible-but-not-activated assignments are not active and are correctly excluded.
    ///     PIM-active assignments do appear in <c>wids</c>.
    /// </summary>
    private async Task<Models.RoleCheckResult> CheckDirectoryRoleAsync(string tenantId, string roleTemplateId, CancellationToken ct)
    {
        // Decode the wids claim from the MSAL access token instead of calling Graph.
        // wids contains role template GUIDs for directory roles the user directly holds.
        // Limitation: group-based role assignments are not reflected in wids.
        // If wids is absent (optional claim not configured on the app registration),
        // we return Unknown and the caller proceeds without role validation.
        try
        {
            var token = await GetGraphAccessTokenAsync(tenantId, ct: ct);
            if (string.IsNullOrWhiteSpace(token))
                return Models.RoleCheckResult.Unknown;

            var parts = token.Split('.');
            if (parts.Length < 2)
                return Models.RoleCheckResult.Unknown;

            var payload = parts[1];
            payload = payload.Replace('-', '+').Replace('_', '/');
            payload = (payload.Length % 4) switch
            {
                2 => payload + "==",
                3 => payload + "=",
                _ => payload
            };

            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("wids", out var wids))
            {
                _logger.LogDebug("wids claim absent from token — role check skipped (add wids as optional claim in Token Configuration on the app registration)");
                return Models.RoleCheckResult.Unknown;
            }

            return wids.EnumerateArray().Any(w =>
                string.Equals(w.GetString(), roleTemplateId, StringComparison.OrdinalIgnoreCase))
                ? Models.RoleCheckResult.HasRole
                : Models.RoleCheckResult.DoesNotHaveRole;
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

        _logger.LogDebug("POST /beta/agentRegistry/agentInstances: ownerIds=[{UserId}], displayName={DisplayName}, agentIdentityBlueprintId={BlueprintId}",
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

        // On 403: the 'Agent Registry Administrator' role may not have propagated yet.
        // Wait 30s before retrying — an immediate retry always returns another 403.
        _logger.LogInformation("403 from agent registry — 'Agent Registry Administrator' role may not have propagated yet. Waiting {Delay}s before retry...", (int)_agentRegistryRetryDelay.TotalSeconds);
        await Task.Delay(_agentRegistryRetryDelay, ct);

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
    /// Registers an agent instance via the Microsoft Graph copilot/agentRegistrations endpoint
    /// (POST <see cref="AgentRegistrationsPath"/>).
    /// Acquires a delegated Graph token via the custom app token provider (.default scope) so the
    /// token includes AgentRegistration.ReadWrite.All, or falls back to the az CLI Graph token.
    /// Returns the new agent registration ID on success (200 OK), or null on failure.
    /// </summary>
    public virtual async Task<(string? Id, bool AlreadyExisted)> RegisterAgentInstanceAsyncV2(
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
            _logger.LogError("Failed to retrieve current user ID — required for agent registration.");
            return (null, false);
        }

        // Use .default so the token includes all permissions consented on the "Agent 365 CLI" app,
        // including AgentRegistration.ReadWrite.All, without enumerating scopes explicitly.
        IEnumerable<string>? registrationScopes = _tokenProvider != null
            ? [$"{Constants.AuthenticationConstants.MicrosoftGraphResourceUri}/.default"]
            : null;

        var now = DateTimeOffset.UtcNow.ToString("o");
        var payload = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["displayName"] = displayName,
            ["ownerIds"] = new[] { currentUserId },
            ["createdBy"] = currentUserId,
            ["sourceCreatedDateTime"] = now,
            ["sourceLastModifiedDateTime"] = now,
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
        // managedByAppId must be the AgentX service app ID, not the CLI client app ID.
        // Using the CLI client app ID causes 424 "You do not have permission to create
        // an agent registration managed by another AppId."
        payload["managedByAppId"] = Constants.AuthenticationConstants.AgentXAppId;

        _logger.LogDebug("POST {Url}", AgentRegistrationsPath);
        _logger.LogDebug("Body: {Body}", JsonSerializer.Serialize(payload));

        var isRetry = false;
        var response = await _retryHelper.ExecuteWithRetryAsync<GraphResponse>(
            token => GraphPostWithResponseAsync(tenantId, AgentRegistrationsPath, payload, token, registrationScopes, forceRefresh: isRetry),
            r =>
            {
                if (r.StatusCode is not (502 or 503 or 504)) return false;
                _logger.LogWarning(
                    "Agent registration request returned HTTP {StatusCode} (transient); retrying...",
                    r.StatusCode);
                r.Json?.Dispose();
                isRetry = true;
                return true;
            },
            maxRetries: 3,
            baseDelaySeconds: 2,
            cancellationToken: ct);

        // Log token claims so scope/audience issues are visible in -v output.
        var registrationToken = _httpClient.DefaultRequestHeaders.Authorization?.Parameter;
        if (!string.IsNullOrWhiteSpace(registrationToken))
            LogJwtClaims(registrationToken, "agent registration token");

        if (response.IsSuccess)
        {
            _logger.LogDebug("Agent registration response body: {Body}", response.Body);

            string? registrationId = null;
            if (response.Json != null && response.Json.RootElement.TryGetProperty("id", out var idProp))
                registrationId = idProp.GetString();
            registrationId ??= payload["id"]?.ToString();

            response.Json?.Dispose();
            return (registrationId, false);
        }

        // 409 Conflict means an agent with the same sourceAgentId already exists.
        // The contract guarantees sourceAgentId uniqueness, so this is an idempotent re-run.
        // Extract the existing registration ID from the response body and return it.
        if (response.StatusCode == 409)
        {
            _logger.LogDebug("Agent registration returned 409 Conflict (sourceAgentId already exists). Body: {Body}", response.Body);

            string? existingId = null;
            if (response.Json != null && response.Json.RootElement.TryGetProperty("id", out var existingIdProp))
                existingId = existingIdProp.GetString();

            response.Json?.Dispose();

            if (!string.IsNullOrWhiteSpace(existingId))
            {
                _logger.LogInformation("Agent already registered (existing ID: {RegistrationId}). Skipping.", existingId);
                return (existingId, true);
            }

            // 409 but no ID in the body — server did not return the existing resource.
            _logger.LogWarning(
                "Agent registration returned 409 Conflict but the response body did not include an 'id'. " +
                "Record the registration ID manually and add it to the generated config as 'agentRegistrationId'.");
            return (null, false);
        }

        if (response.StatusCode == 403)
            _logger.LogError(
                "Agent registration failed (403 Forbidden). " +
                "Ensure the signed-in user has the required Entra role (e.g., Agent Registry Administrator) " +
                "and the tenant is enrolled in the required preview program. Response: {Body}", response.Body);
        else
            _logger.LogError("Agent registration failed with HTTP {StatusCode}. Body: {Body}", response.StatusCode, response.Body);
        response.Json?.Dispose();
        return (null, false);
    }

    /// <summary>
    /// Deletes an agent registration via the Microsoft Graph copilot/agentRegistrations endpoint
    /// (DELETE <see cref="AgentRegistrationsPath"/>/{id}).
    /// Returns true on success or if the registration was already deleted (404).
    /// </summary>
    public virtual async Task<bool> DeleteAgentRegistrationAsync(
        string tenantId,
        string registrationId,
        CancellationToken ct = default)
    {
        // Use .default so the token includes all permissions consented on the "Agent 365 CLI" app,
        // including AgentRegistration.ReadWrite.All, without enumerating scopes explicitly.
        IEnumerable<string>? scopes = _tokenProvider != null
            ? [$"{Constants.AuthenticationConstants.MicrosoftGraphResourceUri}/.default"]
            : null;

        _logger.LogInformation("DELETE https://graph.microsoft.com{Path}/{RegistrationId}", AgentRegistrationsPath, registrationId);

        return await GraphDeleteAsync(
            tenantId,
            $"{AgentRegistrationsPath}/{registrationId}",
            ct,
            treatNotFoundAsSuccess: true,
            scopes: scopes);
    }

    /// <summary>
    /// Checks whether an existing agent registration is still present by fetching
    /// GET <see cref="AgentRegistrationsPath"/>/{registrationId}.
    /// Returns true (200 OK), false (404 Not Found), or null (auth/transient error — result unknown).
    /// Callers must not treat null as "not found"; they should preserve any stored registration ID
    /// rather than triggering re-registration on an inconclusive result.
    /// </summary>
    public virtual async Task<bool?> AgentRegistrationExistsAsync(
        string tenantId,
        string registrationId,
        CancellationToken ct = default)
    {
        IEnumerable<string>? scopes = _tokenProvider != null
            ? [$"{Constants.AuthenticationConstants.MicrosoftGraphResourceUri}/.default"]
            : null;

        var path = $"{AgentRegistrationsPath}/{Uri.EscapeDataString(registrationId)}";
        _logger.LogDebug("GET https://graph.microsoft.com{Path}", path);

        try
        {
            var response = await GraphGetWithResponseAsync(tenantId, path, scopes: scopes, ct: ct);
            response.Json?.Dispose();
            if (response.IsSuccess) return true;
            if (response.StatusCode == 404) return false;
            _logger.LogDebug("Could not verify agent registration {RegistrationId} (HTTP {StatusCode}); treating as unknown.",
                registrationId, response.StatusCode);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not verify agent registration {RegistrationId} (non-fatal): {Message}",
                registrationId, ex.Message);
            return null;
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

            const int maxRetries = 12;
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

                // AADSTS7000215: credential exists but not yet visible on this STS replica.
                // AADSTS700016: blueprint app itself not yet visible on this STS replica.
                // Both are eventual-consistency propagation lag — retry with backoff.
                // AADSTS700016 / AADSTS7000215 are returned as 400 or 401 depending on the STS replica.
                var isCredentialPropagationLag =
                    (errorContent.Contains("AADSTS7000215", StringComparison.OrdinalIgnoreCase)
                        || errorContent.Contains("AADSTS700016", StringComparison.OrdinalIgnoreCase));

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
                _logger.LogDebug(
                    "Blueprint app or credentials not yet propagated (AADSTS7000215/AADSTS700016) — retrying in {Delay}s (attempt {Attempt} of {Max})...",
                    delaySecs, attempt + 1, maxRetries);
                await Task.Delay(TimeSpan.FromSeconds(delaySecs), ct);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
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
                    _logger.LogDebug("Agent identity token scp : {Scp}", scp ?? "(missing)");
                    _logger.LogDebug("Agent identity token upn : {Upn}", upn ?? "(missing)");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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

            _logger.LogDebug("POST https://graph.microsoft.com/beta/servicePrincipals/Microsoft.Graph.AgentIdentity (delegated)");
            _logger.LogDebug("Body: {Body}", body.ToJsonString());

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
            _logger.LogDebug("Agent identity created via delegated flow (ID: {Id})", id);
            return id;
        }
        catch (OperationCanceledException)
        {
            throw;
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

            const int maxAttempts = 5;
            const int baseDelaySeconds = 5;
            const string agentIdentityUrl = "https://graph.microsoft.com/beta/serviceprincipals/Microsoft.Graph.AgentIdentity";

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                // StringContent is a one-shot stream — must be recreated each attempt.
                using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                using var response = await httpClient.PostAsync(agentIdentityUrl, content, ct);

                // Some tenants reject sponsor binding — remove and retry immediately.
                if (!response.IsSuccessStatusCode && response.StatusCode == System.Net.HttpStatusCode.BadRequest
                    && body.ContainsKey("sponsors@odata.bind"))
                {
                    _logger.LogDebug("Agent identity creation with sponsor failed (400); retrying without sponsor.");
                    body.Remove("sponsors@odata.bind");
                    continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    var id = doc.RootElement.GetProperty("id").GetString();
                    _logger.LogInformation("Agent identity created (ID: {Id})", id);
                    return id;
                }

                var err = await response.Content.ReadAsStringAsync(ct);

                // Authorization_IdentityNotFound: blueprint SP not yet propagated to the agent identity
                // service — same eventual consistency window as SP replication. Retry with backoff.
                var isIdentityNotFound = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    && err.Contains("Authorization_IdentityNotFound", StringComparison.OrdinalIgnoreCase);

                if (!isIdentityNotFound || attempt == maxAttempts - 1)
                {
                    _logger.LogError("Failed to create agent identity: {Status} - {Error}", response.StatusCode, err);
                    if (err.Contains("Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase) ||
                        err.Contains("calling identity type", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError("Authorization denied. Ensure the blueprint application has the " +
                            "AgentIdentity.CreateAsManager app role (auto-granted to Blueprint apps). " +
                            "Re-run 'a365 setup blueprint' to recreate the blueprint if setup was incomplete.");
                    }
                    return null;
                }

                var delaySecs = Math.Min(baseDelaySeconds * (int)Math.Pow(2, attempt), 60);
                _logger.LogDebug(
                    "Blueprint identity not yet propagated (Authorization_IdentityNotFound) — retrying in {Delay}s (attempt {Attempt} of {Max})...",
                    delaySecs, attempt + 1, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(delaySecs), ct);
            }

            return null;
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

    private void LogJwtClaims(string token, string label)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return;
            var payload = parts[1];
            // Pad base64url to standard base64
            payload = payload.Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            string Get(string claim) =>
                root.TryGetProperty(claim, out var v) ? v.ToString() : "(absent)";

            string expReadable = "(absent)";
            if (root.TryGetProperty("exp", out var expEl) && expEl.TryGetInt64(out var expEpoch))
                expReadable = DateTimeOffset.FromUnixTimeSeconds(expEpoch).ToString("u");

            _logger.LogDebug(
                "{Label} claims — aud: {Aud} | scp: {Scp} | roles: {Roles} | tid: {Tid} | oid: {Oid} | upn: {Upn} | appid: {AppId} | exp: {Exp}",
                label,
                Get("aud"),
                Get("scp"),
                Get("roles"),
                Get("tid"),
                Get("oid"),
                Get("upn"),
                Get("appid"),
                expReadable);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not decode JWT claims for {Label}: {Message}", label, ex.Message);
        }
    }

    /// <summary>
    /// Looks up the GUID of an OAuth2 permission scope on a service principal by scope name.
    /// </summary>
    public virtual async Task<Guid?> GetOAuth2PermissionScopeIdAsync(
        string tenantId, string resourceAppId, string scopeName, CancellationToken ct = default)
    {
        if (!Guid.TryParse(resourceAppId, out var validGuid))
        {
            _logger.LogWarning("Invalid resourceAppId format: {AppId}", resourceAppId);
            return null;
        }

        using var doc = await GraphGetAsync(
            tenantId,
            $"/v1.0/servicePrincipals?$filter=appId eq '{validGuid:D}'&$select=oauth2PermissionScopes",
            ct);
        if (doc == null) return null;
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.GetArrayLength() == 0) return null;

        var sp = value[0];
        if (!sp.TryGetProperty("oauth2PermissionScopes", out var scopes)) return null;

        foreach (var scope in scopes.EnumerateArray())
        {
            if (scope.TryGetProperty("value", out var v) &&
                string.Equals(v.GetString(), scopeName, StringComparison.OrdinalIgnoreCase) &&
                scope.TryGetProperty("id", out var id))
            {
                if (!Guid.TryParse(id.GetString(), out var scopeId))
                {
                    _logger.LogWarning("Scope '{ScopeName}' on resource {ResourceAppId} has invalid ID: {ScopeIdValue}", scopeName, resourceAppId, id.GetString());
                    return null;
                }

                _logger.LogDebug("Found scope '{ScopeName}' with ID {ScopeId} on resource {ResourceAppId}", scopeName, scopeId, resourceAppId);
                return scopeId;
            }
        }

        _logger.LogWarning("Scope '{ScopeName}' not found on resource {ResourceAppId}", scopeName, resourceAppId);
        return null;
    }

    /// <summary>
    /// Creates a new Entra application registration.
    /// </summary>
    public virtual async Task<(string ObjectId, string ClientId)?> CreateEntraAppAsync(
        string tenantId, string displayName, string? serviceTreeId = null, CancellationToken ct = default)
    {
        object payload;
        if (!string.IsNullOrWhiteSpace(serviceTreeId))
        {
            payload = new
            {
                displayName,
                signInAudience = "AzureADMyOrg",
                serviceManagementReference = serviceTreeId,
            };
        }
        else
        {
            payload = new
            {
                displayName,
                signInAudience = "AzureADMyOrg",
            };
        }

        using var doc = await GraphPostAsync(tenantId, "/v1.0/applications", payload, ct);
        if (doc == null)
        {
            _logger.LogError("Failed to create Entra application {DisplayName}", displayName);
            return null;
        }

        if (!doc.RootElement.TryGetProperty("id", out var objectIdElement) ||
            !doc.RootElement.TryGetProperty("appId", out var clientIdElement))
        {
            _logger.LogError("Graph response for application {DisplayName} missing required fields (id or appId)", displayName);
            return null;
        }

        var objectId = objectIdElement.GetString();
        var clientId = clientIdElement.GetString();
        if (string.IsNullOrEmpty(objectId) || string.IsNullOrEmpty(clientId))
        {
            _logger.LogError("Graph response for application {DisplayName} returned empty id or appId", displayName);
            return null;
        }

        _logger.LogDebug("Created Entra application {DisplayName}: objectId={ObjectId}, clientId={ClientId}", displayName, objectId, clientId);
        return (objectId, clientId);
    }

    /// <summary>
    /// Adds a password (client secret) to an Entra application.
    /// </summary>
    public virtual async Task<string?> AddAppPasswordAsync(
        string tenantId, string applicationObjectId, string displayName = "CLI-generated secret", CancellationToken ct = default)
    {
        var payload = new
        {
            passwordCredential = new
            {
                displayName,
            },
        };

        using var doc = await GraphPostAsync(tenantId, $"/v1.0/applications/{applicationObjectId}/addPassword", payload, ct);
        if (doc == null)
        {
            _logger.LogError("Failed to add password to application {ObjectId}", applicationObjectId);
            return null;
        }

        if (!doc.RootElement.TryGetProperty("secretText", out var secretTextElement))
        {
            _logger.LogError("Graph response for application {ObjectId} did not contain secretText", applicationObjectId);
            return null;
        }

        var secretText = secretTextElement.GetString();
        _logger.LogDebug("Added password to application {ObjectId}", applicationObjectId);
        return secretText;
    }

    /// <summary>
    /// Updates the redirect URIs (web platform) on an Entra application.
    /// </summary>
    public virtual async Task<bool> UpdateAppRedirectUrisAsync(
        string tenantId, string applicationObjectId, IEnumerable<string> redirectUris, CancellationToken ct = default)
    {
        var payload = new
        {
            web = new
            {
                redirectUris,
            },
        };

        var result = await GraphPatchAsync(tenantId, $"/v1.0/applications/{applicationObjectId}", payload, ct);
        if (result)
        {
            _logger.LogDebug("Updated redirect URIs for application {ObjectId}", applicationObjectId);
        }
        else
        {
            _logger.LogError("Failed to update redirect URIs for application {ObjectId}", applicationObjectId);
        }

        return result;
    }

    /// <summary>
    /// Updates the publicClient redirect URIs on an Entra application registration.
    /// </summary>
    public virtual async Task<bool> UpdateAppPublicClientRedirectUrisAsync(
        string tenantId, string applicationObjectId, IEnumerable<string> redirectUris, CancellationToken ct = default)
    {
        var payload = new
        {
            publicClient = new
            {
                redirectUris,
            },
        };

        var result = await GraphPatchAsync(tenantId, $"/v1.0/applications/{applicationObjectId}", payload, ct);
        if (result)
        {
            _logger.LogDebug("Updated publicClient redirect URIs for application {ObjectId}", applicationObjectId);
        }
        else
        {
            _logger.LogError("Failed to update publicClient redirect URIs for application {ObjectId}", applicationObjectId);
        }

        return result;
    }

    /// <summary>
    /// Looks up an application by its appId (clientId) and returns the object ID.
    /// Retries up to 6 times with a 10-second delay to handle replication lag for newly created apps.
    /// </summary>
    public virtual async Task<string?> GetAppObjectIdByClientIdAsync(
        string tenantId, string clientId, CancellationToken ct = default)
    {
        const int maxAttempts = 6;
        const int delayMs = 10_000;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var response = await GraphGetWithResponseAsync(tenantId, $"/v1.0/applications?$filter=appId eq '{clientId}'&$select=id", ct: ct);
            if (response.IsSuccess && response.Json != null)
            {
                var values = response.Json.RootElement.GetProperty("value");
                if (values.GetArrayLength() > 0)
                {
                    return values[0].GetProperty("id").GetString();
                }
            }
            else
            {
                _logger.LogDebug("App {ClientId} query failed: {Code} {Reason} (attempt {Attempt}/{Max})", clientId, response.StatusCode, response.ReasonPhrase, attempt + 1, maxAttempts);
            }

            if (attempt < maxAttempts - 1)
            {
                _logger.LogDebug("App {ClientId} not found yet, retrying in {Delay}s (attempt {Attempt}/{Max})...", clientId, delayMs / 1000, attempt + 1, maxAttempts);
                await Task.Delay(delayMs, ct);
            }
        }

        return null;
    }

    /// <summary>
    /// Finds an application's object ID by its display name.
    /// </summary>
    public virtual async Task<string?> GetAppObjectIdByDisplayNameAsync(
        string tenantId, string displayName, CancellationToken ct = default)
    {
        var escapedDisplayName = displayName.Replace("'", "''", StringComparison.Ordinal);
        var response = await GraphGetWithResponseAsync(
            tenantId,
            $"/v1.0/applications?$filter=displayName eq '{escapedDisplayName}'&$select=id",
            ct: ct);

        if (response.IsSuccess && response.Json != null)
        {
            var values = response.Json.RootElement.GetProperty("value");
            if (values.GetArrayLength() > 0)
            {
                return values[0].GetProperty("id").GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Deletes an Entra application by its object ID.
    /// </summary>
    public virtual async Task<bool> DeleteEntraAppAsync(
        string tenantId, string applicationObjectId, CancellationToken ct = default)
    {
        _logger.LogDebug("Deleting Entra application {ObjectId}", applicationObjectId);
        return await GraphDeleteAsync(tenantId, $"/v1.0/applications/{applicationObjectId}", ct);
    }

    /// <summary>
    /// Sets the identifierUris on an application.
    /// </summary>
    public virtual async Task<bool> SetIdentifierUriAsync(
        string tenantId, string applicationObjectId, string identifierUri, CancellationToken ct = default)
    {
        var payload = new { identifierUris = new[] { identifierUri } };
        var result = await GraphPatchAsync(tenantId, $"/v1.0/applications/{applicationObjectId}", payload, ct);
        if (result)
        {
            _logger.LogDebug("Set identifierUri on application {ObjectId}: {Uri}", applicationObjectId, identifierUri);
        }
        else
        {
            _logger.LogError("Failed to set identifierUri on application {ObjectId}", applicationObjectId);
        }
        return result;
    }

    /// <summary>
    /// Adds an oauth2PermissionScope to an application's api section.
    /// </summary>
    public virtual async Task<Guid?> AddOAuth2PermissionScopeAsync(
        string tenantId, string applicationObjectId, string scopeName, string scopeDescription, CancellationToken ct = default)
    {
        using var doc = await GraphGetAsync(tenantId, $"/v1.0/applications/{applicationObjectId}?$select=api", ct);
        if (doc == null)
        {
            _logger.LogError("Failed to read application {ObjectId} for adding scope", applicationObjectId);
            return null;
        }

        var existingScopes = new List<object>();
        if (doc.RootElement.TryGetProperty("api", out var api) &&
            api.TryGetProperty("oauth2PermissionScopes", out var scopes))
        {
            foreach (var scope in scopes.EnumerateArray())
            {
                existingScopes.Add(JsonSerializer.Deserialize<object>(scope.GetRawText())!);
            }
        }

        var newScopeId = Guid.NewGuid();
        existingScopes.Add(new
        {
            id = newScopeId,
            adminConsentDescription = scopeDescription,
            adminConsentDisplayName = scopeName,
            isEnabled = true,
            type = "Admin",
            value = scopeName,
        });

        var payload = new
        {
            api = new
            {
                oauth2PermissionScopes = existingScopes,
            },
        };

        var result = await GraphPatchAsync(tenantId, $"/v1.0/applications/{applicationObjectId}", payload, ct);
        if (result)
        {
            _logger.LogDebug("Added scope '{ScopeName}' (ID: {ScopeId}) to application {ObjectId}", scopeName, newScopeId, applicationObjectId);
            return newScopeId;
        }

        _logger.LogError("Failed to add scope '{ScopeName}' to application {ObjectId}", scopeName, applicationObjectId);
        return null;
    }

    /// <summary>
    /// Adds a required resource access entry (API permission) to an application for a single scope.
    /// </summary>
    public virtual async Task<bool> AddRequiredResourceAccessAsync(
        string tenantId, string applicationObjectId, string resourceAppId, Guid scopeId, CancellationToken ct = default)
    {
        return await AddRequiredResourceAccessAsync(tenantId, applicationObjectId, resourceAppId, new[] { scopeId }, ct);
    }

    /// <summary>
    /// Adds a required resource access entry (API permission) to an application for one or more scopes.
    /// </summary>
    public virtual async Task<bool> AddRequiredResourceAccessAsync(
        string tenantId, string applicationObjectId, string resourceAppId, IEnumerable<Guid> scopeIds, CancellationToken ct = default)
    {
        using var doc = await GraphGetAsync(tenantId, $"/v1.0/applications/{applicationObjectId}?$select=requiredResourceAccess", ct);
        if (doc == null)
        {
            _logger.LogError("Failed to read application {ObjectId} for adding API permission", applicationObjectId);
            return false;
        }

        var existingAccess = new System.Text.Json.Nodes.JsonArray();
        bool merged = false;
        if (doc.RootElement.TryGetProperty("requiredResourceAccess", out var rra))
        {
            foreach (var entry in rra.EnumerateArray())
            {
                var entryNode = System.Text.Json.Nodes.JsonNode.Parse(entry.GetRawText()) as System.Text.Json.Nodes.JsonObject;
                if (entryNode != null &&
                    entryNode["resourceAppId"]?.GetValue<string>() == resourceAppId)
                {
                    var existingScopes = entryNode["resourceAccess"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
                    var existingScopeIds = new HashSet<string>();
                    foreach (var scope in existingScopes)
                    {
                        var id = scope?["id"]?.GetValue<string>();
                        if (id != null) existingScopeIds.Add(id);
                    }

                    foreach (var scopeId in scopeIds)
                    {
                        if (!existingScopeIds.Contains(scopeId.ToString()))
                        {
                            existingScopes.Add(System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new { id = scopeId, type = "Scope" })));
                        }
                    }

                    entryNode["resourceAccess"] = existingScopes;
                    existingAccess.Add(entryNode);
                    merged = true;
                }
                else
                {
                    existingAccess.Add(entryNode);
                }
            }
        }

        if (!merged)
        {
            existingAccess.Add(System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new
            {
                resourceAppId,
                resourceAccess = scopeIds.Select(id => new { id, type = "Scope" }).ToArray(),
            })));
        }

        var payload = new
        {
            requiredResourceAccess = existingAccess,
        };

        var result = await GraphPatchAsync(tenantId, $"/v1.0/applications/{applicationObjectId}", payload, ct);
        if (result)
        {
            _logger.LogDebug("Added API permissions for resource {ResourceAppId} on application {ObjectId}", resourceAppId, applicationObjectId);
        }
        else
        {
            _logger.LogError("Failed to add API permissions for resource {ResourceAppId} on application {ObjectId}", resourceAppId, applicationObjectId);
        }

        return result;
    }
}
