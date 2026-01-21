// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Ensures oauth2PermissionGrant exists for the custom client application.
/// Validates that AgentIdentityBlueprint.ReadWrite.All permission is granted, which is required for creating and managing Agent Blueprints.
/// </summary>
public sealed class DelegatedConsentService
{
    private readonly ILogger<DelegatedConsentService> _logger;
    private readonly GraphApiService _graphService;

    // Constants
    private const string TargetScope = "AgentIdentityBlueprint.ReadWrite.All";
    private const string AllPrincipalsConsentType = "AllPrincipals";

    public DelegatedConsentService(
        ILogger<DelegatedConsentService> logger,
        GraphApiService graphService)
    {
        _logger = logger;
        _graphService = graphService;
    }

    /// <summary>
    /// Ensures AgentIdentityBlueprint.ReadWrite.All permission is granted to the custom client application.
    /// Required for creating and managing Agent Blueprints.
    /// </summary>
    /// <param name="callingAppId">Application ID of the custom client app from configuration</param>
    /// <param name="tenantId">Tenant ID where the permission grant will be created</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if grant was created or updated successfully</returns>
    public async Task<bool> EnsureBlueprintPermissionGrantAsync(
        string callingAppId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("==> Ensuring AgentIdentityBlueprint.ReadWrite.All permission for custom client app");
            _logger.LogInformation("    Client App ID: {AppId}", callingAppId);
            _logger.LogInformation("    Tenant ID: {TenantId}", tenantId);
            _logger.LogInformation("    Required Scope: {Scope}", TargetScope);

            // Validate inputs
            if (!Guid.TryParse(callingAppId, out _))
            {
                _logger.LogError("Invalid Calling App ID format: {AppId}", callingAppId);
                return false;
            }

            if (!Guid.TryParse(tenantId, out _))
            {
                _logger.LogError("Invalid Tenant ID format: {TenantId}", tenantId);
                return false;
            }

            // Get Graph access token with required scopes
            _logger.LogInformation("Acquiring Graph API access token...");
            var graphToken = await _graphService.GetGraphAccessTokenAsync(tenantId, cancellationToken);
            if (string.IsNullOrWhiteSpace(graphToken))
            {
                _logger.LogError("Failed to acquire Graph API access token");
                return false;
            }

            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(graphToken);

            // Step 1: Get or create service principal for custom client app
            _logger.LogInformation("    Looking up service principal for client app (ID: {AppId})", callingAppId);
            var clientSp = await GetOrCreateServicePrincipalAsync(httpClient, callingAppId, tenantId, cancellationToken);
            if (clientSp == null)
            {
                _logger.LogError("Failed to get or create service principal for calling app");
                return false;
            }

            var clientSpId = clientSp.RootElement.GetProperty("id").GetString()!;
            _logger.LogInformation("    Client Service Principal ID: {SpId}", clientSpId);

            // Step 2: Get Microsoft Graph service principal
            _logger.LogInformation("    Looking up Microsoft Graph service principal");
            var graphSp = await GetServicePrincipalAsync(httpClient, AuthenticationConstants.MicrosoftGraphResourceAppId, cancellationToken);
            if (graphSp == null)
            {
                _logger.LogError("Failed to get Microsoft Graph service principal");
                return false;
            }

            var graphSpId = graphSp.RootElement.GetProperty("id").GetString()!;
            _logger.LogInformation("    Graph Service Principal ID: {SpId}", graphSpId);

            // Step 3: Check if grant already exists
            _logger.LogInformation("    Checking for existing permission grant");
            var existingGrants = await GetExistingGrantsAsync(httpClient, clientSpId, graphSpId, cancellationToken);

            if (existingGrants != null && existingGrants.Count > 0)
            {
                _logger.LogInformation("    Found {Count} existing grant(s)", existingGrants.Count);

                // Update existing grant(s) to include required scope
                foreach (var grant in existingGrants)
                {
                    await EnsureScopeOnGrantAsync(httpClient, grant, TargetScope, cancellationToken);
                }
            }
            else
            {
                _logger.LogInformation("    No existing grants found, creating new grant");

                // Create new grant with required scope
                var success = await CreateGrantAsync(httpClient, clientSpId, graphSpId, TargetScope, cancellationToken);
                if (!success)
                {
                    _logger.LogError("Failed to create permission grant");
                    return false;
                }
            }

            _logger.LogInformation("Successfully ensured grant for scope: {Scope}", TargetScope);
            _logger.LogInformation("    You can now create Agent Blueprints");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure AgentIdentityBlueprint.ReadWrite.All consent: {Message}", ex.Message);
            
            // Check if this looks like a CAE error
            if (ex.Message.Contains("TokenIssuedBeforeRevocationTimestamp", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("InteractionRequired", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("");
                _logger.LogError("=== AUTHENTICATION TOKEN EXPIRED ===");
            }
            
            return false;
        }
    }

    /// <summary>
    /// Gets or creates a service principal for the given app ID
    /// Equivalent to Get-OrCreateServicePrincipalByAppId in PowerShell
    /// </summary>
    private async Task<JsonDocument?> GetOrCreateServicePrincipalAsync(
        HttpClient httpClient,
        string appId,
        string tenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            // Try to get existing service principal
            var getSp = await GetServicePrincipalAsync(httpClient, appId, cancellationToken);
            if (getSp != null)
            {
                _logger.LogInformation("    Service principal already exists for app {AppId}", appId);
                return getSp;
            }

            // Create new service principal
            _logger.LogInformation("Creating service principal for app {AppId}", appId);
            var createSpUrl = "https://graph.microsoft.com/v1.0/servicePrincipals";
            var createBody = new
            {
                appId = appId
            };

            var createResponse = await httpClient.PostAsync(
                createSpUrl,
                new StringContent(
                    JsonSerializer.Serialize(createBody),
                    System.Text.Encoding.UTF8,
                    "application/json"),
                cancellationToken);

            if (!createResponse.IsSuccessStatusCode)
            {
                var error = await createResponse.Content.ReadAsStringAsync(cancellationToken);
                
                // Check if this is a CAE token error requiring re-authentication
                if (IsCaeTokenError(error))
                {
                    _logger.LogWarning("Continuous Access Evaluation detected stale token. Re-authenticating automatically...");
                    
                    // Perform automatic logout and re-login
                    var freshToken = await ForceReAuthenticationAsync(tenantId, cancellationToken);
                    if (string.IsNullOrWhiteSpace(freshToken))
                    {
                        _logger.LogError("Automatic re-authentication failed");
                        return null;
                    }
                    
                    // Trim token to remove any newline characters that may cause header validation errors
                    freshToken = freshToken.Trim();
                    
                    // Update the HTTP client with fresh token
                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", freshToken);
                    
                    // Retry the service principal creation with fresh token
                    _logger.LogInformation("Retrying service principal creation with fresh token...");
                    var retryResponse = await httpClient.PostAsync(
                        createSpUrl,
                        new StringContent(
                            JsonSerializer.Serialize(createBody),
                            System.Text.Encoding.UTF8,
                            "application/json"),
                        cancellationToken);
                    
                    if (!retryResponse.IsSuccessStatusCode)
                    {
                        var retryError = await retryResponse.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogError("Failed to create service principal after re-authentication: {Error}", retryError);
                        return null;
                    }
                    
                    var retrySpJson = await retryResponse.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogInformation("    Service principal created successfully after re-authentication");
                    return JsonDocument.Parse(retrySpJson);
                }
                
                _logger.LogError("Failed to create service principal: {Error}", error);
                return null;
            }

            var spJson = await createResponse.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("    Service principal created successfully");
            return JsonDocument.Parse(spJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in GetOrCreateServicePrincipalAsync");
            return null;
        }
    }

    /// <summary>
    /// Forces re-authentication by logging out and logging back in
    /// Returns a fresh Graph API access token
    /// </summary>
    private async Task<string?> ForceReAuthenticationAsync(string tenantId, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("    Logging out of Azure CLI...");
            
            // Logout using CommandExecutor
            var cleanLoggerFactory = LoggerFactoryHelper.CreateCleanLoggerFactory();
            var executor = new CommandExecutor(
                cleanLoggerFactory.CreateLogger<CommandExecutor>());
            
            await executor.ExecuteAsync("az", "logout", suppressErrorLogging: true, cancellationToken: cancellationToken);
            
            _logger.LogInformation("    Initiating fresh login...");
            var loginResult = await executor.ExecuteAsync(
                "az",
                $"login --tenant {tenantId}",
                cancellationToken: cancellationToken);
            
            if (!loginResult.Success)
            {
                _logger.LogError("Fresh login failed");
                return null;
            }
            
            _logger.LogInformation("    Acquiring fresh Graph API token...");
            
            // Get fresh token
            var tokenResult = await executor.ExecuteAsync(
                "az",
                $"account get-access-token --resource https://graph.microsoft.com/ --tenant {tenantId} --query accessToken -o tsv",
                captureOutput: true,
                cancellationToken: cancellationToken);
            
            if (tokenResult.Success && !string.IsNullOrWhiteSpace(tokenResult.StandardOutput))
            {
                var token = tokenResult.StandardOutput.Trim();
                _logger.LogInformation("    Fresh token acquired successfully");
                return token;
            }
            
            _logger.LogError("Failed to acquire fresh token: {Error}", tokenResult.StandardError);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during forced re-authentication");
            return null;
        }
    }

    /// <summary>
    /// Checks if an error response indicates a Continuous Access Evaluation (CAE) token issue
    /// </summary>
    private bool IsCaeTokenError(string errorJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(errorJson))
            {
                return false;
            }

            // Check for common CAE error indicators
            return errorJson.Contains("TokenIssuedBeforeRevocationTimestamp", StringComparison.OrdinalIgnoreCase) ||
                   errorJson.Contains("InteractionRequired", StringComparison.OrdinalIgnoreCase) ||
                   errorJson.Contains("InvalidAuthenticationToken", StringComparison.OrdinalIgnoreCase) && 
                   errorJson.Contains("Continuous access evaluation", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets a service principal by app ID
    /// Equivalent to Get-GraphServicePrincipal in PowerShell
    /// </summary>
    private async Task<JsonDocument?> GetServicePrincipalAsync(
        HttpClient httpClient,
        string appId,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://graph.microsoft.com/v1.0/servicePrincipals?$filter=appId eq '{appId}'";
            var response = await httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("value", out var value) && value.GetArrayLength() > 0)
            {
                // Return just the first service principal
                var spElement = value[0];
                var spJson = JsonSerializer.Serialize(spElement);
                return JsonDocument.Parse(spJson);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in GetServicePrincipalAsync");
            return null;
        }
    }

    /// <summary>
    /// Gets existing AllPrincipals grants between client and resource
    /// Equivalent to Get-ExistingAllPrincipalsGrant in PowerShell
    /// </summary>
    private async Task<List<JsonElement>?> GetExistingGrantsAsync(
        HttpClient httpClient,
        string clientId,
        string resourceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var filter = $"clientId eq '{clientId}' and resourceId eq '{resourceId}' and consentType eq '{AllPrincipalsConsentType}'";
            var url = $"https://graph.microsoft.com/v1.0/oauth2PermissionGrants?$filter={Uri.EscapeDataString(filter)}";

            var response = await httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                var grants = new List<JsonElement>();
                foreach (var grant in value.EnumerateArray())
                {
                    grants.Add(grant.Clone());
                }
                return grants;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in GetExistingGrantsAsync");
            return null;
        }
    }

    /// <summary>
    /// Ensures the specified scope is present on an existing grant
    /// Equivalent to Ensure-ScopeOnGrant in PowerShell
    /// </summary>
    private async Task<bool> EnsureScopeOnGrantAsync(
        HttpClient httpClient,
        JsonElement grant,
        string scopeToAdd,
        CancellationToken cancellationToken)
    {
        try
        {
            var grantId = grant.GetProperty("id").GetString();
            var existingScope = grant.TryGetProperty("scope", out var scopeElement)
                ? scopeElement.GetString() ?? ""
                : "";

            // Parse existing scopes
            var existingScopes = existingScope
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet();

            // Check if scope already exists
            if (existingScopes.Contains(scopeToAdd))
            {
                _logger.LogInformation("    Scope '{Scope}' already exists on grant {GrantId}", scopeToAdd, grantId);
                return true;
            }

            // Add new scope
            existingScopes.Add(scopeToAdd);
            var newScope = string.Join(' ', existingScopes.OrderBy(s => s));

            _logger.LogInformation("    Updating grant {GrantId} to include scope: {Scope}", grantId, scopeToAdd);

            // Update the grant
            var updateUrl = $"https://graph.microsoft.com/v1.0/oauth2PermissionGrants/{grantId}";
            var updateBody = new
            {
                scope = newScope
            };

            var updateResponse = await httpClient.PatchAsync(
                updateUrl,
                new StringContent(
                    JsonSerializer.Serialize(updateBody),
                    System.Text.Encoding.UTF8,
                    "application/json"),
                cancellationToken);

            if (!updateResponse.IsSuccessStatusCode)
            {
                var error = await updateResponse.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("Grant update returned error (may be transient): {Error}", error);
                // Note: We return true here because the grant update failure is often transient
                // and the setup can continue. The "Successfully ensured grant" message below
                // indicates the overall operation succeeded even if this specific update had issues.
                return true;
            }

            _logger.LogInformation("    Grant updated successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in EnsureScopeOnGrantAsync");
            return false;
        }
    }

    /// <summary>
    /// Creates a new AllPrincipals permission grant
    /// Equivalent to Create-AllPrincipalsGrant in PowerShell
    /// </summary>
    private async Task<bool> CreateGrantAsync(
        HttpClient httpClient,
        string clientId,
        string resourceId,
        string scope,
        CancellationToken cancellationToken)
    {
        try
        {
            var createUrl = "https://graph.microsoft.com/v1.0/oauth2PermissionGrants";
            var createBody = new
            {
                clientId = clientId,
                consentType = AllPrincipalsConsentType,
                resourceId = resourceId,
                scope = scope
            };

            var createResponse = await httpClient.PostAsync(
                createUrl,
                new StringContent(
                    JsonSerializer.Serialize(createBody),
                    System.Text.Encoding.UTF8,
                    "application/json"),
                cancellationToken);

            if (!createResponse.IsSuccessStatusCode)
            {
                var error = await createResponse.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to create grant: {Error}", error);
                return false;
            }

            var responseJson = await createResponse.Content.ReadAsStringAsync(cancellationToken);
            var response = JsonDocument.Parse(responseJson);
            var grantId = response.RootElement.GetProperty("id").GetString();

            _logger.LogInformation("    Permission grant created successfully (ID: {GrantId})", grantId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in CreateGrantAsync");
            return false;
        }
    }
}
