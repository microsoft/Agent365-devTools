// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers;

/// <summary>
/// Helper methods for publish command operations
/// </summary>
public static class PublishHelpers
{
    /// <summary>
    /// Checks if all MOS prerequisites are already configured (idempotency check).
    /// Returns true if service principals, permissions, and admin consent are all in place.
    /// </summary>
    private static async Task<bool> CheckMosPrerequisitesAsync(
        GraphApiService graph,
        Agent365Config config,
        System.Text.Json.JsonElement app,
        ILogger logger,
        CancellationToken ct)
    {
        var authScopes = AuthenticationConstants.PermissionGrantAuthScopes;

        // Check 1: Verify all required service principals exist
        var firstPartyClientSpId = await graph.LookupServicePrincipalByAppIdAsync(
            config.TenantId, 
            MosConstants.TpsAppServicesClientAppId, 
            ct,
            authScopes);
        if (string.IsNullOrWhiteSpace(firstPartyClientSpId))
        {
            logger.LogDebug("Service principal for {ConstantName} ({AppId}) not found - configuration needed", 
                nameof(MosConstants.TpsAppServicesClientAppId), MosConstants.TpsAppServicesClientAppId);
            return false;
        }
        logger.LogDebug("Verified service principal for {ConstantName} ({AppId})", 
            nameof(MosConstants.TpsAppServicesClientAppId), MosConstants.TpsAppServicesClientAppId);

        foreach (var resourceAppId in MosConstants.AllResourceAppIds)
        {
            var spId = await graph.LookupServicePrincipalByAppIdAsync(config.TenantId, resourceAppId, ct, authScopes);
            if (string.IsNullOrWhiteSpace(spId))
            {
                logger.LogDebug("Service principal for {ResourceAppId} not found - configuration needed", resourceAppId);
                return false;
            }
            logger.LogDebug("Verified service principal for resource app ({ResourceAppId})", resourceAppId);
        }

        // Check 2: Verify all MOS permissions are in requiredResourceAccess with correct scopes
        var mosResourcePermissions = MosConstants.ResourcePermissions.GetAll()
            .ToDictionary(p => p.ResourceAppId, p => (p.ScopeName, p.ScopeId));

        if (!app.TryGetProperty("requiredResourceAccess", out var currentResourceAccess))
        {
            logger.LogDebug("No requiredResourceAccess found - configuration needed");
            return false;
        }

        var existingResources = currentResourceAccess.EnumerateArray()
            .Where(r => r.TryGetProperty("resourceAppId", out var _))
            .ToList();

        foreach (var (resourceAppId, (scopeName, scopeId)) in mosResourcePermissions)
        {
            var existingResource = existingResources
                .FirstOrDefault(r => r.GetProperty("resourceAppId").GetString() == resourceAppId);

            if (existingResource.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                logger.LogDebug("MOS resource {ResourceAppId} not in requiredResourceAccess - configuration needed", resourceAppId);
                return false;
            }

            // Verify the correct scope is present
            if (!existingResource.TryGetProperty("resourceAccess", out var resourceAccessArray))
            {
                logger.LogDebug("MOS resource {ResourceAppId} missing resourceAccess - configuration needed", resourceAppId);
                return false;
            }

            var hasCorrectScope = resourceAccessArray.EnumerateArray()
                .Where(permission => permission.TryGetProperty("id", out var _))
                .Any(permission => permission.GetProperty("id").GetString() == scopeId);

            if (!hasCorrectScope)
            {
                logger.LogDebug("MOS resource {ResourceAppId} missing correct scope {ScopeName} - configuration needed", 
                    resourceAppId, scopeName);
                return false;
            }
            
            logger.LogDebug("Verified permission {ScopeName} ({ScopeId}) for resource app ({ResourceAppId})", 
                scopeName, scopeId, resourceAppId);
        }

        // Check 3: Verify admin consent is granted for all MOS resources
        var mosResourceScopes = MosConstants.ResourcePermissions.GetAll()
            .ToDictionary(p => p.ResourceAppId, p => p.ScopeName);

        foreach (var (resourceAppId, scopeName) in mosResourceScopes)
        {
            var resourceSpId = await graph.LookupServicePrincipalByAppIdAsync(config.TenantId, resourceAppId, ct);
            if (string.IsNullOrWhiteSpace(resourceSpId))
            {
                logger.LogDebug("Service principal for {ResourceAppId} not found - configuration needed", resourceAppId);
                return false;
            }

            // Check if OAuth2 permission grant exists
            var grantDoc = await graph.GraphGetAsync(
                config.TenantId,
                $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{firstPartyClientSpId}' and resourceId eq '{resourceSpId}'",
                ct,
                authScopes);

            if (grantDoc == null || !grantDoc.RootElement.TryGetProperty("value", out var grants) || grants.GetArrayLength() == 0)
            {
                logger.LogDebug("Admin consent not granted for {ResourceAppId} - configuration needed", resourceAppId);
                return false;
            }

            // Verify the grant has the correct scope
            var grant = grants[0];
            if (!grant.TryGetProperty("scope", out var grantedScopes))
            {
                logger.LogDebug("Admin consent for {ResourceAppId} missing scope property - configuration needed", resourceAppId);
                return false;
            }

            var scopesString = grantedScopes.GetString();
            if (string.IsNullOrWhiteSpace(scopesString) || !scopesString.Contains(scopeName))
            {
                logger.LogDebug("Admin consent for {ResourceAppId} missing scope {ScopeName} - configuration needed", 
                    resourceAppId, scopeName);
                return false;
            }
            
            logger.LogDebug("Verified admin consent for resource app ({ResourceAppId}) with scope {ScopeName}", 
                resourceAppId, scopeName);
        }

        // All checks passed
        logger.LogDebug("All MOS prerequisites verified: {Count} service principals, {Count} permissions, {Count} admin consents", 
            MosConstants.AllResourceAppIds.Length + 1, MosConstants.AllResourceAppIds.Length, MosConstants.AllResourceAppIds.Length);
        return true;
    }

    /// <summary>
    /// Ensures all required service principals exist for MOS access.
    /// Idempotent - only creates service principals that don't already exist.
    /// </summary>
    private static async Task EnsureMosServicePrincipalsAsync(
        GraphApiService graph,
        Agent365Config config,
        ILogger logger,
        CancellationToken ct)
    {
        var authScopes = AuthenticationConstants.PermissionGrantAuthScopes;

        // Check 1: First-party client app service principal
        var firstPartySpId = await graph.LookupServicePrincipalByAppIdAsync(
            config.TenantId, 
            MosConstants.TpsAppServicesClientAppId, 
            ct,
            authScopes);
        
        if (string.IsNullOrWhiteSpace(firstPartySpId))
        {
            logger.LogInformation("Creating service principal for Microsoft first-party client app...");
            
            try
            {
                firstPartySpId = await graph.EnsureServicePrincipalForAppIdAsync(
                    config.TenantId, 
                    MosConstants.TpsAppServicesClientAppId, 
                    ct,
                    authScopes);
                
                if (string.IsNullOrWhiteSpace(firstPartySpId))
                {
                    throw new SetupValidationException(
                        $"Failed to create service principal for Microsoft first-party client app {MosConstants.TpsAppServicesClientAppId}.",
                        mitigationSteps: ErrorMessages.GetFirstPartyClientAppServicePrincipalMitigation());
                }
                
                logger.LogDebug("Created first-party client app service principal: {SpObjectId}", firstPartySpId);
            }
            catch (Exception ex) when (ex is not SetupValidationException)
            {
                logger.LogError(ex, "Failed to create service principal for first-party client app");
                
                if (ex.Message.Contains("403") || ex.Message.Contains("Insufficient privileges") || 
                    ex.Message.Contains("Authorization_RequestDenied"))
                {
                    throw new SetupValidationException(
                        "Insufficient privileges to create service principal for Microsoft first-party client app.",
                        mitigationSteps: ErrorMessages.GetFirstPartyClientAppServicePrincipalMitigation());
                }

                throw new SetupValidationException($"Failed to create service principal for first-party client app: {ex.Message}");
            }
        }
        else
        {
            logger.LogDebug("First-party client app service principal already exists: {SpObjectId}", firstPartySpId);
        }

        // Check 2: MOS resource app service principals
        var missingResourceApps = new List<string>();
        foreach (var resourceAppId in MosConstants.AllResourceAppIds)
        {
            var spId = await graph.LookupServicePrincipalByAppIdAsync(config.TenantId, resourceAppId, ct, authScopes);
            if (string.IsNullOrWhiteSpace(spId))
            {
                missingResourceApps.Add(resourceAppId);
            }
        }

        if (missingResourceApps.Count > 0)
        {
            logger.LogInformation("Creating service principals for {Count} MOS resource applications...", missingResourceApps.Count);
            
            foreach (var resourceAppId in missingResourceApps)
            {
                try
                {
                    var spId = await graph.EnsureServicePrincipalForAppIdAsync(config.TenantId, resourceAppId, ct, authScopes);
                    
                    if (string.IsNullOrWhiteSpace(spId))
                    {
                        throw new SetupValidationException(
                            $"Failed to create service principal for MOS resource app {resourceAppId}.",
                            mitigationSteps: ErrorMessages.GetMosServicePrincipalMitigation(resourceAppId));
                    }
                    
                    logger.LogDebug("Created service principal for {ResourceAppId}: {SpObjectId}", resourceAppId, spId);
                }
                catch (Exception ex) when (ex is not SetupValidationException)
                {
                    logger.LogError(ex, "Failed to create service principal for MOS resource app {ResourceAppId}", resourceAppId);
                    
                    if (ex.Message.Contains("403") || ex.Message.Contains("Insufficient privileges") || 
                        ex.Message.Contains("Authorization_RequestDenied"))
                    {
                        throw new SetupValidationException(
                            $"Insufficient privileges to create service principal for MOS resource app {resourceAppId}.",
                            mitigationSteps: ErrorMessages.GetMosServicePrincipalMitigation(resourceAppId));
                    }

                    throw new SetupValidationException($"Failed to create service principal for MOS resource app {resourceAppId}: {ex.Message}");
                }
            }
        }
        else
        {
            logger.LogDebug("All MOS resource app service principals already exist");
        }
    }

    /// <summary>
    /// Ensures MOS permissions are configured in custom client app's requiredResourceAccess.
    /// Idempotent - only updates if permissions are missing or incorrect.
    /// </summary>
    private static async Task EnsureMosPermissionsConfiguredAsync(
        GraphApiService graph,
        Agent365Config config,
        System.Text.Json.JsonElement app,
        ILogger logger,
        CancellationToken ct)
    {
        var authScopes = AuthenticationConstants.PermissionGrantAuthScopes;

        if (!app.TryGetProperty("id", out var appObjectIdElement))
        {
            throw new SetupValidationException($"Application {config.ClientAppId} missing id property");
        }
        var appObjectId = appObjectIdElement.GetString()!;

        // Get existing requiredResourceAccess
        var resourceAccessList = new List<System.Text.Json.JsonElement>();
        if (app.TryGetProperty("requiredResourceAccess", out var currentResourceAccess))
        {
            resourceAccessList = currentResourceAccess.EnumerateArray().ToList();
        }

        var mosResourcePermissions = MosConstants.ResourcePermissions.GetAll()
            .ToDictionary(p => p.ResourceAppId, p => (p.ScopeName, p.ScopeId));

        // Check what needs to be added or fixed
        var needsUpdate = false;
        var updatedResourceAccess = new List<object>();
        var processedMosResources = new HashSet<string>();

        // Process existing resources
        foreach (var existingResource in resourceAccessList)
        {
            if (!existingResource.TryGetProperty("resourceAppId", out var resAppIdProp))
            {
                continue;
            }

            var existingResourceAppId = resAppIdProp.GetString();
            if (string.IsNullOrEmpty(existingResourceAppId))
            {
                continue;
            }

            if (MosConstants.AllResourceAppIds.Contains(existingResourceAppId))
            {
                var (expectedScopeName, expectedScopeId) = mosResourcePermissions[existingResourceAppId];
                var hasCorrectPermission = false;

                if (existingResource.TryGetProperty("resourceAccess", out var resourceAccessArray))
                {
                    hasCorrectPermission = resourceAccessArray.EnumerateArray()
                        .Where(permission => permission.TryGetProperty("id", out var _))
                        .Any(permission => permission.GetProperty("id").GetString() == expectedScopeId);
                }

                if (hasCorrectPermission)
                {
                    logger.LogDebug("MOS resource app {ResourceAppId} already has correct permission", existingResourceAppId);
                    var resourceObj = System.Text.Json.JsonSerializer.Deserialize<object>(existingResource.GetRawText());
                    if (resourceObj != null)
                    {
                        updatedResourceAccess.Add(resourceObj);
                    }
                }
                else
                {
                    logger.LogDebug("Fixing permission for MOS resource app {ResourceAppId}", existingResourceAppId);
                    needsUpdate = true;
                    updatedResourceAccess.Add(new
                    {
                        resourceAppId = existingResourceAppId,
                        resourceAccess = new[]
                        {
                            new { id = expectedScopeId, type = "Scope" }
                        }
                    });
                }

                processedMosResources.Add(existingResourceAppId);
            }
            else
            {
                // Non-MOS resource - preserve as-is
                var resourceObj = System.Text.Json.JsonSerializer.Deserialize<object>(existingResource.GetRawText());
                if (resourceObj != null)
                {
                    updatedResourceAccess.Add(resourceObj);
                }
            }
        }

        // Add missing MOS resources
        var missingResources = MosConstants.AllResourceAppIds
            .Where(id => !processedMosResources.Contains(id))
            .ToList();

        if (missingResources.Count > 0)
        {
            logger.LogInformation("Adding {Count} missing MOS permissions to custom client app", missingResources.Count);
            needsUpdate = true;

            foreach (var resourceAppId in missingResources)
            {
                var (scopeName, scopeId) = mosResourcePermissions[resourceAppId];
                logger.LogDebug("Adding MOS resource app {ResourceAppId} with scope {ScopeName}", resourceAppId, scopeName);

                updatedResourceAccess.Add(new
                {
                    resourceAppId = resourceAppId,
                    resourceAccess = new[]
                    {
                        new { id = scopeId, type = "Scope" }
                    }
                });
            }
        }

        // Only update if something changed
        if (!needsUpdate)
        {
            logger.LogDebug("MOS permissions already configured correctly");
            return;
        }

        try
        {
            var patchPayload = new { requiredResourceAccess = updatedResourceAccess };
            logger.LogDebug("Updating application {AppObjectId} with {Count} resource access entries", 
                appObjectId, updatedResourceAccess.Count);

            var updated = await graph.GraphPatchAsync(config.TenantId, $"/v1.0/applications/{appObjectId}", patchPayload, ct, authScopes);
            if (!updated)
            {
                throw new SetupValidationException("Failed to update application with MOS API permissions.");
            }

            logger.LogInformation("MOS API permissions configured successfully");
        }
        catch (Exception ex) when (ex is not SetupValidationException)
        {
            logger.LogError(ex, "Error configuring MOS API permissions");
            throw new SetupValidationException($"Failed to configure MOS API permissions: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensures MOS (Microsoft Online Services) prerequisites are configured for the custom client app.
    /// This includes creating service principals for MOS resource apps and verifying admin consent.
    /// </summary>
    /// <param name="graph">Graph API service for making Microsoft Graph calls</param>
    /// <param name="config">Agent365 configuration containing tenant and client app information</param>
    /// <param name="logger">Logger for diagnostic output</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if prerequisites are configured successfully</returns>
    /// <exception cref="SetupValidationException">Thrown when prerequisites cannot be configured</exception>
    public static async Task<bool> EnsureMosPrerequisitesAsync(
        GraphApiService graph,
        AgentBlueprintService blueprintService,
        Agent365Config config,
        ILogger logger,
        CancellationToken ct = default)
    {
        var authScopes = AuthenticationConstants.PermissionGrantAuthScopes;

        if (string.IsNullOrWhiteSpace(config.ClientAppId))
        {
            logger.LogError("Custom client app ID not found in configuration. Run 'a365 config init' first.");
            throw new SetupValidationException("Custom client app ID is required for MOS token acquisition.");
        }

        // Load custom client app
        logger.LogDebug("Checking MOS prerequisites for custom client app {ClientAppId}", config.ClientAppId);
        var appDoc = await graph.GraphGetAsync(
            config.TenantId, 
            $"/v1.0/applications?$filter=appId eq '{config.ClientAppId}'&$select=id,requiredResourceAccess", 
            ct,
            authScopes);
        
        if (appDoc == null || !appDoc.RootElement.TryGetProperty("value", out var appsArray) || appsArray.GetArrayLength() == 0)
        {
            logger.LogError("Custom client app {ClientAppId} not found in tenant", config.ClientAppId);
            throw new SetupValidationException($"Custom client app {config.ClientAppId} not found. Verify the app exists and you have access.");
        }

        var app = appsArray[0];

        // Check if all MOS prerequisites are already configured (idempotency check)
        var prerequisitesMet = await CheckMosPrerequisitesAsync(graph, config, app, logger, ct);
        if (prerequisitesMet)
        {
            logger.LogDebug("MOS prerequisites already configured");
            return true;
        }
        
        logger.LogDebug("Configuring MOS API prerequisites...");

        // Step 1: Ensure service principals exist (idempotent - only creates if missing)
        await EnsureMosServicePrincipalsAsync(graph, config, logger, ct);

        // Step 2: Ensure MOS permissions are configured in requiredResourceAccess (idempotent - only updates if needed)
        await EnsureMosPermissionsConfiguredAsync(graph, config, app, logger, ct);

        // Step 3: Ensure admin consent is granted for MOS permissions (idempotent - only grants if missing)
        await EnsureMosAdminConsentAsync(graph, blueprintService, config, logger, ct);

        return true;
    }

    /// <summary>
    /// Ensures admin consent is granted for MOS permissions.
    /// Idempotent - only grants consent for resources that don't already have it.
    /// Uses the Microsoft first-party client app for MOS access (required by MOS APIs).
    /// </summary>
    private static async Task EnsureMosAdminConsentAsync(
        GraphApiService graph,
        AgentBlueprintService blueprintService,
        Agent365Config config,
        ILogger logger,
        CancellationToken ct)
    {
        var authScopes = AuthenticationConstants.PermissionGrantAuthScopes;

        // Look up the first-party client app's service principal
        var clientSpObjectId = await graph.LookupServicePrincipalByAppIdAsync(
            config.TenantId, 
            MosConstants.TpsAppServicesClientAppId, 
            ct,
            authScopes);
        
        if (string.IsNullOrWhiteSpace(clientSpObjectId))
        {
            throw new SetupValidationException(
                $"Service principal not found for Microsoft first-party client app {MosConstants.TpsAppServicesClientAppId}");
        }

        logger.LogDebug("First-party client service principal ID: {ClientSpObjectId}", clientSpObjectId);

        var mosResourceScopes = MosConstants.ResourcePermissions.GetAll()
            .ToDictionary(p => p.ResourceAppId, p => p.ScopeName);

        var resourcesToConsent = new List<(string ResourceAppId, string ScopeName, string ResourceSpId)>();

        // Check which resources need consent
        foreach (var (resourceAppId, scopeName) in mosResourceScopes)
        {
            var resourceSpObjectId = await graph.LookupServicePrincipalByAppIdAsync(config.TenantId, resourceAppId, ct, authScopes);
            if (string.IsNullOrWhiteSpace(resourceSpObjectId))
            {
                logger.LogWarning("Service principal not found for MOS resource app {ResourceAppId} - skipping consent", resourceAppId);
                continue;
            }

            // Check if consent already exists
            var grantDoc = await graph.GraphGetAsync(
                config.TenantId,
                $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{clientSpObjectId}' and resourceId eq '{resourceSpObjectId}'",
                ct,
                authScopes);

            var hasConsent = false;
            if (grantDoc != null && grantDoc.RootElement.TryGetProperty("value", out var grants) && grants.GetArrayLength() > 0)
            {
                var grant = grants[0];
                if (grant.TryGetProperty("scope", out var grantedScopes))
                {
                    var scopesString = grantedScopes.GetString();
                    hasConsent = !string.IsNullOrWhiteSpace(scopesString) && scopesString.Contains(scopeName);
                }
            }

            if (hasConsent)
            {
                logger.LogDebug("Admin consent already granted for {ResourceAppId}", resourceAppId);
            }
            else
            {
                resourcesToConsent.Add((resourceAppId, scopeName, resourceSpObjectId));
            }
        }

        // Grant consent for resources that need it
        if (resourcesToConsent.Count == 0)
        {
            logger.LogDebug("Admin consent already configured for all MOS resources");
            return;
        }

        logger.LogInformation("Granting admin consent for {Count} MOS resources", resourcesToConsent.Count);

        var failedGrants = new List<string>();
        
        foreach (var (resourceAppId, scopeName, resourceSpObjectId) in resourcesToConsent)
        {
            logger.LogDebug("Granting admin consent for {ResourceAppId} with scope {ScopeName}", resourceAppId, scopeName);

            var success = await blueprintService.ReplaceOauth2PermissionGrantAsync(
                config.TenantId,
                clientSpObjectId,
                resourceSpObjectId,
                new[] { scopeName },
                ct);

            if (!success)
            {
                logger.LogError("Failed to grant admin consent for {ResourceAppId}", resourceAppId);
                failedGrants.Add(resourceAppId);
            }
        }

        if (failedGrants.Count > 0)
        {
            var failedList = string.Join(", ", failedGrants);
            logger.LogError("Failed to grant admin consent for {Count} MOS resource(s): {FailedResources}", 
                failedGrants.Count, failedList);
            throw new SetupValidationException(
                $"Failed to grant admin consent for {failedGrants.Count} MOS resource(s): {failedList}. " +
                "MOS token acquisition will fail without proper consent.",
                mitigationSteps: ErrorMessages.GetMosAdminConsentMitigation(config.ClientAppId));
        }

        logger.LogInformation("Admin consent granted successfully for all {Count} MOS resources", resourcesToConsent.Count);
        
        // Clear cached MOS tokens to force re-acquisition with new scopes
        logger.LogDebug("Clearing cached MOS tokens to force re-acquisition with updated permissions");
        var cacheDir = FileHelper.GetSecureCrossOsDirectory();
        var cacheFilePath = Path.Combine(cacheDir, "mos-token-cache.json");
        if (File.Exists(cacheFilePath))
        {
            try
            {
                File.Delete(cacheFilePath);
                logger.LogDebug("Deleted MOS token cache file: {CacheFile}", cacheFilePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not delete MOS token cache file {CacheFile}: {Message}", 
                    cacheFilePath, ex.Message);
            }
        }
        else
        {
            logger.LogDebug("No MOS token cache file found at {CacheFile}", cacheFilePath);
        }
    }
}
