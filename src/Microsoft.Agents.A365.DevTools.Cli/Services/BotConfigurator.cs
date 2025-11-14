// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for configuring Azure Bot resources
/// </summary>
public class BotConfigurator
{
    private readonly ILogger<BotConfigurator> _logger;
    private readonly CommandExecutor _executor;
    private readonly HttpClient _httpClient;

    public BotConfigurator(ILogger<BotConfigurator> logger, CommandExecutor executor)
    {
        _logger = logger;
        _executor = executor;
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Check if Microsoft.BotService provider is registered in the subscription
    /// </summary>
    public async Task<bool> EnsureBotServiceProviderAsync(string subscriptionId, string resourceGroupName)
    {
        _logger.LogDebug("Checking if Microsoft.BotService provider is registered...");

        var checkArgs = $"provider show --namespace Microsoft.BotService --subscription {subscriptionId} --query registrationState --output tsv";
        var checkResult = await _executor.ExecuteAsync("az", checkArgs, captureOutput: true);

        if (checkResult == null)
        {
            _logger.LogError("Failed to execute provider show command - null result");
            return false;
        }

        if (checkResult.Success)
        {
            var state = checkResult.StandardOutput.Trim();
            if (state.Equals("Registered", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Microsoft.BotService provider is already registered");
                return true;
            }
        }

        _logger.LogInformation("Registering Microsoft.BotService provider...");
        var registerArgs = $"provider register --namespace Microsoft.BotService --subscription {subscriptionId} --wait";
        var registerResult = await _executor.ExecuteAsync("az", registerArgs, captureOutput: true);

        if (registerResult == null)
        {
            _logger.LogError("Failed to execute provider register command - null result");
            return false;
        }

        if (registerResult.Success)
        {
            _logger.LogInformation("Microsoft.BotService provider registered successfully");
            return true;
        }

        _logger.LogError("Failed to register Microsoft.BotService provider");
        return false;
    }

    /// <summary>
    /// Get existing user-assigned managed identity (created by createinstance command)
    /// Does NOT create new identities - they must be created beforehand
    /// </summary>
    public async Task<(bool Success, string? ClientId, string? TenantId, string? ResourceId)> GetManagedIdentityAsync(
        string identityName,
        string resourceGroupName,
        string subscriptionId,
        string location)
    {
        _logger.LogDebug("Looking up managed identity: {IdentityName}", identityName);

        // Check if identity exists (suppress error logging for expected "not found")
        var checkArgs = $"identity show --name {identityName} --resource-group {resourceGroupName} --query \"{{clientId:clientId, tenantId:tenantId, id:id}}\" --output json";
        var checkResult = await _executor.ExecuteAsync("az", checkArgs, captureOutput: true, suppressErrorLogging: true);

        if (checkResult == null)
        {
            _logger.LogError("Failed to execute identity show command for {IdentityName} - null result", identityName);
            return (false, null, null, null);
        }

        if (checkResult.Success && !string.IsNullOrWhiteSpace(checkResult.StandardOutput))
        {
            try
            {
                var identity = JsonSerializer.Deserialize<JsonElement>(checkResult.StandardOutput);
                var clientId = identity.GetProperty("clientId").GetString();
                var tenantId = identity.GetProperty("tenantId").GetString();
                var resourceId = identity.GetProperty("id").GetString();

                _logger.LogDebug("Found managed identity");
                _logger.LogDebug("   Client ID: {ClientId}", clientId);
                _logger.LogDebug("   Principal ID will be used for Graph permissions");
                return (true, clientId, tenantId, resourceId);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to parse identity information: {Message}", ex.Message);
                return (false, null, null, null);
            }
        }

        // Identity not found - user needs to create it first
        _logger.LogError("Managed identity '{IdentityName}' not found in resource group '{ResourceGroup}'", identityName, resourceGroupName);
        _logger.LogError("   This identity should be created with 'a365 createinstance' command");
        _logger.LogError("   You can create it manually with: az identity create --name {IdentityName} --resource-group {ResourceGroup} --location {Location}", 
            identityName, resourceGroupName, location);
        return (false, null, null, null);
    }

    /// <summary>
    /// Create or update Azure Bot Service with System-Assigned Managed Identity
    /// </summary>
    public async Task<bool> CreateOrUpdateBotWithSystemIdentityAsync(
        string appServiceName,
        string botName,
        string resourceGroupName,
        string subscriptionId,
        string location,
        string messagingEndpoint,
        string agentDescription,
        string sku)
    {
        _logger.LogInformation("Creating/updating Azure Bot Service with System-Assigned Identity...");
        _logger.LogDebug("   Bot Name: {BotName}", botName);
        _logger.LogDebug("   Messaging Endpoint: {Endpoint}", messagingEndpoint);

        // Get the system-assigned identity from the app service
        var identityArgs = $"webapp identity show --name {appServiceName} --resource-group {resourceGroupName} --query \"{{principalId:principalId, tenantId:tenantId}}\" --output json";
        var identityResult = await _executor.ExecuteAsync("az", identityArgs, captureOutput: true);

        if (identityResult == null)
        {
            _logger.LogError("Failed to execute identity command for app service {AppServiceName} - null result", appServiceName);
            return false;
        }

        if (!identityResult.Success || string.IsNullOrWhiteSpace(identityResult.StandardOutput))
        {
            _logger.LogError("Cannot get system-assigned identity from app service {AppServiceName}", appServiceName);
            return false;
        }

        try
        {
            var identity = JsonSerializer.Deserialize<JsonElement>(identityResult.StandardOutput);
            var principalId = identity.GetProperty("principalId").GetString();
            var tenantId = identity.GetProperty("tenantId").GetString();

            if (string.IsNullOrEmpty(principalId) || string.IsNullOrEmpty(tenantId))
            {
                _logger.LogError("App service {AppServiceName} does not have a system-assigned identity", appServiceName);
                _logger.LogError("   Please enable system-assigned identity first");
                return false;
            }

            _logger.LogDebug("Found system-assigned identity");
            _logger.LogDebug("   Principal ID: {PrincipalId}", principalId);

            // Check if bot exists (suppress error logging for expected "not found")
            var checkArgs = $"bot show --resource-group {resourceGroupName} --name {botName} --subscription {subscriptionId} --query id --output tsv";
            var checkResult = await _executor.ExecuteAsync("az", checkArgs, captureOutput: true, suppressErrorLogging: true);

            if (checkResult.Success && !string.IsNullOrWhiteSpace(checkResult.StandardOutput))
            {
                _logger.LogInformation("Bot already exists, updating configuration...");
                return await UpdateBotAsync(botName, resourceGroupName, subscriptionId, messagingEndpoint);
            }

            // Create new bot with system-assigned identity
            _logger.LogInformation("Creating new Azure Bot with system-assigned identity...");
            
            var createArgs = $"bot create " +
                            $"--resource-group {resourceGroupName} " +
                            $"--name {botName} " +
                            $"--app-type SingleTenant " +
                            $"--appid {principalId} " +
                            $"--tenant-id {tenantId} " +
                            $"--location {location} " +
                            $"--endpoint \"{messagingEndpoint}\" " +
                            $"--description \"{agentDescription}\" " +
                            $"--sku {sku}";

            var createResult = await _executor.ExecuteAsync("az", createArgs, captureOutput: true);

            if (createResult.Success)
            {
                _logger.LogInformation("Azure Bot created successfully with system-assigned identity");
                return true;
            }

            _logger.LogError("Failed to create Azure Bot");
            _logger.LogError("   Error: {Error}", createResult.StandardError);
            return false;
        }
        catch (JsonException ex)
        {
            _logger.LogError("Failed to parse identity information: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Create or update Azure Bot with Agent Blueprint Identity
    /// </summary>
    public async Task<bool> CreateOrUpdateBotWithAgentBlueprintAsync(
        string appServiceName,
        string botName,
        string resourceGroupName,
        string subscriptionId,
        string location,
        string messagingEndpoint,
        string agentDescription,
        string sku,
        string agentBlueprintId)
    {
        _logger.LogInformation("Creating/updating Azure Bot with Agent Blueprint Identity...");
        _logger.LogDebug("   Bot Name: {BotName}", botName);
        _logger.LogDebug("   Messaging Endpoint: {Endpoint}", messagingEndpoint);
        _logger.LogDebug("   Agent Blueprint ID: {AgentBlueprintId}", agentBlueprintId);

        try
        {
            // Get subscription info for tenant ID
            var subscriptionResult = await _executor.ExecuteAsync("az", "account show", captureOutput: true);
            if (subscriptionResult == null)
            {
                _logger.LogError("Failed to execute account show command - null result");
                return false;
            }
            
            if (!subscriptionResult.Success)
            {
                _logger.LogError("Failed to get subscription information for bot creation");
                return false;
            }

            var subscriptionInfo = JsonSerializer.Deserialize<JsonElement>(subscriptionResult.StandardOutput);
            var tenantId = subscriptionInfo.GetProperty("tenantId").GetString();

            if (string.IsNullOrEmpty(tenantId))
            {
                _logger.LogError("Could not determine tenant ID for bot creation");
                return false;
            }

            // Check if bot already exists (suppress error logging - not existing is expected)
            var existingBotResult = await _executor.ExecuteAsync("az", 
                $"bot show --name {botName} --resource-group {resourceGroupName}", 
                captureOutput: true,
                suppressErrorLogging: true);

            if (existingBotResult.Success)
            {
                _logger.LogInformation("Bot '{BotName}' already exists, updating configuration...", botName);
                // Bot exists, we could update it here if needed
                return true;
            }

            // Create new bot with agent blueprint identity
            _logger.LogInformation("Creating new Azure Bot with Agent Blueprint Identity...");
            
            var createArgs = $"bot create " +
                            $"--resource-group {resourceGroupName} " +
                            $"--name {botName} " +
                            $"--app-type SingleTenant " +
                            $"--appid {agentBlueprintId} " +
                            $"--tenant-id {tenantId} " +
                            $"--location {location} " +
                            $"--endpoint \"{messagingEndpoint}\" " +
                            $"--description \"{agentDescription}\" " +
                            $"--sku {sku}";

            var createResult = await _executor.ExecuteAsync("az", createArgs, captureOutput: true);

            if (createResult.Success)
            {
                _logger.LogInformation("Azure Bot created successfully with Agent Blueprint Identity");
                return true;
            }

            _logger.LogError("Failed to create Azure Bot");
            _logger.LogError("   Error: {Error}", createResult.StandardError);
            return false;
        }
        catch (JsonException ex)
        {
            _logger.LogError("Failed to parse tenant information: {Message}", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating bot with agent blueprint: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Create or update Azure Bot Service with User-Assigned Managed Identity
    /// </summary>
    public async Task<bool> CreateOrUpdateBotAsync(
        string managedIdentityName,
        string botName,
        string resourceGroupName,
        string subscriptionId,
        string location,
        string messagingEndpoint,
        string agentDescription,
        string sku)
    {
        _logger.LogInformation("Creating/updating Azure Bot Service...");
        _logger.LogInformation("   Bot Name: {BotName}", botName);
        _logger.LogInformation("   Messaging Endpoint: {Endpoint}", messagingEndpoint);

        // Get existing managed identity (must be created with createinstance command)
        var identityLocation = location == "global" ? "eastus" : location;  // Identity needs actual region
        var (identitySuccess, clientId, tenantId, resourceId) = await GetManagedIdentityAsync(
            managedIdentityName,
            resourceGroupName,
            subscriptionId,
            identityLocation);

        if (!identitySuccess || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(resourceId))
        {
            _logger.LogError("Cannot create bot without a valid managed identity");
            _logger.LogError("   Please create the identity first using 'a365 createinstance' or manually");
            return false;
        }

        // Check if bot exists (suppress error logging for expected "not found")
        var checkArgs = $"bot show --resource-group {resourceGroupName} --name {botName} --subscription {subscriptionId} --query id --output tsv";
        var checkResult = await _executor.ExecuteAsync("az", checkArgs, captureOutput: true, suppressErrorLogging: true);

        if (checkResult.Success && !string.IsNullOrWhiteSpace(checkResult.StandardOutput))
        {
            _logger.LogInformation("Bot already exists, updating configuration...");
            return await UpdateBotAsync(botName, resourceGroupName, subscriptionId, messagingEndpoint);
        }

        // Create new bot
        _logger.LogInformation("Creating new Azure Bot...");
        
        var createArgs = $"bot create " +
                        $"--resource-group {resourceGroupName} " +
                        $"--name {botName} " +
                        $"--app-type UserAssignedMSI " +
                        $"--appid {clientId} " +
                        $"--tenant-id {tenantId} " +
                        $"--msi-resource-id \"{resourceId}\" " +
                        $"--location {location} " +
                        $"--endpoint \"{messagingEndpoint}\" " +
                        $"--description \"{agentDescription}\" " +
                        $"--sku {sku}";

        var createResult = await _executor.ExecuteAsync("az", createArgs, captureOutput: true);

        if (createResult.Success)
        {
            _logger.LogInformation("Azure Bot created successfully");
            return true;
        }

        _logger.LogError("Failed to create Azure Bot");
        _logger.LogError("   Error: {Error}", createResult.StandardError);
        return false;
    }

    /// <summary>
    /// Update Bot messaging endpoint
    /// </summary>
    private async Task<bool> UpdateBotAsync(string botName, string resourceGroupName, string subscriptionId, string messagingEndpoint)
    {
        var updateArgs = $"bot update " +
                        $"--resource-group {resourceGroupName} " +
                        $"--name {botName} " +
                        $"--subscription {subscriptionId} " +
                        $"--endpoint {messagingEndpoint}";

        var updateResult = await _executor.ExecuteAsync("az", updateArgs, captureOutput: true);

        if (updateResult.Success)
        {
            _logger.LogInformation("Bot messaging endpoint updated successfully");
            return true;
        }

        _logger.LogError("Failed to update bot");
        return false;
    }

    /// <summary>
    /// Configure Bot channels (Teams, etc.)
    /// </summary>
    public async Task<bool> ConfigureMsTeamsChannelAsync(string botName, string resourceGroupName)
    {
        _logger.LogDebug("Configuring Microsoft Teams channel...");

        // Check if Teams channel already exists (suppress error logging for expected "not found")
        var checkArgs = $"bot msteams show --resource-group {resourceGroupName} --name {botName}";
        var checkResult = await _executor.ExecuteAsync("az", checkArgs, captureOutput: true, suppressErrorLogging: true);

        if (checkResult.Success)
        {
            _logger.LogDebug("Microsoft Teams channel is already configured");
            return true;
        }

        // Create Teams channel
        var createArgs = $"bot msteams create --resource-group {resourceGroupName} --name {botName}";
        var createResult = await _executor.ExecuteAsync("az", createArgs, captureOutput: true);

        if (createResult.Success)
        {
            _logger.LogInformation("Microsoft Teams channel configured successfully");
            return true;
        }

        _logger.LogError("Failed to configure Microsoft Teams channel");
        return false;
    }

    /// <summary>
    /// Configure Email integration for agent communication via Microsoft Graph API
    /// Note: Email communication will work through the agent's Graph API permissions (Mail.Send, Mail.ReadWrite)
    /// rather than a separate bot email channel
    /// </summary>
    public Task<bool> ConfigureEmailIntegrationAsync(string botName, string resourceGroupName, string? agentUserPrincipalName = null)
    {
        _logger.LogDebug("Configuring Email integration via Microsoft Graph API...");
        
        if (!string.IsNullOrEmpty(agentUserPrincipalName))
        {
            _logger.LogDebug("   Agent Email: {Email}", agentUserPrincipalName);
            _logger.LogDebug("   Email capabilities enabled through Microsoft Graph API");
            _logger.LogDebug("   Required permissions: Mail.Send, Mail.ReadWrite");
            _logger.LogInformation("Email integration configured via Graph API permissions");
            return Task.FromResult(true);
        }
        else
        {
            _logger.LogDebug("   No agent user email provided");
            _logger.LogDebug("   Email integration requires agent user identity with email permissions");
            _logger.LogWarning("Email integration skipped - no agent user email available");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Configure channels based on configuration settings
    /// </summary>
    public async Task<bool> ConfigureChannelsAsync(string botName, string resourceGroupName, bool enableTeams = true, bool enableEmail = false, string? agentUserPrincipalName = null)
    {
        _logger.LogDebug("Configuring bot channels...");
        
        bool teamsSuccess = true;
        if (enableTeams)
        {
            _logger.LogDebug("   Configuring Microsoft Teams channel");
            teamsSuccess = await ConfigureMsTeamsChannelAsync(botName, resourceGroupName);
        }
        else
        {
            _logger.LogDebug("   Teams channel disabled in configuration");
        }
        
        bool emailSuccess = true;
        if (enableEmail)
        {
            _logger.LogDebug("   Configuring Email integration");
            emailSuccess = await ConfigureEmailIntegrationAsync(botName, resourceGroupName, agentUserPrincipalName);
        }
        else
        {
            _logger.LogDebug("   Email integration disabled in configuration");
        }
        
        var allSuccess = teamsSuccess && emailSuccess;
        
        if (allSuccess)
        {
            _logger.LogInformation("All configured channels are working properly");
        }
        else
        {
            _logger.LogWarning("Some channels failed to configure");
        }
        
        return allSuccess;
    }

    /// <summary>
    /// Configure multiple channels (Teams + Email integration) - Legacy method for backward compatibility
    /// </summary>
    public async Task<bool> ConfigureAllChannelsAsync(string botName, string resourceGroupName, string? agentUserPrincipalName = null)
    {
        return await ConfigureChannelsAsync(botName, resourceGroupName, enableTeams: true, enableEmail: true, agentUserPrincipalName);
    }

    /// <summary>
    /// Test Bot Service configuration
    /// </summary>
    public async Task<bool> TestBotConfigurationAsync(string botName, string resourceGroupName)
    {
        _logger.LogInformation("Testing Bot Service configuration...");
        _logger.LogInformation("   Bot Name: {BotName}", botName);
        _logger.LogInformation("   Resource Group: {ResourceGroup}", resourceGroupName);

        try
        {
            // Get bot details to verify configuration
            var showArgs = $"bot show --resource-group {resourceGroupName} --name {botName} --query \"{{endpoint:endpoint,appId:appId}}\" --output json";
            var showResult = await _executor.ExecuteAsync("az", showArgs, captureOutput: true);
            
            if (showResult == null)
            {
                _logger.LogError("Failed to execute bot show command for testing {BotName} - null result", botName);
                return false;
            }
            
            if (showResult.Success && !string.IsNullOrWhiteSpace(showResult.StandardOutput))
            {
                _logger.LogInformation("Bot Service configuration is valid");
                _logger.LogInformation("   Details: {Details}", showResult.StandardOutput.Trim());
                return true;
            }
            
            _logger.LogError("Failed to retrieve bot configuration");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing bot configuration");
            return false;
        }
    }

    /// <summary>
    /// Get Bot configuration details
    /// </summary>
    public async Task<BotConfiguration?> GetBotConfigurationAsync(string resourceGroup, string botName)
    {
        var showArgs = $"bot show --resource-group {resourceGroup} --name {botName} --output json";
        var result = await _executor.ExecuteAsync("az", showArgs, captureOutput: true);

        if (result == null)
        {
            _logger.LogError("Failed to execute bot show command for {BotName} - null result", botName);
            return null;
        }

        if (result.Success && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            try
            {
                var botConfig = JsonSerializer.Deserialize<BotConfiguration>(result.StandardOutput, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return botConfig;
            }
            catch (JsonException ex)
            {
                _logger.LogError("Failed to parse bot configuration: {Message}", ex.Message);
            }
        }

        return null;
    }
}

/// <summary>
/// Bot configuration model
/// </summary>
public class BotConfiguration
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public BotProperties Properties { get; set; } = new();
}

public class BotProperties
{
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconUrl { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string MsaAppId { get; set; } = string.Empty;
    public string DeveloperAppInsightKey { get; set; } = string.Empty;
    public string DeveloperAppInsightsApiKey { get; set; } = string.Empty;
    public string DeveloperAppInsightsApplicationId { get; set; } = string.Empty;
}
