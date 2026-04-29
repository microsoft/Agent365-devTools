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
    private string _graphBaseUrl;

    // Token caching is handled at the process level by AzCliHelper.AcquireAzCliTokenAsync.
    // All GraphApiService instances (and other services) share a single token per
    // (resource, tenantId) pair — no per-instance cache needed.

    // Login hint resolved once per GraphApiService instance from 'az account show'.
    // Used to direct MSAL/WAM to the correct Azure CLI identity, preventing the Windows
    // account (WAM default) or a stale cached MSAL account from being used instead.
    private string? _loginHint;
    private bool _loginHintResolved;

    // Resolver delegate for the login hint. Defaults to AzCliHelper.ResolveLoginHintAsync;
    // injectable via constructor so unit tests can bypass the real 'az account show' process.
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
    // loginHintResolver: optional override for 'az account show' login-hint resolution.
    // Pass () => Task.FromResult<string?>(null) in unit tests to skip the real az process.
    public GraphApiService(ILogger<GraphApiService> logger, CommandExecutor executor, HttpMessageHandler? handler = null, IMicrosoftGraphTokenProvider? tokenProvider = null, Func<Task<string?>>? loginHintResolver = null, string? graphBaseUrl = null)
    {
        _logger = logger;
        _executor = executor;
        _httpClient = handler != null ? new HttpClient(handler) : HttpClientFactory.CreateAuthenticatedClient();
        _tokenProvider = tokenProvider;
        _loginHintResolver = loginHintResolver ?? AzCliHelper.ResolveLoginHintAsync;
        _graphBaseUrl = string.IsNullOrWhiteSpace(graphBaseUrl) ? GraphApiConstants.BaseUrl : graphBaseUrl;
    }

    // Parameterless constructor to ease test mocking/substitution frameworks which may
    // require creating proxy instances without providing constructor arguments.
    public GraphApiService()
        : this(NullLogger<GraphApiService>.Instance, new CommandExecutor(NullLogger<CommandExecutor>.Instance), null, null, null)
    {
    }

    // Two-argument convenience constructor used by tests and callers that supply
    // a logger and an existing CommandExecutor (no custom handler).
    public GraphApiService(ILogger<GraphApiService> logger, CommandExecutor executor)
        : this(logger ?? NullLogger<GraphApiService>.Instance, executor ?? throw new ArgumentNullException(nameof(executor)), null, null, null)
    {
    }

    /// <summary>
    /// Get access token for Microsoft Graph API using Azure CLI
    /// </summary>
    public virtual async Task<string?> GetGraphAccessTokenAsync(string tenantId, CancellationToken ct = default)
    {
        _logger.LogDebug("Acquiring Graph API access token for tenant {TenantId}", tenantId);

        try
        {
            var resource = GraphApiConstants.GetResource(_graphBaseUrl);

            // Check the process-level cache first. AzCliHelper caches tokens per (resource, tenantId)
            // for the process lifetime — avoids spawning duplicate 'az account get-access-token'
            // subprocesses when multiple services request the same token.
            var cachedToken = await AzCliHelper.AcquireAzCliTokenAsync(resource, tenantId);
            if (!string.IsNullOrWhiteSpace(cachedToken))
            {
                _logger.LogDebug("Graph API access token acquired from cache");
                return cachedToken;
            }

            // Cache miss or az CLI not authenticated — check login state via the injectable resolver.
            // Using _loginHintResolver (not the static helper directly) keeps the test seam consistent.
            var loginHint = await _loginHintResolver();
            if (loginHint == null)
            {
                _logger.LogDebug("Azure CLI not authenticated. Initiating login...");
                _logger.LogDebug("A browser window will open for authentication. Please check your taskbar or browser if you don't see it.");
                var loginResult = await _executor.ExecuteAsync(
                    "az",
                    $"login --tenant {tenantId}",
                    cancellationToken: ct);

                if (!loginResult.Success)
                {
                    _logger.LogError("Azure CLI login failed");
                    return null;
                }

                // Bust the caches so the fresh login identity and token are picked up.
                AzCliHelper.InvalidateLoginHintCache();
                AzCliHelper.InvalidateAzCliTokenCache();
            }

            // Acquire the token — this goes through AzCliHelper so it is cached for all
            // subsequent callers (including those that call AzCliHelper directly).
            var tokenResult = await _executor.ExecuteAsync(
                "az",
                $"account get-access-token --resource {resource} --tenant {tenantId} --query accessToken -o tsv",
                captureOutput: true,
                cancellationToken: ct);

            if (tokenResult.Success && !string.IsNullOrWhiteSpace(tokenResult.StandardOutput))
            {
                var token = tokenResult.StandardOutput.Trim();
                // Warm the shared cache so other services that call AzCliHelper.AcquireAzCliTokenAsync
                // directly receive this token without spawning another subprocess.
                AzCliHelper.WarmAzCliTokenCache(resource, tenantId, token);
                _logger.LogDebug("Graph API access token acquired successfully");
                return token;
            }

            // Check for CAE-related errors in the error output
            var errorOutput = tokenResult.StandardError ?? "";
            if (errorOutput.Contains("AADSTS50173", StringComparison.OrdinalIgnoreCase) ||
                errorOutput.Contains("session", StringComparison.OrdinalIgnoreCase) ||
                errorOutput.Contains("expired", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Authentication session may have expired. Attempting fresh login...");

                // Force logout and re-login
                _logger.LogDebug("Logging out of Azure CLI...");
                await _executor.ExecuteAsync("az", "logout", suppressErrorLogging: true, cancellationToken: ct);

                _logger.LogDebug("Initiating fresh login...");
                var freshLoginResult = await _executor.ExecuteAsync(
                    "az",
                    $"login --tenant {tenantId}",
                    cancellationToken: ct);

                if (!freshLoginResult.Success)
                {
                    _logger.LogError("Fresh login failed. Please manually run: az login --tenant {TenantId}", tenantId);
                    return null;
                }

                // Bust caches after re-authentication so stale tokens are not returned.
                AzCliHelper.InvalidateLoginHintCache();
                AzCliHelper.InvalidateAzCliTokenCache();

                // Retry token acquisition
                _logger.LogDebug("Retrying token acquisition...");
                var retryTokenResult = await _executor.ExecuteAsync(
                    "az",
                    $"account get-access-token --resource {resource} --tenant {tenantId} --query accessToken -o tsv",
                    captureOutput: true,
                    cancellationToken: ct);

                if (retryTokenResult.Success && !string.IsNullOrWhiteSpace(retryTokenResult.StandardOutput))
                {
                    var token = retryTokenResult.StandardOutput.Trim();
                    AzCliHelper.WarmAzCliTokenCache(resource, tenantId, token);
                    _logger.LogDebug("Graph API access token acquired successfully after re-authentication");
                    return token;
                }

                _logger.LogError("Failed to acquire token after re-authentication: {Error}", retryTokenResult.StandardError);
                return null;
            }

            _logger.LogError("Failed to acquire Graph API access token: {Error}", tokenResult.StandardError);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring Graph API access token");
            
            // Check for CAE-related exceptions
            if (ex.Message.Contains("TokenIssuedBeforeRevocationTimestamp", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("InteractionRequired", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("");
                _logger.LogError("=== AUTHENTICATION SESSION EXPIRED ===");
                _logger.LogError("Your authentication session is no longer valid.");
                _logger.LogError("");
                _logger.LogError("TO RESOLVE:");
                _logger.LogError("  1. Run: az logout");
                _logger.LogError("  2. Run: az login --tenant {TenantId}", tenantId);
                _logger.LogError("  3. Retry your command");
                _logger.LogError("");
            }
            
            return null;
        }
    }


    private async Task<bool> EnsureGraphHeadersAsync(string tenantId, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        // Authentication Strategy:
        // 1. If specific scopes required AND token provider configured: Use interactive browser auth (cached)
        // 2. Otherwise: Use Azure CLI token (requires 'az login')
        // This dual approach minimizes auth prompts while supporting special scope requirements

        string? token;

        if (scopes != null && _tokenProvider != null)
        {
            // Use token provider with delegated scopes (interactive browser auth with caching)
            _logger.LogDebug("Acquiring Graph token with specific scopes via token provider: {Scopes}", string.Join(", ", scopes));
            var loginHint = await ResolveLoginHintAsync();
            token = await _tokenProvider.GetMgGraphAccessTokenAsync(tenantId, scopes, false, CustomClientAppId, ct, loginHint);

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
            // Use the process-level token cache in AzCliHelper — shared across all service
            // instances so a token acquired in any phase is reused by subsequent phases.
            token = await AzCliHelper.AcquireAzCliTokenAsync(GraphApiConstants.GetResource(_graphBaseUrl), tenantId);

            if (string.IsNullOrWhiteSpace(token))
            {
                // Cache miss or az CLI not authenticated — run full auth + recovery flow.
                _logger.LogDebug("Process-level token cache miss; running full auth flow for tenant {TenantId}", tenantId);
                token = await GetGraphAccessTokenAsync(tenantId, ct);

                if (string.IsNullOrWhiteSpace(token))
                {
                    _logger.LogError("Failed to acquire Graph token via Azure CLI. Ensure 'az login' is completed.");
                    return false;
                }

                // Warm the process-level cache so subsequent callers (including other
                // GraphApiService instances and services) skip the auth flow entirely.
                AzCliHelper.WarmAzCliTokenCache(GraphApiConstants.GetResource(_graphBaseUrl), tenantId, token);
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
        if (!await EnsureGraphHeadersAsync(tenantId, ct, scopes)) return null;
        var url = GraphApiConstants.BuildUrl(_graphBaseUrl, relativePath);
        using var resp = await _httpClient.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("Graph GET {Url} failed {Code} {Reason}: {Body}", url, (int)resp.StatusCode, resp.ReasonPhrase, errorBody);
            return null;
        }
        var json = await resp.Content.ReadAsStringAsync(ct);

        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// GET from Graph and always return HTTP response details (status, body, parsed JSON).
    /// Use this instead of GraphGetAsync when the caller needs to distinguish auth failures
    /// (401) from transient server errors (503, 429, network exceptions).
    /// </summary>
    public virtual async Task<GraphResponse> GraphGetWithResponseAsync(string tenantId, string relativePath, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, ct, scopes))
            return new GraphResponse { IsSuccess = false, StatusCode = 0, ReasonPhrase = "NoAuth", Body = "Failed to acquire token" };

        var url = GraphApiConstants.BuildUrl(_graphBaseUrl, relativePath);

        try
        {
            using var resp = await _httpClient.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

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
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Graph GET {Url} threw an exception", url);
            return new GraphResponse { IsSuccess = false, StatusCode = 0, ReasonPhrase = ex.Message, Body = string.Empty };
        }
    }

    public virtual async Task<JsonDocument?> GraphPostAsync(string tenantId, string relativePath, object payload, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, ct, scopes)) return null;
        var url = GraphApiConstants.BuildUrl(_graphBaseUrl, relativePath);
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
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

    /// <summary>
    /// POST to Graph but always return HTTP response details (status, body, parsed JSON)
    /// </summary>
    public virtual async Task<GraphResponse> GraphPostWithResponseAsync(string tenantId, string relativePath, object payload, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, ct, scopes))
        {
            return new GraphResponse { IsSuccess = false, StatusCode = 0, ReasonPhrase = "NoAuth", Body = "Failed to acquire token" };
        }

        var url = GraphApiConstants.BuildUrl(_graphBaseUrl, relativePath);

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
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

    /// <summary>
    /// Executes a PATCH request to Microsoft Graph API.
    /// Virtual to allow mocking in unit tests using Moq.
    /// </summary>
    public virtual async Task<bool> GraphPatchAsync(string tenantId, string relativePath, object payload, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, ct, scopes)) return false;
        var url = GraphApiConstants.BuildUrl(_graphBaseUrl, relativePath);
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
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

    public async Task<bool> GraphDeleteAsync(
        string tenantId,
        string relativePath,
        CancellationToken ct = default,
        bool treatNotFoundAsSuccess = true,
        IEnumerable<string>? scopes = null)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, ct, scopes)) return false;

        var url = GraphApiConstants.BuildUrl(_graphBaseUrl, relativePath);

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

            var token = await GetGraphAccessTokenAsync(tenantId, ct);
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
                if (!await EnsureGraphHeadersAsync(tenantId, ct, scopes))
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

    /// <summary>
    /// Creates a single-tenant Entra application registration.
    /// </summary>
    /// <param name="tenantId">The tenant ID (empty string for default tenant).</param>
    /// <param name="displayName">Display name for the application.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (applicationObjectId, applicationClientId), or null on failure.</returns>
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
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="applicationObjectId">The application object ID (not the appId/clientId).</param>
    /// <param name="displayName">Display name for the secret.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The secret text, or null on failure.</returns>
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
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="applicationObjectId">The application object ID.</param>
    /// <param name="redirectUris">The redirect URIs to set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if successful.</returns>
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
            var response = await GraphGetWithResponseAsync(tenantId, $"/v1.0/applications?$filter=appId eq '{clientId}'&$select=id", ct);
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
    /// Returns null if not found.
    /// </summary>
    public virtual async Task<string?> GetAppObjectIdByDisplayNameAsync(
        string tenantId, string displayName, CancellationToken ct = default)
    {
        var escapedDisplayName = displayName.Replace("'", "''", StringComparison.Ordinal);
        var response = await GraphGetWithResponseAsync(
            tenantId,
            $"/v1.0/applications?$filter=displayName eq '{escapedDisplayName}'&$select=id",
            ct);

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
    public async Task<bool> SetIdentifierUriAsync(
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
    public async Task<Guid?> AddOAuth2PermissionScopeAsync(
        string tenantId, string applicationObjectId, string scopeName, string scopeDescription, CancellationToken ct = default)
    {
        // First read current scopes
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
        // First read current requiredResourceAccess
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
