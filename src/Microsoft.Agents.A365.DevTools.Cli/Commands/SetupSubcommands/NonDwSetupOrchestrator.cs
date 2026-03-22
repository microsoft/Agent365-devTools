// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Orchestrates setup for non-AI Teammate agent (non-digital-worker) deployments.
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
        var rg = config.ResourceGroup;

        var messagingEndpoint = !string.IsNullOrWhiteSpace(config.MessagingEndpoint)
            ? config.MessagingEndpoint
            : config.NeedDeployment && !string.IsNullOrWhiteSpace(config.WebAppName)
                ? $"https://{config.WebAppName}.azurewebsites.net/api/messages"
                : "<messaging-endpoint>";

        logger.LogInformation("Non-DW Setup Plan (dry run — no changes will be made)");
        logger.LogInformation("");

        // App Registration
        logger.LogInformation("  App Registration");
        logger.LogInformation("    [CREATE] App Registration    \"{DisplayName}\"  (multi-tenant)", displayName);
        logger.LogInformation("    [CREATE] Client Secret       expires in 2 years");
        logger.LogInformation("    [CONFIG] API Identifier URI  api://botid-<appId>");
        logger.LogInformation("    [CREATE] Scope               access_as_user");
        logger.LogInformation("    [CONFIG] Pre-authorize       Teams desktop ({TeamsDesktop})", TeamsDesktopMobileClientId);
        logger.LogInformation("                                 Teams web     ({TeamsWeb})", TeamsWebClientId);
        logger.LogInformation("    [CONFIG] API Permissions     Microsoft Graph: {GraphScopes}",
            string.Join(", ", GraphDelegatedPermissions));
        logger.LogInformation("                                 Agent 365 Tools: {A365Scopes}",
            string.Join(", ", Agent365ToolsDelegatedPermissions));
        logger.LogInformation("");

        // Azure Resources
        logger.LogInformation("  Azure Resources");

        if (config.NeedDeployment && !string.IsNullOrWhiteSpace(config.WebAppName))
        {
            var acrName = DeriveAcrName(config.WebAppName);
            logger.LogInformation("    [CREATE] Container Registry  {AcrName}  sku: Basic", acrName);
            logger.LogInformation("    [CREATE] App Service Plan    {PlanName}  sku: {Sku}  Linux",
                config.AppServicePlanName, string.IsNullOrWhiteSpace(config.AppServicePlanSku)
                    ? ConfigConstants.DefaultAppServicePlanSku
                    : config.AppServicePlanSku);
            logger.LogInformation("    [CREATE] Web App             {WebAppName}  Docker Linux", config.WebAppName);
        }
        else
        {
            logger.LogInformation("    [SKIP]   Deployment infrastructure (needDeployment is false)");
        }

        if (config.NeedAzureOpenAI)
        {
            var aoaiName = config.AzureOpenAIName ?? $"{displayName}-aoai";
            var aoaiLocation = config.AzureOpenAILocation ?? config.Location;
            logger.LogInformation("    [CREATE] Azure OpenAI        {AoaiName}  location: {Location}",
                aoaiName, aoaiLocation);
            if (!string.IsNullOrWhiteSpace(config.AzureOpenAIModelDeploymentName))
                logger.LogInformation("    [DEPLOY] Model               {ModelName}", config.AzureOpenAIModelDeploymentName);
        }

        logger.LogInformation("");

        // Register Messaging Endpoint
        logger.LogInformation("  Register Messaging Endpoint");
        logger.LogInformation("    [CREATE] Azure Bot           \"{DisplayName}\"  rg: {ResourceGroup}  sku: F0",
            displayName, rg);
        logger.LogInformation("    [CONFIG] Messaging Endpoint  {Endpoint}", messagingEndpoint);
        logger.LogInformation("    [CREATE] Teams Channel");
        logger.LogInformation("    [CREATE] OAuth Connection    {ConnectionName}", OboConnectionName);
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

        if (candidate.Length < 5)
            candidate = candidate.PadRight(5, '0');

        return candidate.Length > 50 ? candidate[..50] : candidate;
    }
}
