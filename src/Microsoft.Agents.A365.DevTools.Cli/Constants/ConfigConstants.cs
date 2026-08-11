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
    /// Microsoft Learn documentation URL for configuring the messaging endpoint manually in
    /// the Teams Developer Portal. Used as the fallback action item whenever automated Teams
    /// Graph backend configuration cannot complete (non-M365 agents, contract-mismatch
    /// responses, ownership/permission failures).
    /// </summary>
    public const string TeamsDeveloperPortalConfigureEndpointUrl =
        "https://learn.microsoft.com/en-us/microsoft-agent-365/developer/create-instance#1-configure-agent-in-teams-developer-portal";

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
    /// Defender API App ID
    /// </summary>
    public const string DefenderApiAppId = "86a21212-634e-4553-b3d6-e477e4c9d9ec";

    /// <summary>
    /// Defender API identifier URI.
    /// </summary>
    public const string DefenderApiIdentifierUri = "https://rtp-a365.ai.defender.microsoft.com";

    /// <summary>
    /// Single source of truth for the Messaging Bot API delegated scope.
    /// The resource SP (appId 5a807f24-c9de-44ee-a3a7-329e88a00ffc) exposes exactly
    /// one delegated scope, "AgentData.ReadWrite". Both the per-resource and combined
    /// /v2.0/adminconsent URL builders and the spec list consumed by
    /// BatchPermissionsOrchestrator must reference this constant — a mismatch causes
    /// the strict /v2.0/adminconsent endpoint to reject the entire URL with
    /// AADSTS650053 (issue #429).
    /// </summary>
    public const string MessagingBotApiAdminConsentScope = "AgentData.ReadWrite";

    /// <summary>
    /// Observability API scope for writing OpenTelemetry data.
    /// Used in admin consent URLs and granted to provisioned agent identities via OAuth2PermissionGrants.
    /// </summary>
    public const string ObservabilityApiOtelWriteScope = "Agent365.Observability.OtelWrite";

    /// <summary>
    /// Defender API app role and delegated scope for the Defender security integration.
    /// Must match the value published on the resource SP.
    /// </summary>
    public const string DefenderApiRealtimeProtectionScope = "RealtimeProtection.Process";

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
    var customAppId = Environment.GetEnvironmentVariable($"A365_MCP_APP_ID_{environment?.ToUpperInvariant()}");
    if (!string.IsNullOrEmpty(customAppId))
        return customAppId;

    return McpConstants.WorkIQToolsProdAppId;
}
}