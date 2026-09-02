// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Text.RegularExpressions;

namespace Microsoft.Agents.A365.DevTools.Cli.Constants;

/// <summary>
/// Constants for configuration file paths and names
/// </summary>
public static class ConfigConstants
{
    private const string ProductionAgent365ToolsOrigin = "https://agent365.svc.cloud.microsoft";
    internal const string CreateAgentBlueprintPath = "/agents/botManagement/createAgentBlueprint";
    internal const string DeleteAgentBlueprintPath = "/agents/botManagement/deleteAgentBlueprint";

    /// <summary>
    /// Commercial-cloud OAuth authority host. Used as the fallback when no cloud-specific
    /// override is configured.
    /// </summary>
    public const string DefaultAuthorityHost = "https://login.microsoftonline.com";
    private const string AuthorityHostEnvVar = "A365_AUTHORITY_HOST";
    private const string GraphBaseUrlEnvVar = "A365_GRAPH_BASE_URL";

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
    public const string ProductionCreateEndpointUrl = ProductionAgent365ToolsOrigin + CreateAgentBlueprintPath;

    /// <summary>
    /// Production Agent 365 Tools Delete endpoint URL
    /// </summary>
    public const string ProductionDeleteEndpointUrl = ProductionAgent365ToolsOrigin + DeleteAgentBlueprintPath;

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
        => ResolveDiscoverEndpointUri(environment).AbsoluteUri;

    internal static string GetAgent365ToolsOrigin(string environment)
        => ResolveDiscoverEndpointUri(environment).GetLeftPart(UriPartial.Authority);

    internal static string BuildAgent365ToolsEndpointUrl(string environment, string endpointPath)
        => $"{GetAgent365ToolsOrigin(environment)}{endpointPath}";

    /// <summary>
    /// environment-aware Agent 365 Tools resource Application ID
    /// </summary>
    public static string GetAgent365ToolsResourceAppId(string environment)
        => GetEnvironmentScopedSetting("A365_MCP_APP_ID", environment)
            ?? McpConstants.WorkIQToolsProdAppId;

    /// <summary>
    /// Returns the authority host for the selected cloud environment.
    /// </summary>
    public static string GetAuthorityHost(string environment, string? configAuthorityHost = null)
        => NormalizeAuthorityHost(GetEnvironmentScopedSetting(AuthorityHostEnvVar, environment) ?? configAuthorityHost);

    /// <summary>
    /// Returns the Graph base URL for the selected cloud environment.
    /// </summary>
    public static string GetGraphBaseUrl(string environment, string? configGraphBaseUrl = null)
        => NormalizeGraphBaseUrl(GetEnvironmentScopedSetting(GraphBaseUrlEnvVar, environment) ?? configGraphBaseUrl);

    /// <summary>
    /// Composes an OAuth2 admin-consent endpoint from an already-resolved authority host.
    /// </summary>
    public static string BuildAdminConsentEndpointUrl(string? authorityHost, string tenantId)
        => $"{NormalizeAuthorityHost(authorityHost)}/{tenantId}/v2.0/adminconsent";

    /// <summary>
    /// Returns the OAuth2 token endpoint URL for the given tenant and environment.
    /// </summary>
    public static string GetTokenEndpointUrl(string tenantId, string environment, string? configAuthorityHost = null)
        => BuildTokenEndpointUrl(GetAuthorityHost(environment, configAuthorityHost), tenantId);

    /// <summary>
    /// Composes an OAuth2 token endpoint from an already-resolved authority host.
    /// </summary>
    public static string BuildTokenEndpointUrl(string? authorityHost, string tenantId)
        => $"{NormalizeAuthorityHost(authorityHost)}/{tenantId}/oauth2/v2.0/token";

    internal static string NormalizeAuthorityHost(string? authorityHost)
        => NormalizeHttpsOrigin(authorityHost, DefaultAuthorityHost, "Authority host");

    internal static string NormalizeGraphBaseUrl(string? graphBaseUrl)
        => NormalizeHttpsOrigin(graphBaseUrl, GraphApiConstants.BaseUrl, "Graph base URL");

    /// <summary>
    /// Normalizes an environment key so arbitrary cloud names can map to env vars.
    /// </summary>
    public static string NormalizeEnvironmentKey(string? environment)
    {
        if (string.IsNullOrWhiteSpace(environment))
            return "PROD";

        var normalized = Regex.Replace(environment.Trim(), "[^A-Za-z0-9]", "_").ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "PROD" : normalized;
    }

    private static string? GetEnvironmentScopedSetting(string prefix, string? environment)
        => GetEnvironmentScopedValue(prefix, environment) is { } value
            && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;

    private static string? GetEnvironmentScopedValue(string prefix, string? environment)
        => Environment.GetEnvironmentVariable($"{prefix}_{NormalizeEnvironmentKey(environment)}");

    private static string NormalizeHttpsOrigin(string? value, string fallback, string settingName)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var uri = ParseHttpsUri(candidate, settingName);
        if (!string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/")
        {
            throw new ArgumentException(
                $"{settingName} must be an HTTPS origin without a path, query, fragment, or user info.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static Uri ResolveDiscoverEndpointUri(string environment)
    {
        var configuredEndpoint = GetEnvironmentScopedValue("A365_DISCOVER_ENDPOINT", environment);
        var candidate = configuredEndpoint is null
            ? ProductionDiscoverEndpointUrl
            : configuredEndpoint.Trim();
        var uri = ParseHttpsUri(candidate, "Agent 365 Tools discover endpoint");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "Agent 365 Tools discover endpoint must not contain a query or fragment.");
        }

        return uri;
    }

    private static Uri ParseHttpsUri(string value, string settingName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(
                $"{settingName} must be an absolute HTTPS URL without user info.");
        }

        return uri;
    }
}