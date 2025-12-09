// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// QueryEntra command - Query Microsoft Entra ID for agent-related information
/// </summary>
public class QueryEntraCommand
{
    public static Command CreateCommand(
        ILogger<QueryEntraCommand> logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService)
    {
        var command = new Command("query-entra", "Query Microsoft Entra ID for agent information (scopes, permissions, consent status)");

        // Add subcommands for different query types
        command.AddCommand(CreateBlueprintScopesSubcommand(logger, configService, executor, graphApiService));
        command.AddCommand(CreateInstanceScopesSubcommand(logger, configService, executor));

        return command;
    }

    /// <summary>
    /// Create blueprint-scopes subcommand to query Entra ID for blueprint scopes and consent status
    /// </summary>
    private static Command CreateBlueprintScopesSubcommand(
        ILogger<QueryEntraCommand> logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService)
    {
        var command = new Command("blueprint-scopes", "List configured scopes and consent status for the agent blueprint");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Configuration file path");

        command.AddOption(configOption);

        command.SetHandler(async (config) =>
        {
            try
            {
                logger.LogInformation("Querying Entra ID for agent blueprint inheritable permissions...");
                
                // Load configuration to get the blueprint ID and tenant ID
                var setupConfig = await LoadConfigAsync(config, logger, configService);
                if (setupConfig == null)
                {
                    logger.LogError("Failed to load configuration");
                    Environment.Exit(1);
                    return;
                }

                if (string.IsNullOrEmpty(setupConfig.AgentBlueprintId))
                {
                    logger.LogError("Agent Blueprint ID not found in configuration. Please run 'a365 setup blueprint' first.");
                    logger.LogInformation("The blueprint must be created before you can query its scopes.");
                    Environment.Exit(1);
                    return;
                }

                if (string.IsNullOrEmpty(setupConfig.TenantId))
                {
                    logger.LogError("Tenant ID not found in configuration.");
                    Environment.Exit(1);
                    return;
                }

                logger.LogInformation("Agent Blueprint ID: {BlueprintId}", setupConfig.AgentBlueprintId);
                logger.LogInformation("");

                // Query Microsoft Graph for inheritable permissions
                logger.LogInformation("Querying Microsoft Graph API for blueprint inheritable permissions...");
                
                var inheritablePermissionsJson = await graphApiService.GetBlueprintInheritablePermissionsAsync(
                    setupConfig.AgentBlueprintId, 
                    setupConfig.TenantId);
                
                if (string.IsNullOrEmpty(inheritablePermissionsJson))
                {
                    logger.LogError("Failed to query inheritable permissions from Microsoft Graph API");
                    logger.LogInformation("Make sure you are authenticated and have permission to read agent blueprints.");
                    Environment.Exit(1);
                    return;
                }

                // Parse the inheritable permissions response
                using var responseDoc = JsonDocument.Parse(inheritablePermissionsJson);
                var responseRoot = responseDoc.RootElement;
                
                logger.LogInformation("Blueprint Inheritable Permissions:");
                logger.LogInformation("==================================");

                if (responseRoot.TryGetProperty("value", out var valueElement) && 
                    valueElement.ValueKind == JsonValueKind.Array)
                {
                    var resourcesList = valueElement.EnumerateArray().ToList();
                    if (resourcesList.Any())
                    {
                        foreach (var resourceElement in resourcesList)
                        {
                            var resourceAppId = resourceElement.TryGetProperty("resourceAppId", out var resourceAppIdElement) 
                                ? resourceAppIdElement.GetString() 
                                : "Unknown";
                            
                            var resourceName = GetWellKnownResourceName(resourceAppId);
                            logger.LogInformation("Resource: {ResourceName} ({ResourceAppId})", resourceName, resourceAppId);

                            // Parse inheritable scopes
                            if (resourceElement.TryGetProperty("inheritableScopes", out var inheritableScopesElement))
                            {
                                if (inheritableScopesElement.TryGetProperty("kind", out var kindElement))
                                {
                                    var kind = kindElement.GetString();
                                    logger.LogInformation("  Scope Kind: {Kind}", kind);
                                }

                                if (inheritableScopesElement.TryGetProperty("scopes", out var scopesElement) && 
                                    scopesElement.ValueKind == JsonValueKind.Array)
                                {
                                    logger.LogInformation("  Inheritable Scopes:");
                                    
                                    foreach (var scopeElement in scopesElement.EnumerateArray())
                                    {
                                        var scopeValue = scopeElement.GetString();
                                        if (!string.IsNullOrWhiteSpace(scopeValue))
                                        {
                                            // Split space-separated scopes and display each one
                                            var individualScopes = scopeValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                            foreach (var individualScope in individualScopes)
                                            {
                                                logger.LogInformation("      {Scope}", individualScope);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    logger.LogInformation("      No inheritable scopes found");
                                }
                            }
                            else
                            {
                                logger.LogInformation("      No inheritable scopes configuration found");
                            }
                            
                            logger.LogInformation("");
                        }
                        
                        logger.LogInformation("Total resources with inheritable permissions: {Count}", resourcesList.Count);
                    }
                    else
                    {
                        logger.LogInformation("No inheritable permissions configured for this blueprint.");
                    }
                }
                else
                {
                    logger.LogInformation("No inheritable permissions found for this blueprint.");
                }

                logger.LogInformation("");
                logger.LogInformation("To manage blueprint permissions, visit:");
                logger.LogInformation("https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/{AppId}", setupConfig.AgentBlueprintId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to query blueprint inheritable permissions: {Message}", ex.Message);
                Environment.Exit(1);
            }
        }, configOption);

        return command;
    }

    /// <summary>
    /// Create instance-scopes subcommand to query Entra ID for instance scopes and consent status
    /// </summary>
    private static Command CreateInstanceScopesSubcommand(
        ILogger<QueryEntraCommand> logger,
        IConfigService configService,
        CommandExecutor executor)
    {
        var command = new Command("instance-scopes", "List configured scopes and consent status for the agent instance");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Configuration file path");

        command.AddOption(configOption);

        command.SetHandler(async (config) =>
        {
            try
            {
                logger.LogInformation("Querying Entra ID for agent instance scopes and consent status...");
                
                // Load configuration to get the instance identity
                var instanceConfig = await LoadConfigAsync(config, logger, configService);
                if (instanceConfig == null)
                {
                    logger.LogError("Failed to load configuration");
                    Environment.Exit(1);
                    return;
                }

                // Check for agent identity (could be AgentBlueprintId or specific instance identity)
                string? agenticAppId = null;
                string identityType = "";

                if (!string.IsNullOrEmpty(instanceConfig.AgenticAppId))
                {
                    agenticAppId = instanceConfig.AgenticAppId;
                    identityType = "Agent Identity";
                }
                else if (!string.IsNullOrEmpty(instanceConfig.AgentBlueprintId))
                {
                    agenticAppId = instanceConfig.AgentBlueprintId;
                    identityType = "Agent Blueprint";
                }
                else
                {
                    logger.LogError("No agent identity found in configuration. Please run 'a365 create-instance' first.");
                    logger.LogInformation("An agent identity must be created before you can query OAuth2 grants.");
                    Environment.Exit(1);
                    return;
                }

                logger.LogInformation("{IdentityType} ID: {IdentityId}", identityType, agenticAppId);
                logger.LogInformation("");

                // Query Entra ID for the agent identity and OAuth2 grants
                logger.LogInformation("Querying Microsoft Entra ID for agent identity and OAuth2 grants...");
                
                // Get the service principal details for this application  
                var spResult = await executor.ExecuteAsync("az", 
                    $"ad sp list --filter \"appId eq '{agenticAppId}'\" --query \"[].{{objectId:id,appId:appId,displayName:displayName}}\" --output json");

                if (!spResult.Success)
                {
                    logger.LogError("Failed to query service principal: {Error}", spResult.StandardError);
                    logger.LogInformation("Make sure you are logged in with 'az login' and have permission to read the application.");
                    Environment.Exit(1);
                    return;
                }

                using var spDoc = JsonDocument.Parse(spResult.StandardOutput);
                
                if (spDoc.RootElement.ValueKind != JsonValueKind.Array || spDoc.RootElement.GetArrayLength() == 0)
                {
                    logger.LogWarning("No service principal found for this application. The app may not be installed in this tenant.");
                    Environment.Exit(1);
                    return;
                }
                
                var spElement = spDoc.RootElement[0]; // Get the first (and only) service principal
                var displayName = spElement.TryGetProperty("displayName", out var nameElement) ? nameElement.GetString() : "Unknown";
                var appId = spElement.TryGetProperty("appId", out var appIdElement) ? appIdElement.GetString() : agenticAppId;
                
                logger.LogInformation("Application: {DisplayName}", displayName);
                logger.LogInformation("App ID: {AppId}", appId);
                
                if (!string.IsNullOrEmpty(instanceConfig.AgentUserPrincipalName))
                {
                    logger.LogInformation("Agent User: {AgentUserPrincipalName}", instanceConfig.AgentUserPrincipalName);
                }
                logger.LogInformation("");

                // Query OAuth2 permission grants for this service principal
                logger.LogInformation("OAuth2 Permission Grants (Admin Consented):");
                logger.LogInformation("============================================");
                
                // Use Microsoft Graph API through Azure CLI to get OAuth2 permission grants
                var grantsResult = await executor.ExecuteAsync("az", 
                    $"rest --method GET --url \"https://graph.microsoft.com/v1.0/oauth2PermissionGrants?$filter=clientId eq '{agenticAppId}'\" --output json");

                bool hasGrants = false;
                if (grantsResult.Success && !string.IsNullOrWhiteSpace(grantsResult.StandardOutput))
                {
                    try
                    {
                        using var grantsDoc = JsonDocument.Parse(grantsResult.StandardOutput);
                        if (grantsDoc.RootElement.TryGetProperty("value", out var valueElement) &&
                            valueElement.ValueKind == JsonValueKind.Array && valueElement.GetArrayLength() > 0)
                        {
                            hasGrants = true;
                            
                            foreach (var grantElement in valueElement.EnumerateArray())
                            {
                                var scope = grantElement.TryGetProperty("scope", out var scopeElement) ? scopeElement.GetString() : "Unknown";
                                var resourceId = grantElement.TryGetProperty("resourceId", out var resourceIdElement) ? resourceIdElement.GetString() : "Unknown";
                                
                                // Get the resource display name using Graph API
                                var resourceResult = await executor.ExecuteAsync("az", 
                                    $"rest --method GET --url \"https://graph.microsoft.com/v1.0/servicePrincipals/{resourceId}?$select=displayName,appId\" --output json");
                                
                                string resourceName = "Unknown Resource";
                                string resourceAppId = "Unknown";
                                
                                if (resourceResult.Success)
                                {
                                    try
                                    {
                                        using var resourceDoc = JsonDocument.Parse(resourceResult.StandardOutput);
                                        resourceName = resourceDoc.RootElement.TryGetProperty("displayName", out var resNameElement) ? resNameElement.GetString() ?? "Unknown" : "Unknown";
                                        resourceAppId = resourceDoc.RootElement.TryGetProperty("appId", out var resAppIdElement) ? resAppIdElement.GetString() ?? resourceAppId : resourceAppId;
                                        
                                        // Use well-known names for Microsoft services
                                        var wellKnownName = GetWellKnownResourceName(resourceAppId);
                                        if (wellKnownName != $"Unknown Resource ({resourceAppId})")
                                        {
                                            resourceName = wellKnownName;
                                        }
                                    }
                                    catch
                                    {
                                        // Use fallback if parsing fails
                                    }
                                }
                                
                                logger.LogInformation("Resource: {ResourceName}", resourceName);
                                if (!string.IsNullOrWhiteSpace(scope))
                                {
                                    var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                    foreach (var individualScope in scopes)
                                    {
                                        logger.LogInformation("  {Scope}", individualScope);
                                    }
                                }
                                else
                                {
                                    logger.LogInformation("    No specific scopes granted");
                                }
                                logger.LogInformation("");
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        logger.LogWarning("Failed to parse OAuth2 grants response: {Error}", ex.Message);
                    }
                }

                if (!hasGrants)
                {
                    logger.LogInformation("    No OAuth2 permission grants found");
                    logger.LogInformation("    This means admin consent has not been granted for any API permissions");
                    logger.LogInformation("");
                    logger.LogInformation("To grant admin consent:");
                    logger.LogInformation("  1. Visit the Azure portal: https://portal.azure.com");
                    logger.LogInformation("  2. Go to Entra ID > App registrations");
                    logger.LogInformation("  3. Find your application: {DisplayName}", displayName);
                    logger.LogInformation("  4. Go to API permissions and click 'Grant admin consent'");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to query instance scopes: {Message}", ex.Message);
                Environment.Exit(1);
            }
        }, configOption);

        return command;
    }

    /// <summary>
    /// Load configuration from file using the config service
    /// </summary>
    private static async Task<Agent365Config?> LoadConfigAsync(
        FileInfo config, 
        ILogger<QueryEntraCommand> logger, 
        IConfigService configService)
    {
        try
        {
            return await configService.LoadAsync(config.FullName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load configuration from {Path}: {Message}", config.FullName, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Get well-known resource names for common Microsoft services
    /// </summary>
    private static string GetWellKnownResourceName(string? resourceAppId)
    {
        return resourceAppId switch
        {
            AuthenticationConstants.MicrosoftGraphResourceAppId => "Microsoft Graph",
            "00000002-0000-0000-c000-000000000000" => "Azure Active Directory Graph",
            "797f4846-ba00-4fd7-ba43-dac1f8f63013" => "Azure Service Management",
            "00000001-0000-0000-c000-000000000000" => "Azure ESTS Service",
            _ => $"Unknown Resource ({resourceAppId})"
        };
    }
}