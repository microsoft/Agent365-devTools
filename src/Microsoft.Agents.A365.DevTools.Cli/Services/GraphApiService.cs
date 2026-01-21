// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
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
                _logger.LogInformation("A browser window will open for authentication. Please check your taskbar or browser if you don't see it.");
                var loginResult = await _executor.ExecuteAsync(
                    "az",
                    $"login --tenant {tenantId}",
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
                    $"login --tenant {tenantId}",
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
        // When specific scopes are required, token provider must be configured
        if (scopes != null && _tokenProvider == null)
        {
            _logger.LogError("Token provider is not configured, but specific scopes are required: {Scopes}", string.Join(", ", scopes));
            return false;
        }

        // When specific scopes are required, use custom client app if configured
        // CustomClientAppId should be set by callers who have access to config
        var token = (scopes != null && _tokenProvider != null)
            ? await _tokenProvider.GetMgGraphAccessTokenAsync(tenantId, scopes, false, CustomClientAppId, ct)
            : await GetGraphAccessTokenAsync(tenantId, ct);
        
        if (string.IsNullOrWhiteSpace(token)) return false;

        // Trim token to remove any newline characters that may cause header validation errors
        token = token.Trim();

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

            // Trim token to remove any newline characters that may cause header validation errors
            token = token.Trim();

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
                    "https://graph.microsoft.com/v1.0/me?$select=id");
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
}
