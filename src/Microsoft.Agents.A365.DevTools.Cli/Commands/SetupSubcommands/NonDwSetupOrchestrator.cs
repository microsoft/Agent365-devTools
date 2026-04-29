// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Orchestrates setup for blueprint agent deployments.
/// Uses standard App Registration + Azure Bot Service pattern — no Agent Identity Blueprint.
///
/// Phase A (current): dry-run plan output only.
/// Phase B (pending team feedback): full Azure resource provisioning.
/// </summary>
internal static class NonDwSetupOrchestrator
{
    // Teams client app IDs required for SSO pre-authorization (Expose an API)
    internal const string TeamsDesktopMobileClientId = "1fec8e78-bce4-4aaf-ab1b-5451cc387264";
    internal const string TeamsWebClientId = "5e3ce6c0-2b1f-4285-8d4b-75ee78787346";

    // OAuth connection name created on the Azure Bot for OBO token exchange
    internal const string OboConnectionName = "GraphOBoConnection";

    // Microsoft Graph delegated permissions added to the app registration
    internal static readonly string[] GraphDelegatedPermissions =
    [
        "User.Read", "openid", "profile", "email", "offline_access"
    ];

    // Agent 365 Tools delegated permissions added to the app registration
    internal static readonly string[] Agent365ToolsDelegatedPermissions =
    [
        "McpServers.Mail.All", "McpServersMetadata.Read.All", "AgentTools.ListMCPServers.All"
    ];

    /// <summary>
    /// Prints a dry-run plan showing all resources that would be created or configured,
    /// using actual names and values from the loaded config. Makes no API calls.
    /// </summary>
    public static void PrintDryRunPlan(Agent365Config config, ILogger logger)
    {
        var displayName = config.AgentIdentityDisplayName;

        var messagingEndpoint = !string.IsNullOrWhiteSpace(config.MessagingEndpoint)
            ? config.MessagingEndpoint
            : "<messaging-endpoint>";

        logger.LogWarning(
            "Blueprint agent setup (classic App Registration path) is not yet fully implemented. " +
            "Use --use-blueprint for the blueprint-based non-DW setup path.");
        logger.LogInformation("");
        logger.LogInformation("Non-DW Setup Plan (dry run — no changes will be made)");
        logger.LogInformation("");

        // App Registration
        logger.LogInformation("  App Registration");
        logger.LogInformation("    Create App Registration:    \"{DisplayName}\"  (multi-tenant)", displayName);
        logger.LogInformation("    Create Client Secret:       expires in 2 years");
        logger.LogInformation("    Configure API Identifier URI: api://botid-<appId>");
        logger.LogInformation("    Create Scope:               access_as_user");
        logger.LogInformation("    Configure Pre-authorization: Teams desktop ({TeamsDesktop})", TeamsDesktopMobileClientId);
        logger.LogInformation("                                 Teams web     ({TeamsWeb})", TeamsWebClientId);
        logger.LogInformation("    Assign API Permissions:      Microsoft Graph: {GraphScopes}",
            string.Join(", ", GraphDelegatedPermissions));
        logger.LogInformation("                                 Agent 365 Tools: {A365Scopes}",
            string.Join(", ", Agent365ToolsDelegatedPermissions));
        logger.LogInformation("");

        // Azure Resources
        logger.LogInformation("  Azure Resources");

        logger.LogInformation("    Skip Deployment infrastructure: not provisioned by this tool");

        if (config.NeedAzureOpenAI)
        {
            var aoaiName = config.AzureOpenAIName ?? $"{displayName}-aoai";
            var aoaiLocation = config.AzureOpenAILocation ?? "<location>";
            logger.LogInformation("    Create Azure OpenAI:        {AoaiName}  location: {Location}",
                aoaiName, aoaiLocation);
            if (!string.IsNullOrWhiteSpace(config.AzureOpenAIModelDeploymentName))
                logger.LogInformation("    Deploy Model:               {ModelName}", config.AzureOpenAIModelDeploymentName);
        }

        logger.LogInformation("");

        // Register Messaging Endpoint
        logger.LogInformation("  Register Messaging Endpoint");
        logger.LogInformation("    Create Azure Bot:           \"{DisplayName}\"  sku: F0",
            displayName);
        logger.LogInformation("    Configure Messaging Endpoint: {Endpoint}", messagingEndpoint);
        logger.LogInformation("    Create Teams Channel");
        logger.LogInformation("    Create OAuth Connection:    {ConnectionName}", OboConnectionName);
        logger.LogInformation("                                 scopes: api://botid-<appId>/access_as_user");
        logger.LogInformation("                                 tokenExchangeUrl: api://botid-<appId>");
        logger.LogInformation("");

        logger.LogInformation("Run without --dry-run to execute these steps.");
    }

    /// <summary>
    /// Derives an ACR-compatible name from a web app name.
    /// ACR names must be alphanumeric, 5-50 chars, globally unique.
    /// </summary>
    private static string DeriveAcrName(string webAppName)
    {
        var candidate = new string(webAppName
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        // ACR names must start with a letter; prefix 'a' if the first char is a digit.
        if (candidate.Length > 0 && !char.IsLetter(candidate[0]))
            candidate = "a" + candidate;

        if (candidate.Length < 5)
            candidate = candidate.PadRight(5, '0');

        return candidate.Length > 50 ? candidate[..50] : candidate;
    }
}
