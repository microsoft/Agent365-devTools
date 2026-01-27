// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// C# implementation fully equivalent to a365-createinstance.ps1.
/// Supports all phases: Identity/User creation and License assignment.
/// MCP permissions are configured via inheritable permissions during setup phase.
/// </summary>
public sealed class A365CreateInstanceRunner
{
    private readonly ILogger<A365CreateInstanceRunner> _logger;
    private readonly CommandExecutor _executor;
    private readonly GraphApiService _graphService;

    // License SKU IDs
    private const string SkuTeamsEntNew = "7e31c0d9-9551-471d-836f-32ee72be4a01"; // Microsoft_Teams_Enterprise_New
    private const string SkuE5NoTeams = "18a4bd3f-0b5b-4887-b04f-61dd0ee15f5e"; // Microsoft_365_E5_(no_Teams)

    public A365CreateInstanceRunner(
        ILogger<A365CreateInstanceRunner> logger,
        CommandExecutor executor,
        GraphApiService graphService)
    {
        _logger = logger;
        _executor = executor;
        _graphService = graphService;
    }

    /// <summary>
    /// Execute instance creation workflow.
    /// </summary>
    /// <param name="configPath">Path to a365.config.json</param>
    /// <param name="generatedConfigPath">Path to a365.generated.config.json</param>
    /// <param name="step">Phase to execute: 'identity', 'licenses', 'all' (default: 'all')</param>
    public async Task<bool> RunAsync(
        string configPath,
        string generatedConfigPath,
        string step = "all",
        CancellationToken cancellationToken = default)
    {
        // DEPRECATED: This service bypasses MOS workflows
        _logger.LogError("===============================================================================");
        _logger.LogError("WARNING: A365CreateInstanceRunner bypasses MOS workflow");
        _logger.LogError("===============================================================================");
        _logger.LogError("");
        _logger.LogError("This service uses Graph API directly and skips Microsoft Online Services");
        _logger.LogError("(MOS) workflows. Agents provisioned this way will NOT:");
        _logger.LogError("  - Be properly registered with Microsoft 365 partners");
        _logger.LogError("  - Receive OnHire events");
        _logger.LogError("  - Work correctly with messaging and event propagation");
        _logger.LogError("");
        _logger.LogError("Use 'a365 publish' followed by Teams-based hiring instead.");
        _logger.LogError("See: https://learn.microsoft.com/microsoft-agent-365/onboard");
        _logger.LogError("");
        return false;

        // Unreachable code below - preserved for local development use cases
        #pragma warning disable CS0162 // Unreachable code detected

        // Validate inputs
        if (!File.Exists(configPath))
        {
            _logger.LogError("Config file not found: {Path}", configPath);
            return false;
        }

        // Load config files
        JsonObject config;
        try
        {
            config = JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken))!.AsObject();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse config JSON: {Path}", configPath);
            return false;
        }

        // Get the directory containing the config file for later use
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Environment.CurrentDirectory;

        // Load or create generated config
        JsonObject instance = new JsonObject();
        if (File.Exists(generatedConfigPath))
        {
            try
            {
                instance = JsonNode.Parse(await File.ReadAllTextAsync(generatedConfigPath, cancellationToken))!.AsObject();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WARN] Could not parse existing generated config; starting fresh");
            }
        }

        // Helper to get values from config
        string GetConfig(string name) =>
            config.TryGetPropertyValue(name, out var node) && node is JsonValue jv && jv.TryGetValue(out string? s)
                ? s ?? string.Empty
                : string.Empty;

        // Validate & map core inputs
        var tenantId = GetConfig("tenantId");
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogError("TenantId missing in setup config");
            return false;
        }

        var agentBlueprintId = instance.TryGetPropertyValue("agentBlueprintId", out var bpNode)
            ? bpNode?.GetValue<string>()
            : null;

        if (string.IsNullOrWhiteSpace(agentBlueprintId))
        {
            _logger.LogError("agentBlueprintId missing in generated config");
            return false;
        }

        var agentBlueprintClientSecret = instance.TryGetPropertyValue("agentBlueprintClientSecret", out var secretNode)
            ? secretNode?.GetValue<string>()
            : null;

        // Check if secret is protected (encrypted)
        var isProtected = instance.TryGetPropertyValue("agentBlueprintClientSecretProtected", out var protectedNode)
            ? protectedNode?.GetValue<bool>() ?? false
            : false;

        // Check inheritance status from ResourceConsents
        var inheritanceConfigured = IsInheritanceConfigured(instance);

        // Decrypt the secret if it was encrypted
        if (!string.IsNullOrWhiteSpace(agentBlueprintClientSecret) && isProtected)
        {
            agentBlueprintClientSecret = Microsoft.Agents.A365.DevTools.Cli.Helpers.SecretProtectionHelper.UnprotectSecret(
                agentBlueprintClientSecret, 
                isProtected, 
                _logger);
            _logger.LogInformation("Decrypted agent blueprint client secret");
        }

        if (string.IsNullOrWhiteSpace(agentBlueprintClientSecret))
        {
            _logger.LogWarning("agentBlueprintClientSecret missing; downstream token exchange may fail");
        }

        // Persist core blueprint data
        SetInstanceField(instance, "tenantId", tenantId);
        SetInstanceField(instance, "agentBlueprintId", agentBlueprintId);
        SetInstanceField(instance, "agentBlueprintClientSecret", agentBlueprintClientSecret);

        // Get environment (test/preprod/prod) for endpoint configuration
        var environment = GetConfig("environment");
        if (string.IsNullOrWhiteSpace(environment))
        {
            environment = "preprod"; // default
            _logger.LogInformation("Environment not specified in config, using default: {Env}", environment);
        }
        else
        {
            _logger.LogInformation("Using environment from config: {Env}", environment);
        }

        // AgentIdentityScopes (fallback to hardcoded defaults)
        var agentIdentityScopes = new List<string>();
        if (config.TryGetPropertyValue("agentIdentityScopes", out var scopesNode) && scopesNode is JsonArray scopesArray)
        {
            _logger.LogInformation("Found 'agentIdentityScopes' in config");
            agentIdentityScopes = scopesArray
                .Select(s => s?.GetValue<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList()!;
        }
        else if (config.TryGetPropertyValue("agentIdentityScope", out var singleScopeNode))
        {
            var singleScope = singleScopeNode?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(singleScope))
            {
                _logger.LogInformation("Found single 'agentIdentityScope' in config");
                agentIdentityScopes.Add(singleScope);
            }
        }
        else
        {
            _logger.LogInformation("'agentIdentityScopes' not found in config, using hardcoded defaults");
            agentIdentityScopes.AddRange(ConfigConstants.DefaultAgentIdentityScopes);
        }

        if (agentIdentityScopes.Count == 0)
        {
            _logger.LogWarning("No agent identity scopes available, falling back to Graph default");
            agentIdentityScopes.Add("https://graph.microsoft.com/.default");
        }

        var usageLocation = GetConfig("agentUserUsageLocation");

        await SaveInstanceAsync(generatedConfigPath, instance, cancellationToken);
        _logger.LogInformation("Core inputs mapped and instance seed saved to {Path}", generatedConfigPath);

        // ==============================================
        // Phase 1: Agent Identity + Agent User Creation 
        // ==============================================
        if (step == "identity" || step == "all")
        {
            _logger.LogInformation("Phase 1: Creating Agent Identity and Agent User");

            // Create RetryHelper for this phase
            var retryHelper = new RetryHelper(_logger);

            var agentIdentityDisplayName = GetConfig("agentIdentityDisplayName");
            var agentUserDisplayName = GetConfig("agentUserDisplayName");
            var agentUserPrincipalName = GetConfig("agentUserPrincipalName");
            var managerEmail = GetConfig("managerEmail");

            // Check if identity already exists (idempotent)
            string? agenticAppId = instance.TryGetPropertyValue("AgenticAppId", out var existingIdentityNode)
                ? existingIdentityNode?.GetValue<string>()
                : null;

            if (string.IsNullOrWhiteSpace(agenticAppId))
            {
                // Create new agent identity
                var identityResult = await CreateAgentIdentityAsync(
                    tenantId,
                    agentBlueprintId!,
                    agentBlueprintClientSecret!,
                    agentIdentityDisplayName,
                    cancellationToken);

                if (!identityResult.success)
                {
                    _logger.LogError("Failed to create agent identity");
                    return false;
                }

                agenticAppId = identityResult.identityId;
                SetInstanceField(instance, "AgenticAppId", agenticAppId);
                await SaveInstanceAsync(generatedConfigPath, instance, cancellationToken);
                
                if (string.IsNullOrWhiteSpace(agenticAppId))
                {
                    _logger.LogError("Agent identity ID is null or empty after creation");
                    return false;
                }
                
                _logger.LogInformation("Waiting for Agent Identity to propagate in Azure AD...");
                _logger.LogInformation("This may take 30-60 seconds for full propagation.");
                
                // Use RetryHelper to verify service principal exists
                try
                {
                    var servicePrincipalExists = await retryHelper.ExecuteWithRetryAsync(
                        async ct =>
                        {
                            _logger.LogInformation("Verifying Agent Identity propagation...");
                            var spExists = await VerifyServicePrincipalExistsAsync(tenantId, agenticAppId, ct);
                            if (spExists)
                            {
                                _logger.LogInformation("Agent Identity service principal verified in directory!");
                            }
                            return spExists;
                        },
                        result => !result,
                        maxRetries: 12,
                        baseDelaySeconds: 5,
                        cancellationToken);
                    
                    if (!servicePrincipalExists)
                    {
                        _logger.LogError("Agent Identity service principal not found in directory after 60+ seconds");
                        _logger.LogError("The identity was created but has not fully propagated yet.");
                        _logger.LogError("");
                        _logger.LogError("RECOMMENDED ACTIONS:");
                        _logger.LogError("  1. Wait 5-10 more minutes for Azure AD propagation");
                        _logger.LogError("  2. Verify the identity exists in Azure Portal > Enterprise Applications");
                        _logger.LogError("  3. Re-run 'a365 create-instance identity' to retry user creation");
                        _logger.LogError("");
                        return false;
                    }
                    
                    // Service principal exists, wait a bit more for complete propagation
                    _logger.LogInformation("Waiting 10 more seconds for complete propagation...");
                    await Task.Delay(10000, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error verifying service principal: {Message}", ex.Message);
                    return false;
                }
            }
            else
            {
                _logger.LogInformation("Agent Identity already exists: {Id}", agenticAppId);
            }

            // Check if user already exists (idempotent)
            string? agenticUserId = instance.TryGetPropertyValue("AgenticUserId", out var existingUserNode)
                ? existingUserNode?.GetValue<string>()
                : null;

            if (string.IsNullOrWhiteSpace(agenticUserId))
            {
                // Create agent user with retry logic using RetryHelper
                _logger.LogInformation("Creating Agent User...");
                
                var userResult = await retryHelper.ExecuteWithRetryAsync(
                    async ct => await CreateAgentUserAsync(
                        tenantId,
                        agenticAppId!,
                        agentUserDisplayName,
                        agentUserPrincipalName,
                        usageLocation,
                        managerEmail,
                        ct),
                    result => !result.success,
                    maxRetries: 3,
                    baseDelaySeconds: 10,
                    cancellationToken);

                if (!userResult.success)
                {
                    _logger.LogError("Failed to create agent user after 3 attempts - this is a critical error");
                    _logger.LogError("");
                    _logger.LogError("POSSIBLE CAUSES:");
                    _logger.LogError("  1. Agent Identity service principal has not fully propagated in Azure AD");
                    _logger.LogError("  2. User Principal Name '{UPN}' is already in use", agentUserPrincipalName);
                    _logger.LogError("  3. Insufficient permissions to create users");
                    _logger.LogError("");
                    _logger.LogError("RECOMMENDED ACTIONS:");
                    _logger.LogError("  1. Wait 5-10 minutes and run: a365 setup createinstance --step user");
                    _logger.LogError("  2. Verify User.ReadWrite.All permission is granted");
                    _logger.LogError("  3. Check Azure AD audit logs for detailed error information");
                    return false;
                }

                agenticUserId = userResult.userId;
                SetInstanceField(instance, "AgenticUserId", agenticUserId);
                SetInstanceField(instance, "agentUserPrincipalName", agentUserPrincipalName);
                await SaveInstanceAsync(generatedConfigPath, instance, cancellationToken);
            }
            else
            {
                _logger.LogInformation("Agent User already exists: {Id}", agenticUserId);
            }

            // Consent URLs and polling
            if (!string.IsNullOrWhiteSpace(agenticAppId))
            {
                if (inheritanceConfigured)
                {
                    _logger.LogInformation("Inheritance already configured; skipping admin consent requests");
                    _logger.LogInformation("Phase 1 complete.");
                }
                else
                {

                    var scopesJoined = string.Join(' ', agentIdentityScopes);
                    var consentGraph = $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent?client_id={agenticAppId}&scope={Uri.EscapeDataString(scopesJoined)}&redirect_uri=https://entra.microsoft.com/TokenAuthorize&state=xyz123";

                    SetInstanceField(instance, "agentIdentityConsentUrlGraph", consentGraph);

                    // Request admin consent for Graph API
                    var consentGraphSuccess = await RequestAdminConsentAsync(
                        consentGraph,
                        agenticAppId,
                        tenantId,
                        "Agent Instance Graph scopes",
                        180,
                        cancellationToken);

                    // Consent for MCP servers from ToolingManifest.json
                    var consentMcpSuccess = await ProcessMcpConsentAsync(
                        instance,
                        agenticAppId,
                        tenantId,
                        configDirectory,
                        cancellationToken);

                    instance["consent1Granted"] = consentGraphSuccess;
                    instance["consent3Granted"] = consentMcpSuccess;

                    if (!consentGraphSuccess || !consentMcpSuccess)
                    {
                        _logger.LogWarning("One or more consents may not have been detected");
                        _logger.LogInformation("The setup will continue, but you may need to grant consent manually if needed.");
                    }
                }
            }

            await SaveInstanceAsync(generatedConfigPath, instance, cancellationToken);
            _logger.LogInformation("Phase 1 complete.");
        }

        // ============================
        // Phase 2: License Assignment 
        // ============================
        if (step == "licenses" || step == "all")
        {
            _logger.LogInformation("Phase 2: License assignment");

            if (instance.TryGetPropertyValue("AgenticUserId", out var userIdNode))
            {
                var agenticUserId = userIdNode?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(agenticUserId))
                {
                    await AssignLicensesAsync(agenticUserId, usageLocation, tenantId, cancellationToken);
                }
            }
            else
            {
                _logger.LogInformation("AgenticUserId absent; skipping license assignment");
            }

            await SaveInstanceAsync(generatedConfigPath, instance, cancellationToken);
            _logger.LogInformation("Phase 2 complete.");
        }

        _logger.LogInformation("All phases complete. Instance state saved: {Path}", generatedConfigPath);
        _logger.LogInformation("All phases complete. Agent 365 instance is ready.");

        return true;
    }

    /// <summary>
    /// Create Agent Identity using Microsoft Graph API
    /// Replaces createAgenticUser.ps1 (identity creation part)
    /// IMPORTANT: Uses blueprint client credentials for authentication (application permissions required)
    /// </summary>
    private async Task<(bool success, string? identityId)> CreateAgentIdentityAsync(
        string tenantId,
        string agentBlueprintId,
        string agentBlueprintClientSecret,
        string displayName,
        CancellationToken ct)
    {
        // Generate correlation ID at workflow entry point
        var correlationId = HttpClientFactory.GenerateCorrelationId();

        try
        {
            _logger.LogInformation("Creating Agent Identity using Graph API (CorrelationId: {CorrelationId})...", correlationId);
            _logger.LogInformation("  - Display Name: {Name}", displayName);
            _logger.LogInformation("  - Agent Blueprint ID: {Id}", agentBlueprintId);
            _logger.LogInformation("  - Authenticating using blueprint client credentials...");

            // Validate that we have client secret
            if (string.IsNullOrWhiteSpace(agentBlueprintClientSecret))
            {
                _logger.LogError("Blueprint client secret is required to create agent identity");
                _logger.LogError("The client secret should have been created during blueprint setup");
                return (false, null);
            }

            // Get access token using client credentials flow (blueprint ID + secret)
            string? accessToken = await GetBlueprintAccessTokenAsync(
                tenantId,
                agentBlueprintId,
                agentBlueprintClientSecret,
                ct,
                correlationId: correlationId);

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogError("Failed to acquire access token using blueprint credentials");
                return (false, null);
            }

            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(accessToken, correlationId: correlationId);

            // Get current user for sponsor (optional - use delegated token for this)
            string? currentUserId = null;
            try
            {
                // Use Azure CLI token to get current user (this requires delegated context)
                var delegatedToken = await _graphService.GetGraphAccessTokenAsync(tenantId, ct);
                if (!string.IsNullOrWhiteSpace(delegatedToken))
                {
                    using var delegatedClient = HttpClientFactory.CreateAuthenticatedClient(delegatedToken, correlationId: correlationId);

                    var meResponse = await delegatedClient.GetAsync("https://graph.microsoft.com/v1.0/me", ct);
                    if (meResponse.IsSuccessStatusCode)
                    {
                        var meJson = await meResponse.Content.ReadAsStringAsync(ct);
                        var me = JsonNode.Parse(meJson)!.AsObject();
                        currentUserId = me["id"]!.GetValue<string>();
                        _logger.LogInformation("  - Current user ID (sponsor): {UserId}", currentUserId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get current user ID for sponsor, will create without sponsor");
            }

            // Create agent identity via service principal endpoint
            var createIdentityUrl = "https://graph.microsoft.com/beta/serviceprincipals/Microsoft.Graph.AgentIdentity";
            var identityBody = new JsonObject
            {
                ["displayName"] = displayName,
                ["agentAppId"] = agentBlueprintId
            };

            // Add sponsor if we have current user ID
            if (!string.IsNullOrWhiteSpace(currentUserId))
            {
                identityBody["sponsors@odata.bind"] = new JsonArray
                {
                    $"https://graph.microsoft.com/v1.0/users/{currentUserId}"
                };
            }

            _logger.LogInformation("  - Sending request to create agent identity...");
            var identityResponse = await httpClient.PostAsync(
                createIdentityUrl,
                new StringContent(identityBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct);

            // Handle case where sponsor is not supported (fallback without sponsor)
            if (!identityResponse.IsSuccessStatusCode)
            {
                var errorContent = await identityResponse.Content.ReadAsStringAsync(ct);
                
                // Check if error is due to calling identity type
                if (errorContent.Contains("Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase) ||
                    errorContent.Contains("calling identity type", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Failed to create agent identity: Authorization denied");
                    _logger.LogError("This usually means the blueprint application doesn't have the required permissions");
                    _logger.LogError("");
                    _logger.LogError("REQUIRED PERMISSIONS:");
                    _logger.LogError("  - Application.ReadWrite.All (Application permission)");
                    _logger.LogError("  - AgentIdentity.Create.OwnedBy (Application permission)");
                    _logger.LogError("");
                    return (false, null);
                }
                
                if (identityResponse.StatusCode == System.Net.HttpStatusCode.BadRequest &&
                    !string.IsNullOrWhiteSpace(currentUserId))
                {
                    _logger.LogWarning("Agent Identity creation with sponsor failed, retrying without sponsor...");
                    
                    // Remove sponsor and try again
                    identityBody.Remove("sponsors@odata.bind");
                    
                    identityResponse = await httpClient.PostAsync(
                        createIdentityUrl,
                        new StringContent(identityBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                        ct);
                    
                    if (!identityResponse.IsSuccessStatusCode)
                    {
                        errorContent = await identityResponse.Content.ReadAsStringAsync(ct);
                    }
                }
            }

            if (!identityResponse.IsSuccessStatusCode)
            {
                var errorContent = await identityResponse.Content.ReadAsStringAsync(ct);
                _logger.LogError("Failed to create agent identity: {Status} - {Error}", identityResponse.StatusCode, errorContent);
                return (false, null);
            }

            var identityJson = await identityResponse.Content.ReadAsStringAsync(ct);
            var identity = JsonNode.Parse(identityJson)!.AsObject();
            var identityId = identity["id"]!.GetValue<string>();

            _logger.LogInformation("Agent Identity created successfully!");
            _logger.LogInformation("  - Agent Identity ID: {Id}", identityId);

            return (true, identityId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create agent identity: {Message}", ex.Message);
            return (false, null);
        }
    }

    /// <summary>
    /// Get access token for blueprint using client credentials flow (OAuth 2.0 Client Credentials Grant)
    /// This uses the blueprint's client ID and secret to authenticate as the application itself
    /// </summary>
    private async Task<string?> GetBlueprintAccessTokenAsync(
        string tenantId,
        string clientId,
        string clientSecret,
        CancellationToken ct,
        string? correlationId = null)
    {
        try
        {
            _logger.LogInformation("Acquiring access token using client credentials...");

            // Use provided correlation ID or generate a new one
            var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId)
                ? HttpClientFactory.GenerateCorrelationId()
                : correlationId;

            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(correlationId: effectiveCorrelationId);
            var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
            
            var requestBody = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("scope", "https://graph.microsoft.com/.default"),
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });

            var response = await httpClient.PostAsync(tokenEndpoint, requestBody, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Failed to acquire token: {Status} - {Error}", response.StatusCode, errorContent);
                
                if (errorContent.Contains("invalid_client", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("");
                    _logger.LogError("AUTHENTICATION FAILED: Invalid client credentials");
                    _logger.LogError("The blueprint client ID or secret may be incorrect or expired.");
                    _logger.LogError("");
                    _logger.LogError("TO FIX:");
                    _logger.LogError("  1. Verify the blueprint was created successfully during setup");
                    _logger.LogError("  2. Check that the client secret in a365.generated.config.json is correct");
                    _logger.LogError("  3. If the secret expired, create a new one in Azure Portal");
                    _logger.LogError("");
                }
                
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync(ct);
            var tokenResponse = JsonNode.Parse(responseContent)!.AsObject();
            var accessToken = tokenResponse["access_token"]!.GetValue<string>();
            
            _logger.LogInformation("Access token acquired successfully using client credentials");
            return accessToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception acquiring access token: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Create Agent User using Microsoft Graph API
    /// Replaces createAgenticUser.ps1 (user creation part)
    /// </summary>
    private async Task<(bool success, string? userId)> CreateAgentUserAsync(
        string tenantId,
        string agenticAppId,
        string displayName,
        string userPrincipalName,
        string? usageLocation,
        string? managerEmail,
        CancellationToken ct)
    {
        // Generate correlation ID at workflow entry point
        var correlationId = HttpClientFactory.GenerateCorrelationId();

        try
        {
            _logger.LogInformation("Creating Agent User using Graph API (CorrelationId: {CorrelationId})...", correlationId);
            _logger.LogInformation("  - Display Name: {Name}", displayName);
            _logger.LogInformation("  - User Principal Name: {UPN}", userPrincipalName);
            _logger.LogInformation("  - Agent Identity ID: {Id}", agenticAppId);

            // Get Graph access token
            var graphToken = await _graphService.GetGraphAccessTokenAsync(tenantId, ct);
            if (string.IsNullOrWhiteSpace(graphToken))
            {
                _logger.LogError("Failed to acquire Graph API access token");
                return (false, null);
            }

            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(graphToken, correlationId: correlationId);

            // Check if user already exists
            try
            {
                var checkUserUrl = $"https://graph.microsoft.com/beta/users/{Uri.EscapeDataString(userPrincipalName)}";
                var checkResponse = await httpClient.GetAsync(checkUserUrl, ct);
                
                if (checkResponse.IsSuccessStatusCode)
                {
                    var existingUserJson = await checkResponse.Content.ReadAsStringAsync(ct);
                    var existingUser = JsonNode.Parse(existingUserJson)!.AsObject();
                    var existingUserId = existingUser["id"]!.GetValue<string>();
                    
                    _logger.LogInformation("User already exists: {Name} ({UPN})", 
                        existingUser["displayName"]?.GetValue<string>(), 
                        existingUser["userPrincipalName"]?.GetValue<string>());
                    _logger.LogInformation("Using existing user instead of creating new one.");
                    
                    return (true, existingUserId);
                }
            }
            catch
            {
                // User does not exist, proceed with creation
            }

            // Create agent user
            var mailNickname = userPrincipalName.Split('@')[0];
            var createUserUrl = "https://graph.microsoft.com/beta/users";
            var userBody = new JsonObject
            {
                ["@odata.type"] = "microsoft.graph.agentUser",
                ["displayName"] = displayName,
                ["userPrincipalName"] = userPrincipalName,
                ["mailNickname"] = mailNickname,
                ["accountEnabled"] = true,
                ["usageLocation"] = usageLocation ?? "US",
                ["identityParent"] = new JsonObject
                {
                    ["id"] = agenticAppId
                }
            };

            var userResponse = await httpClient.PostAsync(
                createUserUrl,
                new StringContent(userBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct);

            if (!userResponse.IsSuccessStatusCode)
            {
                var errorContent = await userResponse.Content.ReadAsStringAsync(ct);
                _logger.LogError("Failed to create agent user: {Status} - {Error}", userResponse.StatusCode, errorContent);
                return (false, null);
            }

            var userJson = await userResponse.Content.ReadAsStringAsync(ct);
            var user = JsonNode.Parse(userJson)!.AsObject();
            var userId = user["id"]!.GetValue<string>();

            _logger.LogInformation("Agent User created successfully!");
            _logger.LogInformation("  - Agent User ID: {Id}", userId);
            _logger.LogInformation("  - User Principal Name: {UPN}", userPrincipalName);

            // Assign manager if provided
            if (!string.IsNullOrWhiteSpace(managerEmail))
            {
                await AssignManagerAsync(userId, managerEmail, graphToken, correlationId: correlationId, ct);
            }

            return (true, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create agent user: {Message}", ex.Message);
            return (false, null);
        }
    }

    /// <summary>
    /// Assign manager to agent user
    /// </summary>
    private async Task AssignManagerAsync(
        string userId,
        string managerEmail,
        string graphToken,
        string correlationId,
        CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("  - Assigning manager");

            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(graphToken, correlationId: correlationId);

            // Look up manager by email
            var managerUrl = $"https://graph.microsoft.com/v1.0/users?$filter=mail eq '{managerEmail}'";
            var managerResponse = await httpClient.GetAsync(managerUrl, ct);

            if (!managerResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to find manager with the given email");
                return;
            }

            var managerJson = await managerResponse.Content.ReadAsStringAsync(ct);
            var managers = JsonNode.Parse(managerJson)!.AsObject();
            var managersArray = managers["value"]!.AsArray();

            if (managersArray.Count == 0)
            {
                _logger.LogWarning("No manager found with the given email");
                return;
            }

            var manager = managersArray[0]!.AsObject();
            var managerId = manager["id"]!.GetValue<string>();
            var managerName = manager["displayName"]?.GetValue<string>();

            // Assign manager
            var assignManagerUrl = $"https://graph.microsoft.com/v1.0/users/{userId}/manager/$ref";
            var assignBody = new JsonObject
            {
                ["@odata.id"] = $"https://graph.microsoft.com/v1.0/users/{managerId}"
            };

            var assignResponse = await httpClient.PutAsync(
                assignManagerUrl,
                new StringContent(assignBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct);

            if (assignResponse.IsSuccessStatusCode)
            {
                _logger.LogInformation("  - Manager assigned");
            }
            else
            {
                var errorContent = await assignResponse.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Failed to assign manager: {Error}", errorContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to assign manager: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Process MCP consent from ToolingManifest.json
    /// </summary>
    private async Task<bool> ProcessMcpConsentAsync(
        JsonObject instance,
        string agenticAppId,
        string tenantId,
        string configDirectory,
        CancellationToken ct)
    {
        var scriptDir = Path.GetDirectoryName(configDirectory) ?? Environment.CurrentDirectory;
        var toolingManifestPath = Path.GetFullPath(Path.Combine(
            scriptDir,
            "../../dotnet/samples/semantic-kernel-multiturn/ToolingManifest.json"));

        if (!File.Exists(toolingManifestPath))
        {
            _logger.LogWarning("ToolingManifest.json not found at {Path}; skipping MCP consent", toolingManifestPath);
            return false;
        }

        try
        {
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(toolingManifestPath, ct))!.AsObject();
            var mcpAudiences = new Dictionary<string, List<string>>();

            if (manifest.TryGetPropertyValue("mcpServers", out var serversNode) &&
                serversNode is JsonArray servers)
            {
                foreach (var server in servers)
                {
                    var serverObj = server?.AsObject();
                    if (serverObj == null) continue;

                    var audience = serverObj["audience"]?.GetValue<string>();
                    var scope = serverObj["scope"]?.GetValue<string>();

                    if (string.IsNullOrWhiteSpace(audience) || string.IsNullOrWhiteSpace(scope))
                        continue;

                    var audienceId = audience.Replace("api://", "");

                    if (!mcpAudiences.ContainsKey(audienceId))
                    {
                        mcpAudiences[audienceId] = new List<string>();
                    }

                    mcpAudiences[audienceId].Add(scope);
                }
            }

            // Build consent for each unique audience
            var allConsentSuccess = true;
            var consentCounter = 3;

            foreach (var (audienceId, scopes) in mcpAudiences)
            {
                var uniqueScopes = scopes.Distinct().ToList();
                var scopesWithAudience = uniqueScopes.Select(s => $"api://{audienceId}/{s}");
                var mcpScopesJoined = string.Join(' ', scopesWithAudience);

                var consentUrl = $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent?client_id={agenticAppId}&scope={Uri.EscapeDataString(mcpScopesJoined)}&redirect_uri=https://entra.microsoft.com/TokenAuthorize&state=xyz123";

                SetInstanceField(instance, $"agentIdentityConsentUrlMcp{consentCounter}", consentUrl);

                var consentSuccess = await RequestAdminConsentAsync(
                    consentUrl,
                    agenticAppId,
                    tenantId,
                    $"Agent Instance MCP scopes for audience {audienceId}",
                    180,
                    ct);

                instance[$"consent{consentCounter}Granted"] = consentSuccess;

                if (!consentSuccess) allConsentSuccess = false;
                consentCounter++;
            }

            return allConsentSuccess;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process ToolingManifest.json for MCP consent");
            return false;
        }
    }

    /// <summary>
    /// Assign licenses using Microsoft Graph API
    /// Replaces inline PowerShell license assignment script
    /// </summary>
    private async Task AssignLicensesAsync(
        string userId,
        string? usageLocation,
        string tenantId,
        CancellationToken cancellationToken)
    {
        // Generate correlation ID at workflow entry point
        var correlationId = HttpClientFactory.GenerateCorrelationId();

        try
        {
            _logger.LogInformation("Assigning licenses to user {UserId} using Graph API (CorrelationId: {CorrelationId})", userId, correlationId);

            // Get Graph access token
            var graphToken = await _graphService.GetGraphAccessTokenAsync(tenantId, cancellationToken);
            if (string.IsNullOrWhiteSpace(graphToken))
            {
                _logger.LogError("Failed to acquire Graph API access token for license assignment");
                return;
            }

            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(graphToken, correlationId: correlationId);

            // Set usage location if provided
            if (!string.IsNullOrWhiteSpace(usageLocation))
            {
                _logger.LogInformation("  - Setting usage location: {Location}", usageLocation);
                var updateUserUrl = $"https://graph.microsoft.com/v1.0/users/{userId}";
                var updateBody = new JsonObject
                {
                    ["usageLocation"] = usageLocation
                };

                var updateResponse = await httpClient.PatchAsync(
                    updateUserUrl,
                    new StringContent(updateBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                    cancellationToken);

                if (!updateResponse.IsSuccessStatusCode)
                {
                    var errorContent = await updateResponse.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("Failed to set usage location: {Error}", errorContent);
                }
            }

            // Assign licenses
            _logger.LogInformation("  - Assigning Microsoft 365 licenses");
            var assignLicenseUrl = $"https://graph.microsoft.com/v1.0/users/{userId}/assignLicense";
            var licenseBody = new JsonObject
            {
                ["addLicenses"] = new JsonArray
                {
                    new JsonObject { ["skuId"] = SkuTeamsEntNew },
                    new JsonObject { ["skuId"] = SkuE5NoTeams }
                },
                ["removeLicenses"] = new JsonArray()
            };

            var licenseResponse = await httpClient.PostAsync(
                assignLicenseUrl,
                new StringContent(licenseBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                cancellationToken);

            if (licenseResponse.IsSuccessStatusCode)
            {
                _logger.LogInformation("Licenses assigned successfully");
                _logger.LogInformation("  - Microsoft Teams Enterprise");
                _logger.LogInformation("  - Microsoft 365 E5 (no Teams)");
            }
            else
            {
                var errorContent = await licenseResponse.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("License assignment failed: {Error}", errorContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to assign licenses: {Message}", ex.Message);
        }
    }

    // ========================================================================
    // Helper Methods (Unchanged)
    // ========================================================================

    private void SetInstanceField(JsonObject instance, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _logger.LogWarning("Skipping Set-InstanceField for {Name} (null or empty value)", name);
            return;
        }

        instance[name] = value;
        _logger.LogInformation("Added/Updated field {Name} = {Value}", name, value);
    }

    private async Task SaveInstanceAsync(string path, JsonObject instance, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            path,
            instance.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        _logger.LogInformation("Saved instance state to {Path}", path);
    }

    private async Task<bool> RequestAdminConsentAsync(
        string consentUrl,
        string appId,
        string tenantId,
        string description,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("");
        _logger.LogInformation("=== Consent Required: {Desc} ===", description);
        _logger.LogInformation("Opening browser for admin consent...");
        _logger.LogInformation("URL: {Url}", consentUrl);

        // Open browser
        TryOpenBrowser(consentUrl);

        _logger.LogInformation("");
        _logger.LogInformation("Waiting for admin consent (timeout: {Timeout} seconds)...", timeoutSeconds);
        _logger.LogInformation("Polling for consent status...");

        var startTime = DateTime.UtcNow;
        var pollInterval = 5;
        string? spId = null;
        var dotCount = 0;

        while ((DateTime.UtcNow - startTime).TotalSeconds < timeoutSeconds && !cancellationToken.IsCancellationRequested)
        {
            // Get service principal ID
            if (spId == null)
            {
                var spResult = await _executor.ExecuteAsync(
                    "az",
                    $"rest --method GET --url \"https://graph.microsoft.com/v1.0/servicePrincipals?$filter=appId eq '{appId}'\"",
                    captureOutput: true,
                    suppressErrorLogging: true,
                    cancellationToken: cancellationToken);

                if (spResult.Success)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(spResult.StandardOutput);
                        var value = doc.RootElement.GetProperty("value");
                        if (value.GetArrayLength() > 0)
                        {
                            spId = value[0].GetProperty("id").GetString();
                        }
                    }
                    catch { /* ignore parse errors */ }
                }
            }

            // Check for grants
            if (spId != null)
            {
                var grants = await _executor.ExecuteAsync(
                    "az",
                    $"rest --method GET --url \"https://graph.microsoft.com/v1.0/oauth2PermissionGrants?$filter=clientId eq '{spId}'\"",
                    captureOutput: true,
                    suppressErrorLogging: true,
                    cancellationToken: cancellationToken);

                if (grants.Success)
                {
                    try
                    {
                        using var gdoc = JsonDocument.Parse(grants.StandardOutput);
                        var arr = gdoc.RootElement.GetProperty("value");
                        if (arr.GetArrayLength() > 0)
                        {
                            _logger.LogInformation("");
                            _logger.LogInformation("Consent granted successfully!");
                            await Task.Delay(2000, cancellationToken); // Brief pause to ensure consent propagates
                            return true;
                        }
                    }
                    catch { /* ignore parse errors */ }
                }
            }

            // Show progress
            Console.Write(".");
            dotCount++;
            if (dotCount >= 12)
            {
                Console.Write(" (still waiting...)");
                Console.WriteLine();
                dotCount = 0;
            }

            await Task.Delay(TimeSpan.FromSeconds(pollInterval), cancellationToken);
        }

        Console.WriteLine();
        _logger.LogWarning("Timeout waiting for admin consent");
        _logger.LogInformation("You can manually verify consent was granted and continue.");
        return false;
    }

    private void TryOpenBrowser(string url)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open browser automatically");
            _logger.LogInformation("Please manually open: {Url}", url);
        }
    }

    /// <summary>
    /// Verify that a service principal exists in Azure AD for the given app ID.
    /// This is critical before creating an agent user that references the identity as a parent.
    /// </summary>
    /// <param name="tenantId">Azure AD tenant ID</param>
    /// <param name="appId">Application (client) ID of the agent identity</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the service principal exists, false otherwise</returns>
    private async Task<bool> VerifyServicePrincipalExistsAsync(
        string tenantId,
        string appId,
        CancellationToken ct)
    {
        // Generate correlation ID at workflow entry point
        var correlationId = HttpClientFactory.GenerateCorrelationId();

        try
        {
            // Use Graph API to check if service principal exists
            var graphToken = await _graphService.GetGraphAccessTokenAsync(tenantId, ct);
            if (string.IsNullOrWhiteSpace(graphToken))
            {
                _logger.LogWarning("Failed to acquire Graph token for service principal verification");
                return false;
            }

            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(graphToken, correlationId: correlationId);

            // Query for service principal by appId
            var spUrl = $"https://graph.microsoft.com/v1.0/servicePrincipals?$filter=appId eq '{appId}'";
            var response = await httpClient.GetAsync(spUrl, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Service principal query failed: {Status} - {Error}", response.StatusCode, errorContent);
                return false;
            }

            var jsonContent = await response.Content.ReadAsStringAsync(ct);
            var spResult = JsonNode.Parse(jsonContent)!.AsObject();
            var valueArray = spResult["value"]?.AsArray();

            if (valueArray != null && valueArray.Count > 0)
            {
                var sp = valueArray[0]!.AsObject();
                var spObjectId = sp["id"]?.GetValue<string>();
                var spDisplayName = sp["displayName"]?.GetValue<string>();
                
                _logger.LogInformation("  Service Principal found:");
                _logger.LogInformation("    - Object ID: {ObjectId}", spObjectId);
                _logger.LogInformation("    - Display Name: {DisplayName}", spDisplayName);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception verifying service principal: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Checks if inheritable permissions are configured by examining ResourceConsents.
    /// Returns true if all resources with inheritable permissions have them configured.
    /// </summary>
    private static bool IsInheritanceConfigured(JsonNode instance)
    {
        if (!instance.AsObject().TryGetPropertyValue("resourceConsents", out var resourceConsentsNode))
            return false;

        if (resourceConsentsNode?.AsArray() is not { } resourceConsents || resourceConsents.Count == 0)
            return false;

        var resourcesWithInheritance = resourceConsents
            .Where(rc => rc?["inheritablePermissionsConfigured"] != null)
            .ToList();

        if (resourcesWithInheritance.Count == 0)
            return false;

        return resourcesWithInheritance.All(rc => 
            rc?["inheritablePermissionsConfigured"]?.GetValue<bool>() == true);
    }
}
