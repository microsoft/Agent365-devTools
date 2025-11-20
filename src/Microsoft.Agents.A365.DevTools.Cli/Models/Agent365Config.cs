// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Unified configuration model for Agent 365 CLI.
/// Merges static configuration (from a365.config.json) and dynamic state (from a365.generated.config.json).
/// 
/// DESIGN PATTERN: Hybrid Merged Model (Option C)
/// - Static properties use 'init' (immutable after construction, from a365.config.json)
/// - Dynamic properties use 'get; set' (mutable at runtime, from a365.generated.config.json)
/// - ConfigService handles merge (load) and split (save) logic
/// </summary>
public class Agent365Config
{
    /// <summary>
    /// Validates the configuration. Returns a list of error messages if invalid, or empty if valid.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(TenantId)) errors.Add("tenantId is required.");

        if (NeedWebAppDeployment)
        {
            if (string.IsNullOrWhiteSpace(SubscriptionId)) errors.Add("subscriptionId is required.");
            if (string.IsNullOrWhiteSpace(ResourceGroup)) errors.Add("resourceGroup is required.");
            if (string.IsNullOrWhiteSpace(Location)) errors.Add("location is required.");
            if (string.IsNullOrWhiteSpace(AppServicePlanName)) errors.Add("appServicePlanName is required.");
            if (string.IsNullOrWhiteSpace(WebAppName)) errors.Add("webAppName is required.");
        }
        else
        {
            // Non-Azure hosting
            if (string.IsNullOrWhiteSpace(MessagingEndpoint))
                errors.Add("messagingEndpoint is required when needWebAppDeployment is 'no'.");
        }

        if (string.IsNullOrWhiteSpace(AgentIdentityDisplayName)) errors.Add("agentIdentityDisplayName is required.");
        if (string.IsNullOrWhiteSpace(DeploymentProjectPath)) errors.Add("deploymentProjectPath is required.");

        // agentIdentityScopes and agentApplicationScopes are now hardcoded defaults
        // botName and botDisplayName are now derived, not required in config
        // Add more validation as needed (e.g., GUID format, allowed values, etc.)
        return errors;
    }
    // ========================================================================
    // STATIC PROPERTIES (init-only) - from a365.config.json
    // Developer-managed, immutable after construction
    // ========================================================================

    #region Azure Configuration

    /// <summary>
    /// Azure AD Tenant ID where resources will be created.
    /// </summary>
    [JsonPropertyName("tenantId")]
    public string TenantId { get; init; } = string.Empty;

    /// <summary>
    /// Azure Subscription ID for resource deployment.
    /// </summary>
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; init; } = string.Empty;

    /// <summary>
    /// Azure Resource Group name where all resources will be deployed.
    /// </summary>
    [JsonPropertyName("resourceGroup")]
    public string ResourceGroup { get; init; } = string.Empty;

    /// <summary>
    /// Azure region for resource deployment (e.g., "eastus", "westus2").
    /// </summary>
    [JsonPropertyName("location")]
    public string Location { get; init; } = string.Empty;

    /// <summary>
    /// Target environment for Agent 365 services (test, preprod, prod).
    /// Controls which endpoints are used for Teams Graph API, Agent 365 Tools, etc.
    /// Default: preprod
    /// </summary>
    [JsonPropertyName("environment")]
    public string Environment { get; init; } = "preprod";

    /// <summary>
    /// For External hosting, this is the HTTPS messaging endpoint that Bot Framework will call.
    /// For AzureAppService, this is optional; the CLI derives the endpoint from webAppName.
    /// </summary>
    [JsonPropertyName("messagingEndpoint")]
    public string? MessagingEndpoint { get; init; }

    /// <summary>
    /// Whether the CLI should create and deploy an Azure Web App for this agent.
    /// Backed by the 'needDeployment' config value:
    /// - "yes" (default) => CLI provisions App Service + MSI, a365 deploy app is active.
    /// - "no"  => CLI does NOT create a web app; a365 deploy app is a no-op and botMessagingEndpoint must be provided.
    /// </summary>
    [JsonPropertyName("needDeployment")]
    public string NeedDeployment { get; init; } = "yes";

    #endregion

    #region App Service Configuration

    /// <summary>
    /// Name of the App Service Plan for hosting the agent web app.
    /// </summary>
    [JsonPropertyName("appServicePlanName")]
    public string AppServicePlanName { get; init; } = string.Empty;

    /// <summary>
    /// App Service Plan SKU/pricing tier (e.g., "B1", "S1", "P1v2").
    /// </summary>
    [JsonPropertyName("appServicePlanSku")]
    public string AppServicePlanSku { get; init; } = "B1";

    /// <summary>
    /// Name of the Azure Web App (must be globally unique).
    /// </summary>
    [JsonPropertyName("webAppName")]
    public string WebAppName { get; init; } = string.Empty;

    #endregion

    #region Agent Configuration

    /// <summary>
    /// Display name for the agent identity in Azure AD.
    /// </summary>
    [JsonPropertyName("agentIdentityDisplayName")]
    public string AgentIdentityDisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Display name for the agent blueprint application.
    /// Used for manifest updates and Teams app registration.
    /// </summary>
    [JsonPropertyName("agentBlueprintDisplayName")]
    public string? AgentBlueprintDisplayName { get; init; }

    /// <summary>
    /// User Principal Name (UPN) for the agentic user to be created in Azure AD.
    /// </summary>
    [JsonPropertyName("agentUserPrincipalName")]
    public string? AgentUserPrincipalName { get; init; }

    /// <summary>
    /// Display name for the agentic user to be created in Azure AD.
    /// </summary>
    [JsonPropertyName("agentUserDisplayName")]
    public string? AgentUserDisplayName { get; init; }

    /// <summary>
    /// Email address of the manager for the agentic user.
    /// </summary>
    [JsonPropertyName("managerEmail")]
    public string? ManagerEmail { get; init; }

    /// <summary>
    /// Two-letter country code for the agentic user's usage location (required for license assignment).
    /// </summary>
    [JsonPropertyName("agentUserUsageLocation")]
    public string AgentUserUsageLocation { get; init; } = string.Empty;

    /// <summary>
    /// List of Microsoft Graph API scopes required by the agent identity.
    /// Hardcoded defaults - not user-configurable.
    /// </summary>
    [JsonIgnore]
    public List<string> AgentIdentityScopes => ConfigConstants.DefaultAgentIdentityScopes;

    /// <summary>
    /// Additional Graph API scopes required by the agent application (different from identity scopes).
    /// Hardcoded defaults - not user-configurable.
    /// </summary>
    [JsonIgnore]
    public List<string> AgentApplicationScopes => ConfigConstants.DefaultAgentApplicationScopes;

    /// <summary>
    /// Relative or absolute path to the agent project directory for deployment.
    /// </summary>
    [JsonPropertyName("deploymentProjectPath")]
    public string DeploymentProjectPath { get; init; } = string.Empty;

    #endregion

    // BotName and BotDisplayName are now derived properties
    /// <summary>
    /// Gets the internal name for the endpoint registration.
    /// - For AzureAppService, derived from WebAppName.
    /// - For non-Azure hosting, derived from BotMessagingEndpoint host if possible.
    /// </summary>
    [JsonIgnore]
    public string BotName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(WebAppName))
            {
                return $"{WebAppName}-endpoint";
            }

            if (!string.IsNullOrWhiteSpace(MessagingEndpoint) &&
                Uri.TryCreate(MessagingEndpoint, UriKind.Absolute, out var uri))
            {
                return $"{uri.Host.Replace('.', '-')}-endpoint";
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// Whether the CLI should perform web app deployment for the agent.
    /// </summary>
    [JsonIgnore]
    public bool NeedWebAppDeployment => !string.Equals(NeedDeployment, "no", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the display name for the bot, derived from AgentBlueprintDisplayName or WebAppName.
    /// </summary>
    [JsonIgnore]
    public string BotDisplayName => !string.IsNullOrWhiteSpace(AgentBlueprintDisplayName) ? AgentBlueprintDisplayName! : WebAppName;

    #region Bot Configuration

    /// <summary>
    /// Description of the agent's capabilities.
    /// </summary>
    [JsonPropertyName("agentDescription")]
    public string? AgentDescription { get; init; }

    #endregion

    #region Channel Configuration

    /// <summary>
    /// Enable Teams channel for the bot.
    /// Hardcoded default - not user-configurable.
    /// </summary>
    [JsonIgnore]
    public bool EnableTeamsChannel => true;

    /// <summary>
    /// Enable Email channel for the bot.
    /// Hardcoded default - not user-configurable.
    /// </summary>
    [JsonIgnore]
    public bool EnableEmailChannel => true;

    /// <summary>
    /// Enable Graph API registration for the agent.
    /// Hardcoded default - not user-configurable.
    /// </summary>
    [JsonIgnore]
    public bool EnableGraphApiRegistration => true;

    #endregion

    #region MCP Configuration

    /// <summary>
    /// List of default MCP server configurations to enable.
    /// </summary>
    [JsonPropertyName("mcpDefaultServers")]
    public List<McpServerConfig>? McpDefaultServers { get; init; }

    #endregion

    // ========================================================================
    // DYNAMIC PROPERTIES (get/set) - from a365.generated.config.json
    // CLI-managed, mutable at runtime
    // ========================================================================

    #region App Service State

    /// <summary>
    /// Principal ID of the managed identity assigned to the App Service.
    /// </summary>
    [JsonPropertyName("managedIdentityPrincipalId")]
    public string? ManagedIdentityPrincipalId { get; set; }

    #endregion

    #region Agent State

    /// <summary>
    /// Unique identifier for the agent blueprint created during setup.
    /// </summary>
    [JsonPropertyName("agentBlueprintId")]
    public string? AgentBlueprintId { get; set; }

    /// <summary>
    /// Azure AD application/identity ID for the agentic app.
    /// </summary>
    [JsonPropertyName("AgenticAppId")]
    public string? AgenticAppId { get; set; }

    /// <summary>
    /// User ID for the agentic user created during setup.
    /// </summary>
    [JsonPropertyName("AgenticUserId")]
    public string? AgenticUserId { get; set; }

    /// <summary>
    /// Client secret for the agent blueprint application.
    /// NOTE: This is sensitive data - consider using Azure Key Vault in production.
    /// </summary>
    [JsonPropertyName("agentBlueprintClientSecret")]
    public string? AgentBlueprintClientSecret { get; set; }

    #endregion

    #region Bot State

    /// <summary>
    /// Bot Framework registration ID.
    /// </summary>
    [JsonPropertyName("botId")]
    public string? BotId { get; set; }

    /// <summary>
    /// Microsoft App ID (AAD App ID) for the bot.
    /// </summary>
    [JsonPropertyName("botMsaAppId")]
    public string? BotMsaAppId { get; set; }

    /// <summary>
    /// Messaging endpoint URL for the bot.
    /// </summary>
    [JsonPropertyName("botMessagingEndpoint")]
    public string? BotMessagingEndpoint { get; set; }

    #endregion

    #region Consent State

    /// <summary>
    /// Status of admin consent for the agent identity.
    /// </summary>
    [JsonPropertyName("consentStatus")]
    public string? ConsentStatus { get; set; }

    /// <summary>
    /// Timestamp when consent was granted.
    /// </summary>
    [JsonPropertyName("consentTimestamp")]
    public DateTime? ConsentTimestamp { get; set; }

    /// <summary>
    /// Graph API consent URL for admin consent flow.
    /// </summary>
    [JsonPropertyName("consentUrlGraph")]
    public string? ConsentUrlGraph { get; set; }

    /// <summary>
    /// Connectivity consent URL for admin consent flow.
    /// </summary>
    [JsonPropertyName("consentUrlConnectivity")]
    public string? ConsentUrlConnectivity { get; set; }

    /// <summary>
    /// Whether the first consent (Graph API) has been granted.
    /// </summary>
    [JsonPropertyName("consent1Granted")]
    public bool Consent1Granted { get; set; }

    /// <summary>
    /// Whether the second consent (connectivity) has been granted.
    /// </summary>
    [JsonPropertyName("consent2Granted")]
    public bool Consent2Granted { get; set; }

    /// <summary>
    /// Whether inheritable permissions already exist in the tenant.
    /// </summary>
    [JsonPropertyName("inheritablePermissionsAlreadyExist")]
    public bool InheritablePermissionsAlreadyExist { get; set; }

    /// <summary>
    /// Whether inheritance mode setup was successful.
    /// </summary>
    [JsonPropertyName("inheritanceConfigured")]
    public bool InheritanceConfigured { get; set; }

    /// <summary>
    /// Error message if inheritance mode setup failed.
    /// </summary>
    [JsonPropertyName("inheritanceConfigError")]
    public string? InheritanceConfigError { get; set; }

    #endregion

    #region MCP State

    #endregion

    #region Deployment State

    /// <summary>
    /// Timestamp of the most recent deployment.
    /// </summary>
    [JsonPropertyName("deploymentLastTimestamp")]
    public DateTime? DeploymentLastTimestamp { get; set; }

    /// <summary>
    /// Status of the most recent deployment.
    /// </summary>
    [JsonPropertyName("deploymentLastStatus")]
    public string? DeploymentLastStatus { get; set; }

    /// <summary>
    /// Git commit hash of the last deployed code.
    /// </summary>
    [JsonPropertyName("deploymentLastCommitHash")]
    public string? DeploymentLastCommitHash { get; set; }

    /// <summary>
    /// Build identifier from the deployment system.
    /// </summary>
    [JsonPropertyName("deploymentLastBuildId")]
    public string? DeploymentLastBuildId { get; set; }

    #endregion

    #region Metadata

    /// <summary>
    /// Timestamp when this configuration was last updated by the CLI.
    /// </summary>
    [JsonPropertyName("lastUpdated")]
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// Version of the CLI tool that last modified this file.
    /// </summary>
    [JsonPropertyName("cliVersion")]
    public string? CliVersion { get; set; }

    #endregion

    #region Workflow State

    /// <summary>
    /// Whether the instance creation workflow has completed.
    /// </summary>
    [JsonPropertyName("completed")]
    public bool Completed { get; set; }

    /// <summary>
    /// Timestamp when the instance creation workflow completed.
    /// </summary>
    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    #endregion

    // ========================================================================
    // CONFIGURATION VIEW METHODS
    // ========================================================================

    /// <summary>
    /// Returns an object containing only the static configuration fields (init-only properties) that should be persisted to a365.config.json.
    /// These are the user-configured, immutable fields.
    /// </summary>
    public object GetStaticConfig()
    {
        var result = new Dictionary<string, object?>();
        var properties = GetType().GetProperties();
        
        foreach (var prop in properties)
        {
            // Check if property has init-only setter (static config)
            if (prop.SetMethod?.ReturnParameter?.GetRequiredCustomModifiers()
                .Any(t => t.Name == "IsExternalInit") == true)
            {
                var jsonAttr = prop.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>();
                var jsonName = jsonAttr?.Name ?? prop.Name;
                var value = prop.GetValue(this);
                
                // Only include non-null/non-empty values to keep config clean
                if (value != null && (value is not string str || !string.IsNullOrEmpty(str)))
                {
                    result[jsonName] = value;
                }
            }
        }
        
        return result;
    }

    /// <summary>
    /// Returns an object containing only the generated/runtime configuration fields (get;set properties) that should be persisted to a365.generated.config.json.
    /// These are the dynamic, mutable fields managed by the CLI.
    /// </summary>
    public object GetGeneratedConfig()
    {
        var result = new Dictionary<string, object?>();
        var properties = GetType().GetProperties();
        
        foreach (var prop in properties)
        {
            // Check if property has regular setter (generated config) - not init-only
            if (prop.CanWrite && prop.SetMethod?.ReturnParameter?.GetRequiredCustomModifiers()
                .Any(t => t.Name == "IsExternalInit") != true)
            {
                var jsonAttr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
                var jsonName = jsonAttr?.Name ?? prop.Name;
                var value = prop.GetValue(this);
                
                // Only include non-null/non-empty values to keep config clean
                if (value != null && (value is not string str || !string.IsNullOrEmpty(str)))
                {
                    result[jsonName] = value;
                }
            }
        }
        
        return result;
    }

    /// <summary>
    /// Returns the full configuration object with all fields (both static and generated).
    /// This represents the complete merged view of the configuration.
    /// </summary>
    public Agent365Config GetFullConfig()
    {
        return this;
    }
}

// ============================================================================
// Service Helper Classes
// ============================================================================
// These are internal DTOs used by various services for specific operations.
// They are not part of the unified configuration file format.

/// <summary>
/// Internal DTO for deployment operations - supports multi-platform deployments
/// </summary>
public class DeploymentConfiguration
{
    // Universal properties
    public string ResourceGroup { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string ProjectPath { get; set; } = string.Empty;
    public string DeploymentZip { get; set; } = "app.zip";
    public string PublishOutputPath { get; set; } = "publish";
    
    // Platform-specific (optional, auto-detected if null)
    public ProjectPlatform? Platform { get; set; }
    
    // Legacy properties (kept for backward compatibility)
    public string ProjectFile { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = "8.0";
    public string BuildConfiguration { get; set; } = "Release";
    public PublishOptions PublishOptions { get; set; } = new();
}

/// <summary>
/// Publish options for deployment
/// </summary>
public class PublishOptions
{
    public bool SelfContained { get; set; } = true;
    public string Runtime { get; set; } = "win-x64";
    public string OutputPath { get; set; } = "./publish";
}

/// <summary>
/// Internal DTO for ATG (Agent Tooling Gateway) configuration operations
/// </summary>
public class AtgConfiguration
{
    public string ResourceGroup { get; set; } = string.Empty;
    public string AppServiceName { get; set; } = string.Empty;
    public string Agent365ToolsUrl { get; set; } = string.Empty;
    public List<McpServerConfig> McpServers { get; set; } = new();
    public List<string> ToolsServers { get; set; } = new();
    public string Agent365ToolsEndpoint { get; set; } = string.Empty;
}