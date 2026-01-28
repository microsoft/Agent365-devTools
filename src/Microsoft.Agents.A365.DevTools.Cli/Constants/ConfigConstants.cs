// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Constants;

/// <summary>
/// Constants for configuration file paths and names
/// </summary>
public static class ConfigConstants
{
    /// <summary>
    /// Default static configuration file name (user-managed, version-controlled)
    /// </summary>
    public const string DefaultConfigFileName = "a365.config.json";

    /// <summary>
    /// Default dynamic state file name (CLI-managed, auto-generated)
    /// </summary>
    public const string DefaultStateFileName = "a365.generated.config.json";

    /// <summary>
    /// Example configuration file name for copying
    /// </summary>
    public const string ExampleConfigFileName = "a365.config.example.json";

    /// <summary>
    /// Microsoft Learn documentation URL for Agent 365 CLI setup and usage
    /// </summary>
    public const string Agent365CliDocumentationUrl = "https://learn.microsoft.com/microsoft-agent-365/developer/agent-365-cli";

    /// <summary>
    /// Microsoft Learn documentation URL for custom client app registration
    /// </summary>
    public const string CustomClientAppRegistrationUrl = "https://learn.microsoft.com/microsoft-agent-365/developer/custom-client-app-registration";

    /// <summary>
    /// Production Agent 365 Tools Discover endpoint URL
    /// </summary>
    public const string ProductionDiscoverEndpointUrl = "https://agent365.svc.cloud.microsoft/agents/discoverToolServers";

    /// <summary>
    /// Production Agent 365 Tools Create endpoint URL
    /// </summary>
    public const string ProductionCreateEndpointUrl = "https://agent365.svc.cloud.microsoft/agents/botManagement/createAgentBlueprint";

    /// <summary>
    /// Production Agent 365 Tools Delete endpoint URL
    /// </summary>
    public const string ProductionDeleteEndpointUrl = "https://agent365.svc.cloud.microsoft/agents/botManagement/deleteAgentBlueprint";

    /// <summary>
    /// Messaging Bot API App ID
    /// </summary>
    public const string MessagingBotApiAppId = "5a807f24-c9de-44ee-a3a7-329e88a00ffc";

    /// <summary>
    /// Observability API App ID
    /// </summary>
    public const string ObservabilityApiAppId = "9b975845-388f-4429-889e-eab1ef63949c";

    /// <summary>
    /// Microsoft Dynamics CRM / Power Platform Common Data Service API App ID
    /// Well-known Microsoft API ID for accessing Power Platform connections and Dynamics resources.
    /// Exposes permissions like Connectivity.Connections.Read for Power Platform connection access.
    /// 
    /// Note: This is different from MosConstants.PowerPlatformApiResourceAppId (8578e004-a5c6-46e7-913e-12f58912df43),
    /// which is the MOS-specific Power Platform API used for environment management with EnvironmentManagement.Environments.Read.
    /// </summary>
    public const string PowerPlatformApiAppId = "00000003-0000-0ff1-ce00-000000000000";

    /// <summary>
    /// Production deployment environment
    /// </summary>
    public const string ProductionDeploymentEnvironment = "prd";

    /// <summary>
    /// Production cluster category
    /// </summary>
    public const string ProductionClusterCategory = "prod";

    // Hardcoded default scopes

    /// <summary>
    /// Default Microsoft Graph API scopes for agent identity
    /// </summary>
    public static readonly List<string> DefaultAgentIdentityScopes = new()
    {
        "User.Read.All",
        "Mail.Send",
        "Mail.ReadWrite",
        "Chat.Read",
        "Chat.ReadWrite",
        "Files.Read.All",
        "Sites.Read.All"
    };

    /// <summary>
    /// Default App Service Plan SKU - B1 (Basic tier) for production workloads.
    /// Note: B1 often has zero quota by default in Azure subscriptions.
    /// For development/testing without quota issues, consider F1 (Free tier).
    /// </summary>
    public const string DefaultAppServicePlanSku = "B1";

    /// <summary>
    /// Default Azure location for resource deployment when not specified.
    /// East US is chosen as a widely available region with good quota availability.
    /// </summary>
    public const string DefaultAzureLocation = "eastus";

    /// <summary>
    /// Default Microsoft Graph API scopes for agent application
    /// </summary>
    public static readonly List<string> DefaultAgentApplicationScopes = new()
    {
        "Mail.ReadWrite",       // Read and write user's mailbox
        "Mail.Send",            // Send mail on behalf of user
        "Chat.Read",            // Read user's Teams chats
        "Chat.ReadWrite",       // Read and send Teams chat messages
        "Files.Read.All",       // Read files in OneDrive/SharePoint
        "Sites.Read.All",       // Read SharePoint sites
        "User.Read.All",        // Read all user profiles
        "User.ReadBasic.All",   // Read basic user info
        "Presence.ReadWrite",   // Read/write user presence status
        "AgentInstance.Read.All" // Read agent instance information
    };


    /// <summary>
    /// Get Discover endpoint URL based on environment
    /// </summary>
    public static string GetDiscoverEndpointUrl(string environment)
    {
        // Check for custom endpoint in environment variable first
        var customEndpoint = Environment.GetEnvironmentVariable($"A365_DISCOVER_ENDPOINT_{environment?.ToUpper()}");
        if (!string.IsNullOrEmpty(customEndpoint))
            return customEndpoint;

        // Default to production endpoint
        return environment?.ToLower() switch
        {
            "prod" => ProductionDiscoverEndpointUrl,
            _ => ProductionDiscoverEndpointUrl
        };
    }

    /// <summary>
    /// environment-aware Agent 365 Tools resource Application ID
    /// </summary>
public static string GetAgent365ToolsResourceAppId(string environment)
{
    // Check for custom app ID in environment variable first
    var customAppId = Environment.GetEnvironmentVariable($"A365_MCP_APP_ID_{environment?.ToUpper()}");
    if (!string.IsNullOrEmpty(customAppId))
        return customAppId;

    // Default to production app ID
    return environment?.ToLower() switch
    {
        "prod" => McpConstants.Agent365ToolsProdAppId,
        _ => McpConstants.Agent365ToolsProdAppId
    };
}
}