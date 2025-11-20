// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Azure.Identity;
using System.Linq;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using static System.Formats.Asn1.AsnWriter;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// C# implementation of a365-setup.ps1 with full feature parity.
/// Handles infrastructure setup, blueprint creation, consent flows, and MCP server configuration.
/// </summary>
public sealed class A365SetupRunner
{
    private readonly ILogger<A365SetupRunner> _logger;
    private readonly CommandExecutor _executor;
    private readonly GraphApiService _graphService;
    private readonly AzureWebAppCreator _webAppCreator;
    private readonly DelegatedConsentService _delegatedConsentService;
    private readonly PlatformDetector _platformDetector;
    private const string GraphResourceAppId = "00000003-0000-0000-c000-000000000000"; // Microsoft Graph
    private const string ConnectivityResourceAppId = "0ddb742a-e7dc-4899-a31e-80e797ec7144"; // Connectivity
    private const string InheritablePermissionsResourceAppIdId = "00000003-0000-0ff1-ce00-000000000000";
    private const string MicrosoftGraphCommandLineToolsAppId = "14d82eec-204b-4c2f-b7e8-296a70dab67e"; // Microsoft Graph Command Line Tools
    private const string DocumentationMessage = "See documentation at https://aka.ms/agent365/setup for more information.";

    public A365SetupRunner(
        ILogger<A365SetupRunner> logger, 
        CommandExecutor executor,
        GraphApiService graphService,
        AzureWebAppCreator webAppCreator,
        DelegatedConsentService delegatedConsentService,
        PlatformDetector platformDetector)
    {
        _logger = logger;
        _executor = executor;
        _graphService = graphService;
        _webAppCreator = webAppCreator;
        _delegatedConsentService = delegatedConsentService;
        _platformDetector = platformDetector;
    }

    /// <summary>
    /// Validates Graph token string for disallowed high-privilege scopes.
    /// Throws GraphTokenScopeException if Directory.AccessAsUser.All is present.
    /// </summary>
    private void ValidateGraphToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return;

        try
        {
            // JWT format: header.payload.signature
            var parts = token.Split('.');
            if (parts.Length < 2) return;

            string payload = parts[1];
            // Base64URL decode
            payload = payload.Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);

            // scopes may appear as 'scp' (space-separated) or 'roles' or 'wids'. Check scp
            if (doc.RootElement.TryGetProperty("scp", out var scp))
            {
                var scpValue = scp.GetString() ?? string.Empty;
                if (scpValue.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(s => s.Equals("Directory.AccessAsUser.All", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogError("Detected high-privilege scope in token: Directory.AccessAsUser.All");
                    throw new GraphTokenScopeException("Directory.AccessAsUser.All");
                }
            }
        }
        catch (Exception ex) when (ex is not Agent365Exception)
        {
            _logger.LogDebug(ex, "Failed to validate graph token payload");
        }
    }

    /// <summary>
    /// Execute setup using provided JSON config file.
    /// Fully compatible with a365-setup.ps1 functionality.
    /// </summary>
    /// <param name="configPath">Path to a365.config.json</param>
    /// <param name="generatedConfigPath">Path where a365.generated.config.json will be written</param>
    /// <param name="blueprintOnly">If true, skip Azure infrastructure (Phase 1) and create blueprint only</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<bool> RunAsync(string configPath, string generatedConfigPath, bool blueprintOnly = false, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath))
        {
            _logger.LogError("Config file not found at {Path}", configPath);
            return false;
        }

        JsonObject cfg;
        try
        {
            cfg = JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken))!.AsObject();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse config JSON: {Path}", configPath);
            return false;
        }

        string Get(string name) => cfg.TryGetPropertyValue(name, out var node) && node is JsonValue jv && jv.TryGetValue(out string? s) ? s ?? string.Empty : string.Empty;

        var subscriptionId = Get("subscriptionId");
        var tenantId = Get("tenantId");
        var resourceGroup = Get("resourceGroup");
        var planName = Get("appServicePlanName");
        var webAppName = Get("webAppName");
        var location = Get("location");
        var planSku = Get("appServicePlanSku");
        if (string.IsNullOrWhiteSpace(planSku)) planSku = "B1";
        
        var deploymentProjectPath = Get("deploymentProjectPath");

        if (new[] { subscriptionId, tenantId, resourceGroup, planName, webAppName, location }.Any(string.IsNullOrWhiteSpace))
        {
            _logger.LogError("Config missing required properties. Need subscriptionId, tenantId, resourceGroup, appServicePlanName, webAppName, location.");
            return false;
        }

        // Detect project platform for appropriate runtime configuration
        var platform = Models.ProjectPlatform.DotNet; // Default fallback
        if (!string.IsNullOrWhiteSpace(deploymentProjectPath))
        {
            platform = _platformDetector.Detect(deploymentProjectPath);
            _logger.LogInformation("Detected project platform: {Platform}", platform);
        }
        else
        {
            _logger.LogWarning("No deploymentProjectPath specified, defaulting to .NET runtime");
        }

        _logger.LogInformation("Agent 365 Setup - Starting...");
        _logger.LogInformation("Subscription: {Sub}", subscriptionId);
        _logger.LogInformation("Resource Group: {RG}", resourceGroup);
        _logger.LogInformation("App Service Plan: {Plan}", planName);
        _logger.LogInformation("Web App: {App}", webAppName);
        _logger.LogInformation("Location: {Loc}", location);
        _logger.LogInformation("");

        // ========================================================================
        // Phase 0: Ensure Azure CLI is logged in with proper scope
        // ========================================================================
        _logger.LogInformation("==> [0/5] Verifying Azure CLI authentication");
        
        // Check if logged in
        var accountCheck = await _executor.ExecuteAsync("az", "account show", captureOutput: true, suppressErrorLogging: true);
        if (!accountCheck.Success)
        {
            _logger.LogInformation("Azure CLI not authenticated. Initiating login with management scope...");
            _logger.LogInformation("A browser window will open for authentication.");
            
            // Use standard login without scope parameter (more reliable)
            var loginResult = await _executor.ExecuteAsync("az", $"login --tenant {tenantId}", cancellationToken: cancellationToken);
            
            if (!loginResult.Success)
            {
                _logger.LogError("Azure CLI login failed. Please run manually: az login --scope https://management.core.windows.net//.default");
                return false;
            }
            
            _logger.LogInformation("Azure CLI login successful!");
            
            // Wait a moment for the login to fully complete
            await Task.Delay(2000, cancellationToken);
        }
        else
        {
            _logger.LogInformation("Azure CLI already authenticated");
        }
        
        // Verify we have the management scope - if not, try to acquire it
        _logger.LogInformation("Verifying access to Azure management resources...");
        var tokenCheck = await _executor.ExecuteAsync(
            "az", 
            "account get-access-token --resource https://management.core.windows.net/ --query accessToken -o tsv", 
            captureOutput: true, 
            suppressErrorLogging: true,
            cancellationToken: cancellationToken);
            
        if (!tokenCheck.Success)
        {
            _logger.LogWarning("Unable to acquire management scope token. Attempting re-authentication...");
            _logger.LogInformation("A browser window will open for authentication.");
            
            // Try standard login first (more reliable than scope-specific login)
            var loginResult = await _executor.ExecuteAsync("az", $"login --tenant {tenantId}", cancellationToken: cancellationToken);
            
            if (!loginResult.Success)
            {
                    _logger.LogError("Azure CLI login with management scope failed. Please run manually: az login --scope https://management.core.windows.net//.default");
                return false;
            }
            
            _logger.LogInformation("Azure CLI re-authentication successful!");
            
            // Wait a moment for the token cache to update
            await Task.Delay(2000, cancellationToken);
            
            // Verify management token is now available
            var retryTokenCheck = await _executor.ExecuteAsync(
                "az", 
                "account get-access-token --resource https://management.core.windows.net/ --query accessToken -o tsv", 
                captureOutput: true, 
                suppressErrorLogging: true,
                cancellationToken: cancellationToken);
                
            if (!retryTokenCheck.Success)
            {
                _logger.LogWarning("Still unable to acquire management scope token after re-authentication.");
                _logger.LogWarning("Continuing anyway - you may encounter permission errors later.");
            }
            else
            {
                _logger.LogInformation("Management scope token acquired successfully!");
            }
        }
        else
        {
            _logger.LogInformation("Management scope verified successfully");
        }
        
        _logger.LogInformation("");

        // ========================================================================
        // Phase 1: Deploy Agent runtime (App Service) + System-assigned Managed Identity
        // ========================================================================
        string? principalId = null;
        JsonObject generatedConfig = new JsonObject();

        if (blueprintOnly)
        {
            _logger.LogInformation("==> [1/5] Skipping Azure infrastructure (--blueprint mode)");
            _logger.LogInformation("Loading existing configuration...");

            // Load existing generated config if available
            if (File.Exists(generatedConfigPath))
            {
                try
                {
                    generatedConfig = JsonNode.Parse(await File.ReadAllTextAsync(generatedConfigPath, cancellationToken))?.AsObject() ?? new JsonObject();

                    if (generatedConfig.TryGetPropertyValue("managedIdentityPrincipalId", out var existingPrincipalId))
                    {
                        principalId = existingPrincipalId?.GetValue<string>();
                        _logger.LogInformation("Found existing Managed Identity Principal ID: {Id}", principalId ?? "(none)");
                    }

                    _logger.LogInformation("Existing configuration loaded successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not load existing config: {Message}. Starting fresh.", ex.Message);
                }
            }
            else
            {
                _logger.LogInformation("No existing configuration found - blueprint will be created without managed identity");
            }

            _logger.LogInformation("");
        }
        else
        {
            _logger.LogInformation("==> [1/5] Deploying App Service + enabling Managed Identity");

            // Set subscription context
            try
            {
                await _executor.ExecuteAsync("az", $"account set --subscription {subscriptionId}");
            }
            catch (Exception)
            {
                _logger.LogWarning("Failed to set az subscription context explicitly");
            }

            // Resource group
            var rgExists = await _executor.ExecuteAsync("az", $"group exists -n {resourceGroup} --subscription {subscriptionId}", captureOutput: true);
            if (rgExists.Success && rgExists.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Resource group already exists: {RG} (skipping creation)", resourceGroup);
            }
            else
            {
                _logger.LogInformation("Creating resource group {RG}", resourceGroup);
                await AzWarnAsync($"group create -n {resourceGroup} -l {location} --subscription {subscriptionId}", "Create resource group");
            }

            // App Service plan
            var planShow = await _executor.ExecuteAsync("az", $"appservice plan show -g {resourceGroup} -n {planName} --subscription {subscriptionId}", captureOutput: true, suppressErrorLogging: true);
            if (planShow.Success)
            {
                _logger.LogInformation("App Service plan already exists: {Plan} (skipping creation)", planName);
            }
            else
            {
                _logger.LogInformation("Creating App Service plan {Plan}", planName);
                await AzWarnAsync($"appservice plan create -g {resourceGroup} -n {planName} --sku {planSku} --is-linux --subscription {subscriptionId}", "Create App Service plan");
            }

            // Web App
            var webShow = await _executor.ExecuteAsync("az", $"webapp show -g {resourceGroup} -n {webAppName} --subscription {subscriptionId}", captureOutput: true);
            if (!webShow.Success)
            {
                var runtime = GetRuntimeForPlatform(platform);
                _logger.LogInformation("Creating web app {App} with runtime {Runtime}", webAppName, runtime);
                var createResult = await _executor.ExecuteAsync("az", $"webapp create -g {resourceGroup} -p {planName} -n {webAppName} --runtime \"{runtime}\" --subscription {subscriptionId}", captureOutput: true, suppressErrorLogging: true);
                if (!createResult.Success)
                {
                    if (createResult.StandardError.Contains("AuthorizationFailed", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new AzureResourceException("WebApp", webAppName, createResult.StandardError, true);
                    }
                    else
                    {
                        _logger.LogError("ERROR: Web app creation failed: {Err}", createResult.StandardError);
                        throw new InvalidOperationException($"Failed to create web app '{webAppName}'. Setup cannot continue.");
                    }
                }
            }
            else
            {
                var linuxFxVersion = GetLinuxFxVersionForPlatform(platform);
                _logger.LogInformation("Web app already exists: {App} (skipping creation)", webAppName);
                _logger.LogInformation("Configuring web app to use {Platform} runtime ({LinuxFxVersion})...", platform, linuxFxVersion);
                await AzWarnAsync($"webapp config set -g {resourceGroup} -n {webAppName} --linux-fx-version \"{linuxFxVersion}\" --subscription {subscriptionId}", "Configure runtime");
            }

            // Verify web app
            var verifyResult = await _executor.ExecuteAsync("az", $"webapp show -g {resourceGroup} -n {webAppName} --subscription {subscriptionId}", captureOutput: true, suppressErrorLogging: true);
            if (!verifyResult.Success)
            {
                _logger.LogWarning("WARNING: Unable to verify web app via az webapp show.");
            }
            else
            {
                _logger.LogInformation("Verified web app presence.");
            }

            // Managed Identity
            _logger.LogInformation("Assigning (or confirming) system-assigned managed identity");
            var identity = await _executor.ExecuteAsync("az", $"webapp identity assign -g {resourceGroup} -n {webAppName} --subscription {subscriptionId}");
            if (identity.Success)
            {
                try
                {
                    var json = JsonDocument.Parse(identity.StandardOutput);
                    principalId = json.RootElement.GetProperty("principalId").GetString();
                    if (!string.IsNullOrEmpty(principalId))
                    {
                        _logger.LogInformation("Managed Identity principalId: {Id}", principalId);
                    }
                }
                catch
                {
                    // ignore parse error
                }
            }
            else if (identity.StandardError.Contains("already has a managed identity", StringComparison.OrdinalIgnoreCase) ||
                     identity.StandardError.Contains("Conflict", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Managed identity already assigned (ignoring conflict).");
            }
            else
            {
                _logger.LogWarning("WARNING: identity assign returned error: {Err}", identity.StandardError.Trim());
            }

            // Load or create generated config
            if (File.Exists(generatedConfigPath))
            {
                try
                {
                    generatedConfig = JsonNode.Parse(await File.ReadAllTextAsync(generatedConfigPath, cancellationToken))?.AsObject() ?? new JsonObject();
                }
                catch
                {
                    _logger.LogWarning("Could not parse existing generated config, starting fresh");
                }
            }

            if (!string.IsNullOrWhiteSpace(principalId))
            {
                generatedConfig["managedIdentityPrincipalId"] = principalId;
                await File.WriteAllTextAsync(generatedConfigPath, generatedConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
                _logger.LogInformation("Generated config updated with MSI principalId: {Id}", principalId);
            }

            _logger.LogInformation("Waiting 10 seconds to ensure Service Principal is fully propagated...");
            await Task.Delay(10000, cancellationToken);

        }  // End of !blueprintOnly block

        // ========================================================================
        // Phase 2: Agent Application (Blueprint) + Consent 
        // ========================================================================
        _logger.LogInformation("");
        _logger.LogInformation("==> [2/5] Creating Agent Blueprint");

        // CRITICAL: Grant AgentApplication.Create permission BEFORE creating blueprint
        // This replaces the PowerShell call to DelegatedAgentApplicationCreateConsent.ps1
        _logger.LogInformation("");
        _logger.LogInformation("==> [2.1/5] Ensuring AgentApplication.Create Permission");
        _logger.LogInformation("This permission is required to create Agent Blueprints");
        
        var consentResult = await EnsureDelegatedConsentWithRetriesAsync(tenantId, cancellationToken);
        if (!consentResult)
        {
            _logger.LogError("Failed to ensure AgentApplication.Create permission after multiple attempts");
            return false;
        }

        _logger.LogInformation("");
        _logger.LogInformation("==> [2.2/5] Creating Agent Blueprint Application");

        // Get required configuration values
        var agentBlueprintDisplayName = Get("agentBlueprintDisplayName");
        var agentIdentityDisplayName = Get("agentIdentityDisplayName");
        
        if (string.IsNullOrWhiteSpace(agentBlueprintDisplayName))
        {
            _logger.LogError("agentBlueprintDisplayName missing in configuration");
            return false;
        }

        try
        {
            // Create the agent blueprint using Graph API directly (no PowerShell)
            var blueprintResult = await CreateAgentBlueprintAsync(
                tenantId, 
                agentBlueprintDisplayName,
                agentIdentityDisplayName,
                principalId,
                generatedConfig,
                cfg,
                cancellationToken);

            if (!blueprintResult.success)
            {
                throw new InvalidOperationException("Failed to create agent blueprint");
            }

            var blueprintAppId = blueprintResult.appId;
            var blueprintObjectId = blueprintResult.objectId;

            _logger.LogInformation("Agent Blueprint Details:");
            _logger.LogInformation("  - Display Name: {Name}", agentBlueprintDisplayName);
            _logger.LogInformation("  - App ID: {Id}", blueprintAppId);
            _logger.LogInformation("  - Object ID: {Id}", blueprintObjectId);
            _logger.LogInformation("  - Identifier URI: api://{Id}", blueprintAppId);

            // Convert to camelCase and save
            var camelCaseConfig = new JsonObject
            {
                ["managedIdentityPrincipalId"] = generatedConfig["managedIdentityPrincipalId"]?.DeepClone(),
                ["agentBlueprintId"] = blueprintAppId,
                ["agentBlueprintObjectId"] = blueprintObjectId,
                ["displayName"] = agentBlueprintDisplayName,
                ["servicePrincipalId"] = blueprintResult.servicePrincipalId,
                ["identifierUri"] = $"api://{blueprintAppId}",
                ["tenantId"] = tenantId
            };
            
            await File.WriteAllTextAsync(generatedConfigPath, camelCaseConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            generatedConfig = camelCaseConfig;

            // ========================================================================
            // Phase 2.5: Create Client Secret for Agent Blueprint
            // ========================================================================
            _logger.LogInformation("");
            _logger.LogInformation("==> [2.5/5] Creating Client Secret for Agent Blueprint");
            
            await CreateBlueprintClientSecretAsync(blueprintObjectId!, blueprintAppId!, generatedConfig, generatedConfigPath, cancellationToken);

        }
        catch (Exception ex) when (ex is not Agent365Exception)
        {
            _logger.LogError(ex, "Failed to create agent blueprint: {Message}", ex.Message);
            return false;
        }

        // ====================================
        // Phase 3: MCP Server API Permissions 
        // ====================================
        _logger.LogInformation("");
        _logger.LogInformation("==> [3/5] Adding MCP Server API Permissions to Blueprint");

        var blueprintAppIdForMcp = generatedConfig["agentBlueprintId"]?.GetValue<string>();
        var blueprintObjectIdForMcp = generatedConfig["agentBlueprintObjectId"]?.GetValue<string>();
        
        if (!string.IsNullOrWhiteSpace(blueprintAppIdForMcp) && !string.IsNullOrWhiteSpace(blueprintObjectIdForMcp))
        {
            await ConfigureMcpServerPermissionsAsync(cfg, generatedConfig, blueprintAppIdForMcp!, blueprintObjectIdForMcp!, tenantId, cancellationToken);
        }

        // ========================================================================
        // Phase 4: Configure Inheritable Permissions (matching PowerShell Step 6)
        // ========================================================================
        _logger.LogInformation("");
        _logger.LogInformation("==> [4/5] Configuring Inheritable Permissions for Agent Identities");
        
        if (!string.IsNullOrWhiteSpace(blueprintObjectIdForMcp))
        {
            await ConfigureInheritablePermissionsAsync(tenantId, generatedConfig, cfg, cancellationToken);
        }
        else
        {
            _logger.LogWarning("Blueprint Object ID not available, skipping inheritable permissions configuration");
        }

        // ========================================================================
        // Phase 5: Finalization
        // ========================================================================
        _logger.LogInformation("");
        _logger.LogInformation("==> [5/5] Finalizing Setup");

        generatedConfig["completed"] = true;
        generatedConfig["completedAt"] = DateTime.UtcNow.ToString("o");
        await File.WriteAllTextAsync(generatedConfigPath, generatedConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

        _logger.LogDebug("Generated config saved at: {Path}", generatedConfigPath);
        _logger.LogInformation("");
        _logger.LogInformation("Blueprint installation completed successfully");
        _logger.LogInformation("Blueprint ID: {BlueprintId}", generatedConfig["agentBlueprintId"]?.GetValue<string>());
        _logger.LogInformation("Identifier URI: api://{AppId}", generatedConfig["agentBlueprintId"]?.GetValue<string>());

        return true;
    }

    /// <summary>
    /// Create Agent Blueprint using Microsoft Graph API 
    /// 
    /// IMPORTANT: This requires interactive authentication with Application.ReadWrite.All permission.
    /// Uses the same authentication flow as Connect-MgGraph in PowerShell.
    /// </summary>
    private async Task<(bool success, string? appId, string? objectId, string? servicePrincipalId)> CreateAgentBlueprintAsync(
        string tenantId,
        string displayName,
        string? agentIdentityDisplayName,
        string? managedIdentityPrincipalId,
        JsonObject generatedConfig,
        JsonObject setupConfig,
        CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Creating Agent Blueprint using Microsoft Graph SDK...");
            
            GraphServiceClient graphClient;
            try
            {
                graphClient = await GetAuthenticatedGraphClientAsync(tenantId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get authenticated Graph client: {Message}", ex.Message);
                return (false, null, null, null);
            }

            // Get current user for sponsors field (mimics PowerShell script behavior)
            string? sponsorUserId = null;
            try
            {
                var me = await graphClient.Me.GetAsync(cancellationToken: ct);
                if (me != null && !string.IsNullOrEmpty(me.Id))
                {
                    sponsorUserId = me.Id;
                    _logger.LogInformation("Current user: {DisplayName} <{UPN}>", me.DisplayName, me.UserPrincipalName);
                    _logger.LogInformation("Sponsor: https://graph.microsoft.com/v1.0/users/{UserId}", sponsorUserId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not retrieve current user for sponsors field: {Message}", ex.Message);
            }

            // Define the application manifest with @odata.type for Agent Identity Blueprint
            var appManifest = new JsonObject
            {
                ["@odata.type"] = "Microsoft.Graph.AgentIdentityBlueprint", // CRITICAL: Required for Agent Blueprint type
                ["displayName"] = displayName,
                ["signInAudience"] = "AzureADMultipleOrgs" // Multi-tenant
            };
            
            // Add sponsors field if we have the current user (PowerShell script includes this)
            if (!string.IsNullOrEmpty(sponsorUserId))
            {
                appManifest["sponsors@odata.bind"] = new JsonArray 
                { 
                    $"https://graph.microsoft.com/v1.0/users/{sponsorUserId}" 
                };
            }

            // Create the application using Microsoft Graph SDK
            using var httpClient = new HttpClient();
            var graphToken = await GetTokenFromGraphClient(graphClient, tenantId);
            if (string.IsNullOrEmpty(graphToken))
            {
                _logger.LogError("Failed to extract access token from Graph client");
                return (false, null, null, null);
            }
            
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);
            httpClient.DefaultRequestHeaders.Add("ConsistencyLevel", "eventual");
            httpClient.DefaultRequestHeaders.Add("OData-Version", "4.0"); // Required for @odata.type

            var createAppUrl = "https://graph.microsoft.com/beta/applications";
            
            _logger.LogInformation("Creating Agent Blueprint application...");
            _logger.LogInformation("  - Display Name: {DisplayName}", displayName);
            if (!string.IsNullOrEmpty(sponsorUserId))
            {
                _logger.LogInformation("  - Sponsor: User ID {UserId}", sponsorUserId);
            }
            
            var appResponse = await httpClient.PostAsync(
                createAppUrl,
                new StringContent(appManifest.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct);

            if (!appResponse.IsSuccessStatusCode)
            {
                var errorContent = await appResponse.Content.ReadAsStringAsync(ct);
                
                // If sponsors field causes error (Bad Request 400), retry without it
                if (appResponse.StatusCode == System.Net.HttpStatusCode.BadRequest && 
                    !string.IsNullOrEmpty(sponsorUserId))
                {
                    _logger.LogWarning("Agent Blueprint creation with sponsors failed (Bad Request). Retrying without sponsors...");
                    
                    // Remove sponsors field and retry
                    appManifest.Remove("sponsors@odata.bind");
                    
                    appResponse = await httpClient.PostAsync(
                        createAppUrl,
                        new StringContent(appManifest.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                        ct);
                    
                    if (!appResponse.IsSuccessStatusCode)
                    {
                        errorContent = await appResponse.Content.ReadAsStringAsync(ct);
                        _logger.LogError("Failed to create application (fallback): {Status} - {Error}", appResponse.StatusCode, errorContent);
                        return (false, null, null, null);
                    }
                }
                else
                {
                    _logger.LogError("Failed to create application: {Status} - {Error}", appResponse.StatusCode, errorContent);
                    return (false, null, null, null);
                }
            }

            var appJson = await appResponse.Content.ReadAsStringAsync(ct);
            var app = JsonNode.Parse(appJson)!.AsObject();
            var appId = app["appId"]!.GetValue<string>();
            var objectId = app["id"]!.GetValue<string>();

            _logger.LogInformation("Application created successfully");
            _logger.LogInformation("  - App ID: {AppId}", appId);
            _logger.LogInformation("  - Object ID: {ObjectId}", objectId);

            // Wait for application propagation
            const int maxRetries = 30;
            const int delayMs = 4000;
            bool appAvailable = false;
            for (int i = 0; i < maxRetries; i++)
            {
                var checkResp = await httpClient.GetAsync($"https://graph.microsoft.com/v1.0/applications/{objectId}", ct);
                if (checkResp.IsSuccessStatusCode)
                {
                    appAvailable = true;
                    break;
                }
                _logger.LogInformation("Waiting for application object to be available in directory (attempt {Attempt}/{Max})...", i + 1, maxRetries);
                await Task.Delay(delayMs, ct);
            }
            
            if (!appAvailable)
            {
                _logger.LogError("App object not available after creation. Aborting setup.");
                return (false, null, null, null);
            }

            // Update application with identifier URI
            var identifierUri = $"api://{appId}";
            var patchAppUrl = $"https://graph.microsoft.com/v1.0/applications/{objectId}";
            var patchBody = new JsonObject
            {
                ["identifierUris"] = new JsonArray { identifierUri }
            };

            var patchResponse = await httpClient.PatchAsync(
                patchAppUrl,
                new StringContent(patchBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct);

            if (!patchResponse.IsSuccessStatusCode)
            {
                var patchError = await patchResponse.Content.ReadAsStringAsync(ct);
                _logger.LogInformation("Waiting for application propagation before setting identifier URI...");
                _logger.LogDebug("Identifier URI update deferred (propagation delay): {Error}", patchError);
            }
            else
            {
                _logger.LogInformation("Identifier URI set to: {Uri}", identifierUri);
            }

            // Create service principal
            _logger.LogInformation("Creating service principal...");
            
            var spManifest = new JsonObject
            {
                ["appId"] = appId
            };

            var createSpUrl = "https://graph.microsoft.com/v1.0/servicePrincipals";
            var spResponse = await httpClient.PostAsync(
                createSpUrl,
                new StringContent(spManifest.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct);

            string? servicePrincipalId = null;
            if (spResponse.IsSuccessStatusCode)
            {
                var spJson = await spResponse.Content.ReadAsStringAsync(ct);
                var sp = JsonNode.Parse(spJson)!.AsObject();
                servicePrincipalId = sp["id"]!.GetValue<string>();
                _logger.LogInformation("Service principal created: {SpId}", servicePrincipalId);
            }
            else
            {
                var spError = await spResponse.Content.ReadAsStringAsync(ct);
                _logger.LogInformation("Waiting for application propagation before creating service principal...");
                _logger.LogDebug("Service principal creation deferred (propagation delay): {Error}", spError);
            }

            // Wait for service principal propagation
            _logger.LogInformation("Waiting 10 seconds to ensure Service Principal is fully propagated...");
            await Task.Delay(10000, ct);

            // Create Federated Identity Credential (if managed identity provided)
            if (!string.IsNullOrWhiteSpace(managedIdentityPrincipalId))
            {
                _logger.LogInformation("Creating Federated Identity Credential...");
                var credentialName = $"{displayName.Replace(" ", "")}-MSI";
                
                var ficSuccess = await CreateFederatedIdentityCredentialAsync(
                    tenantId,
                    objectId,
                    credentialName,
                    managedIdentityPrincipalId,
                    graphToken,
                    ct);

                if (ficSuccess)
                {
                    _logger.LogInformation("Federated Identity Credential created successfully");
                }
                else
                {
                    _logger.LogWarning("Failed to create Federated Identity Credential");
                }
            }
            else
            {
                _logger.LogInformation("Skipping Federated Identity Credential creation (no MSI Principal ID provided)");
            }

            // Request admin consent
            _logger.LogInformation("Requesting admin consent for application");
            
            // Get application scopes from config (fallback to hardcoded defaults)
            var applicationScopes = new List<string>();
            if (setupConfig.TryGetPropertyValue("agentApplicationScopes", out var appScopesNode) && 
                appScopesNode is JsonArray appScopesArr)
            {
                _logger.LogInformation("  Found 'agentApplicationScopes' in config");
                foreach (var scopeItem in appScopesArr)
                {
                    var scope = scopeItem?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(scope))
                    {
                        applicationScopes.Add(scope);
                    }
                }
            }
            else
            {
                _logger.LogInformation("  'agentApplicationScopes' not found in config, using hardcoded defaults");
                applicationScopes.AddRange(ConfigConstants.DefaultAgentApplicationScopes);
            }

            // Final fallback (should not happen with proper defaults)
            if (applicationScopes.Count == 0)
            {
                _logger.LogWarning("No application scopes available, falling back to User.Read");
                applicationScopes.Add("User.Read");
            }

            _logger.LogInformation("  - Application scopes: {Scopes}", string.Join(", ", applicationScopes));

            // Generate consent URLs for Graph and Connectivity
            var applicationScopesJoined = string.Join(' ', applicationScopes);
            var consentUrlGraph = $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent?client_id={appId}&scope={Uri.EscapeDataString(applicationScopesJoined)}&redirect_uri=https://entra.microsoft.com/TokenAuthorize&state=xyz123";
            var consentUrlConnectivity = $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent?client_id={appId}&scope=0ddb742a-e7dc-4899-a31e-80e797ec7144/Connectivity.Connections.Read&redirect_uri=https://entra.microsoft.com/TokenAuthorize&state=xyz123";
            
            _logger.LogInformation("Opening browser for Graph API admin consent...");
            TryOpenBrowser(consentUrlGraph);

            var consent1Success = await AdminConsentHelper.PollAdminConsentAsync(_executor, _logger, appId, "Graph API Scopes", 180, 5, ct);

            if (consent1Success)
            {
                _logger.LogInformation("Graph API admin consent granted successfully!");
            }
            else
            {
                _logger.LogWarning("Graph API admin consent may not have completed");
            }

            _logger.LogInformation("");
            _logger.LogInformation("Opening browser for Connectivity admin consent...");
            TryOpenBrowser(consentUrlConnectivity);

            var consent2Success = await AdminConsentHelper.PollAdminConsentAsync(_executor, _logger, appId, "Connectivity Scope", 180, 5, ct);

            if (consent2Success)
            {
                _logger.LogInformation("Connectivity admin consent granted successfully!");
            }
            else
            {
                _logger.LogWarning("Connectivity admin consent may not have completed");
            }

            // Save consent URLs and status to generated config
            generatedConfig["consentUrlGraph"] = consentUrlGraph;
            generatedConfig["consentUrlConnectivity"] = consentUrlConnectivity;
            generatedConfig["consent1Granted"] = consent1Success;
            generatedConfig["consent2Granted"] = consent2Success;

            if (!consent1Success || !consent2Success)
            {
                _logger.LogWarning("");
                _logger.LogWarning("One or more consents may not have been detected");
                _logger.LogWarning("The setup will continue, but you may need to grant consent manually.");
                _logger.LogWarning("Consent URL (Graph): {Url}", consentUrlGraph);
                _logger.LogWarning("Consent URL (Connectivity): {Url}", consentUrlConnectivity);
            }

            return (true, appId, objectId, servicePrincipalId);
        }
        catch (Exception ex) when (ex is not Agent365Exception)
        {
            _logger.LogError(ex, "Failed to create agent blueprint: {Message}", ex.Message);
            return (false, null, null, null);
        }
    }

    /// <summary>
    /// Create Federated Identity Credential to link managed identity to blueprint
    /// Equivalent to createFederatedIdentityCredential function in PowerShell
    /// Implements retry logic to handle Azure AD propagation delays
    /// </summary>
    private async Task<bool> CreateFederatedIdentityCredentialAsync(
        string tenantId,
        string blueprintObjectId,
        string credentialName,
        string msiPrincipalId,
        string graphToken,
        CancellationToken ct)
    {
        const int maxRetries = 5;
        const int initialDelayMs = 2000; // Start with 2 seconds

        try
        {
            if (string.IsNullOrWhiteSpace(graphToken))
            {
                _logger.LogError("Graph token is required for FIC creation");
                return false;
            }

            var federatedCredential = new JsonObject
            {
                ["name"] = credentialName,
                ["issuer"] = $"https://login.microsoftonline.com/{tenantId}/v2.0",
                ["subject"] = msiPrincipalId,
                ["audiences"] = new JsonArray { "api://AzureADTokenExchange" }
            };

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);
            httpClient.DefaultRequestHeaders.Add("ConsistencyLevel", "eventual");

            // Try standard endpoint first, then fallback to Agent Blueprint-specific path
            var urls = new[]
            {
                $"https://graph.microsoft.com/beta/applications/{blueprintObjectId}/federatedIdentityCredentials",
                $"https://graph.microsoft.com/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/federatedIdentityCredentials"
            };

            string? lastError = null;

            foreach (var url in urls)
            {
                // Retry loop to handle propagation delays
                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    var response = await httpClient.PostAsync(
                        url,
                        new StringContent(federatedCredential.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                        ct);

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("  - Credential Name: {Name}", credentialName);
                        _logger.LogInformation("  - Issuer: https://login.microsoftonline.com/{TenantId}/v2.0", tenantId);
                        _logger.LogInformation("  - Subject (MSI Principal ID): {MsiId}", msiPrincipalId);
                        return true;
                    }

                    var error = await response.Content.ReadAsStringAsync(ct);
                    lastError = error;

                    // Check if it's a propagation issue (resource not found)
                    if ((error.Contains("Request_ResourceNotFound") || error.Contains("does not exist")) && attempt < maxRetries)
                    { 
                        var delayMs = initialDelayMs * (int)Math.Pow(2, attempt - 1); // Exponential backoff
                        _logger.LogWarning("Application object not yet propagated (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}ms...", 
                            attempt, maxRetries, delayMs);
                        await Task.Delay(delayMs, ct);
                        continue;
                    }

                    // Check if it's an Agent Blueprint API version error - try fallback URL
                    if (error.Contains("Agent Blueprints are not supported on the API version"))
                    {
                        _logger.LogDebug("Standard endpoint not supported, trying Agent Blueprint-specific path...");
                        break; // Break retry loop to try next URL
                    }

                    // Other error - break retry loop to try next URL
                    _logger.LogDebug("FIC creation failed with error: {Error}", error);
                    break;
                }
            }

            // All attempts failed
            _logger.LogDebug("Failed to create federated identity credential after trying all endpoints (may not be supported for Agent Blueprints yet): {Error}", lastError);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception creating federated identity credential: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Configure MCP server API permissions (Step 6.5 from PowerShell script).
    /// This was missing in the original C# implementation.
    /// </summary>
    private async Task ConfigureMcpServerPermissionsAsync(
        JsonObject setupConfig,
        JsonObject generatedConfig,
        string blueprintAppId,
        string blueprintObjectId,
        string tenantId,
        CancellationToken ct)
    {
        try
        {
            // Read ToolingManifest.json
            string? toolingManifestPath = null;
            var deploymentProjectPath = setupConfig["deploymentProjectPath"]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(deploymentProjectPath))
            {
                toolingManifestPath = Path.Combine(deploymentProjectPath, "ToolingManifest.json");
                _logger.LogInformation("Looking for ToolingManifest.json in deployment project path: {Path}", toolingManifestPath);
            }
            else
            {
                var scriptDir = Path.GetDirectoryName(Path.GetFullPath(setupConfig.ToJsonString())) ?? Environment.CurrentDirectory;
                toolingManifestPath = Path.Combine(scriptDir, "ToolingManifest.json");
                _logger.LogInformation("Looking for ToolingManifest.json in script directory: {Path}", toolingManifestPath);
            }

            if (!File.Exists(toolingManifestPath))
            {
                _logger.LogInformation("ToolingManifest.json not found - skipping MCP API permissions");
                return;
            }

            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(toolingManifestPath, ct))!.AsObject();

            if (!manifest.TryGetPropertyValue("mcpServers", out var serversNode) || serversNode is not JsonArray servers || servers.Count == 0)
            {
                _logger.LogInformation("No MCP servers found in ToolingManifest.json");
                return;
            }

            var audienceGroups = new Dictionary<string, List<string>>();

            // Group servers by audience
            foreach (var server in servers)
            {
                var serverObj = server?.AsObject();
                if (serverObj == null) continue;

                var scope = serverObj["scope"]?.GetValue<string>();
                var audience = serverObj["audience"]?.GetValue<string>();

                if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(audience))
                    continue;

                // Extract app ID from audience (remove "api://" prefix)
                var mcpAppId = audience.Replace("api://", "");

                // Validate GUID format
                if (!Guid.TryParse(mcpAppId, out _))
                {
                    _logger.LogWarning("Skipping MCP server - invalid audience format: {Audience} (not a valid App ID)", audience);
                    continue;
                }

                if (!audienceGroups.ContainsKey(mcpAppId))
                {
                    audienceGroups[mcpAppId] = new List<string>();
                }

                if (!audienceGroups[mcpAppId].Contains(scope))
                {
                    audienceGroups[mcpAppId].Add(scope);
                }

                _logger.LogInformation("  Found MCP scope: {Scope} for audience: {Audience}", scope, audience);
            }

            if (audienceGroups.Count == 0)
            {
                _logger.LogInformation("  No MCP API permissions found to add");
                return;
            }

            // Note: Agentic Applications don't support RequiredResourceAccess property
            // Skip updating the application with MCP API permissions, but still request admin consent
            _logger.LogInformation("  Skipping MCP API permissions update (not supported for Agentic Applications)");
            _logger.LogInformation("  Will request admin consent directly for MCP scopes");

            // Build consent URL for all MCP scopes
            var mcpConsentScopes = new List<string>();
            foreach (var (appId, scopes) in audienceGroups)
            {
                foreach (var scope in scopes)
                {
                    mcpConsentScopes.Add($"{appId}/{scope}");
                }
            }

            if (mcpConsentScopes.Count > 0)
            {
                var scopesJoined = string.Join(' ', mcpConsentScopes);
                var consentUrlMcp = $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent?client_id={blueprintAppId}&scope={Uri.EscapeDataString(scopesJoined)}&redirect_uri=https://entra.microsoft.com/TokenAuthorize&state=xyz123";

                _logger.LogInformation("  Opening browser for MCP server admin consent...");
                TryOpenBrowser(consentUrlMcp);

                var consentMcpSuccess = await AdminConsentHelper.PollAdminConsentAsync(_executor, _logger, blueprintAppId, "MCP Server Scopes", 180, 5, ct);

                if (consentMcpSuccess)
                {
                    _logger.LogInformation("  MCP server admin consent granted successfully!");
                }
                else
                {
                    _logger.LogWarning("  WARNING: MCP server admin consent may not have completed");
                }

                generatedConfig["agentIdentityConsentUrlMcp"] = consentUrlMcp;
                generatedConfig["consentMcpGranted"] = consentMcpSuccess;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WARNING: Failed to add MCP API permissions: {Message}", ex.Message);
            _logger.LogInformation("  Continuing with Blueprint setup...");
        }
    }

    /// <summary>
    /// Create a client secret for the Agent Blueprint using Microsoft Graph API.
    /// The secret is encrypted using DPAPI on Windows before storage.
    /// </summary>
    private async Task CreateBlueprintClientSecretAsync(
        string blueprintObjectId,
        string blueprintAppId,
        JsonObject generatedConfig,
        string generatedConfigPath,
        CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Creating client secret for Agent Blueprint using Graph API...");
            
            // Get Graph access token
            var graphToken = await _graphService.GetGraphAccessTokenAsync(generatedConfig["tenantId"]?.GetValue<string>() ?? string.Empty, ct);
            
            if (string.IsNullOrWhiteSpace(graphToken))
            {
                _logger.LogError("Failed to acquire Graph API access token");
                throw new InvalidOperationException("Cannot create client secret without Graph API token");
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);

            // Create password credential (client secret)
            var secretBody = new JsonObject
            {
                ["passwordCredential"] = new JsonObject
                {
                    ["displayName"] = "Agent 365 CLI Generated Secret",
                    ["endDateTime"] = DateTime.UtcNow.AddYears(2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                }
            };

            var addPasswordUrl = $"https://graph.microsoft.com/v1.0/applications/{blueprintObjectId}/addPassword";
            var passwordResponse = await httpClient.PostAsync(
                addPasswordUrl,
                new StringContent(secretBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct);

            if (!passwordResponse.IsSuccessStatusCode)
            {
                var errorContent = await passwordResponse.Content.ReadAsStringAsync(ct);
                _logger.LogError("Failed to create client secret: {Status} - {Error}", passwordResponse.StatusCode, errorContent);
                throw new InvalidOperationException($"Failed to create client secret: {errorContent}");
            }

            var passwordJson = await passwordResponse.Content.ReadAsStringAsync(ct);
            var passwordResult = JsonNode.Parse(passwordJson)!.AsObject();

            // Extract and immediately encrypt the secret (no plaintext variable)
            var secretTextNode = passwordResult["secretText"];
            if (secretTextNode == null || string.IsNullOrWhiteSpace(secretTextNode.GetValue<string>()))
            {
                _logger.LogError("Client secret text is empty in response");
                throw new InvalidOperationException("Client secret creation returned empty secret");
            }

            // Encrypt immediately without intermediate plaintext storage
            var protectedSecret = ProtectSecret(secretTextNode.GetValue<string>());
            
            // Store the encrypted client secret in generated config using camelCase
            generatedConfig["agentBlueprintClientSecret"] = protectedSecret;
            generatedConfig["agentBlueprintClientSecretProtected"] = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            
            await File.WriteAllTextAsync(
                generatedConfigPath,
                generatedConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                ct);

            _logger.LogInformation("Client secret created successfully!");
            _logger.LogInformation("  - Secret stored in generated config (encrypted: {IsProtected})", RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
            _logger.LogWarning("IMPORTANT: The client secret has been stored in {Path}", generatedConfigPath);
            _logger.LogWarning("Keep this file secure and do not commit it to source control!");
            
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _logger.LogWarning("WARNING: Secret encryption is only available on Windows. The secret is stored in plaintext.");
                _logger.LogWarning("Consider using environment variables or Azure Key Vault for production deployments.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create client secret: {Message}", ex.Message);
            _logger.LogInformation("You can create a client secret manually:");
            _logger.LogInformation("  1. Go to Azure Portal > App Registrations");
            _logger.LogInformation("  2. Find your Agent Blueprint: {AppId}", blueprintAppId);
            _logger.LogInformation("  3. Navigate to Certificates & secrets > Client secrets");
            _logger.LogInformation("  4. Click 'New client secret' and save the value");
            _logger.LogInformation("  5. Add it to {Path} as 'agentBlueprintClientSecret'", generatedConfigPath);
        }
    }

    /// <summary>
    /// Protects (encrypts) a secret string using DPAPI on Windows.
    /// On non-Windows platforms, returns the plaintext with a warning.
    /// </summary>
    /// <param name="plaintext">The secret to protect</param>
    /// <returns>Base64-encoded encrypted secret on Windows, plaintext on other platforms</returns>
    private string ProtectSecret(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return plaintext;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use Windows DPAPI to encrypt the secret
                var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
                var protectedBytes = ProtectedData.Protect(
                    plaintextBytes,
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);
                
                // Return as base64-encoded string
                return Convert.ToBase64String(protectedBytes);
            }
            else
            {
                // On non-Windows platforms, we cannot use DPAPI
                // Return plaintext and rely on file system permissions
                _logger.LogWarning("DPAPI encryption not available on this platform. Secret will be stored in plaintext.");
                return plaintext;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to encrypt secret, storing in plaintext: {Message}", ex.Message);
            return plaintext;
        }
    }

    private async Task AzWarnAsync(string args, string description)
    {
        var result = await _executor.ExecuteAsync("az", args, suppressErrorLogging: true);
        if (!result.Success)
        {
            if (result.StandardError.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("{Description} already exists (skipping creation)", description);
            }
            else if (result.StandardError.Contains("AuthorizationFailed", StringComparison.OrdinalIgnoreCase))
            {
                var exception = new AzureResourceException(description, string.Empty, result.StandardError, true);
                ExceptionHandler.HandleAgent365Exception(exception);
            }
            else
            {
                _logger.LogWarning("az {Description} returned non-success (exit code {Code}). Error: {Err}",
                    description, result.ExitCode, Short(result.StandardError));
            }
        }
    }

    private void TryOpenBrowser(string url)
    {
        try
        {
            using var p = new System.Diagnostics.Process();
            p.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            p.Start();
        }
        catch
        {
            // non-fatal
        }
    }

    private async Task ConfigureInheritablePermissionsAsync(
        string tenantId,
        JsonObject generatedConfig,
        JsonObject setupConfig,
        CancellationToken ct)
    {
        // Get the App Object ID from generatedConfig
        var blueprintObjectId = generatedConfig["agentBlueprintObjectId"]?.ToString();
        if (string.IsNullOrWhiteSpace(blueprintObjectId))
        {
            _logger.LogError("Blueprint Object ID missing in generated config.");
            throw new InvalidOperationException("Blueprint Object ID missing.");
        }

        // TODO: Detect 1P vs 3P agent blueprint. For now, assume 1P. Replace with real detection logic if available.
        bool is1p = true; // Placeholder: set to false for 3P, or add detection logic

        if (is1p)
        {
            // 1P: POST inheritable permissions to beta endpoint
            GraphServiceClient graphClient;
            try
            {
                graphClient = await GetAuthenticatedGraphClientAsync(tenantId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get authenticated Graph client.");
                _logger.LogWarning("Authentication failed, skipping inheritable permissions configuration.");
                _logger.LogWarning("");
                _logger.LogWarning("MANUAL CONFIGURATION REQUIRED:");
                _logger.LogWarning("  You need to configure inheritable permissions manually in Azure Portal.");
                _logger.LogWarning("  {DocumentationMessage}", DocumentationMessage);
                _logger.LogWarning("");
                
                generatedConfig["inheritanceConfigured"] = false;
                generatedConfig["inheritanceConfigError"] = "Authentication failed: " + ex.Message;
                return;
            }

            var graphToken = await GetTokenFromGraphClient(graphClient, tenantId);
            if (string.IsNullOrWhiteSpace(graphToken))
            {
                _logger.LogError("Failed to acquire Graph API access token");
                _logger.LogWarning("Skipping inheritable permissions configuration");
                _logger.LogWarning("");
                _logger.LogWarning("MANUAL CONFIGURATION REQUIRED:");
                _logger.LogWarning("  You need to configure inheritable permissions manually in Azure Portal.");
                _logger.LogWarning("  {DocumentationMessage}", DocumentationMessage);
                _logger.LogWarning("");
                
                generatedConfig["inheritanceConfigured"] = false;
                generatedConfig["inheritanceConfigError"] = "Failed to acquire Graph API access token";
                return;
            }

            // Read scopes from a365.config.json
            var inheritableScopes = ReadInheritableScopesFromConfig(setupConfig);
            
            if (inheritableScopes.Count == 0)
            {
                _logger.LogInformation("No inheritable scopes found in configuration, skipping inheritable permissions");
                return;
            }

            _logger.LogInformation("Configuring inheritable permissions with {Count} scopes: {Scopes}", 
                inheritableScopes.Count, string.Join(", ", inheritableScopes));

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);

            // ===================================================================
            // Step 1: Configure Microsoft Graph inheritable permissions
            // ===================================================================
            var graphUrl = $"https://graph.microsoft.com/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions";
            
            _logger.LogInformation("Configuring Graph inheritable permissions");
            _logger.LogDebug("  - Request URL: {Url}", graphUrl);
            _logger.LogDebug("  - Blueprint Object ID: {ObjectId}", blueprintObjectId);

            // Convert scope list to JsonArray
            var scopesArray = new JsonArray();
            foreach (var scope in inheritableScopes)
            {
                scopesArray.Add(scope);
            }

            var graphBody = new JsonObject
            {
                ["resourceAppId"] = GraphResourceAppId,
                ["inheritableScopes"] = new JsonObject
                {
                    ["@odata.type"] = "microsoft.graph.enumeratedScopes",
                    ["scopes"] = scopesArray
                }
            };

            _logger.LogDebug("  - Request body: {Body}", graphBody.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            
            var graphResponse = await httpClient.PostAsync(
                graphUrl,
                new StringContent(graphBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct);

            if (!graphResponse.IsSuccessStatusCode)
            {
                var error = await graphResponse.Content.ReadAsStringAsync(ct);
                
                bool isAlreadyConfigured = 
                    (error.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                     error.Contains("duplicate", StringComparison.OrdinalIgnoreCase)) ||
                    graphResponse.StatusCode == System.Net.HttpStatusCode.Conflict;
                
                if (isAlreadyConfigured)
                {
                    _logger.LogInformation("  - Graph inheritable permissions already configured (idempotent)");
                }
                else
                {
                    // Check if this is an authorization error
                    bool isAuthorizationError = 
                        error.Contains("Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase) ||
                        error.Contains("Insufficient privileges", StringComparison.OrdinalIgnoreCase) ||
                        graphResponse.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                        graphResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized;
                    
                    if (isAuthorizationError)
                    {
                        _logger.LogError("Failed to configure Graph inheritable permissions: {Status} - {Error}", 
                            graphResponse.StatusCode, error);
                        _logger.LogError("");
                        _logger.LogError("=== INSUFFICIENT PERMISSIONS DETECTED ===");
                        _logger.LogError("");
                        _logger.LogError("The current user account does not have sufficient privileges to configure inheritable permissions.");
                        _logger.LogError("");
                        foreach (var scope in inheritableScopes)
                        {
                            _logger.LogError("     - {Scope}", scope);
                        }
                        _logger.LogError("  5. Click 'Grant admin consent'");
                        _logger.LogError("");
                        
                        generatedConfig["inheritanceConfigured"] = false;
                        generatedConfig["graphInheritanceError"] = error;
                    }
                    else
                    {
                        _logger.LogError("Failed to configure Graph inheritable permissions: {Status} - {Error}", 
                            graphResponse.StatusCode, error);
                        generatedConfig["inheritanceConfigured"] = false;
                        generatedConfig["graphInheritanceError"] = error;
                    }
                }
            }
            else
            {
                _logger.LogInformation("Successfully configured Graph inheritable permissions");
                _logger.LogInformation("    - Resource: Microsoft Graph");
                _logger.LogInformation("    - Scopes: {Scopes}", string.Join(", ", inheritableScopes));
                generatedConfig["graphInheritanceConfigured"] = true;
            }

            // ===================================================================
            // Step 2: Configure Connectivity inheritable permissions
            // ===================================================================
            var connectivityUrl = $"https://graph.microsoft.com/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/inheritablePermissions";
            
            _logger.LogInformation("");
            _logger.LogInformation("Configuring Connectivity inheritable permissions");
            _logger.LogDebug("  - Request URL: {Url}", connectivityUrl);
            
            var connectivityBody = new JsonObject
            {
                ["resourceAppId"] = ConnectivityResourceAppId,
                ["inheritableScopes"] = new JsonObject
                {
                    ["@odata.type"] = "microsoft.graph.enumeratedScopes",
                    ["scopes"] = new JsonArray { "Connectivity.Connections.Read" }
                }
            };

            _logger.LogDebug("  - Request body: {Body}", connectivityBody.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var connectivityResponse = await httpClient.PostAsync(
                connectivityUrl,
                new StringContent(connectivityBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct);

            if (!connectivityResponse.IsSuccessStatusCode)
            {
                var error = await connectivityResponse.Content.ReadAsStringAsync(ct);
                
                bool isAlreadyConfigured = 
                    (error.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                     error.Contains("duplicate", StringComparison.OrdinalIgnoreCase)) ||
                    connectivityResponse.StatusCode == System.Net.HttpStatusCode.Conflict;
                
                if (isAlreadyConfigured)
                {
                    _logger.LogInformation("  - Connectivity inheritable permissions already configured (idempotent)");
                }
                else
                {
                    // Check if this is an authorization error
                    bool isAuthorizationError = 
                        error.Contains("Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase) ||
                        error.Contains("Insufficient privileges", StringComparison.OrdinalIgnoreCase) ||
                        connectivityResponse.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                        connectivityResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized;
                    
                    if (isAuthorizationError)
                    {
                        _logger.LogError("Failed to configure Connectivity inheritable permissions: {Status} - {Error}", 
                            connectivityResponse.StatusCode, error);
                        _logger.LogError("See the troubleshooting steps above for resolving permission issues.");
                        generatedConfig["connectivityInheritanceError"] = error;
                    }
                    else
                    {
                        _logger.LogError("Failed to configure Connectivity inheritable permissions: {Status} - {Error}", 
                            connectivityResponse.StatusCode, error);
                        generatedConfig["connectivityInheritanceError"] = error;
                    }
                }
            }
            else
            {
                _logger.LogInformation("Successfully configured Connectivity inheritable permissions");
                _logger.LogInformation("    - Resource: Connectivity Service");
                _logger.LogInformation("    - Scope: Connectivity.Connections.Read");
                generatedConfig["connectivityInheritanceConfigured"] = true;
            }

            // Set overall inheritance configured status
            var bothSucceeded = 
                (generatedConfig["graphInheritanceConfigured"]?.GetValue<bool>() ?? false) &&
                (generatedConfig["connectivityInheritanceConfigured"]?.GetValue<bool>() ?? false);
            
            generatedConfig["inheritanceConfigured"] = bothSucceeded;
            
            if (!bothSucceeded)
            {
                _logger.LogWarning("One or more inheritable permissions failed to configure");
                _logger.LogWarning("You may need to configure these manually in Azure Portal");
            }
            else
            {
                _logger.LogInformation("");
                _logger.LogInformation("All inheritable permissions configured successfully!");
            }
        }
        else
        {
            // 3P: Not supported yet
            _logger.LogWarning("Inheritable permissions configuration is not supported for 3P agent blueprints. Skipping.");
            // TODO: Implement 3P logic if/when supported
        }
    }

    /// <summary>
    /// Read inheritable scopes from a365.config.json
    /// Looks for 'agentIdentityScopes' property, falls back to hardcoded defaults
    /// </summary>
    private List<string> ReadInheritableScopesFromConfig(JsonObject setupConfig)
    {
        var inheritableScopes = new List<string>();
        
        try
        {
            _logger.LogInformation("Reading inheritable scopes from a365.config.json");

            // Try to read from agentIdentityScopes property in the setupConfig
            if (setupConfig.TryGetPropertyValue("agentIdentityScopes", out var agentIdentityScopesNode) && 
                agentIdentityScopesNode is JsonArray agentIdentityScopesArr)
            {
                _logger.LogInformation("  Found 'agentIdentityScopes' property in config");
                
                foreach (var scopeItem in agentIdentityScopesArr)
                {
                    var scope = scopeItem?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(scope) && !inheritableScopes.Contains(scope))
                    {
                        inheritableScopes.Add(scope);
                        _logger.LogInformation("  Found inheritable scope: {Scope}", scope);
                    }
                }
            }
            else
            {
                _logger.LogInformation("  'agentIdentityScopes' property not found in config, using hardcoded defaults");
                
                // Use hardcoded defaults from ConfigConstants
                inheritableScopes.AddRange(ConfigConstants.DefaultAgentIdentityScopes);
                
                _logger.LogInformation("  Using {Count} default scopes: {Scopes}", 
                    inheritableScopes.Count, string.Join(", ", inheritableScopes));
            }

            if (inheritableScopes.Count > 0)
            {
                _logger.LogInformation("Total inheritable scopes configured: {Count}", inheritableScopes.Count);
            }
            else
            {
                _logger.LogWarning("No inheritable scopes available - this should not happen");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read inheritable scopes from configuration, using defaults");
            
            // Fallback to defaults on any error
            inheritableScopes.AddRange(ConfigConstants.DefaultAgentIdentityScopes);
            _logger.LogInformation("Using {Count} default scopes as fallback", inheritableScopes.Count);
        }

        return inheritableScopes;
    }

    /// <summary>
    /// Creates and authenticates a GraphServiceClient using InteractiveGraphAuthService.
    /// This common method consolidates the authentication logic used across multiple methods.
    /// </summary>
    private async Task<GraphServiceClient> GetAuthenticatedGraphClientAsync(string tenantId, CancellationToken ct)
    {
        _logger.LogInformation("Authenticating to Microsoft Graph using interactive browser authentication...");
        _logger.LogInformation("IMPORTANT: Agent Blueprint operations require Application.ReadWrite.All permission.");
        _logger.LogInformation("This will open a browser window for interactive authentication.");
        _logger.LogInformation("Please sign in with a Global Administrator account.");
        _logger.LogInformation("");
        
        // Use InteractiveGraphAuthService to get proper authentication
        var interactiveAuth = new InteractiveGraphAuthService(
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<InteractiveGraphAuthService>());
        
        try
        {
            var graphClient = await interactiveAuth.GetAuthenticatedGraphClientAsync(tenantId, ct);
            _logger.LogInformation("Successfully authenticated to Microsoft Graph");
            return graphClient;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to authenticate to Microsoft Graph: {Message}", ex.Message);
            _logger.LogError("");
            _logger.LogError("TROUBLESHOOTING:");
            _logger.LogError("1. Ensure you are a Global Administrator or have Application.ReadWrite.All permission");
            _logger.LogError("2. The account must have already consented to these permissions");
            _logger.LogError("");
            throw new InvalidOperationException($"Microsoft Graph authentication failed: {ex.Message}", ex);
        }
    }

    private static string Short(string? text)
        => string.IsNullOrWhiteSpace(text) ? string.Empty : (text.Length <= 180 ? text.Trim() : text[..177] + "...");
    
    private async Task<string?> GetTokenFromGraphClient(GraphServiceClient graphClient, string tenantId)
    {
        // Use Azure.Identity to get the token directly with specific scopes
        var credential = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            TenantId = tenantId,
            ClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e" // Microsoft Graph PowerShell app ID
        });

        var tokenRequestContext = new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" });
        var token = await credential.GetTokenAsync(tokenRequestContext, CancellationToken.None);
        ValidateGraphToken(token.Token);

        return token.Token;
    }

    /// <summary>
    /// Ensures delegated consent with retry logic (3 attempts with 5-second delays)
    /// Matches the PowerShell script's retry behavior for DelegatedAgentApplicationCreateConsent.ps1
    /// </summary>
    private async Task<bool> EnsureDelegatedConsentWithRetriesAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        const int retryDelaySeconds = 5;
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (attempt > 1)
                {
                    _logger.LogInformation("Retry attempt {Attempt} of {MaxRetries} for delegated consent", attempt, maxRetries);
                    await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), cancellationToken);
                }
                
                var success = await _delegatedConsentService.EnsureAgentApplicationCreateConsentAsync(
                    MicrosoftGraphCommandLineToolsAppId,
                    tenantId,
                    cancellationToken);
                
                if (success)
                {
                    _logger.LogInformation("Successfully ensured delegated application consent on attempt {Attempt}", attempt);
                    return true;
                }
                
                _logger.LogWarning("Consent attempt {Attempt} returned false", attempt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Consent attempt {Attempt} failed: {Message}", attempt, ex.Message);
                
                if (attempt == maxRetries)
                {
                    _logger.LogError("All retry attempts exhausted for delegated consent");
                    _logger.LogError("Common causes:");
                    _logger.LogError("  1. Insufficient permissions - You need Application.ReadWrite.All and DelegatedPermissionGrant.ReadWrite.All");
                    _logger.LogError("  2. Not a Global Administrator or similar privileged role");
                    _logger.LogError("  3. Azure CLI authentication expired - Run 'az login' and retry");
                    _logger.LogError("  4. Network connectivity issues");
                    return false;
                }
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Get the Azure Web App runtime string based on the detected platform
    /// </summary>
    private static string GetRuntimeForPlatform(Models.ProjectPlatform platform)
    {
        return platform switch
        {
            Models.ProjectPlatform.Python => "PYTHON:3.11",
            Models.ProjectPlatform.NodeJs => "NODE:18-lts", 
            Models.ProjectPlatform.DotNet => "DOTNETCORE:8.0",
            _ => "DOTNETCORE:8.0" // Default fallback
        };
    }
    
    /// <summary>
    /// Get the Azure Web App Linux FX Version string based on the detected platform
    /// </summary>
    private static string GetLinuxFxVersionForPlatform(Models.ProjectPlatform platform)
    {
        return platform switch
        {
            Models.ProjectPlatform.Python => "PYTHON|3.11",
            Models.ProjectPlatform.NodeJs => "NODE|18-lts",
            Models.ProjectPlatform.DotNet => "DOTNETCORE|8.0",
            _ => "DOTNETCORE|8.0" // Default fallback
        };
    }
}
