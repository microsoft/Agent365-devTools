// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

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
    /// Microsoft Learn documentation URL for configuring the messaging endpoint manually
    /// in the Teams Developer Portal. Used as the fallback action item when the M365 Teams
    /// Graph backend configuration is skipped (e.g., during the rollout window).
    /// </summary>
    public const string TeamsDeveloperPortalConfigureEndpointUrl =
        "https://learn.microsoft.com/en-us/microsoft-agent-365/developer/create-instance#1-configure-agent-in-teams-developer-portal";

    /// <summary>
    /// Cutoff date (UTC) after which the MCP Platform Teams Graph rollout is expected to be
    /// complete in all environments. Before this date, a contract-mismatch response is logged
    /// as INFO (rollout in progress). On or after this date, it is logged as WARNING since it
    /// is no longer expected.
    /// TEMPORARY: remove along with <see cref="Models.EndpointRegistrationResult.SkippedDueToRollout"/>
    /// once v1/v2 contract versioning replaces this heuristic.
    /// </summary>
    public static readonly DateTime TeamsGraphRolloutCompleteOnUtc = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Agent 365 Tools Discover endpoint URL (V2)
    /// </summary>

    public const string ProductionDiscoverEndpointUrl = "https://agent365.svc.cloud.microsoft/agents/v2/discoverMCPServers";

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
    /// Messaging Bot API identifier URI (used for admin consent URL construction).
    /// </summary>
    public const string MessagingBotApiIdentifierUri = "https://botapi.skype.com";

    /// <summary>
    /// Observability API App ID
    /// </summary>
    public const string ObservabilityApiAppId = "9b975845-388f-4429-889e-eab1ef63949c";

    /// <summary>
    /// Observability API identifier URI (uses api:// scheme — no public https URI registered).
    /// </summary>
    public const string ObservabilityApiIdentifierUri = "api://9b975845-388f-4429-889e-eab1ef63949c";

    /// <summary>
    /// Messaging Bot API scope used for admin consent URL construction.
    /// Note: the orchestrator grants "Authorization.ReadWrite" + "user_impersonation" via OAuth2
    /// permission grants; this scope name is what the /adminconsent endpoint accepts for the
    /// same resource and maps to the same effective consent.
    /// </summary>
    public const string MessagingBotApiAdminConsentScope = "AgentData.ReadWrite";

    /// <summary>
    /// Observability API scope used in admin consent URLs.
    /// This is the only scope published by the Observability API resource app manifest
    /// that is valid for the /v2.0/adminconsent endpoint.
    /// Note: OtelWrite causes AADSTS650053 in the consent URL flow; OtelWrite is granted
    /// separately via OAuth2PermissionGrants.
    /// </summary>
    public const string ObservabilityApiAdminConsentScope = "Maven.ReadWrite.All";

    /// <summary>
    /// Observability API scope for writing OpenTelemetry data.
    /// Granted to all provisioned agent identities via OAuth2PermissionGrants.
    /// </summary>
    public const string ObservabilityApiOtelWriteScope = "Agent365.Observability.OtelWrite";

    /// <summary>
    /// Delegated scope value exposed on the blueprint app registration to enable
    /// OBO (On-Behalf-Of) callers to acquire tokens scoped to the agent.
    /// </summary>
    public const string BlueprintOboScope = "access_agent_as_user";

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
        "Sites.Read.All",
        "ChannelMessage.Read.All",
        "ChannelMessage.Send",
    };

    /// <summary>
    /// Default Microsoft Graph API scopes for agent application
    /// </summary>
    public static readonly List<string> DefaultAgentApplicationScopes = new()
    {
        "Mail.ReadWrite",
        "Mail.Send",
        "Chat.ReadWrite",
        "User.Read.All",
        "Sites.Read.All",
        "Files.ReadWrite.All",
        "ChannelMessage.Read.All",
        "ChannelMessage.Send",
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
        "prod" => McpConstants.WorkIQToolsProdAppId,
        _ => McpConstants.WorkIQToolsProdAppId
    };
}
}