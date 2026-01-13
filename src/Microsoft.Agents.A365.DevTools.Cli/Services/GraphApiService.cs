// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
    
    /// <summary>
    /// Optional custom client app ID to use for authentication with Microsoft Graph PowerShell.
    /// When set, this will be passed to Connect-MgGraph -ClientId parameter.
    /// </summary>
    public string? CustomClientAppId { get; set; }

    // Lightweight wrapper to surface HTTP status, reason and body to callers
    public record GraphResponse
    {
        public bool IsSuccess { get; init; }
        public int StatusCode { get; init; }
        public string ReasonPhrase { get; init; } = string.Empty;
        public string Body { get; init; } = string.Empty;
        public JsonDocument? Json { get; init; }
    }

    // Allow injecting a custom HttpMessageHandler for unit testing
    public GraphApiService(ILogger<GraphApiService> logger, CommandExecutor executor, HttpMessageHandler? handler = null, IMicrosoftGraphTokenProvider? tokenProvider = null)
    {
        _logger = logger;
        _executor = executor;
        _httpClient = handler != null ? new HttpClient(handler) : HttpClientFactory.CreateAuthenticatedClient();
        _tokenProvider = tokenProvider;
    }

    // Parameterless constructor to ease test mocking/substitution frameworks which may
    // require creating proxy instances without providing constructor arguments.
    public GraphApiService()
        : this(NullLogger<GraphApiService>.Instance, new CommandExecutor(NullLogger<CommandExecutor>.Instance), null)
    {
    }

    // Two-argument convenience constructor used by tests and callers that supply
    // a logger and an existing CommandExecutor (no custom handler).
    public GraphApiService(ILogger<GraphApiService> logger, CommandExecutor executor)
        : this(logger ?? NullLogger<GraphApiService>.Instance, executor ?? throw new ArgumentNullException(nameof(executor)), null, null)
    {
    }

    /// <summary>
    /// Get access token for Microsoft Graph API using Azure CLI
    /// </summary>
    public async Task<string?> GetGraphAccessTokenAsync(string tenantId, CancellationToken ct = default)
    {
        _logger.LogDebug("Acquiring Graph API access token for tenant {TenantId}", tenantId);
        
        try
        {
            // Check if Azure CLI is authenticated
            var accountCheck = await _executor.ExecuteAsync(
                "az", 
                "account show", 
                captureOutput: true, 
                suppressErrorLogging: true,
                cancellationToken: ct);

            if (!accountCheck.Success)
            {
                _logger.LogInformation("Azure CLI not authenticated. Initiating login...");
                var loginResult = await _executor.ExecuteAsync(
                    "az", 
                    $"login --tenant {tenantId} --use-device-code --allow-no-subscriptions",
                    cancellationToken: ct);
                
                if (!loginResult.Success)
                {
                    _logger.LogError("Azure CLI login failed");
                    return null;
                }
            }

            // Get access token for Microsoft Graph
            var tokenResult = await _executor.ExecuteAsync(
                "az",
                $"account get-access-token --resource https://graph.microsoft.com/ --tenant {tenantId} --query accessToken -o tsv",
                captureOutput: true,
                cancellationToken: ct);

            if (tokenResult.Success && !string.IsNullOrWhiteSpace(tokenResult.StandardOutput))
            {
                var token = tokenResult.StandardOutput.Trim();
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
                _logger.LogInformation("Logging out of Azure CLI...");
                await _executor.ExecuteAsync("az", "logout", suppressErrorLogging: true, cancellationToken: ct);
                
                _logger.LogInformation("Initiating fresh login...");
                var freshLoginResult = await _executor.ExecuteAsync(
                    "az",
                    $"login --tenant {tenantId} --use-device-code --allow-no-subscriptions",
                    cancellationToken: ct);
                
                if (!freshLoginResult.Success)
                {
                    _logger.LogError("Fresh login failed. Please manually run: az login --tenant {TenantId}", tenantId);
                    return null;
                }
                
                // Retry token acquisition
                _logger.LogInformation("Retrying token acquisition...");
                var retryTokenResult = await _executor.ExecuteAsync(
                    "az",
                    $"account get-access-token --resource https://graph.microsoft.com/ --tenant {tenantId} --query accessToken -o tsv",
                    captureOutput: true,
                    cancellationToken: ct);
                
                if (retryTokenResult.Success && !string.IsNullOrWhiteSpace(retryTokenResult.StandardOutput))
                {
                    var token = retryTokenResult.StandardOutput.Trim();
                    _logger.LogInformation("Graph API access token acquired successfully after re-authentication");
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
        // When specific scopes are required AND token provider is configured, use delegated auth
        // Otherwise fall back to Azure CLI (useful for tests and when token provider is not available)
        string? token;

        if (scopes != null && _tokenProvider != null)
        {
            // Use token provider with delegated scopes (device code flow)
            token = await _tokenProvider.GetMgGraphAccessTokenAsync(tenantId, scopes, useDeviceCode: true, clientAppId: CustomClientAppId, ct: ct);
        }
        else
        {
            // Fall back to Azure CLI token (for tests or when token provider is not configured)
            if (scopes != null && _tokenProvider == null)
            {
                _logger.LogWarning("Token provider is not configured, falling back to Azure CLI for scopes: {Scopes}", string.Join(", ", scopes));
            }
            token = await GetGraphAccessTokenAsync(tenantId, ct);
        }

        if (string.IsNullOrWhiteSpace(token)) return false;

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // NOTE: Do NOT add "ConsistencyLevel: eventual" header here.
        // This header is only required for advanced Graph query capabilities ($count, $search, certain $filter operations).
        // For simple queries like service principal lookups, this header is not needed and causes HTTP 400 errors.
        // See: https://learn.microsoft.com/en-us/graph/aad-advanced-queries

        return true;
    }

    /// <summary>
    /// Executes a GET request to Microsoft Graph API.
    /// Virtual to allow mocking in unit tests using Moq.
    /// </summary>
    public virtual async Task<JsonDocument?> GraphGetAsync(string tenantId, string relativePath, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, ct, scopes)) return null;
        var url = relativePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativePath
            : $"https://graph.microsoft.com{relativePath}";
        var resp = await _httpClient.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);

        return JsonDocument.Parse(json);
    }

    public virtual async Task<JsonDocument?> GraphPostAsync(string tenantId, string relativePath, object payload, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, ct, scopes)) return null;
        var url = relativePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativePath
            : $"https://graph.microsoft.com{relativePath}";
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await _httpClient.PostAsync(url, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return null;

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

        var url = relativePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativePath
            : $"https://graph.microsoft.com{relativePath}";

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await _httpClient.PostAsync(url, content, ct);
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
        var url = relativePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativePath
            : $"https://graph.microsoft.com{relativePath}";
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(new HttpMethod("PATCH"), url) { Content = content };
        var resp = await _httpClient.SendAsync(request, ct);

        // Many PATCH calls return 204 NoContent on success
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("Graph PATCH {Url} failed {Code} {Reason}: {Body}", url, (int)resp.StatusCode, resp.ReasonPhrase, body);
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

        var url = relativePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativePath
            : $"https://graph.microsoft.com{relativePath}";

        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        using var resp = await _httpClient.SendAsync(req, ct);

        // 404 can be considered success for idempotent deletes
        if (treatNotFoundAsSuccess && (int)resp.StatusCode == 404) return true;

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("Graph DELETE {Url} failed {Code} {Reason}: {Body}", url, (int)resp.StatusCode, resp.ReasonPhrase, body);
            return false;
        }

        return true;
    }

    public virtual async Task<GraphResponse> GraphPostWithResponseAsync(
        string tenantId,
        string relativePath,
        object payload,
        CancellationToken ct = default,
        IEnumerable<string>? scopes = null,
        IDictionary<string, string>? extraHeaders = null)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, ct, scopes))
        {
            return new GraphResponse { IsSuccess = false, StatusCode = 0, ReasonPhrase = "NoAuth", Body = "Failed to acquire token" };
        }

        var url = relativePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativePath
            : $"https://graph.microsoft.com{relativePath}";

        using var req = CreateJsonRequest(HttpMethod.Post, url, payload, extraHeaders);
        using var resp = await _httpClient.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        JsonDocument? json = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try { json = JsonDocument.Parse(body); } catch { /* ignore */ }
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

    public virtual async Task<bool> GraphPatchAsync(
        string tenantId,
        string relativePath,
        object payload,
        CancellationToken ct = default,
        IEnumerable<string>? scopes = null,
        IDictionary<string, string>? extraHeaders = null)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, ct, scopes)) return false;

        var url = relativePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativePath
            : $"https://graph.microsoft.com{relativePath}";

        using var req = CreateJsonRequest(new HttpMethod("PATCH"), url, payload, extraHeaders);
        using var resp = await _httpClient.SendAsync(req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("Graph PATCH {Url} failed {Code} {Reason}: {Body}", url, (int)resp.StatusCode, resp.ReasonPhrase, body);
        }

        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Looks up a service principal by its application (client) ID.
    /// Virtual to allow mocking in unit tests using Moq.
    /// </summary>
    public virtual async Task<string?> LookupServicePrincipalByAppIdAsync(
        string tenantId, string appId, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        var doc = await GraphGetAsync(tenantId, $"/v1.0/servicePrincipals?$filter=appId eq '{appId}'&$select=id", ct, scopes);
        if (doc == null) return null;
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.GetArrayLength() == 0) return null;
        return value[0].GetProperty("id").GetString();
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

        // Read existing
        var listDoc = await GraphGetAsync(
            tenantId,
            $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{clientSpObjectId}' and resourceId eq '{resourceSpObjectId}'",
            ct,
            permissionGrantScopes);

        var existing = listDoc?.RootElement.TryGetProperty("value", out var arr) == true && arr.GetArrayLength() > 0
            ? arr[0]
            : (JsonElement?)null;

        if (existing is null)
        {
            // Create
            var payload = new
            {
                clientId = clientSpObjectId,
                consentType = "AllPrincipals",
                resourceId = resourceSpObjectId,
                scope = desiredScopeString
            };
            var created = await GraphPostAsync(tenantId, "/v1.0/oauth2PermissionGrants", payload, ct, permissionGrantScopes);
            return created != null; // success if response parsed
        }

        // Merge scopes if needed
        var current = existing.Value.TryGetProperty("scope", out var s) ? s.GetString() ?? "" : "";
        var currentSet = new HashSet<string>(current.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var desiredSet = new HashSet<string>(desiredScopeString.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        if (desiredSet.IsSubsetOf(currentSet)) return true; // already satisfied

        currentSet.UnionWith(desiredSet);
        var merged = string.Join(' ', currentSet);

        var id = existing.Value.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(id)) return false;

        return await GraphPatchAsync(tenantId, $"/v1.0/oauth2PermissionGrants/{id}", new { scope = merged }, ct, permissionGrantScopes);
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

            using var request = new HttpRequestMessage(HttpMethod.Get, 
                "https://graph.microsoft.com/v1.0/me/memberOf/microsoft.graph.directoryRole");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request, ct);
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

    private static HttpRequestMessage CreateJsonRequest(
        HttpMethod method,
        string url,
        object? payload = null,
        IDictionary<string, string>? extraHeaders = null)
    {
        var req = new HttpRequestMessage(method, url);

        if (payload != null)
        {
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");
        }

        if (extraHeaders != null)
        {
            foreach (var kvp in extraHeaders)
            {
                // avoid duplicates if caller reuses service instance
                req.Headers.Remove(kvp.Key);
                req.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
            }
        }

        return req;
    }
}
