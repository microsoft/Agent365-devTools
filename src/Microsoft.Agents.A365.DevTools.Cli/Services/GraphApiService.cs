// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Linq;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
        _httpClient = handler != null ? new HttpClient(handler) : new HttpClient();
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
        _logger.LogInformation("Acquiring Graph API access token...");
        
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
                _logger.LogInformation("Graph API access token acquired successfully");
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


    #region Publish Operations

    /// <summary>
    /// Execute all Graph API operations for publish:
    /// 1. Create federated identity credential
    /// 2. Lookup service principal
    /// 3. Assign app role (if supported)
    /// </summary>
    public async Task<bool> ExecutePublishGraphStepsAsync(
        string tenantId,
        string blueprintId,
        string manifestId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("=== PUBLISH GRAPH STEPS START ===");
            _logger.LogInformation("TenantId: {TenantId}", tenantId);
            _logger.LogInformation("BlueprintId: {BlueprintId}", blueprintId);
            _logger.LogInformation("ManifestId: {ManifestId}", manifestId);

            // Get Graph access token
            var graphToken = await GetGraphAccessTokenAsync(tenantId, cancellationToken);
            if (string.IsNullOrWhiteSpace(graphToken))
            {
                _logger.LogError("Failed to acquire Graph API access token");
                return false;
            }

            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", graphToken);
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("ConsistencyLevel", "eventual");

            // Step 1: Derive federated identity subject using FMI ID logic
            _logger.LogInformation("[STEP 1] Deriving federated identity subject (FMI ID)...");
            
            // MOS3 App ID - well-known identifier for MOS (Microsoft Online Services)
            const string mos3AppId = "e8be65d6-d430-4289-a665-51bf2a194bda";
            var subjectValue = ConstructFmiId(tenantId, mos3AppId, manifestId);
            _logger.LogInformation("Subject value (FMI ID): {Subject}", subjectValue);

            // Step 2: Create federated identity credential
            _logger.LogInformation("[STEP 2] Creating federated identity credential...");
            await CreateFederatedIdentityCredentialAsync(
                blueprintId, 
                subjectValue, 
                tenantId,
                manifestId,
                cancellationToken);

            // Step 3: Lookup Service Principal
            _logger.LogInformation("[STEP 3] Looking up service principal...");
            var spObjectId = await LookupServicePrincipalAsync(blueprintId, cancellationToken);
            if (string.IsNullOrWhiteSpace(spObjectId))
            {
                _logger.LogError("Failed to lookup service principal");
                return false;
            }

            _logger.LogInformation("Service principal objectId: {ObjectId}", spObjectId);

            // Step 4: Lookup Microsoft Graph Service Principal
            _logger.LogInformation("[STEP 4] Looking up Microsoft Graph service principal...");
            var msGraphResourceId = await LookupMicrosoftGraphServicePrincipalAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(msGraphResourceId))
            {
                _logger.LogError("Failed to lookup Microsoft Graph service principal");
                return false;
            }

            _logger.LogInformation("Microsoft Graph service principal objectId: {ObjectId}", msGraphResourceId);

            // Step 5: Assign app role (optional for agent applications)
            _logger.LogInformation("[STEP 5] Assigning app role...");
            await AssignAppRoleAsync(spObjectId, msGraphResourceId, cancellationToken);

            _logger.LogInformation("=== PUBLISH GRAPH STEPS COMPLETED SUCCESSFULLY ===");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Publish graph steps failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Base64URL encode a byte array (URL-safe Base64 encoding without padding)
    /// </summary>
    private static string Base64UrlEncode(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            throw new ArgumentException("Data cannot be null or empty", nameof(data));
        }

        // Convert to Base64
        var base64 = Convert.ToBase64String(data);
        
        // Make URL-safe: Remove padding and replace characters
        return base64.TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Construct an FMI (Federated Member Identifier) ID
    /// Format: /eid1/c/pub/t/{tenantId}/a/{appId}/{fmiPath}
    /// Based on the PowerShell create-fmi.ps1 script
    /// </summary>
    /// <param name="tenantId">Tenant ID (GUID)</param>
    /// <param name="rmaId">RMA/App ID (GUID) - typically the MOS3 App ID</param>
    /// <param name="manifestId">Manifest ID (string) - will be Base64URL encoded as the FMI path</param>
    private static string ConstructFmiId(string tenantId, string rmaId, string manifestId)
    {
        // Parse GUIDs
        if (!Guid.TryParse(tenantId, out var tenantGuid))
        {
            throw new ArgumentException($"Invalid tenant ID format: {tenantId}", nameof(tenantId));
        }

        if (!Guid.TryParse(rmaId, out var rmaGuid))
        {
            throw new ArgumentException($"Invalid RMA/App ID format: {rmaId}", nameof(rmaId));
        }

        // Encode GUIDs as Base64URL
        var tenantIdEncoded = Base64UrlEncode(tenantGuid.ToByteArray());
        var rmaIdEncoded = Base64UrlEncode(rmaGuid.ToByteArray());

        // Construct the FMI namespace
        var fmiNamespace = $"/eid1/c/pub/t/{tenantIdEncoded}/a/{rmaIdEncoded}";

        if (string.IsNullOrWhiteSpace(manifestId))
        {
            return fmiNamespace;
        }

        // Convert manifestId to Base64URL - this is what MOS will do when impersonating
        var manifestIdBytes = Encoding.UTF8.GetBytes(manifestId);
        var fmiPath = Base64UrlEncode(manifestIdBytes);

        return $"{fmiNamespace}/{fmiPath}";
    }

    private async Task CreateFederatedIdentityCredentialAsync(
        string blueprintId,
        string subjectValue,
        string tenantId,
        string manifestId,
        CancellationToken cancellationToken)
    {
        try
        {
            var ficName = $"fic-{manifestId}";

            // Check if FIC already exists
            var existingUrl = $"https://graph.microsoft.com/beta/applications/{blueprintId}/federatedIdentityCredentials";
            var existingResponse = await _httpClient.GetAsync(existingUrl, cancellationToken);

            if (existingResponse.IsSuccessStatusCode)
            {
                var existingJson = await existingResponse.Content.ReadAsStringAsync(cancellationToken);
                var existing = System.Text.Json.JsonDocument.Parse(existingJson);

                if (existing.RootElement.TryGetProperty("value", out var fics))
                {
                    foreach (var fic in fics.EnumerateArray())
                    {
                        if (fic.TryGetProperty("subject", out var subject) && 
                            subject.GetString() == subjectValue)
                        {
                            var name = fic.TryGetProperty("name", out var n) ? n.GetString() : "unknown";
                            _logger.LogInformation("Federated identity credential already exists: {Name}", name);
                            return;
                        }
                    }
                }
            }

            // Create new FIC
            var payload = new
            {
                name = ficName,
                issuer = $"https://login.microsoftonline.com/{tenantId}/v2.0",
                subject = subjectValue,
                audiences = new[] { "api://AzureADTokenExchange" }
            };

            var createUrl = $"https://graph.microsoft.com/beta/applications/{blueprintId}/federatedIdentityCredentials";
            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync(createUrl, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("Failed to create FIC (expected in some scenarios): {Error}", error);
                return;
            }

            _logger.LogInformation("Federated identity credential created: {Name}", ficName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception creating federated identity credential");
        }
    }

    private async Task<string?> LookupServicePrincipalAsync(
        string blueprintId,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://graph.microsoft.com/v1.0/servicePrincipals?$filter=appId eq '{blueprintId}'";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to lookup service principal");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = System.Text.Json.JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("value", out var value) && value.GetArrayLength() > 0)
            {
                var sp = value[0];
                if (sp.TryGetProperty("id", out var id))
                {
                    return id.GetString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception looking up service principal");
            return null;
        }
    }

    private async Task<string?> LookupMicrosoftGraphServicePrincipalAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            const string msGraphAppId = "00000003-0000-0000-c000-000000000000";
            var url = $"https://graph.microsoft.com/v1.0/servicePrincipals?$filter=appId eq '{msGraphAppId}'&$select=id,appId,displayName";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to lookup Microsoft Graph service principal");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = System.Text.Json.JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("value", out var value) && value.GetArrayLength() > 0)
            {
                var sp = value[0];
                if (sp.TryGetProperty("id", out var id))
                {
                    return id.GetString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception looking up Microsoft Graph service principal");
            return null;
        }
    }

    private async Task AssignAppRoleAsync(
        string spObjectId,
        string msGraphResourceId,
        CancellationToken cancellationToken)
    {
        try
        {
            // AgentIdUser.ReadWrite.IdentityParentedBy well-known role ID
            const string appRoleId = "4aa6e624-eee0-40ab-bdd8-f9639038a614";

            // Check if role assignment already exists
            var existingUrl = $"https://graph.microsoft.com/v1.0/servicePrincipals/{spObjectId}/appRoleAssignments";
            var existingResponse = await _httpClient.GetAsync(existingUrl, cancellationToken);

            if (existingResponse.IsSuccessStatusCode)
            {
                var existingJson = await existingResponse.Content.ReadAsStringAsync(cancellationToken);
                var existing = System.Text.Json.JsonDocument.Parse(existingJson);

                if (existing.RootElement.TryGetProperty("value", out var assignments))
                {
                    foreach (var assignment in assignments.EnumerateArray())
                    {
                        var resourceId = assignment.TryGetProperty("resourceId", out var r) ? r.GetString() : null;
                        var roleId = assignment.TryGetProperty("appRoleId", out var ar) ? ar.GetString() : null;

                        if (resourceId == msGraphResourceId && roleId == appRoleId)
                        {
                            _logger.LogInformation("App role assignment already exists (idempotent check passed)");
                            return;
                        }
                    }
                }
            }

            // Create new app role assignment
            var payload = new
            {
                principalId = spObjectId,
                resourceId = msGraphResourceId,
                appRoleId = appRoleId
            };

            var createUrl = $"https://graph.microsoft.com/v1.0/servicePrincipals/{spObjectId}/appRoleAssignments";
            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync(createUrl, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);

                // Check if this is the known agent application limitation
                if (error.Contains("Service principals of agent applications cannot be set as the source type", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("App role assignment skipped: Agent applications have restrictions");
                    _logger.LogInformation("Agent application permissions should be configured through admin consent URLs");
                    return;
                }

                _logger.LogWarning("App role assignment failed (continuing anyway): {Error}", error);
                return;
            }

            _logger.LogInformation("App role assignment succeeded");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception assigning app role (continuing anyway)");
        }
    }

    /// <summary>
    /// Get inheritable permissions for an agent blueprint
    /// </summary>
    /// <param name="blueprintId">The blueprint ID</param>
    /// <param name="tenantId">The tenant ID for authentication</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>JSON response from the inheritable permissions endpoint</returns>
    public async Task<string?> GetBlueprintInheritablePermissionsAsync(
        string blueprintId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get access token for Microsoft Graph
            var accessToken = await GetGraphAccessTokenAsync(tenantId, cancellationToken);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogError("Failed to acquire Graph API access token");
                return null;
            }

            // Set authorization header
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // Make the API call to get inheritable permissions
            var url = $"https://graph.microsoft.com/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintId}/inheritablePermissions";
            _logger.LogInformation("Calling Graph API: {Url}", url);

            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Graph API call failed. Status: {StatusCode}, Error: {Error}",
                    response.StatusCode, errorContent);
                return null;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("Successfully retrieved inheritable permissions from Graph API");

            return jsonResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception calling inheritable permissions endpoint");
            return null;
        }
        finally
        {
            // Clear authorization header to avoid issues with other requests
            _httpClient.DefaultRequestHeaders.Authorization = null;
        }
    }

    #endregion
    
    /// <summary>
    /// Delete an Agent Blueprint application using the special agentIdentityBlueprint endpoint.
    /// 
    /// SPECIAL AUTHENTICATION REQUIREMENTS:
    /// Agent Blueprint deletion requires the AgentIdentityBlueprint.ReadWrite.All delegated permission scope.
    /// This scope is not available through Azure CLI tokens, so we use interactive authentication via
    /// the token provider (same authentication method used during blueprint creation in the setup command).
    /// 
    /// This method uses the GraphDeleteAsync helper but with special scopes - the duplication is intentional
    /// because blueprint operations require elevated permissions that standard Graph operations don't need.
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication</param>
    /// <param name="blueprintId">The blueprint application ID (object ID or app ID)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deletion succeeded or resource not found; false otherwise</returns>
    public async Task<bool> DeleteAgentBlueprintAsync(
        string tenantId,
        string blueprintId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Deleting agent blueprint application: {BlueprintId}", blueprintId);
            
            // Agent Blueprint deletion requires special delegated permission scope
            var requiredScopes = new[] { "AgentIdentityBlueprint.ReadWrite.All" };
            
            if (_tokenProvider == null)
            {
                _logger.LogError("Token provider is not configured. Agent Blueprint deletion requires interactive authentication.");
                _logger.LogError("Please ensure the GraphApiService is initialized with a token provider.");
                return false;
            }
            
            _logger.LogInformation("Acquiring access token with AgentIdentityBlueprint.ReadWrite.All scope...");
            _logger.LogInformation("A browser window will open for authentication.");
            
            // Use the special agentIdentityBlueprint endpoint for deletion
            var deletePath = $"/beta/applications/{blueprintId}/microsoft.graph.agentIdentityBlueprint";
            
            // Use GraphDeleteAsync with the special scopes required for blueprint operations
            var success = await GraphDeleteAsync(
                tenantId,
                deletePath,
                cancellationToken,
                treatNotFoundAsSuccess: true,
                scopes: requiredScopes);
            
            if (success)
            {
                _logger.LogInformation("Agent blueprint application deleted successfully");
            }
            else
            {
                _logger.LogError("Failed to delete agent blueprint application");
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception deleting agent blueprint application");
            return false;
        }
        finally
        {
            // Clear authorization header to avoid issues with other requests
            _httpClient.DefaultRequestHeaders.Authorization = null;
        }
    }
    
    private async Task<bool> EnsureGraphHeadersAsync(string tenantId, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        var token = (scopes != null && _tokenProvider != null) ? await _tokenProvider.GetMgGraphAccessTokenAsync(tenantId, scopes, false, ct) : await GetGraphAccessTokenAsync(tenantId, ct);
        if (string.IsNullOrWhiteSpace(token)) return false;

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _httpClient.DefaultRequestHeaders.Remove("ConsistencyLevel");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("ConsistencyLevel", "eventual");

        return true;
    }

    public async Task<JsonDocument?> GraphGetAsync(string tenantId, string relativePath, CancellationToken ct = default)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, ct)) return null;
        var url = relativePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativePath
            : $"https://graph.microsoft.com{relativePath}";
        var resp = await _httpClient.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);

        return JsonDocument.Parse(json);
    }

    public async Task<JsonDocument?> GraphPostAsync(string tenantId, string relativePath, object payload, CancellationToken ct = default, IEnumerable<string>? scopes = null)
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
    public async Task<GraphResponse> GraphPostWithResponseAsync(string tenantId, string relativePath, object payload, CancellationToken ct = default, IEnumerable<string>? scopes = null)
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

    public async Task<bool> GraphPatchAsync(string tenantId, string relativePath, object payload, CancellationToken ct = default, IEnumerable<string>? scopes = null)
    {
        if (!await EnsureGraphHeadersAsync(tenantId, ct)) return false;
        var url = relativePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativePath
            : $"https://graph.microsoft.com{relativePath}";
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(new HttpMethod("PATCH"), url) { Content = content };
        var resp = await _httpClient.SendAsync(request, ct);

        // Many PATCH calls return 204 NoContent on success
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

    public async Task<string?> LookupServicePrincipalByAppIdAsync(string tenantId, string appId, CancellationToken ct = default)
    {
        var doc = await GraphGetAsync(tenantId, $"/v1.0/servicePrincipals?$filter=appId eq '{appId}'&$select=id", ct);
        if (doc == null) return null;
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.GetArrayLength() == 0) return null;
        return value[0].GetProperty("id").GetString();
    }

    public async Task<string> EnsureServicePrincipalForAppIdAsync(
        string tenantId, string appId, CancellationToken ct = default)
    {
        // Try existing
        var spId = await LookupServicePrincipalByAppIdAsync(tenantId, appId, ct);
        if (!string.IsNullOrWhiteSpace(spId)) return spId!;

        // Create SP for this application
        var created = await GraphPostAsync(tenantId, "/v1.0/servicePrincipals", new { appId }, ct);
        if (created == null || !created.RootElement.TryGetProperty("id", out var idProp))
            throw new InvalidOperationException($"Failed to create servicePrincipal for appId {appId}");

        return idProp.GetString()!;
    }

    public async Task<bool> CreateOrUpdateOauth2PermissionGrantAsync(
        string tenantId,
        string clientSpObjectId,
        string resourceSpObjectId,
        IEnumerable<string> scopes,
        CancellationToken ct = default)
    {
        var desiredScopeString = string.Join(' ', scopes);

        // Read existing
        var listDoc = await GraphGetAsync(
            tenantId,
            $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{clientSpObjectId}' and resourceId eq '{resourceSpObjectId}'",
            ct);

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
            var created = await GraphPostAsync(tenantId, "/v1.0/oauth2PermissionGrants", payload, ct);
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

        return await GraphPatchAsync(tenantId, $"/v1.0/oauth2PermissionGrants/{id}", new { scope = merged }, ct);
    }

    /// <summary>
    /// Sets inheritable permissions for an agent blueprint with proper scope merging.
    /// Checks if permissions already exist and merges scopes if needed via PATCH.
    /// </summary>
    public async Task<(bool ok, bool alreadyExists, string? error)> SetInheritablePermissionsAsync(
        string tenantId,
        string blueprintAppId,
        string resourceAppId,
        IEnumerable<string> scopes,
        IEnumerable<string>? requiredScopes = null,
        CancellationToken ct = default)
    {
        var desiredSet = new HashSet<string>(scopes ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        // Normalize into array form expected by Graph (each element is a single scope string)
        var desiredArray = desiredSet.ToArray();

        try
        {
            // First, try to resolve blueprintAppId to an application object id if needed
            string blueprintObjectId = blueprintAppId;

            // Try GET for inheritablePermissions - if it fails, attempt to lookup application by appId
            var getPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions";
            var existingDoc = await GraphGetAsync(tenantId, getPath, ct);

            if (existingDoc == null)
            {
                // Attempt to resolve as appId -> application object id
                var apps = await GraphGetAsync(tenantId, $"/v1.0/applications?$filter=appId eq '{blueprintAppId}'&$select=id", ct);
                if (apps != null && apps.RootElement.TryGetProperty("value", out var arr) && arr.GetArrayLength() > 0)
                {
                    var appObj = arr[0];
                    if (appObj.TryGetProperty("id", out var idEl))
                    {
                        blueprintObjectId = idEl.GetString() ?? blueprintAppId;
                        getPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions";
                        existingDoc = await GraphGetAsync(tenantId, getPath, ct);
                    }
                }
            }

            // Inspect existing entries
            JsonElement? existingEntry = null;
            if (existingDoc != null && existingDoc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    var rId = item.TryGetProperty("resourceAppId", out var r) ? r.GetString() : null;
                    if (string.Equals(rId, resourceAppId, StringComparison.OrdinalIgnoreCase))
                    {
                        existingEntry = item;
                        break;
                    }
                }
            }

            if (existingEntry is not null)
            {
                // Merge scopes if necessary
                var currentScopes = new List<string>();
                if (existingEntry.Value.TryGetProperty("inheritableScopes", out var inheritable) &&
                    inheritable.TryGetProperty("scopes", out var scopesEl) && scopesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in scopesEl.EnumerateArray())
                    {
                        if (s.ValueKind == JsonValueKind.String)
                        {
                            var raw = s.GetString() ?? string.Empty;
                            // Some entries may contain space-separated tokens; split defensively
                            foreach (var tok in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                currentScopes.Add(tok);
                        }
                    }
                }

                var currentSet = new HashSet<string>(currentScopes, StringComparer.OrdinalIgnoreCase);
                if (desiredSet.IsSubsetOf(currentSet))
                {
                    _logger.LogInformation("Inheritable permissions already exist for blueprint {Blueprint} resource {Resource}", blueprintObjectId, resourceAppId);
                    return (ok: true, alreadyExists: true, error: null);
                }

                // Union and PATCH
                currentSet.UnionWith(desiredSet);
                var mergedArray = currentSet.OrderBy(s => s).ToArray();

                var patchPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions/{resourceAppId}";
                var patchPayload = new
                {
                    inheritableScopes = new EnumeratedScopes
                    {
                        Scopes = mergedArray
                    }
                };

                var patched = await GraphPatchAsync(tenantId, patchPath, patchPayload, ct, requiredScopes);
                if (!patched)
                {
                    return (ok: false, alreadyExists: false, error: "PATCH failed");
                }

                _logger.LogInformation("Patched inheritable permissions for blueprint {Blueprint} resource {Resource}", blueprintObjectId, resourceAppId);
                return (ok: true, alreadyExists: false, error: null);
            }

            // No existing entry -> create
            var postPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions";
            var postPayload = new
            {
                resourceAppId = resourceAppId,
                inheritableScopes = new EnumeratedScopes
                {
                    Scopes = desiredArray
                }
            };

            var createdResp = await GraphPostWithResponseAsync(tenantId, postPath, postPayload, ct, requiredScopes);
            if (!createdResp.IsSuccess)
            {
                var err = string.IsNullOrWhiteSpace(createdResp.Body)
                    ? $"HTTP {createdResp.StatusCode} {createdResp.ReasonPhrase}"
                    : createdResp.Body;
                _logger.LogError("Failed to create inheritable permissions: {Status} {Reason} Body: {Body}", createdResp.StatusCode, createdResp.ReasonPhrase, createdResp.Body);
                return (ok: false, alreadyExists: false, error: err);
            }

            _logger.LogInformation("Created inheritable permissions for blueprint {Blueprint} resource {Resource}", blueprintObjectId, resourceAppId);
            return (ok: true, alreadyExists: false, error: null);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to set inheritable permissions: {Error}", ex.Message);
            return (ok: false, alreadyExists: false, error: ex.Message);
        }
    }

    /// <summary>
    /// Verifies that inheritable permissions are correctly configured for a resource
    /// </summary>
    public async Task<(bool exists, string[] scopes, string? error)> VerifyInheritablePermissionsAsync(
        string tenantId,
        string blueprintAppId,
        string resourceAppId,
        CancellationToken ct = default)
    {
        try
        {
            string blueprintObjectId = blueprintAppId;
            var getPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions";
            var existingDoc = await GraphGetAsync(tenantId, getPath, ct);

            if (existingDoc == null)
            {
                // Try to resolve as appId -> application object id
                var apps = await GraphGetAsync(tenantId, $"/v1.0/applications?$filter=appId eq '{blueprintAppId}'&$select=id", ct);
                if (apps != null && apps.RootElement.TryGetProperty("value", out var arr) && arr.GetArrayLength() > 0)
                {
                    var appObj = arr[0];
                    if (appObj.TryGetProperty("id", out var idEl))
                    {
                        blueprintObjectId = idEl.GetString() ?? blueprintAppId;
                        getPath = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions";
                        existingDoc = await GraphGetAsync(tenantId, getPath, ct);
                    }
                }
            }

            if (existingDoc == null)
            {
                return (exists: false, scopes: Array.Empty<string>(), error: "Failed to retrieve inheritable permissions");
            }

            // Find the entry for this resource
            if (existingDoc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    var rId = item.TryGetProperty("resourceAppId", out var r) ? r.GetString() : null;
                    if (string.Equals(rId, resourceAppId, StringComparison.OrdinalIgnoreCase))
                    {
                        // Found the resource, extract scopes
                        var scopesList = new List<string>();
                        if (item.TryGetProperty("inheritableScopes", out var inheritable) &&
                            inheritable.TryGetProperty("scopes", out var scopesEl) && scopesEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var s in scopesEl.EnumerateArray())
                            {
                                if (s.ValueKind == JsonValueKind.String)
                                {
                                    var raw = s.GetString() ?? string.Empty;
                                    foreach (var tok in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                        scopesList.Add(tok);
                                }
                            }
                        }
                        return (exists: true, scopes: scopesList.ToArray(), error: null);
                    }
                }
            }

            return (exists: false, scopes: Array.Empty<string>(), error: null);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to verify inheritable permissions: {Error}", ex.Message);
            return (exists: false, scopes: Array.Empty<string>(), error: ex.Message);
        }
    }

    public async Task<bool> ReplaceOauth2PermissionGrantAsync(
        string tenantId,
        string clientSpObjectId,  
        string resourceSpObjectId,
        IEnumerable<string> scopes,
        CancellationToken ct = default)
    {
        // Normalize scopes -> single space-delimited string (Graph’s required shape)
        var desiredSet = new HashSet<string>(
            (scopes ?? Enumerable.Empty<string>())
                .SelectMany(s => (s ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            StringComparer.OrdinalIgnoreCase);

        var desiredScopeString = string.Join(' ', desiredSet.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

        // 1) Find existing grant(s) for client resource
        var listDoc = await GraphGetAsync(
            tenantId,
            $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{clientSpObjectId}' and resourceId eq '{resourceSpObjectId}'",
            ct);

        var existing = listDoc?.RootElement.TryGetProperty("value", out var arr) == true ? arr : default;

        // 2) Delete all existing grants for this pair (rare but possible to have >1)
        if (existing.ValueKind == JsonValueKind.Array && existing.GetArrayLength() > 0)
        {
            foreach (var item in existing.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _logger.LogDebug("Deleting existing oauth2PermissionGrant {Id} for client {ClientId} and resource {ResourceId}", 
                        id, clientSpObjectId, resourceSpObjectId);

                    var ok = await GraphDeleteAsync(tenantId, $"/v1.0/oauth2PermissionGrants/{id}", ct);
                    if (!ok)
                    {
                        _logger.LogError("Failed to delete existing oauth2PermissionGrant {Id} for client {ClientId} and resource {ResourceId}. " +
                                       "This may indicate insufficient permissions or the grant is protected. " +
                                       "Required permissions: DelegatedPermissionGrant.ReadWrite.All or Application.ReadWrite.All", 
                                       id, clientSpObjectId, resourceSpObjectId);
                        _logger.LogError("Troubleshooting steps:");
                        _logger.LogError("  1. Verify your account has sufficient Azure AD permissions");
                        _logger.LogError("  2. Check if you are a Global Administrator or Application Administrator");
                        _logger.LogError("  3. Ensure the oauth2PermissionGrant exists and is not system-protected");
                        _logger.LogError("  4. Try running: az login --tenant {TenantId} with elevated privileges", tenantId);
                        
                        throw new InvalidOperationException($"Failed to delete existing oauth2PermissionGrant {id}");
                    }

                    _logger.LogDebug("Successfully deleted oauth2PermissionGrant {Id}", id);
                }
            }
        }

        // If no scopes desired, we’re done (revoke only)
        if (desiredSet.Count == 0) return true;

        // 3) Create the new grant with exactly the desired scopes
        var payload = new
        {
            clientId = clientSpObjectId,
            consentType = "AllPrincipals",
            resourceId = resourceSpObjectId,
            scope = desiredScopeString
        };

        var created = await GraphPostAsync(tenantId, "/v1.0/oauth2PermissionGrants", payload, ct);
        return created != null;
    }

    /// <summary>
    /// Adds required resource access (API permissions) to an application's manifest.
    /// This makes the permissions visible in the Entra portal's "API permissions" blade.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="appId">The application (client) ID to update</param>
    /// <param name="resourceAppId">The resource application ID to add permissions for</param>
    /// <param name="scopes">The permission scope names to add</param>
    /// <param name="isDelegated">True for delegated permissions (Scope), false for application permissions (Role)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if successful, false otherwise</returns>
    public async Task<bool> AddRequiredResourceAccessAsync(
        string tenantId,
        string appId,
        string resourceAppId,
        IEnumerable<string> scopes,
        bool isDelegated = true,
        CancellationToken ct = default)
    {
        try
        {
            // Get the application object by appId
            var appsDoc = await GraphGetAsync(tenantId, $"/v1.0/applications?$filter=appId eq '{appId}'&$select=id,requiredResourceAccess", ct);
            if (appsDoc == null)
            {
                _logger.LogError("Failed to retrieve application with appId {AppId}", appId);
                return false;
            }

            if (!appsDoc.RootElement.TryGetProperty("value", out var appsArray) || appsArray.GetArrayLength() == 0)
            {
                _logger.LogError("Application not found with appId {AppId}", appId);
                return false;
            }

            var app = appsArray[0];
            var objectId = app.GetProperty("id").GetString();

            // Get the resource service principal to look up permission IDs
            var resourceSp = await LookupServicePrincipalByAppIdAsync(tenantId, resourceAppId, ct);
            if (string.IsNullOrEmpty(resourceSp))
            {
                _logger.LogError("Resource service principal not found for appId {ResourceAppId}", resourceAppId);
                return false;
            }

            // Get the resource SP's published permissions
            var resourceSpDoc = await GraphGetAsync(tenantId, $"/v1.0/servicePrincipals/{resourceSp}?$select=oauth2PermissionScopes,appRoles", ct);
            if (resourceSpDoc == null)
            {
                _logger.LogError("Failed to retrieve resource service principal {ResourceSp}", resourceSp);
                return false;
            }

            // Map scope names to permission IDs
            var permissionIds = new List<string>();
            var permissionType = isDelegated ? "Scope" : "Role";
            var permissionsProperty = isDelegated ? "oauth2PermissionScopes" : "appRoles";

            if (resourceSpDoc.RootElement.TryGetProperty(permissionsProperty, out var permissions))
            {
                foreach (var scope in scopes)
                {
                    var found = false;
                    foreach (var permission in permissions.EnumerateArray())
                    {
                        if (permission.TryGetProperty("value", out var valueElement) && 
                            valueElement.GetString()?.Equals(scope, StringComparison.OrdinalIgnoreCase) == true)
                        {
                            if (permission.TryGetProperty("id", out var idElement))
                            {
                                permissionIds.Add(idElement.GetString()!);
                                found = true;
                                break;
                            }
                        }
                    }

                    if (!found)
                    {
                        _logger.LogWarning("Permission scope '{Scope}' not found on resource {ResourceAppId}", scope, resourceAppId);
                    }
                }
            }

            if (permissionIds.Count == 0)
            {
                _logger.LogWarning("No valid permission IDs found for scopes: {Scopes}", string.Join(", ", scopes));
                return false;
            }

            // Get existing requiredResourceAccess
            var existingResourceAccess = new List<object>();
            if (app.TryGetProperty("requiredResourceAccess", out var existingArray))
            {
                existingResourceAccess = JsonSerializer.Deserialize<List<object>>(existingArray.GetRawText()) ?? new List<object>();
            }

            // Check if resource already exists in requiredResourceAccess
            var resourceAccessList = existingResourceAccess
                .Select(x => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(x)))
                .ToList();

            var existingResource = resourceAccessList.FirstOrDefault(x => 
                x != null && 
                x.TryGetValue("resourceAppId", out var resId) && 
                resId.GetString() == resourceAppId);

            if (existingResource != null)
            {
                // Add to existing resource access
                var existingAccess = existingResource.TryGetValue("resourceAccess", out var accessElement)
                    ? JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(accessElement.GetRawText()) ?? new List<Dictionary<string, JsonElement>>()
                    : new List<Dictionary<string, JsonElement>>();

                var existingIds = new HashSet<string>(
                    existingAccess
                        .Where(x => x.TryGetValue("id", out var idEl))
                        .Select(x => x["id"].GetString()!)
                );

                foreach (var permId in permissionIds)
                {
                    if (!existingIds.Contains(permId))
                    {
                        existingAccess.Add(new Dictionary<string, JsonElement>
                        {
                            ["id"] = JsonDocument.Parse($"\"{permId}\"").RootElement,
                            ["type"] = JsonDocument.Parse($"\"{permissionType}\"").RootElement
                        });
                    }
                }

                existingResource["resourceAccess"] = JsonDocument.Parse(JsonSerializer.Serialize(existingAccess)).RootElement;
            }
            else
            {
                // Add new resource access entry
                var newResourceAccess = new Dictionary<string, object>
                {
                    ["resourceAppId"] = resourceAppId,
                    ["resourceAccess"] = permissionIds.Select(id => new Dictionary<string, string>
                    {
                        ["id"] = id,
                        ["type"] = permissionType
                    }).ToList()
                };

                resourceAccessList.Add(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(newResourceAccess))!);
            }

            // Update the application with PATCH
            var patchPayload = new
            {
                requiredResourceAccess = resourceAccessList
            };

            var updated = await GraphPatchAsync(tenantId, $"/v1.0/applications/{objectId}", patchPayload, ct);
            if (updated)
            {
                _logger.LogInformation("Successfully added required resource access for {ResourceAppId} to application {AppId}", resourceAppId, appId);
            }

            return updated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add required resource access: {Message}", ex.Message);
            return false;
        }
    }
}
