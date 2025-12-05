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
            if (missingPermissions.Count > 0)
            {
                return ValidationResult.Failure(
                    ValidationFailureType.MissingPermissions,
                    $"Client app is missing required delegated permissions: {string.Join(", ", missingPermissions)}",
                    "Please add these permissions as DELEGATED (not Application) in Azure Portal > App Registrations > API permissions\nSee: https://github.com/microsoft/Agent365-devTools/blob/main/docs/guides/custom-client-app-registration.md");
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
        JsonObject? graphResource = null;
        foreach (var resource in appInfo.RequiredResourceAccess)
        {
            var resourceObj = resource?.AsObject();
            var resourceAppId = resourceObj?["resourceAppId"]?.GetValue<string>();
            if (resourceAppId == AuthenticationConstants.MicrosoftGraphResourceAppId)
            {
                graphResource = resourceObj;
                break;
            }
        }

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
        var configuredPermissionIds = new HashSet<string>();
        foreach (var access in resourceAccess)
        {
            var accessObj = access?.AsObject();
            var permissionId = accessObj?["id"]?.GetValue<string>();
            var permissionType = accessObj?["type"]?.GetValue<string>();

            if (permissionType == "Scope" && !string.IsNullOrWhiteSpace(permissionId))
            {
                configuredPermissionIds.Add(permissionId);
            }
        }

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
            foreach (var scopeNode in oauth2PermissionScopes)
            {
                var scopeObj = scopeNode?.AsObject();
                var scopeValue = scopeObj?["value"]?.GetValue<string>();
                var scopeId = scopeObj?["id"]?.GetValue<string>();

                if (!string.IsNullOrWhiteSpace(scopeValue) && !string.IsNullOrWhiteSpace(scopeId))
                {
                    permissionNameToIdMap[scopeValue] = scopeId;
                }
            }

            _logger.LogDebug("Retrieved {Count} permission definitions from Microsoft Graph", permissionNameToIdMap.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not retrieve Microsoft Graph permission definitions: {Message}", ex.Message);
        }

        return permissionNameToIdMap;
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
        bool hasGraphGrant = false;
        foreach (var grant in grants)
        {
            var grantObj = grant?.AsObject();
            var scope = grantObj?["scope"]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(scope))
            {
                var grantedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var foundPermissions = AuthenticationConstants.RequiredClientAppPermissions
                    .Intersect(grantedScopes, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (foundPermissions.Count > 0)
                {
                    hasGraphGrant = true;
                    _logger.LogInformation("Admin consent verified for {Count} permissions", foundPermissions.Count);
                    break;
                }
            }
        }

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
