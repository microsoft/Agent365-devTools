// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

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
        if (string.IsNullOrWhiteSpace(ClientAppId))
        {
            errors.Add($"clientAppId is required. This must be a client app you create in your tenant with specific permissions. See {ConfigConstants.Agent365CliDocumentationUrl} for setup instructions.");
        }
        else
        {
            ValidateGuid(ClientAppId, nameof(ClientAppId), errors);
        }

        if (string.IsNullOrWhiteSpace(AgentIdentityDisplayName)) errors.Add("agentIdentityDisplayName is required.");

        // Validate custom blueprint permissions
        if (CustomBlueprintPermissions != null && CustomBlueprintPermissions.Count > 0)
        {
            for (int i = 0; i < CustomBlueprintPermissions.Count; i++)
            {
                var (isValid, permErrors) = CustomBlueprintPermissions[i].Validate();
                if (!isValid)
                {
                    errors.Add($"customBlueprintPermissions[{i}]: {string.Join(", ", permErrors)}");
                }
            }

            // Check for duplicate resourceAppIds
            var duplicates = CustomBlueprintPermissions
                .Where(p => !string.IsNullOrWhiteSpace(p.ResourceAppId))
                .GroupBy(p => p.ResourceAppId.ToLowerInvariant())
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicates.Any())
            {
                errors.Add($"Duplicate resourceAppId found in customBlueprintPermissions: {string.Join(", ", duplicates)}");
            }
        }

        return errors;
    }

    /// <summary>
    /// Minimal validation for the config-free non-DW bootstrap path (--agent-name flow).
    /// Only requires TenantId, ClientAppId, and AgentIdentityDisplayName.
    /// </summary>
    public List<string> ValidateNonDwMinimal()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(TenantId)) errors.Add("tenantId is required.");
        if (string.IsNullOrWhiteSpace(ClientAppId))
            errors.Add($"clientAppId could not be resolved. Ensure an Entra app named \"{AuthenticationConstants.WellKnownClientAppDisplayName}\" exists in your tenant.");
        else
            ValidateGuid(ClientAppId, nameof(ClientAppId), errors);
        if (string.IsNullOrWhiteSpace(AgentIdentityDisplayName)) errors.Add("agentIdentityDisplayName is required.");

        return errors;
    }

    /// <summary>
    /// Helper method to validate GUID format
    /// </summary>
    private static void ValidateGuid(string value, string fieldName, List<string> errors)
    {
        if (!Guid.TryParse(value, out _))
        {
            errors.Add($"{fieldName} must be a valid GUID format.");
        }
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
    /// Target environment for Agent 365 services (test, preprod, prod).
    /// Controls which endpoints are used for Teams Graph API, Agent 365 Tools, etc.
    /// Default: preprod
    /// </summary>
    [JsonPropertyName("environment")]
    public string Environment { get; init; } = "prod";

    /// <summary>
    /// HTTPS messaging endpoint that Bot Framework will call for this agent.
    /// Required when the agent is externally hosted.
    /// </summary>
    [JsonPropertyName("messagingEndpoint")]
    public string MessagingEndpoint { get; init; } = string.Empty;

    /// <summary>
    /// Base URL for Microsoft Graph API.
    /// Override this to target sovereign / government clouds:
    ///   GCC High / DoD : "https://graph.microsoft.us"
    ///   China (21Vianet): "https://microsoftgraph.chinacloudapi.cn"
    /// Defaults to "https://graph.microsoft.com" when omitted.
    /// </summary>
    [JsonPropertyName("graphBaseUrl")]
    public string GraphBaseUrl { get; init; } = Constants.GraphApiConstants.BaseUrl;

    #endregion

    #region Authentication Configuration

    /// <summary>
    /// Client Application ID for interactive authentication with Microsoft Graph.
    /// This must be a client app registration you create in your Entra ID tenant.
    ///
    /// Required delegated permissions are defined in <see cref="Constants.AuthenticationConstants.RequiredClientAppPermissions"/>.
    /// All permissions require admin consent.
    ///
    /// For setup instructions, see the Agent 365 CLI documentation at <see cref="Constants.ConfigConstants.Agent365CliDocumentationUrl"/>.
    /// </summary>
    [JsonPropertyName("clientAppId")]
    public string ClientAppId { get; init; } = string.Empty;

    /// <summary>
    /// Authentication pattern for the agent identity (blueprint agents only).
    /// Accepted values: "obo" (default), "s2s", "both".
    ///   obo  — on-behalf-of; principal-scoped delegated grants; no admin consent needed.
    ///   s2s  — service-to-service; app role assignments on agent identity; Global Admin needed or PowerShell fallback.
    ///   both — delegated grants (OBO) and app permissions (S2S).
    /// Persisted to a365.config.json. Not written to a365.generated.config.json.
    /// </summary>
    [JsonPropertyName("authMode")]
    public string? AuthMode { get; init; }

    #endregion

    #region Azure OpenAI Configuration

    /// <summary>
    /// Name of the Azure OpenAI resource to create (blueprint agents only).
    /// If set and NeedAzureOpenAI is true, setup will provision this resource.
    /// </summary>
    [JsonPropertyName("azureOpenAIName")]
    public string? AzureOpenAIName { get; init; }

    /// <summary>
    /// Azure region for the OpenAI resource.
    /// OpenAI resource availability varies by region.
    /// </summary>
    [JsonPropertyName("azureOpenAILocation")]
    public string? AzureOpenAILocation { get; init; }

    /// <summary>
    /// Name of the model deployment to create inside the Azure OpenAI resource (e.g., "gpt-4.1").
    /// </summary>
    [JsonPropertyName("azureOpenAIModelDeploymentName")]
    public string? AzureOpenAIModelDeploymentName { get; init; }

    /// <summary>
    /// When true, setup will provision an Azure OpenAI resource.
    /// Only relevant for blueprint agent deployments.
    /// </summary>
    [JsonPropertyName("needAzureOpenAI")]
    public bool NeedAzureOpenAI { get; init; }

    #endregion

    #region Agent Configuration

    /// <summary>
    /// Controls which setup and publish flow is used.
    /// true (default) = AI Teammate agent: setup all provisions blueprint and permissions only;
    ///   agent identity SP and Entra user are created separately via 'a365 create-instance'.
    /// false = blueprint-only agent: setup all auto-creates agent identity SP; no Entra user. Two variants:
    ///   - UseBlueprint = false: App Registration + Azure Bot, no blueprint.
    ///   - UseBlueprint = true:  Blueprint-only non-DW flow (Agent Identity Blueprint + Agent Instance).
    /// Can be overridden per-command with the --aiteammate flag.
    /// </summary>
    [JsonPropertyName("aiTeammate")]
    public bool? AiTeammate { get; init; }

    /// <summary>
    /// When true, use the blueprint-based non-DW flow (Agent Identity Blueprint + Agent Instance).
    /// Only meaningful when AiTeammate is false.
    /// Can be overridden per-command with the --use-blueprint flag.
    /// </summary>
    [JsonPropertyName("useBlueprint")]
    public bool? UseBlueprint { get; init; }

    /// <summary>
    /// Returns true when this config represents a blueprint agent deployment.
    /// </summary>
    [JsonIgnore]
    public bool IsBlueprintAgent => AiTeammate == false;

    /// <summary>
    /// Returns true when this config uses the blueprint-based non-DW flow.
    /// </summary>
    [JsonIgnore]
    public bool IsNonDwBlueprint => AiTeammate == false && UseBlueprint == true;

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
    /// Relative or absolute path to the agent project directory for development and publishing.
    /// </summary>
    [JsonPropertyName("deploymentProjectPath")]
    public string DeploymentProjectPath { get; init; } = string.Empty;

    #endregion

    /// <summary>
    /// Gets the endpoint name derived from the MessagingEndpoint host and blueprint ID.
    /// Returns an already-processed name — callers must NOT wrap this in EndpointHelper.GetEndpointName again.
    /// </summary>
    [JsonIgnore]
    public string BotName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(MessagingEndpoint) &&
                Uri.TryCreate(MessagingEndpoint, UriKind.Absolute, out var uri) &&
                !string.IsNullOrWhiteSpace(uri.Host))
            {
                return EndpointHelper.GetEndpointNameFromHost(uri.Host, AgentBlueprintId);
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// Gets the display name for the bot, derived from AgentBlueprintDisplayName.
    /// </summary>
    [JsonIgnore]
    public string BotDisplayName => AgentBlueprintDisplayName ?? string.Empty;

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

    /// <summary>
    /// List of custom API permissions to grant to the agent blueprint.
    /// These permissions are in addition to the standard permissions required for agent operation.
    /// Each custom permission will receive OAuth2 grants and inheritable permissions configuration.
    /// </summary>
    [JsonPropertyName("customBlueprintPermissions")]
    public List<CustomResourcePermission>? CustomBlueprintPermissions { get; init; }

    #endregion

    // ========================================================================
    // DYNAMIC PROPERTIES (get/set) - from a365.generated.config.json
    // CLI-managed, mutable at runtime
    // ========================================================================

    #region App Service State

    /// <summary>
    /// Principal ID of the managed identity. Can be set manually for migration scenarios.
    /// Read by BlueprintSubcommand for Federated Identity Credential creation.
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
    /// Unique identifier for the agent instance registered via the Agent Registry Graph API.
    /// Set by 'a365 publish' for blueprint-based non-DW agents.
    /// </summary>
    [JsonPropertyName("agentInstanceId")]
    public string? AgentInstanceId { get; set; }

    /// <summary>
    /// Unique identifier returned by the AgentX Agent Registration API V2.
    /// Stored separately from agentInstanceId which tracks the Graph agentRegistry instance.
    /// </summary>
    [JsonPropertyName("agentRegistrationId")]
    public string? AgentRegistrationId { get; set; }

    /// <summary>
    /// Azure AD object ID for the agent blueprint application.
    /// Used as authoritative identifier for all blueprint operations to handle cases
    /// where multiple blueprints may exist with the same display name.
    /// </summary>
    [JsonPropertyName("agentBlueprintObjectId")]
    public string? AgentBlueprintObjectId { get; set; }

    /// <summary>
    /// Azure AD object ID for the service principal associated with the agent blueprint.
    /// Required for OAuth2 permission grants and inheritable permissions configuration.
    /// </summary>
    [JsonPropertyName("agentBlueprintServicePrincipalObjectId")]
    public string? AgentBlueprintServicePrincipalObjectId { get; set; }

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

    /// <summary>
    /// Boolean value indicating if the client secret is stored securely (e.g., in Key Vault).
    /// </summary>
    [JsonPropertyName("agentBlueprintClientSecretProtected")]
    public bool AgentBlueprintClientSecretProtected { get; set; }

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
    /// Messaging endpoint URL for the agent (stored in generated config as "messagingEndpoint").
    /// [JsonIgnore] prevents a duplicate-key collision with the static <see cref="MessagingEndpoint"/>
    /// property when Agent365Config is serialized directly via System.Text.Json (both would emit
    /// the same "messagingEndpoint" key). GetGeneratedConfig() uses reflection to read
    /// [JsonPropertyName] independently, so persistence to the generated config file is unaffected.
    /// </summary>
    [JsonIgnore]
    [JsonPropertyName("messagingEndpoint")]
    public string? BotMessagingEndpoint { get; set; }

    #endregion

    #region Azure OpenAI State

    /// <summary>
    /// Endpoint URL for the provisioned Azure OpenAI resource.
    /// Set by setup, consumed by appsettings.generated.json output.
    /// </summary>
    [JsonPropertyName("azureOpenAIEndpoint")]
    public string? AzureOpenAIEndpoint { get; set; }

    /// <summary>
    /// API key for the provisioned Azure OpenAI resource.
    /// </summary>
    [JsonPropertyName("azureOpenAIApiKey")]
    public string? AzureOpenAIApiKey { get; set; }

    #endregion

    #region Consent State

    /// <summary>
    /// Collection of resource consent information for all APIs requiring admin consent.
    /// </summary>
    [JsonPropertyName("resourceConsents")]
    public List<ResourceConsent> ResourceConsents { get; set; } = new();

    /// <summary>
    /// Checks if inheritable permissions are configured for all resources that require them.
    /// Returns true only if all resources with inheritance have it successfully configured.
    /// </summary>
    public bool IsInheritanceConfigured()
    {
        var resourcesWithInheritance = ResourceConsents
            .Where(rc => rc.InheritablePermissionsConfigured.HasValue)
            .ToList();

        if (resourcesWithInheritance.Count == 0)
            return false;

        return resourcesWithInheritance.All(rc => rc.InheritablePermissionsConfigured == true);
    }

    /// <summary>
    /// Checks if inheritable permissions are configured for Bot API resources.
    /// Returns true if any Bot-related resource has inheritable permissions configured.
    /// </summary>
    public bool IsBotInheritanceConfigured()
    {
        var botResources = ResourceConsents
            .Where(rc => rc.ResourceAppId.Equals(ConfigConstants.MessagingBotApiAppId, StringComparison.OrdinalIgnoreCase) ||
                         rc.ResourceAppId.Equals(ConfigConstants.ObservabilityApiAppId, StringComparison.OrdinalIgnoreCase) ||
                         rc.ResourceAppId.Equals(PowerPlatformConstants.PowerPlatformApiResourceAppId, StringComparison.OrdinalIgnoreCase))
            .Where(rc => rc.InheritablePermissionsConfigured.HasValue)
            .ToList();

        if (botResources.Count == 0)
            return false;

        return botResources.All(rc => rc.InheritablePermissionsConfigured == true);
    }

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
    /// Returns the generated configuration with secrets decrypted for display purposes.
    /// This method should ONLY be used for user-facing output, never for persistence.
    /// </summary>
    /// <param name="logger">Logger for decryption warnings/errors</param>
    /// <returns>Dictionary with decrypted secrets suitable for display</returns>
    public Dictionary<string, object?> GetGeneratedConfigForDisplay(Microsoft.Extensions.Logging.ILogger logger)
    {
        var config = GetGeneratedConfig() as Dictionary<string, object?>
            ?? throw new InvalidOperationException("GetGeneratedConfig must return Dictionary<string, object?>");

        // Decrypt agentBlueprintClientSecret if protected
        if (config.TryGetValue("agentBlueprintClientSecret", out var secretObj) &&
            config.TryGetValue("agentBlueprintClientSecretProtected", out var protectedObj) &&
            secretObj is string encryptedSecret &&
            protectedObj is bool isProtected &&
            isProtected)
        {
            var decryptedSecret = Helpers.SecretProtectionHelper.UnprotectSecret(
                encryptedSecret,
                isProtected,
                logger);
            config["agentBlueprintClientSecret"] = decryptedSecret;
        }

        return config;
    }

    /// <summary>
    /// Creates a new Agent365Config instance with the same static properties but updated CustomBlueprintPermissions.
    /// This method handles the complexity of cloning init-only properties when updating custom permissions.
    /// </summary>
    /// <param name="permissions">The updated custom blueprint permissions list</param>
    /// <returns>A new Agent365Config instance with updated permissions</returns>
    public Agent365Config WithCustomBlueprintPermissions(List<CustomResourcePermission>? permissions)
    {
        return new Agent365Config
        {
            TenantId = this.TenantId,
            Environment = this.Environment,
            MessagingEndpoint = this.MessagingEndpoint,
            ClientAppId = this.ClientAppId,
            AuthMode = this.AuthMode,
            AiTeammate = this.AiTeammate,
            UseBlueprint = this.UseBlueprint,
            AzureOpenAIName = this.AzureOpenAIName,
            AzureOpenAILocation = this.AzureOpenAILocation,
            AzureOpenAIModelDeploymentName = this.AzureOpenAIModelDeploymentName,
            NeedAzureOpenAI = this.NeedAzureOpenAI,
            AgentIdentityDisplayName = this.AgentIdentityDisplayName,
            AgentBlueprintDisplayName = this.AgentBlueprintDisplayName,
            AgentUserPrincipalName = this.AgentUserPrincipalName,
            AgentUserDisplayName = this.AgentUserDisplayName,
            ManagerEmail = this.ManagerEmail,
            AgentUserUsageLocation = this.AgentUserUsageLocation,
            DeploymentProjectPath = this.DeploymentProjectPath,
            AgentDescription = this.AgentDescription,
            McpDefaultServers = this.McpDefaultServers,
            CustomBlueprintPermissions = permissions,
        };
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
