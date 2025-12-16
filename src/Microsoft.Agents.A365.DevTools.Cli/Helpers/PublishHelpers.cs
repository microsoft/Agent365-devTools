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
        Agent365Config config,
        ILogger logger,
        CancellationToken ct = default)
    {
        logger.LogInformation("Configuring MOS API permissions for publish operations...");

        if (string.IsNullOrWhiteSpace(config.ClientAppId))
        {
            logger.LogError("Custom client app ID not found in configuration. Run 'a365 config init' first.");
            throw new SetupValidationException("Custom client app ID is required for MOS token acquisition.");
        }

        // Check if MOS permissions already exist (idempotency check)
        logger.LogDebug("Checking if MOS permissions already exist in custom client app {ClientAppId}", config.ClientAppId);
        var appDoc = await graph.GraphGetAsync(config.TenantId, 
            $"/v1.0/applications?$filter=appId eq '{config.ClientAppId}'&$select=id,requiredResourceAccess", ct);
        
        if (appDoc == null || !appDoc.RootElement.TryGetProperty("value", out var appsArray) || appsArray.GetArrayLength() == 0)
        {
            logger.LogError("Custom client app {ClientAppId} not found in tenant", config.ClientAppId);
            throw new SetupValidationException($"Custom client app {config.ClientAppId} not found. Verify the app exists and you have access.");
        }

        var app = appsArray[0];
        
        // Note: Don't early-return here even if MOS apps exist - we need to validate
        // that they have the correct permissions and fix them if wrong (true idempotency)
        logger.LogInformation("Configuring MOS API permissions...");

        // Pre-check: Verify user has privileges to create service principals
        logger.LogDebug("Checking if user has privileges to create service principals...");
        var (hasPrivileges, userRoles) = await graph.CheckServicePrincipalCreationPrivilegesAsync(config.TenantId, ct);
        
        if (!hasPrivileges)
        {
            logger.LogWarning("User does not have required roles for service principal creation");
            logger.LogWarning("User's current roles: {Roles}", string.Join(", ", userRoles));
            logger.LogWarning("Will attempt service principal creation anyway - may fail if privileges are insufficient");
            
            var mitigation = ErrorMessages.GetMosResourceAppsServicePrincipalMitigation();
            foreach (var line in mitigation)
            {
                logger.LogWarning(line);
            }
        }
        else
        {
            logger.LogDebug("User has sufficient privileges to create service principals");
        }

        // Step 1a: Create service principal for Microsoft first-party client app (required for MOS token acquisition)
        logger.LogInformation("Creating service principal for Microsoft first-party client app...");
        logger.LogDebug("Ensuring service principal exists for TPS AppServices 3p App (Client): {ClientAppId}", 
            MosConstants.TpsAppServicesClientAppId);
        
        try
        {
            var firstPartySpObjectId = await graph.EnsureServicePrincipalForAppIdAsync(config.TenantId, 
                MosConstants.TpsAppServicesClientAppId, ct);
            if (string.IsNullOrWhiteSpace(firstPartySpObjectId))
            {
                logger.LogError("Failed to create service principal for Microsoft first-party client app {ClientAppId}", 
                    MosConstants.TpsAppServicesClientAppId);
                throw new SetupValidationException(
                    $"Failed to ensure service principal for Microsoft first-party client app {MosConstants.TpsAppServicesClientAppId}. " +
                    "This is required for MOS token acquisition. Ensure you have Application.ReadWrite.All or Directory.AccessAsUser.All permissions.");
            }
            logger.LogDebug("First-party client app service principal exists with object ID: {SpObjectId}", firstPartySpObjectId);
        }
        catch (Exception ex) when (ex is not SetupValidationException)
        {
            logger.LogError(ex, "Failed to create service principal for first-party client app: {Message}", ex.Message);
            
            if (ex.Message.Contains("403") || ex.Message.Contains("Insufficient privileges") || 
                ex.Message.Contains("Authorization_RequestDenied"))
            {
                var mitigation = ErrorMessages.GetFirstPartyClientAppServicePrincipalMitigation();
                throw new SetupValidationException(
                    "Insufficient privileges to create service principal for Microsoft first-party client app",
                    mitigationSteps: mitigation);
            }

            throw new SetupValidationException(
                $"Failed to create service principal for first-party client app: {ex.Message}");
        }

        // Step 1b: Create service principals for MOS resource apps (fail-fast on privilege errors)
        logger.LogInformation("Creating service principals for MOS resource applications...");
        foreach (var resourceAppId in MosConstants.AllResourceAppIds)
        {
            logger.LogDebug("Ensuring service principal exists for MOS resource app {ResourceAppId}", resourceAppId);
            
            try
            {
                var spObjectId = await graph.EnsureServicePrincipalForAppIdAsync(config.TenantId, resourceAppId, ct);
                if (string.IsNullOrWhiteSpace(spObjectId))
                {
                    logger.LogError("Failed to create or find service principal for MOS resource app {ResourceAppId}", resourceAppId);
                    throw new SetupValidationException(
                        $"Failed to ensure service principal for MOS resource app {resourceAppId}. " +
                        "This operation requires sufficient privileges. Ensure you have Application.ReadWrite.All or Directory.AccessAsUser.All permissions.");
                }
                logger.LogDebug("Service principal exists with object ID: {SpObjectId}", spObjectId);
            }
            catch (Exception ex) when (ex is not SetupValidationException)
            {
                logger.LogError(ex, "Failed to create service principal for MOS resource app {ResourceAppId}: {Message}", 
                    resourceAppId, ex.Message);
                
                // Check if it's a privilege error (403 Forbidden or insufficient privileges message)
                if (ex.Message.Contains("403") || ex.Message.Contains("Insufficient privileges") || 
                    ex.Message.Contains("Authorization_RequestDenied"))
                {
                    var mitigation = ErrorMessages.GetMosServicePrincipalMitigation(resourceAppId);
                    throw new SetupValidationException(
                        $"Insufficient privileges to create service principal for MOS resource app {resourceAppId}",
                        mitigationSteps: mitigation);
                }

                throw new SetupValidationException(
                    $"Failed to create service principal for MOS resource app {resourceAppId}: {ex.Message}");
            }
        }

        logger.LogInformation("Service principals created successfully");

        // Step 2: Add MOS resource apps to custom client app's requiredResourceAccess
        logger.LogInformation("Adding MOS API permissions to custom client app...");
        
        // For .default scope to work, the resource apps must be listed in requiredResourceAccess
        // We don't need to specify individual permissions - just the resource app ID
        try
        {
            // Get the application object (reuse app from earlier check)
            if (!app.TryGetProperty("id", out var appObjectIdElement))
            {
                throw new SetupValidationException($"Application {config.ClientAppId} missing id property");
            }
            var appObjectId = appObjectIdElement.GetString()!;

            // Get existing requiredResourceAccess (already retrieved earlier)
            // Parse as JsonElement to preserve nested structures
            var resourceAccessList = new List<System.Text.Json.JsonElement>();
            if (app.TryGetProperty("requiredResourceAccess", out var currentResourceAccess))
            {
                resourceAccessList = currentResourceAccess.EnumerateArray().ToList();
            }

            // Add MOS resource apps if not already present
            var existingResourceAppIds = new HashSet<string>();
            foreach (var resource in resourceAccessList)
            {
                if (resource.TryGetProperty("resourceAppId", out var resAppId))
                {
                    var id = resAppId.GetString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        existingResourceAppIds.Add(id);
                    }
                }
            }

            // Map each MOS resource app to appropriate delegated permission scopes
            // These permissions are required for publish operations
            var mosResourcePermissions = new Dictionary<string, (string scopeName, string scopeId)>
            {
                // TPS AppServices: AuthConfig.Read (6f17ed22-2455-4cfc-a02d-9ccdde5f7f8c)
                [MosConstants.TpsAppServicesResourceAppId] = ("AuthConfig.Read", "6f17ed22-2455-4cfc-a02d-9ccdde5f7f8c"),
                // Power Platform API: EnvironmentManagement.Environments.Read (177690ed-85f1-41d9-8dbf-2716e60ff46a)
                [MosConstants.PowerPlatformApiResourceAppId] = ("EnvironmentManagement.Environments.Read", "177690ed-85f1-41d9-8dbf-2716e60ff46a"),
                // MOS Titles API: Title.ReadWrite.All (ecb8a615-f488-4c95-9efe-cb0142fc07dd) - required for package upload
                [MosConstants.MosTitlesApiResourceAppId] = ("Title.ReadWrite.All", "ecb8a615-f488-4c95-9efe-cb0142fc07dd")
            };

            // Build the new requiredResourceAccess array (preserve existing + ensure MOS apps have correct permissions)
            var updatedResourceAccess = new List<object>();
            var processedMosResources = new HashSet<string>();
            
            // First, process all existing resource access entries
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
                
                // Check if this is a MOS resource app that needs validation
                if (MosConstants.AllResourceAppIds.Contains(existingResourceAppId))
                {
                    // Validate the permission is correct
                    var (expectedScopeName, expectedScopeId) = mosResourcePermissions[existingResourceAppId];
                    var hasCorrectPermission = false;
                    
                    if (existingResource.TryGetProperty("resourceAccess", out var resourceAccessArray))
                    {
                        foreach (var permission in resourceAccessArray.EnumerateArray())
                        {
                            if (permission.TryGetProperty("id", out var permIdProp))
                            {
                                var permId = permIdProp.GetString();
                                if (permId == expectedScopeId)
                                {
                                    hasCorrectPermission = true;
                                    break;
                                }
                            }
                        }
                    }
                    
                    if (hasCorrectPermission)
                    {
                        logger.LogDebug("MOS resource app {ResourceAppId} has correct permission {ScopeName}", 
                            existingResourceAppId, expectedScopeName);
                        // Keep existing entry as-is
                        var resourceObj = System.Text.Json.JsonSerializer.Deserialize<object>(existingResource.GetRawText());
                        if (resourceObj != null)
                        {
                            updatedResourceAccess.Add(resourceObj);
                        }
                    }
                    else
                    {
                        logger.LogWarning("MOS resource app {ResourceAppId} has incorrect or missing permission - updating to {ScopeName} ({ScopeId})", 
                            existingResourceAppId, expectedScopeName, expectedScopeId);
                        // Replace with correct permission
                        updatedResourceAccess.Add(new
                        {
                            resourceAppId = existingResourceAppId,
                            resourceAccess = new[]
                            {
                                new
                                {
                                    id = expectedScopeId,
                                    type = "Scope"
                                }
                            }
                        });
                    }
                    
                    processedMosResources.Add(existingResourceAppId);
                }
                else
                {
                    // Non-MOS resource app - preserve as-is
                    var resourceObj = System.Text.Json.JsonSerializer.Deserialize<object>(existingResource.GetRawText());
                    if (resourceObj != null)
                    {
                        updatedResourceAccess.Add(resourceObj);
                    }
                }
            }
            
            // Then, add any MOS resource apps that don't exist yet
            foreach (var resourceAppId in MosConstants.AllResourceAppIds)
            {
                if (!processedMosResources.Contains(resourceAppId))
                {
                    var (scopeName, scopeId) = mosResourcePermissions[resourceAppId];
                    logger.LogInformation("Adding MOS resource app {ResourceAppId} with permission {ScopeName} ({ScopeId})", 
                        resourceAppId, scopeName, scopeId);
                    
                    updatedResourceAccess.Add(new
                    {
                        resourceAppId = resourceAppId,
                        resourceAccess = new[]
                        {
                            new
                            {
                                id = scopeId,
                                type = "Scope"
                            }
                        }
                    });
                }
            }

            // Update the application
            var patchPayload = new
            {
                requiredResourceAccess = updatedResourceAccess
            };

            logger.LogDebug("Updating application {AppObjectId} with {Count} resource access entries", 
                appObjectId, updatedResourceAccess.Count);
            logger.LogDebug("Payload: {Payload}", System.Text.Json.JsonSerializer.Serialize(patchPayload));
            
            var updated = await graph.GraphPatchAsync(config.TenantId, $"/v1.0/applications/{appObjectId}", patchPayload, ct);
            if (updated)
            {
                logger.LogInformation("MOS API permissions configured successfully");
            }
            else
            {
                logger.LogError("Failed to update MOS API permissions - Graph PATCH returned false");
                logger.LogError("This likely means the Graph API returned an error status code");
                logger.LogError("Check that you have Application.ReadWrite.All permission and sufficient privileges");
                throw new SetupValidationException("Failed to update application with MOS API permissions. Check logs for details.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error configuring MOS API permissions: {Message}", ex.Message);
            throw new SetupValidationException($"Failed to configure MOS API permissions: {ex.Message}");
        }

        // Step 3: Grant admin consent for MOS permissions
        await GrantMosAdminConsentAsync(graph, config, logger, ct);

        return true;
    }

    /// <summary>
    /// Grants admin consent for MOS permissions by creating OAuth2 permission grants.
    /// Uses the Microsoft first-party client app for MOS access (required by MOS APIs).
    /// This ensures that tokens acquired for MOS resource apps will have the required scopes.
    /// </summary>
    private static async Task GrantMosAdminConsentAsync(
        GraphApiService graph,
        Agent365Config config,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogInformation("Granting admin consent for MOS API permissions...");

        // Use Microsoft first-party client app for MOS access (not custom client app)
        // MOS APIs only accept tokens from Microsoft first-party apps
        var firstPartyClientAppId = MosConstants.TpsAppServicesClientAppId;
        logger.LogDebug("Using Microsoft first-party client app for MOS admin consent: {ClientAppId}", firstPartyClientAppId);
        
        // Look up the first-party client app's service principal
        var clientSpObjectId = await graph.LookupServicePrincipalByAppIdAsync(config.TenantId, firstPartyClientAppId, ct);
        if (string.IsNullOrWhiteSpace(clientSpObjectId))
        {
            logger.LogError("Could not find service principal for Microsoft first-party client app {ClientAppId}", firstPartyClientAppId);
            logger.LogError("The service principal should have been created earlier in this process");
            throw new SetupValidationException($"Service principal not found for Microsoft first-party client app {firstPartyClientAppId}");
        }

        logger.LogDebug("First-party client service principal ID: {ClientSpObjectId}", clientSpObjectId);

        // Define the scope names for each MOS resource app
        // Must match exactly what the internal PowerShell script uses
        var mosResourceScopes = new Dictionary<string, string>
        {
            { MosConstants.TpsAppServicesResourceAppId, "AuthConfig.Read" },
            { MosConstants.PowerPlatformApiResourceAppId, "EnvironmentManagement.Environments.Read" },
            // MOS Titles API requires three scopes: AuthConfig.Read, Title.ReadWrite, and Title.ReadWrite.All
            { MosConstants.MosTitlesApiResourceAppId, "AuthConfig.Read Title.ReadWrite Title.ReadWrite.All" }
        };

        // Grant consent for each MOS resource app
        foreach (var (resourceAppId, scopeName) in mosResourceScopes)
        {
            logger.LogInformation("Granting admin consent for MOS resource app {ResourceAppId} with scopes: {ScopeName}", 
                resourceAppId, scopeName);

            // Look up the resource app's service principal
            var resourceSpObjectId = await graph.LookupServicePrincipalByAppIdAsync(config.TenantId, resourceAppId, ct);
            if (string.IsNullOrWhiteSpace(resourceSpObjectId))
            {
                logger.LogWarning("Service principal not found for MOS resource app {ResourceAppId}", resourceAppId);
                logger.LogWarning("Run the following command to create it:");
                logger.LogWarning("  az ad sp create --id {ResourceAppId}", resourceAppId);
                continue;
            }

            logger.LogDebug("MOS resource service principal ID: {ResourceSpObjectId}", resourceSpObjectId);

            // Grant consent using ReplaceOauth2PermissionGrantAsync
            // This will delete any existing grant and create a new one with the correct scope
            var success = await graph.ReplaceOauth2PermissionGrantAsync(
                config.TenantId,
                clientSpObjectId,
                resourceSpObjectId,
                new[] { scopeName },
                ct);

            if (success)
            {
                logger.LogInformation("Successfully granted admin consent for MOS resource app {ResourceAppId}", 
                    resourceAppId);
            }
            else
            {
                logger.LogWarning("Failed to grant admin consent for {ResourceAppId} ({ScopeName})", 
                    resourceAppId, scopeName);
                logger.LogWarning("You may need to grant consent manually in Azure Portal");
            }
        }

        logger.LogInformation("Admin consent configuration complete");
        
        // Clear cached MOS tokens to force re-acquisition with new scopes
        logger.LogDebug("Clearing cached MOS tokens to force re-acquisition with updated permissions");
        var cacheFilePath = Path.Combine(Environment.CurrentDirectory, ".mos-token-cache.json");
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
