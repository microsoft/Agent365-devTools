// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Shared helper methods for setup subcommands
/// </summary>
internal static class SetupHelpers
{
    /// <summary>
    /// Display verification URLs and next steps after successful setup
    /// </summary>
    public static async Task DisplayVerificationInfoAsync(FileInfo setupConfigFile, ILogger logger)
    {
        try
        {
            logger.LogInformation("Generating verification information...");
            var baseDir = setupConfigFile.DirectoryName ?? Environment.CurrentDirectory;
            var generatedConfigPath = Path.Combine(baseDir, "a365.generated.config.json");
            
            if (!File.Exists(generatedConfigPath))
            {
                logger.LogWarning("Generated config not found - skipping verification info");
                return;
            }

            using var stream = File.OpenRead(generatedConfigPath);
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            logger.LogInformation("");
            logger.LogInformation("Verification URLs and Next Steps:");
            logger.LogInformation("==========================================");

            // Azure Web App URL
            if (root.TryGetProperty("AppServiceName", out var appServiceProp) && !string.IsNullOrWhiteSpace(appServiceProp.GetString()))
            {
                var webAppUrl = $"https://{appServiceProp.GetString()}.azurewebsites.net";
                logger.LogInformation("Agent Web App: {Url}", webAppUrl);
            }

            // Azure Resource Group
            if (root.TryGetProperty("ResourceGroup", out var rgProp) && !string.IsNullOrWhiteSpace(rgProp.GetString()))
            {
                var resourceGroup = rgProp.GetString();
                logger.LogInformation("Azure Resource Group: https://portal.azure.com/#@/resource/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}",
                    root.TryGetProperty("SubscriptionId", out var subProp) ? subProp.GetString() : "{subscription}", 
                    resourceGroup);
            }

            // Entra ID Application
            if (root.TryGetProperty("AgentBlueprintId", out var blueprintProp) && !string.IsNullOrWhiteSpace(blueprintProp.GetString()))
            {
                logger.LogInformation("Entra ID Application: https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/{AppId}",
                    blueprintProp.GetString());
            }

            logger.LogInformation("");
            logger.LogInformation("Next Steps:");
            logger.LogInformation("   1. Review Azure resources in the portal");
            logger.LogInformation("   2. View configuration: a365 config display");
            logger.LogInformation("   3. Create agent instance: a365 create-instance identity");
            logger.LogInformation("   4. Deploy application: a365 deploy app");
            logger.LogInformation("");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not display verification info: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Display comprehensive setup summary showing what succeeded and what failed
    /// </summary>
    public static void DisplaySetupSummary(SetupResults results, ILogger logger)
    {
        logger.LogInformation("");
        logger.LogInformation("==========================================");
        logger.LogInformation("Setup Summary");
        logger.LogInformation("==========================================");
        
        // Show what succeeded
        logger.LogInformation("Completed Steps:");
        if (results.BlueprintCreated)
        {
            logger.LogInformation("  [OK] Agent blueprint created (Blueprint ID: {BlueprintId})", results.BlueprintId ?? "unknown");
        }
        if (results.McpPermissionsConfigured)
            logger.LogInformation("  [OK] MCP server permissions configured");
        if (results.InheritablePermissionsConfigured)
            logger.LogInformation("  [OK] Inheritable permissions configured");
        if (results.BotApiPermissionsConfigured)
            logger.LogInformation("  [OK] Messaging Bot API permissions configured");
        if (results.MessagingEndpointRegistered)
            logger.LogInformation("  [OK] Messaging endpoint registered");
        
        // Show what failed
        if (results.Errors.Count > 0)
        {
            logger.LogInformation("");
            logger.LogInformation("Failed Steps:");
            foreach (var error in results.Errors)
            {
                logger.LogInformation("  [FAILED] {Error}", error);
            }
        }
        
        // Show warnings
        if (results.Warnings.Count > 0)
        {
            logger.LogInformation("");
            logger.LogInformation("Warnings:");
            foreach (var warning in results.Warnings)
            {
                logger.LogInformation("  [WARN] {Warning}", warning);
            }
        }
        
        logger.LogInformation("");
        
        // Overall status
        if (results.HasErrors)
        {
            logger.LogWarning("Setup completed with errors");
            logger.LogInformation("");
            logger.LogInformation("Recovery Actions:");
            
            if (!results.InheritablePermissionsConfigured)
            {
                logger.LogInformation("  - Inheritable Permissions: Run 'a365 setup permissions mcp' to retry");
            }
            
            if (!results.McpPermissionsConfigured)
            {
                logger.LogInformation("  - MCP Permissions: Run 'a365 setup permissions mcp' to retry");
            }
            
            if (!results.BotApiPermissionsConfigured)
            {
                logger.LogInformation("  - Bot API Permissions: Run 'a365 setup permissions bot' to retry");
            }
            
            if (!results.MessagingEndpointRegistered)
            {
                logger.LogInformation("  - Messaging Endpoint: Run 'a365 setup endpoint' to retry");
            }
        }
        else if (results.HasWarnings)
        {
            logger.LogInformation("Setup completed successfully with warnings");
            logger.LogInformation("Review warnings above and take action if needed");
        }
        else
        {
            logger.LogInformation("Setup completed successfully");
            logger.LogInformation("All components configured correctly");
        }
        
        logger.LogInformation("==========================================");
    }

    /// <summary>
    /// Ensure MCP OAuth2 permission grants (admin consent)
    /// </summary>
    public static async Task EnsureMcpOauth2PermissionGrantsAsync(
        GraphApiService graph,
        Agent365Config config,
        string[] scopes,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
            throw new SetupValidationException("AgentBlueprintId (appId) is required.");

        var blueprintSpObjectId = await graph.LookupServicePrincipalByAppIdAsync(config.TenantId, config.AgentBlueprintId, ct);
        if (string.IsNullOrWhiteSpace(blueprintSpObjectId))
        {
            throw new SetupValidationException($"Blueprint Service Principal not found for appId {config.AgentBlueprintId}. " +
                "The service principal may not have propagated yet. Wait a few minutes and retry.");
        }

        var resourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(config.Environment);
        var Agent365ToolsSpObjectId = await graph.LookupServicePrincipalByAppIdAsync(config.TenantId, resourceAppId, ct);
        if (string.IsNullOrWhiteSpace(Agent365ToolsSpObjectId))
        {
            throw new SetupValidationException($"Agent 365 Tools Service Principal not found for appId {resourceAppId}. " +
                $"Ensure the Agent 365 Tools application is available in your tenant for environment: {config.Environment}");
        }

        logger.LogInformation("   - OAuth2 grant: client {ClientId} to resource {ResourceId} scopes [{Scopes}]",
            blueprintSpObjectId, Agent365ToolsSpObjectId, string.Join(' ', scopes));

        var response = await graph.CreateOrUpdateOauth2PermissionGrantAsync(
            config.TenantId, blueprintSpObjectId, Agent365ToolsSpObjectId, scopes, ct);

        if (!response)
        {
            throw new SetupValidationException(
                $"Failed to create/update OAuth2 permission grant from blueprint {config.AgentBlueprintId} to Agent 365 Tools {resourceAppId}. " +
                "This may be due to insufficient permissions. Ensure you have DelegatedPermissionGrant.ReadWrite.All or Application.ReadWrite.All permissions.");
        }
    }

    /// <summary>
    /// Ensure MCP inheritable permissions on blueprint
    /// </summary>
    public static async Task EnsureMcpInheritablePermissionsAsync(
        GraphApiService graph,
        Agent365Config config,
        string[] scopes,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
            throw new SetupValidationException("AgentBlueprintId (appId) is required.");

        var resourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(config.Environment);

        logger.LogInformation("   - Inheritable permissions: blueprint {Blueprint} to resourceAppId {ResourceAppId} scopes [{Scopes}]",
            config.AgentBlueprintId, resourceAppId, string.Join(' ', scopes));

        var (ok, alreadyExists, err) = await graph.SetInheritablePermissionsAsync(
            config.TenantId, config.AgentBlueprintId, resourceAppId, scopes, new List<string>() { "AgentIdentityBlueprint.ReadWrite.All" }, ct);

        if (!ok && !alreadyExists)
        {
            config.InheritanceConfigured = false;
            config.InheritanceConfigError = err;
            throw new SetupValidationException($"Failed to set inheritable permissions: {err}. " +
                "Ensure you have Application.ReadWrite.All permissions and the blueprint supports inheritable permissions.");
        }

        config.InheritanceConfigured = true;
        config.InheritablePermissionsAlreadyExist = alreadyExists;
        config.InheritanceConfigError = null;
    }

    /// <summary>
    /// Register blueprint messaging endpoint
    /// </summary>
    public static async Task RegisterBlueprintMessagingEndpointAsync(
        Agent365Config setupConfig,
        ILogger logger,
        IBotConfigurator botConfigurator)
    {
        // Validate required configuration
        if (string.IsNullOrEmpty(setupConfig.AgentBlueprintId))
        {
            logger.LogError("Agent Blueprint ID not found. Blueprint creation may have failed.");
            throw new SetupValidationException(
                issueDescription: "Agent blueprint was not found – messaging endpoint cannot be registered.",
                errorDetails: new List<string>
                {
                    "AgentBlueprintId is missing from configuration. This usually means the blueprint creation step failed or a365.generated.config.json is out of sync."
                },
                mitigationSteps: new List<string>
                {
                    "Verify that 'a365 setup' completed Step 1 (Agent blueprint creation) without errors.",
                    "Check a365.generated.config.json for 'agentBlueprintId'. If it's missing or incorrect, re-run 'a365 setup'."
                },
                context: new Dictionary<string, string>
                {
                    ["AgentBlueprintId"] = setupConfig.AgentBlueprintId ?? "<null>"
                });
        }

        string messagingEndpoint;
        string endpointName;
        if (setupConfig.NeedDeployment)
        {
            if (string.IsNullOrEmpty(setupConfig.WebAppName))
            {
                logger.LogError("Web App Name not configured in a365.config.json");
                throw new SetupValidationException(
                    issueDescription: "Web App name is required to register a messaging endpoint when needDeployment is 'yes'.",
                    errorDetails: new List<string>
                    {
                        "NeedDeployment is true, but 'webAppName' was not provided in a365.config.json."
                    },
                    mitigationSteps: new List<string>
                    {
                        "Open a365.config.json and ensure 'webAppName' is set to the Azure Web App name.",
                        "If you do not want the CLI to deploy an Azure Web App, set \"needDeployment\": \"no\" and provide \"MessagingEndpoint\" instead.",
                        "Re-run 'a365 setup'."
                    },
                    context: new Dictionary<string, string>
                    {
                        ["needDeployment"] = setupConfig.NeedDeployment.ToString(),
                        ["webAppName"] = setupConfig.WebAppName ?? "<null>"
                    });
            }

            // Generate endpoint name with Azure Bot Service constraints (4-42 chars)
            var baseEndpointName = $"{setupConfig.WebAppName}-endpoint";
            endpointName = EndpointHelper.GetEndpointName(baseEndpointName);

            // Construct messaging endpoint URL from web app name
            messagingEndpoint = $"https://{setupConfig.WebAppName}.azurewebsites.net/api/messages";
        }
        else // Non-Azure hosting
        {
            // No deployment – use the provided MessagingEndpoint
            if (string.IsNullOrWhiteSpace(setupConfig.MessagingEndpoint))
            {
                logger.LogError("MessagingEndpoint must be provided in a365.config.json for non-Azure hosting.");
                throw new SetupValidationException(
                    issueDescription: "Messaging endpoint is required for messaging endpoint registration.",
                    errorDetails: new List<string>
                    {
                        "needDeployment is set to 'no', but MessagingEndpoint was not provided in a365.config.json."
                    },
                    mitigationSteps: new List<string>
                    {
                        "Open your a365.config.json file.",
                        "If you want the CLI to deploy an Azure Web App, set \"needDeployment\": \"yes\" and provide \"webAppName\".",
                        "If your agent is hosted elsewhere, keep \"needDeployment\": \"no\" and add a \"MessagingEndpoint\" with a valid HTTPS URL (e.g. \"https://your-host/api/messages\").",
                        "Re-run 'a365 setup'."
                    }
                );
            }

            if (!Uri.TryCreate(setupConfig.MessagingEndpoint, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                logger.LogError("MessagingEndpoint must be a valid HTTPS URL. Current value: {Endpoint}",
                    setupConfig.MessagingEndpoint);
                throw new SetupValidationException("MessagingEndpoint must be a valid HTTPS URL.");
            }

            messagingEndpoint = setupConfig.MessagingEndpoint;

            // Derive endpoint name from host when there's no WebAppName
            var hostPart = uri.Host.Replace('.', '-');
            var baseEndpointName = $"{hostPart}-endpoint";
            endpointName = EndpointHelper.GetEndpointName(baseEndpointName);
        }

        if (endpointName.Length < 4)
        {
            logger.LogError("Bot endpoint name '{EndpointName}' is too short (must be at least 4 characters)", endpointName);
            throw new SetupValidationException($"Bot endpoint name '{endpointName}' is too short (must be at least 4 characters)");
        }

        logger.LogInformation("   - Registering blueprint messaging endpoint");
        logger.LogInformation("     * Endpoint Name: {EndpointName}", endpointName);
        logger.LogInformation("     * Messaging Endpoint: {Endpoint}", messagingEndpoint);
        logger.LogInformation("     * Using Agent Blueprint ID: {AgentBlueprintId}", setupConfig.AgentBlueprintId);

        var endpointRegistered = await botConfigurator.CreateEndpointWithAgentBlueprintAsync(
            endpointName: endpointName,
            location: setupConfig.Location,
            messagingEndpoint: messagingEndpoint,
            agentDescription: "Agent 365 messaging endpoint for automated interactions",
            agentBlueprintId: setupConfig.AgentBlueprintId);

        if (!endpointRegistered)
        {
            logger.LogError("Failed to register blueprint messaging endpoint");
            throw new SetupValidationException("Blueprint messaging endpoint registration failed");
        }

        // Update Agent365Config state properties
        setupConfig.BotId = setupConfig.AgentBlueprintId;
        setupConfig.BotMsaAppId = setupConfig.AgentBlueprintId;
        setupConfig.BotMessagingEndpoint = messagingEndpoint;
    }
}
