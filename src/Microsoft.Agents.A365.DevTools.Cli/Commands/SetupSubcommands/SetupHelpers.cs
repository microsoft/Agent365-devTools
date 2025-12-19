// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Shared helper methods for setup subcommands
/// </summary>
internal static class SetupHelpers
{
    /// <summary>
    /// Display verification URLs and next steps after successful setup
    /// </summary>
    public static async Task DisplayVerificationInfoAsync(FileInfo setupConfigFile, ILogger logger)
    {
        try
        {
            logger.LogInformation("Generating verification information...");
            var baseDir = setupConfigFile.DirectoryName ?? Environment.CurrentDirectory;
            var generatedConfigPath = Path.Combine(baseDir, "a365.generated.config.json");
            
            if (!File.Exists(generatedConfigPath))
            {
                logger.LogWarning("Generated config not found - skipping verification info");
                return;
            }

            using var stream = File.OpenRead(generatedConfigPath);
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            logger.LogInformation("");
            logger.LogInformation("Verification URLs and Next Steps:");
            logger.LogInformation("==========================================");

            // Azure Web App URL
            if (root.TryGetProperty("AppServiceName", out var appServiceProp) && !string.IsNullOrWhiteSpace(appServiceProp.GetString()))
            {
                var webAppUrl = $"https://{appServiceProp.GetString()}.azurewebsites.net";
                logger.LogInformation("Agent Web App: {Url}", webAppUrl);
            }

            // Azure Resource Group
            if (root.TryGetProperty("ResourceGroup", out var rgProp) && !string.IsNullOrWhiteSpace(rgProp.GetString()))
            {
                var resourceGroup = rgProp.GetString();
                logger.LogInformation("Azure Resource Group: https://portal.azure.com/#@/resource/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}",
                    root.TryGetProperty("SubscriptionId", out var subProp) ? subProp.GetString() : "{subscription}", 
                    resourceGroup);
            }

            // Entra ID Application
            if (root.TryGetProperty("AgentBlueprintId", out var blueprintProp) && !string.IsNullOrWhiteSpace(blueprintProp.GetString()))
            {
                logger.LogInformation("Entra ID Application: https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/{AppId}",
                    blueprintProp.GetString());
            }

            logger.LogInformation("");
            logger.LogInformation("Next Steps:");
            logger.LogInformation("   1. Review Azure resources in the portal");
            logger.LogInformation("   2. View configuration: a365 config display");
            logger.LogInformation("   3. Create agent instance: a365 create-instance identity");
            logger.LogInformation("   4. Deploy application: a365 deploy app");
            logger.LogInformation("");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not display verification info: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Display comprehensive setup summary showing what succeeded and what failed
    /// </summary>
    public static void DisplaySetupSummary(SetupResults results, ILogger logger)
    {
        logger.LogInformation("");
        logger.LogInformation("==========================================");
        logger.LogInformation("Setup Summary");
        logger.LogInformation("==========================================");
        
        // Show what succeeded
        logger.LogInformation("Completed Steps:");
        if (results.InfrastructureCreated)
        {
            logger.LogInformation("  [OK] Infrastructure created");
        }
        if (results.BlueprintCreated)
        {
            logger.LogInformation("  [OK] Agent blueprint created (Blueprint ID: {BlueprintId})", results.BlueprintId ?? "unknown");
        }
        if (results.McpPermissionsConfigured)
            logger.LogInformation("  [OK] MCP server permissions configured");
        if (results.InheritablePermissionsConfigured)
            logger.LogInformation("  [OK] Inheritable permissions configured");
        if (results.BotApiPermissionsConfigured)
            logger.LogInformation("  [OK] Messaging Bot API permissions configured");
        if (results.MessagingEndpointRegistered)
            logger.LogInformation("  [OK] Messaging endpoint registered");
        
        // Show what failed
        if (results.Errors.Count > 0)
        {
            logger.LogInformation("");
            logger.LogInformation("Failed Steps:");
            foreach (var error in results.Errors)
            {
                logger.LogError("  [FAILED] {Error}", error);
            }
        }
        
        // Show warnings
        if (results.Warnings.Count > 0)
        {
            logger.LogInformation("");
            logger.LogInformation("Warnings:");
            foreach (var warning in results.Warnings)
            {
                logger.LogInformation("  [WARN] {Warning}", warning);
            }
        }
        
        logger.LogInformation("");
        
        // Overall status
        if (results.HasErrors)
        {
            logger.LogWarning("Setup completed with errors");
            logger.LogInformation("");
            logger.LogInformation("Recovery Actions:");
            
            if (!results.InheritablePermissionsConfigured)
            {
                logger.LogInformation("  - Inheritable Permissions: Run 'a365 setup permissions mcp' to retry");
            }
            
            if (!results.McpPermissionsConfigured)
            {
                logger.LogInformation("  - MCP Permissions: Run 'a365 setup permissions mcp' to retry");
            }
            
            if (!results.BotApiPermissionsConfigured)
            {
                logger.LogInformation("  - Bot API Permissions: Run 'a365 setup permissions bot' to retry");
            }
            
            if (!results.MessagingEndpointRegistered)
            {
                logger.LogInformation("  - Messaging Endpoint: Run 'a365 setup blueprint --endpoint-only' to retry");
                logger.LogInformation("    Or delete conflicting endpoint first: a365 cleanup azure");
            }
        }
        else if (results.HasWarnings)
        {
            logger.LogInformation("Setup completed successfully with warnings");
            logger.LogInformation("Review warnings above and take action if needed");
        }
        else
        {
            logger.LogInformation("Setup completed successfully");
            logger.LogInformation("All components configured correctly");
        }
        
        logger.LogInformation("==========================================");
    }

    /// <summary>
    /// Unified method to configure all permissions (OAuth2 grants, required resource access, inheritable permissions) for a resource
    /// </summary>
    /// <param name="graph">Graph API service</param>
    /// <param name="config">Agent365 configuration</param>
    /// <param name="resourceAppId">The resource application ID to grant permissions for</param>
    /// <param name="resourceName">Display name of the resource for logging</param>
    /// <param name="scopes">Permission scopes to grant</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="addToRequiredResourceAccess">Whether to add permissions to app manifest (visible in portal)</param>
    /// <param name="setInheritablePermissions">Whether to set inheritable permissions for agent blueprints</param>
    /// <param name="setupResults">Optional setup results for tracking warnings</param>
    /// <param name="ct">Cancellation token</param>
    public static async Task EnsureResourcePermissionsAsync(
        GraphApiService graph,
        Agent365Config config,
        string resourceAppId,
        string resourceName,
        string[] scopes,
        ILogger logger,
        bool addToRequiredResourceAccess = true,
        bool setInheritablePermissions = true,
        SetupResults? setupResults = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
            throw new SetupValidationException("AgentBlueprintId (appId) is required.");

        var blueprintSpObjectId = await graph.LookupServicePrincipalByAppIdAsync(config.TenantId, config.AgentBlueprintId, ct);
        if (string.IsNullOrWhiteSpace(blueprintSpObjectId))
        {
            throw new SetupValidationException($"Blueprint Service Principal not found for appId {config.AgentBlueprintId}. " +
                "The service principal may not have propagated yet. Wait a few minutes and retry.");
        }

        // Ensure resource service principal exists
        var resourceSpObjectId = await graph.EnsureServicePrincipalForAppIdAsync(config.TenantId, resourceAppId, ct);
        if (string.IsNullOrWhiteSpace(resourceSpObjectId))
        {
            throw new SetupValidationException($"{resourceName} Service Principal not found for appId {resourceAppId}. " +
                $"Ensure the {resourceName} application is available in your tenant.");
        }

        // 1. Add to required resource access (makes permissions visible in portal)
        if (addToRequiredResourceAccess)
        {
            logger.LogInformation("   - Adding {ResourceName} to blueprint's required resource access", resourceName);
            var addedResourceAccess = await graph.AddRequiredResourceAccessAsync(
                config.TenantId,
                config.AgentBlueprintId,
                resourceAppId,
                scopes,
                isDelegated: true,
                ct);

            if (!addedResourceAccess)
            {
                logger.LogWarning("Failed to add {ResourceName} to required resource access. Permissions may not be visible in portal.", resourceName);
            }
        }

        // 2. Grant OAuth2 permissions (admin consent)
        logger.LogInformation("   - OAuth2 grant: client {ClientId} to resource {ResourceId} scopes [{Scopes}]",
            blueprintSpObjectId, resourceSpObjectId, string.Join(' ', scopes));

        var response = await graph.CreateOrUpdateOauth2PermissionGrantAsync(
            config.TenantId, blueprintSpObjectId, resourceSpObjectId, scopes, ct);

        if (!response)
        {
            throw new SetupValidationException(
                $"Failed to create/update OAuth2 permission grant from blueprint {config.AgentBlueprintId} to {resourceName} {resourceAppId}. " +
                "This may be due to insufficient permissions. Ensure you have DelegatedPermissionGrant.ReadWrite.All or Application.ReadWrite.All permissions.");
        }

        // 3. Set inheritable permissions (for agent blueprints)
        bool inheritanceConfigured = false;
        bool inheritanceAlreadyExisted = false;
        string? inheritanceError = null;

        if (setInheritablePermissions)
        {
            logger.LogInformation("   - Inheritable permissions: blueprint {Blueprint} to resourceAppId {ResourceAppId} scopes [{Scopes}]",
                config.AgentBlueprintId, resourceAppId, string.Join(' ', scopes));

            // Use custom client app auth for inheritable permissions - Azure CLI doesn't support this operation
            var requiredPermissions = new[] { "AgentIdentityBlueprint.UpdateAuthProperties.All", "Application.ReadWrite.All" };
            
            var (ok, alreadyExists, err) = await graph.SetInheritablePermissionsAsync(
                config.TenantId, config.AgentBlueprintId, resourceAppId, scopes, requiredScopes: requiredPermissions, ct);

            if (!ok && !alreadyExists)
            {
                throw new SetupValidationException($"Failed to set inheritable permissions: {err}. " +
                    "Ensure you have AgentIdentityBlueprint.UpdateAuthProperties.All and Application.ReadWrite.All permissions in your custom client app.");
            }

            inheritanceConfigured = true;
            inheritanceAlreadyExisted = alreadyExists;

            // Verify inheritable permissions were actually set (non-blocking verification with retry)
            try
            {
                logger.LogInformation("   - Verifying inheritable permissions for {ResourceName}", resourceName);
                
                var retryHelper = new RetryHelper(logger);
                var verificationResult = await retryHelper.ExecuteWithRetryAsync(
                    operation: async (ct) =>
                    {
                        var (exists, verifiedScopes, verifyError) = await graph.VerifyInheritablePermissionsAsync(
                            config.TenantId, config.AgentBlueprintId, resourceAppId, ct, requiredPermissions);
                        return (exists, verifiedScopes, verifyError);
                    },
                    shouldRetry: (result) =>
                    {
                        // Retry if permissions don't exist yet (Graph API propagation delay)
                        // Don't retry on actual errors (verifyError != null) - fail fast
                        return !result.exists && string.IsNullOrEmpty(result.verifyError);
                    },
                    maxRetries: 5,
                    baseDelaySeconds: 2,
                    cancellationToken: ct);

                var (exists, verifiedScopes, verifyError) = verificationResult;

                if (!string.IsNullOrEmpty(verifyError))
                {
                    logger.LogWarning("Could not verify {ResourceName} inheritable permissions: {Error}", resourceName, verifyError);
                    setupResults?.Warnings.Add($"Could not verify {resourceName} inheritable permissions: {verifyError}");
                }
                else if (!exists)
                {
                    var warning = $"{resourceName} inheritable permissions not found after configuration. " +
                        $"Agent instances may not inherit these permissions. " +
                        $"Verify manually: GET /beta/applications/microsoft.graph.agentIdentityBlueprint/{config.AgentBlueprintId}/inheritablePermissions";
                    logger.LogWarning(warning);
                    setupResults?.Warnings.Add(warning);
                }
                else
                {
                    // Check if all required scopes are present
                    var missingScopes = scopes.Except(verifiedScopes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase).ToArray();
                    if (missingScopes.Length > 0)
                    {
                        var warning = $"{resourceName} inheritable permissions incomplete. " +
                            $"Missing scopes: [{string.Join(", ", missingScopes)}]. " +
                            $"Expected: [{string.Join(", ", scopes)}]. " +
                            $"Found: [{string.Join(", ", verifiedScopes ?? Array.Empty<string>())}]. " +
                            $"Run 'a365 setup permissions bot' to retry.";
                        logger.LogWarning(warning);
                        setupResults?.Warnings.Add(warning);
                    }
                    else
                    {
                        logger.LogInformation("   - Verified: {ResourceName} inheritable permissions correctly configured", resourceName);
                    }
                }
            }
            catch (Exception verifyEx)
            {
                // Verification is non-critical - log warning but don't fail setup
                logger.LogWarning("Failed to verify {ResourceName} inheritable permissions: {Message}. Setup will continue.", resourceName, verifyEx.Message);
                setupResults?.Warnings.Add($"Could not verify {resourceName} inheritable permissions: {verifyEx.Message}");
            }
        }

        // 4. Update resource consents collection
        var existingConsent = config.ResourceConsents.FirstOrDefault(rc => 
            rc.ResourceAppId.Equals(resourceAppId, StringComparison.OrdinalIgnoreCase));

        if (existingConsent != null)
        {
            // Update existing consent record
            existingConsent.ConsentGranted = true;
            existingConsent.ConsentTimestamp = DateTime.UtcNow;
            existingConsent.Scopes = scopes.ToList();
            existingConsent.InheritablePermissionsConfigured = inheritanceConfigured;
            existingConsent.InheritablePermissionsAlreadyExist = inheritanceAlreadyExisted;
            existingConsent.InheritablePermissionsError = inheritanceError;
        }
        else
        {
            // Add new consent record
            config.ResourceConsents.Add(new ResourceConsent
            {
                ResourceName = resourceName,
                ResourceAppId = resourceAppId,
                ConsentGranted = true,
                ConsentTimestamp = DateTime.UtcNow,
                Scopes = scopes.ToList(),
                InheritablePermissionsConfigured = inheritanceConfigured,
                InheritablePermissionsAlreadyExist = inheritanceAlreadyExisted,
                InheritablePermissionsError = inheritanceError
            });
        }
    }

    /// <summary>
    /// Register blueprint messaging endpoint
    /// Returns (success, alreadyExisted)
    /// </summary>
    public static async Task<(bool success, bool alreadyExisted)> RegisterBlueprintMessagingEndpointAsync(
        Agent365Config setupConfig,
        ILogger logger,
        IBotConfigurator botConfigurator)
    {
        // Validate required configuration
        if (string.IsNullOrEmpty(setupConfig.AgentBlueprintId))
        {
            logger.LogError("Agent Blueprint ID not found. Blueprint creation may have failed.");
            throw new SetupValidationException(
                issueDescription: "Agent blueprint was not found - messaging endpoint cannot be registered.",
                errorDetails: new List<string>
                {
                    "AgentBlueprintId is missing from configuration. This usually means the blueprint creation step failed or a365.generated.config.json is out of sync."
                },
                mitigationSteps: new List<string>
                {
                    "Verify that 'a365 setup' completed Step 1 (Agent blueprint creation) without errors.",
                    "Check a365.generated.config.json for 'agentBlueprintId'. If it's missing or incorrect, re-run 'a365 setup'."
                },
                context: new Dictionary<string, string>
                {
                    ["AgentBlueprintId"] = setupConfig.AgentBlueprintId ?? "<null>"
                });
        }

        string messagingEndpoint;
        string endpointName;
        if (setupConfig.NeedDeployment)
        {
            if (string.IsNullOrEmpty(setupConfig.WebAppName))
            {
                logger.LogError("Web App Name not configured in a365.config.json");
                throw new SetupValidationException(
                    issueDescription: "Web App name is required to register a messaging endpoint when needDeployment is 'yes'.",
                    errorDetails: new List<string>
                    {
                        "NeedDeployment is true, but 'webAppName' was not provided in a365.config.json."
                    },
                    mitigationSteps: new List<string>
                    {
                        "Open a365.config.json and ensure 'webAppName' is set to the Azure Web App name.",
                        "If you do not want the CLI to deploy an Azure Web App, set \"needDeployment\": \"no\" and provide \"MessagingEndpoint\" instead.",
                        "Re-run 'a365 setup'."
                    },
                    context: new Dictionary<string, string>
                    {
                        ["needDeployment"] = setupConfig.NeedDeployment.ToString(),
                        ["webAppName"] = setupConfig.WebAppName ?? "<null>"
                    });
            }

            // Generate endpoint name with Azure Bot Service constraints (4-42 chars)
            var baseEndpointName = $"{setupConfig.WebAppName}-endpoint";
            endpointName = EndpointHelper.GetEndpointName(baseEndpointName);

            // Construct messaging endpoint URL from web app name
            messagingEndpoint = $"https://{setupConfig.WebAppName}.azurewebsites.net/api/messages";
        }
        else // Non-Azure hosting
        {
            // No deployment - use the provided MessagingEndpoint
            if (string.IsNullOrWhiteSpace(setupConfig.MessagingEndpoint))
            {
                logger.LogWarning("MessagingEndpoint not configured. Skipping endpoint registration.");
                logger.LogWarning("Configure 'messagingEndpoint' in a365.config.json and re-run 'a365 setup blueprint' to register the endpoint.");
                return (false, false);
            }

            if (!Uri.TryCreate(setupConfig.MessagingEndpoint, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                logger.LogError("MessagingEndpoint must be a valid HTTPS URL. Current value: {Endpoint}",
                    setupConfig.MessagingEndpoint);
                throw new SetupValidationException("MessagingEndpoint must be a valid HTTPS URL.");
            }

            messagingEndpoint = setupConfig.MessagingEndpoint;

            // Derive endpoint name from host when there's no WebAppName
            var hostPart = uri.Host.Replace('.', '-');
            var baseEndpointName = $"{hostPart}-endpoint";
            endpointName = EndpointHelper.GetEndpointName(baseEndpointName);
        }

        if (endpointName.Length < 4)
        {
            logger.LogError("Bot endpoint name '{EndpointName}' is too short (must be at least 4 characters)", endpointName);
            throw new SetupValidationException($"Bot endpoint name '{endpointName}' is too short (must be at least 4 characters)");
        }

        // Normalize location before logging and sending to API
        var normalizedLocation = setupConfig.Location.Replace(" ", "").ToLowerInvariant();
        
        logger.LogInformation("   - Registering blueprint messaging endpoint");
        logger.LogInformation("     * Endpoint Name: {EndpointName}", endpointName);
        logger.LogInformation("     * Messaging Endpoint: {Endpoint}", messagingEndpoint);
        logger.LogInformation("     * Region: {Location}", normalizedLocation);
        logger.LogInformation("     * Using Agent Blueprint ID: {AgentBlueprintId}", setupConfig.AgentBlueprintId);

        var endpointResult = await botConfigurator.CreateEndpointWithAgentBlueprintAsync(
            endpointName: endpointName,
            location: normalizedLocation,
            messagingEndpoint: messagingEndpoint,
            agentDescription: "Agent 365 messaging endpoint for automated interactions",
            agentBlueprintId: setupConfig.AgentBlueprintId);

        if (endpointResult == Models.EndpointRegistrationResult.Failed)
        {
            logger.LogError("Failed to register blueprint messaging endpoint");
            throw new SetupValidationException("Blueprint messaging endpoint registration failed");
        }

        // Update Agent365Config state properties
        setupConfig.BotId = setupConfig.AgentBlueprintId;
        setupConfig.BotMsaAppId = setupConfig.AgentBlueprintId;
        setupConfig.BotMessagingEndpoint = messagingEndpoint;
        
        bool alreadyExisted = endpointResult == Models.EndpointRegistrationResult.AlreadyExists;
        return (true, alreadyExisted);
    }
}
