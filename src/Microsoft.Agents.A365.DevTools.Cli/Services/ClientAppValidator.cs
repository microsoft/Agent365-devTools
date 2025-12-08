// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Validates that a client app exists and has the required permissions for a365 CLI operations.
/// </summary>
public sealed class ClientAppValidator
{
    private readonly ILogger<ClientAppValidator> _logger;
    private readonly CommandExecutor _executor;

    private const string GraphApiBaseUrl = "https://graph.microsoft.com/v1.0";
    private const string GraphTokenResource = "https://graph.microsoft.com";

    public ClientAppValidator(ILogger<ClientAppValidator> logger, CommandExecutor executor)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <summary>
    /// Ensures the client app exists and has required permissions granted.
    /// Throws ClientAppValidationException if validation fails.
    /// Logs validation progress and results automatically.
    /// </summary>
    /// <param name="clientAppId">The client app ID to validate</param>
    /// <param name="tenantId">The tenant ID where the app should exist</param>
    /// <param name="ct">Cancellation token</param>
    /// <exception cref="ClientAppValidationException">Thrown when validation fails</exception>
    public async Task EnsureValidClientAppAsync(
        string clientAppId,
        string tenantId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        _logger.LogInformation("");
        _logger.LogInformation("==> Validating Client App Configuration");
        
        var result = await ValidateClientAppAsync(clientAppId, tenantId, ct);
        
        if (!result.IsValid)
        {
            ThrowAppropriateException(result, clientAppId, tenantId);
        }
    }

    /// <summary>
    /// Validates that the client app exists and has required permissions granted.
    /// Returns validation result with error details for programmatic handling.
    /// </summary>
    /// <param name="clientAppId">The client app ID to validate</param>
    /// <param name="tenantId">The tenant ID where the app should exist</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Validation result with structured error information</returns>
    public async Task<ValidationResult> ValidateClientAppAsync(
        string clientAppId,
        string tenantId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Step 1: Validate GUID format
        if (!Guid.TryParse(clientAppId, out _))
        {
            return ValidationResult.Failure(
                ValidationFailureType.InvalidFormat,
                $"clientAppId must be a valid GUID format (received: {clientAppId})");
        }

        if (!Guid.TryParse(tenantId, out _))
        {
            return ValidationResult.Failure(
                ValidationFailureType.InvalidFormat,
                $"tenantId must be a valid GUID format (received: {tenantId})");
        }

        try
        {
            // Step 2: Acquire Graph token
            var graphToken = await AcquireGraphTokenAsync(ct);
            if (string.IsNullOrWhiteSpace(graphToken))
            {
                return ValidationResult.Failure(
                    ValidationFailureType.AuthenticationFailed,
                    "Failed to acquire Microsoft Graph access token. Ensure you are logged in with 'az login'");
            }

            // Step 3: Verify app exists
            var appInfo = await GetClientAppInfoAsync(clientAppId, graphToken, ct);
            if (appInfo == null)
            {
                return ValidationResult.Failure(
                    ValidationFailureType.AppNotFound,
                    $"Client app with ID '{clientAppId}' not found in tenant '{tenantId}'",
                    "Please create the app registration in Azure Portal and ensure the app ID is correct");
            }

            _logger.LogInformation("Found client app: {DisplayName} ({AppId})", appInfo.DisplayName, clientAppId);

            // Step 4: Validate permissions in manifest
            var missingPermissions = await ValidatePermissionsConfiguredAsync(appInfo, graphToken, ct);
            
            // Step 4.5: For any unresolvable permissions (beta APIs), check oauth2PermissionGrants as fallback
            if (missingPermissions.Count > 0)
            {
                var consentedPermissions = await GetConsentedPermissionsAsync(clientAppId, graphToken, ct);
                // Remove permissions that have been consented even if not in app registration
                missingPermissions.RemoveAll(p => consentedPermissions.Contains(p, StringComparer.OrdinalIgnoreCase));
                
                if (consentedPermissions.Count > 0)
                {
                    _logger.LogInformation("Found {Count} consented permissions via oauth2PermissionGrants (including beta APIs)", consentedPermissions.Count);
                }
            }
            
            if (missingPermissions.Count > 0)
            {
                return ValidationResult.Failure(
                    ValidationFailureType.MissingPermissions,
                    $"Client app is missing required delegated permissions: {string.Join(", ", missingPermissions)}",
                    $"Please add these permissions as DELEGATED (not Application) in Azure Portal > App Registrations > API permissions\nSee: {ConfigConstants.Agent365CliDocumentationUrl}");
            }

            // Step 5: Verify admin consent
            var consentResult = await ValidateAdminConsentAsync(clientAppId, graphToken, ct);
            if (!consentResult.IsValid)
            {
                return consentResult;
            }

            _logger.LogInformation("Client app validation successful!");
            return ValidationResult.Success();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parsing error during validation");
            return ValidationResult.Failure(
                ValidationFailureType.InvalidResponse,
                $"Failed to parse Microsoft Graph response: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Validation error");
            return ValidationResult.Failure(
                ValidationFailureType.UnexpectedError,
                $"Unexpected error during client app validation: {ex.Message}");
        }
    }

    #region Private Helper Methods

    private async Task<string?> AcquireGraphTokenAsync(CancellationToken ct)
    {
        _logger.LogInformation("Acquiring Microsoft Graph token for validation...");
        
        var tokenResult = await _executor.ExecuteAsync(
            "az",
            $"account get-access-token --resource {GraphTokenResource} --query accessToken -o tsv",
            cancellationToken: ct);

        if (!tokenResult.Success || string.IsNullOrWhiteSpace(tokenResult.StandardOutput))
        {
            _logger.LogError("Token acquisition failed: {Error}", tokenResult.StandardError);
            return null;
        }

        return tokenResult.StandardOutput.Trim();
    }

    private async Task<ClientAppInfo?> GetClientAppInfoAsync(string clientAppId, string graphToken, CancellationToken ct)
    {
        _logger.LogInformation("Checking if client app exists in tenant...");
        
        var appCheckResult = await _executor.ExecuteAsync(
            "az",
            $"rest --method GET --url \"{GraphApiBaseUrl}/applications?$filter=appId eq '{clientAppId}'&$select=id,appId,displayName,requiredResourceAccess\" --headers \"Authorization=Bearer {graphToken}\"",
            cancellationToken: ct);

        if (!appCheckResult.Success)
        {
            // Check for Continuous Access Evaluation (CAE) token issues
            if (appCheckResult.StandardError.Contains("TokenCreatedWithOutdatedPolicies", StringComparison.OrdinalIgnoreCase) ||
                appCheckResult.StandardError.Contains("InvalidAuthenticationToken", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Azure CLI token is stale due to Continuous Access Evaluation. Refreshing token automatically...");
                
                // Force token refresh
                var refreshResult = await _executor.ExecuteAsync(
                    "az",
                    $"account get-access-token --resource {GraphTokenResource} --query accessToken -o tsv",
                    cancellationToken: ct);
                
                if (refreshResult.Success && !string.IsNullOrWhiteSpace(refreshResult.StandardOutput))
                {
                    var freshToken = refreshResult.StandardOutput.Trim();
                    _logger.LogInformation("Token refreshed successfully, retrying...");
                    
                    // Retry with fresh token
                    var retryResult = await _executor.ExecuteAsync(
                        "az",
                        $"rest --method GET --url \"{GraphApiBaseUrl}/applications?$filter=appId eq '{clientAppId}'&$select=id,appId,displayName,requiredResourceAccess\" --headers \"Authorization=Bearer {freshToken}\"",
                        cancellationToken: ct);
                    
                    if (retryResult.Success)
                    {
                        appCheckResult = retryResult;
                    }
                    else
                    {
                        _logger.LogError("App query failed after token refresh: {Error}", retryResult.StandardError);
                        return null;
                    }
                }
            }
            
            if (!appCheckResult.Success)
            {
                _logger.LogError("App query failed: {Error}", appCheckResult.StandardError);
                return null;
            }
        }

        var appResponse = JsonNode.Parse(appCheckResult.StandardOutput);
        var apps = appResponse?["value"]?.AsArray();

        if (apps == null || apps.Count == 0)
        {
            return null;
        }

        var app = apps[0]!.AsObject();
        return new ClientAppInfo(
            app["id"]?.GetValue<string>() ?? string.Empty,
            app["displayName"]?.GetValue<string>() ?? string.Empty,
            app["requiredResourceAccess"]?.AsArray());
    }

    private async Task<List<string>> ValidatePermissionsConfiguredAsync(
        ClientAppInfo appInfo,
        string graphToken,
        CancellationToken ct)
    {
        var missingPermissions = new List<string>();

        if (appInfo.RequiredResourceAccess == null || appInfo.RequiredResourceAccess.Count == 0)
        {
            return AuthenticationConstants.RequiredClientAppPermissions.ToList();
        }

        // Find Microsoft Graph resource in required permissions
        var graphResource = appInfo.RequiredResourceAccess
            .Select(r => r?.AsObject())
            .FirstOrDefault(obj => obj?["resourceAppId"]?.GetValue<string>() == AuthenticationConstants.MicrosoftGraphResourceAppId);

        if (graphResource == null)
        {
            return AuthenticationConstants.RequiredClientAppPermissions.ToList();
        }

        var resourceAccess = graphResource["resourceAccess"]?.AsArray();
        if (resourceAccess == null || resourceAccess.Count == 0)
        {
            return AuthenticationConstants.RequiredClientAppPermissions.ToList();
        }

        // Build set of configured permission IDs
        var configuredPermissionIds = resourceAccess
            .Select(access => access?.AsObject())
            .Select(accessObj => new
            {
                PermissionId = accessObj?["id"]?.GetValue<string>(),
                PermissionType = accessObj?["type"]?.GetValue<string>()
            })
            .Where(x => x.PermissionType == "Scope" && !string.IsNullOrWhiteSpace(x.PermissionId))
            .Select(x => x.PermissionId!)
            .ToHashSet();

        // Resolve ALL permission IDs dynamically from Microsoft Graph
        // This ensures compatibility across different tenants and API versions
        var permissionNameToIdMap = await ResolvePermissionIdsAsync(graphToken, ct);

        // Check each required permission
        foreach (var permissionName in AuthenticationConstants.RequiredClientAppPermissions)
        {
            if (permissionNameToIdMap.TryGetValue(permissionName, out var permissionId))
            {
                if (!configuredPermissionIds.Contains(permissionId))
                {
                    missingPermissions.Add(permissionName);
                }
                _logger.LogDebug("Validated permission {PermissionName} (ID: {PermissionId})", permissionName, permissionId);
            }
            else
            {
                _logger.LogWarning("Could not resolve permission ID for: {PermissionName}", permissionName);
                _logger.LogWarning("This permission may be a beta API or unavailable in your tenant. Validation cannot verify its presence.");
                // Don't add to missing list - we can't verify it
            }
        }

        return missingPermissions;
    }

    /// <summary>
    /// Resolves permission names to their GUIDs by querying Microsoft Graph's published permission definitions.
    /// This approach is tenant-agnostic and works across different API versions.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolvePermissionIdsAsync(string graphToken, CancellationToken ct)
    {
        var permissionNameToIdMap = new Dictionary<string, string>();

        try
        {
            var graphSpResult = await _executor.ExecuteAsync(
                "az",
                $"rest --method GET --url \"{GraphApiBaseUrl}/servicePrincipals?$filter=appId eq '{AuthenticationConstants.MicrosoftGraphResourceAppId}'&$select=id,oauth2PermissionScopes\" --headers \"Authorization=Bearer {graphToken}\"",
                cancellationToken: ct);

            if (!graphSpResult.Success)
            {
                _logger.LogWarning("Failed to query Microsoft Graph for permission definitions");
                return permissionNameToIdMap;
            }

            var graphSpResponse = JsonNode.Parse(graphSpResult.StandardOutput);
            var graphSps = graphSpResponse?["value"]?.AsArray();

            if (graphSps == null || graphSps.Count == 0)
            {
                _logger.LogWarning("No Microsoft Graph service principal found");
                return permissionNameToIdMap;
            }

            var graphSp = graphSps[0]!.AsObject();
            var oauth2PermissionScopes = graphSp["oauth2PermissionScopes"]?.AsArray();

            if (oauth2PermissionScopes == null)
            {
                _logger.LogWarning("No permission scopes found in Microsoft Graph service principal");
                return permissionNameToIdMap;
            }

            // Build map of all available permissions (name -> GUID)
            permissionNameToIdMap = oauth2PermissionScopes
                .Select(scopeNode => scopeNode?.AsObject())
                .Select(scopeObj => new
                {
                    Value = scopeObj?["value"]?.GetValue<string>(),
                    Id = scopeObj?["id"]?.GetValue<string>()
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Value) && !string.IsNullOrWhiteSpace(x.Id))
                .ToDictionary(x => x.Value!, x => x.Id!);

            _logger.LogDebug("Retrieved {Count} permission definitions from Microsoft Graph", permissionNameToIdMap.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not retrieve Microsoft Graph permission definitions: {Message}", ex.Message);
        }

        return permissionNameToIdMap;
    }

    /// <summary>
    /// Gets the list of permissions that have been consented for the app via oauth2PermissionGrants.
    /// This is used as a fallback for beta permissions that may not be visible in the app registration's requiredResourceAccess.
    /// </summary>
    private async Task<HashSet<string>> GetConsentedPermissionsAsync(string clientAppId, string graphToken, CancellationToken ct)
    {
        var consentedPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Get service principal for the app
            var spCheckResult = await _executor.ExecuteAsync(
                "az",
                $"rest --method GET --url \"{GraphApiBaseUrl}/servicePrincipals?$filter=appId eq '{clientAppId}'&$select=id\" --headers \"Authorization=Bearer {graphToken}\"",
                cancellationToken: ct);

            if (!spCheckResult.Success)
            {
                _logger.LogDebug("Could not query service principal for consent check");
                return consentedPermissions;
            }

            var spResponse = JsonNode.Parse(spCheckResult.StandardOutput);
            var servicePrincipals = spResponse?["value"]?.AsArray();

            if (servicePrincipals == null || servicePrincipals.Count == 0)
            {
                _logger.LogDebug("Service principal not found for consent check");
                return consentedPermissions;
            }

            var sp = servicePrincipals[0]!.AsObject();
            var spObjectId = sp["id"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(spObjectId))
            {
                return consentedPermissions;
            }

            // Get oauth2PermissionGrants
            var grantsResult = await _executor.ExecuteAsync(
                "az",
                $"rest --method GET --url \"{GraphApiBaseUrl}/oauth2PermissionGrants?$filter=clientId eq '{spObjectId}'\" --headers \"Authorization=Bearer {graphToken}\"",
                cancellationToken: ct);

            if (!grantsResult.Success)
            {
                _logger.LogDebug("Could not query oauth2PermissionGrants");
                return consentedPermissions;
            }

            var grantsResponse = JsonNode.Parse(grantsResult.StandardOutput);
            var grants = grantsResponse?["value"]?.AsArray();

            if (grants == null || grants.Count == 0)
            {
                return consentedPermissions;
            }

            // Extract all scopes from grants
            foreach (var grant in grants)
            {
                var grantObj = grant?.AsObject();
                var scope = grantObj?["scope"]?.GetValue<string>();
                
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var s in scopes)
                    {
                        consentedPermissions.Add(s);
                    }
                }
            }

            _logger.LogDebug("Found {Count} consented permissions from oauth2PermissionGrants", consentedPermissions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error retrieving consented permissions: {Message}", ex.Message);
        }

        return consentedPermissions;
    }

    private async Task<ValidationResult> ValidateAdminConsentAsync(string clientAppId, string graphToken, CancellationToken ct)
    {
        _logger.LogInformation("Checking admin consent status...");

        // Get service principal for the app
        var spCheckResult = await _executor.ExecuteAsync(
            "az",
            $"rest --method GET --url \"{GraphApiBaseUrl}/servicePrincipals?$filter=appId eq '{clientAppId}'&$select=id,appId\" --headers \"Authorization=Bearer {graphToken}\"",
            cancellationToken: ct);

        if (!spCheckResult.Success)
        {
            _logger.LogWarning("Could not verify service principal (may not exist yet): {Error}", spCheckResult.StandardError);
            _logger.LogWarning("Admin consent will be verified during first interactive authentication");
            return ValidationResult.Success(); // Best-effort check
        }

        var spResponse = JsonNode.Parse(spCheckResult.StandardOutput);
        var servicePrincipals = spResponse?["value"]?.AsArray();

        if (servicePrincipals == null || servicePrincipals.Count == 0)
        {
            _logger.LogWarning("Service principal not created yet for this app");
            _logger.LogWarning("Admin consent will be verified during first interactive authentication");
            return ValidationResult.Success(); // Best-effort check
        }

        var sp = servicePrincipals[0]!.AsObject();
        var spObjectId = sp["id"]?.GetValue<string>();

        // Check OAuth2 permission grants
        var grantsCheckResult = await _executor.ExecuteAsync(
            "az",
            $"rest --method GET --url \"{GraphApiBaseUrl}/oauth2PermissionGrants?$filter=clientId eq '{spObjectId}'\" --headers \"Authorization=Bearer {graphToken}\"",
            cancellationToken: ct);

        if (!grantsCheckResult.Success)
        {
            _logger.LogWarning("Could not verify admin consent status: {Error}", grantsCheckResult.StandardError);
            _logger.LogWarning("Please ensure admin consent has been granted for the configured permissions");
            return ValidationResult.Success(); // Best-effort check
        }

        var grantsResponse = JsonNode.Parse(grantsCheckResult.StandardOutput);
        var grants = grantsResponse?["value"]?.AsArray();

        if (grants == null || grants.Count == 0)
        {
            return ValidationResult.Failure(
                ValidationFailureType.AdminConsentMissing,
                "Admin consent has not been granted for this client app",
                "Please grant admin consent in Azure Portal > App Registrations > API permissions > Grant admin consent");
        }

        // Check if there's a grant for Microsoft Graph with required scopes
        var hasGraphGrant = grants
            .Select(grant => grant?.AsObject())
            .Select(grantObj => grantObj?["scope"]?.GetValue<string>())
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Any(scope =>
            {
                var grantedScopes = scope!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var foundPermissions = AuthenticationConstants.RequiredClientAppPermissions
                    .Intersect(grantedScopes, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (foundPermissions.Count > 0)
                {
                    _logger.LogInformation("Admin consent verified for {Count} permissions", foundPermissions.Count);
                    return true;
                }
                return false;
            });

        if (!hasGraphGrant)
        {
            return ValidationResult.Failure(
                ValidationFailureType.AdminConsentMissing,
                "Admin consent appears to be missing or incomplete",
                "Please grant admin consent in Azure Portal > App Registrations > API permissions > Grant admin consent");
        }

        return ValidationResult.Success();
    }

    private void ThrowAppropriateException(ValidationResult result, string clientAppId, string tenantId)
    {
        switch (result.FailureType)
        {
            case ValidationFailureType.AppNotFound:
                throw ClientAppValidationException.AppNotFound(clientAppId, tenantId);

            case ValidationFailureType.MissingPermissions:
                var missingPerms = result.Errors[0]
                    .Replace("Client app is missing required delegated permissions: ", "")
                    .Split(',', StringSplitOptions.TrimEntries)
                    .ToList();
                throw ClientAppValidationException.MissingPermissions(clientAppId, missingPerms);

            case ValidationFailureType.AdminConsentMissing:
                throw ClientAppValidationException.MissingAdminConsent(clientAppId);

            default:
                throw ClientAppValidationException.ValidationFailed(
                    result.Errors[0],
                    result.Errors.Skip(1).ToList(),
                    clientAppId);
        }
    }

    #endregion

    #region Helper Types

    private record ClientAppInfo(string ObjectId, string DisplayName, JsonArray? RequiredResourceAccess);

    public record ValidationResult(
        bool IsValid,
        ValidationFailureType FailureType,
        List<string> Errors)
    {
        public static ValidationResult Success() =>
            new(true, ValidationFailureType.None, new List<string>());

        public static ValidationResult Failure(ValidationFailureType type, params string[] errors) =>
            new(false, type, errors.ToList());
    }

    public enum ValidationFailureType
    {
        None,
        InvalidFormat,
        AuthenticationFailed,
        AppNotFound,
        MissingPermissions,
        AdminConsentMissing,
        InvalidResponse,
        UnexpectedError
    }

    #endregion
}
