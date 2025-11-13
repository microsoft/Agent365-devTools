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
    /// Production Agent 365 Tools Discover endpoint URL
    /// </summary>
    public const string ProductionDiscoverEndpointUrl = "https://agent365.svc.cloud.microsoft/agents/discoverToolServers";

    /// <summary>
    /// Messaging Bot API App ID
    /// </summary>
    public const string MessagingBotApiAppId = "5a807f24-c9de-44ee-a3a7-329e88a00ffc";


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
    /// Default Microsoft Graph API scopes for agent application
    /// </summary>
    public static readonly List<string> DefaultAgentApplicationScopes = new()
    {
        "Mail.ReadWrite",
        "Mail.Send",
        "Chat.ReadWrite",
        "User.Read.All",
        "Sites.Read.All"
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