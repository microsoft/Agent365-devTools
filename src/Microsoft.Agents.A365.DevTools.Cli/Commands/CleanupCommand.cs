// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

public class CleanupCommand
{
    public static Command CreateCommand(
        ILogger<CleanupCommand> logger,
        IConfigService configService,
        IBotConfigurator botConfigurator,
        CommandExecutor executor,
        GraphApiService graphApiService)
    {
        var cleanupCommand = new Command("cleanup", "Clean up ALL resources (blueprint, instance, Azure) - use subcommands for granular cleanup");

        // Add options for default cleanup behavior (when no subcommand is used)
        var configOption = new Option<FileInfo?>(
            new[] { "--config", "-c" },
            "Path to configuration file")
        {
            ArgumentHelpName = "file"
        };

        var verboseOption = new Option<bool>(
            new[] { "--verbose", "-v" },
            description: "Enable verbose logging");

        cleanupCommand.AddOption(configOption);
        cleanupCommand.AddOption(verboseOption);

        // Set default handler for 'a365 cleanup' (without subcommand) - cleans up everything
        cleanupCommand.SetHandler(async (configFile, verbose) =>
        {
            await ExecuteAllCleanupAsync(logger, configService, botConfigurator, executor, graphApiService, configFile);
        }, configOption, verboseOption);

        // Add subcommands for granular control
        cleanupCommand.AddCommand(CreateBlueprintCleanupCommand(logger, configService, executor, graphApiService));
        cleanupCommand.AddCommand(CreateAzureCleanupCommand(logger, configService, botConfigurator, executor));
        cleanupCommand.AddCommand(CreateInstanceCleanupCommand(logger, configService, executor));

        return cleanupCommand;
    }

    private static Command CreateBlueprintCleanupCommand(
        ILogger<CleanupCommand> logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService)
    {
        var command = new Command("blueprint", "Remove Entra ID blueprint application and service principal");
        
        var configOption = new Option<FileInfo?>(
            new[] { "--config", "-c" },
            "Path to configuration file")
        {
            ArgumentHelpName = "file"
        };

        var verboseOption = new Option<bool>(
            new[] { "--verbose", "-v" },
            description: "Enable verbose logging");

        command.AddOption(configOption);
        command.AddOption(verboseOption);

        command.SetHandler(async (configFile, verbose) =>
        {
            try
            {
                logger.LogInformation("Starting blueprint cleanup...");
                
                var config = await LoadConfigAsync(configFile, logger, configService);
                if (config == null) return;
                
                // Configure GraphApiService with custom client app ID if available
                if (!string.IsNullOrWhiteSpace(config.ClientAppId))
                {
                    graphApiService.CustomClientAppId = config.ClientAppId;
                }

                // Check if there's actually a blueprint to clean up
                if (string.IsNullOrEmpty(config.AgentBlueprintId))
                {
                    logger.LogInformation("No blueprint application found to clean up");
                    return;
                }

                logger.LogInformation("");
                logger.LogInformation("Blueprint Cleanup Preview:");
                logger.LogInformation("=============================");
                logger.LogInformation("Will delete Entra ID application: {BlueprintId}", config.AgentBlueprintId);
                logger.LogInformation("  Name: {DisplayName}", config.AgentBlueprintDisplayName);
                logger.LogInformation("");

                Console.Write("Continue with blueprint cleanup? (y/N): ");
                var response = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (response != "y" && response != "yes")
                {
                    logger.LogInformation("Cleanup cancelled by user");
                    return;
                }

                // Delete the agent blueprint using the special Graph API endpoint
                logger.LogInformation("Deleting agent blueprint application...");
                var deleted = await graphApiService.DeleteAgentBlueprintAsync(
                    config.TenantId,
                    config.AgentBlueprintId);

                // Always clear blueprint data from config, even if deletion failed
                // User can delete manually using Portal/PowerShell/Graph Explorer
                logger.LogInformation("");
                logger.LogInformation("Clearing blueprint data from local configuration...");
                
                config.AgentBlueprintId = string.Empty;
                config.AgentBlueprintClientSecret = string.Empty;
                config.ResourceConsents.Clear();
                
                await configService.SaveStateAsync(config);
                logger.LogInformation("Local configuration cleared");
                
                if (deleted)
                {
                    logger.LogInformation("");
                    logger.LogInformation("Blueprint cleanup completed successfully!");
                }
                else
                {
                    logger.LogWarning("");
                    logger.LogWarning("Blueprint deletion failed, but local configuration has been cleared.");
                    logger.LogWarning("Please manually delete the blueprint application using the Azure Portal, PowerShell, or Microsoft Graph Explorer.");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Blueprint cleanup failed");
            }
        }, configOption, verboseOption);

        return command;
    }

    private static Command CreateAzureCleanupCommand(
        ILogger<CleanupCommand> logger,
        IConfigService configService,
        IBotConfigurator botConfigurator,
        CommandExecutor executor)
    {
        var command = new Command("azure", "Remove Azure resources (App Service, App Service Plan)");
        
        var configOption = new Option<FileInfo?>(
            new[] { "--config", "-c" },
            "Path to configuration file")
        {
            ArgumentHelpName = "file"
        };

        var verboseOption = new Option<bool>(
            new[] { "--verbose", "-v" },
            description: "Enable verbose logging");

        command.AddOption(configOption);
        command.AddOption(verboseOption);

        command.SetHandler(async (configFile, verbose) =>
        {
            try
            {
                logger.LogInformation("Starting Azure cleanup...");
                
                var config = await LoadConfigAsync(configFile, logger, configService);
                if (config == null) return;

                logger.LogInformation("");
                logger.LogInformation("Azure Cleanup Preview:");
                logger.LogInformation("=========================");
                logger.LogInformation("    Web App: {WebAppName}", config.WebAppName);
                logger.LogInformation("    App Service Plan: {PlanName}", config.AppServicePlanName);
                if (!string.IsNullOrEmpty(config.BotId))
                    logger.LogInformation("    Azure Bot: {BotId}", config.BotId);
                logger.LogInformation("    Resource Group: {ResourceGroup}", config.ResourceGroup);
                logger.LogInformation("");

                Console.Write("Continue with Azure cleanup? (y/N): ");
                var response = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (response != "y" && response != "yes")
                {
                    logger.LogInformation("Cleanup cancelled by user");
                    return;
                }

                // Azure CLI cleanup commands
                var commandsList = new List<(string, string)>();

                // If WebAppName is configured
                if (config.NeedDeployment)
                {
                    commandsList.Add(($"az webapp delete --name {config.WebAppName} --resource-group {config.ResourceGroup} --subscription {config.SubscriptionId}", "Web App"));
                    // Only add App Service Plan deletion if AppServicePlanName is configured
                    if (!string.IsNullOrWhiteSpace(config.AppServicePlanName))
                    {
                        commandsList.Add(($"az appservice plan delete --name {config.AppServicePlanName} --resource-group {config.ResourceGroup} --subscription {config.SubscriptionId} --yes", "App Service Plan"));
                    }
                }

                // Add bot deletion if bot exists
                if (!string.IsNullOrEmpty(config.BotName))
                {
                    logger.LogInformation("Deleting messaging endpoint registration...");
                    if (string.IsNullOrEmpty(config.AgentBlueprintId))
                    {
                        logger.LogError("Agent Blueprint ID not found. Agent Blueprint ID is required for deleting endpoint registration.");
                    }
                    else
                    {
                        var endpointName = EndpointHelper.GetEndpointName(config.BotName);

                        var endpointRegistered = await botConfigurator.DeleteEndpointWithAgentBlueprintAsync(
                            endpointName,
                            config.Location,
                            config.AgentBlueprintId);

                        if (!endpointRegistered)
                        {
                            logger.LogWarning("Failed to delete blueprint messaging endpoint");
                        }
                    }
                }

                // Check if there are any Azure resources to delete
                if (commandsList.Count == 0)
                {
                    logger.LogInformation("No Azure Web App resources found to clean up.");
                    logger.LogInformation("This agent is configured with an external messaging endpoint: {MessagingEndpoint}",
                        config.MessagingEndpoint ?? "(not configured)");
                }
                else
                {
                    var commands = commandsList.ToArray();

                    foreach (var (cmd, name) in commands)
                    {
                        logger.LogInformation("Deleting {Name}...", name);
                        var parts = cmd.Split(' ', 2);
                        var result = await executor.ExecuteAsync(parts[0], parts[1], captureOutput: true);

                        if (result.ExitCode == 0)
                        {
                            logger.LogInformation("{Name} deleted successfully", name);
                        }
                        else
                        {
                            logger.LogWarning("Failed to delete {Name}: {Error}", name, result.StandardError);
                        }
                    }
                }

                logger.LogInformation("Azure cleanup completed!");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Azure cleanup failed with exception");
            }
        }, configOption, verboseOption);

        return command;
    }

    private static Command CreateInstanceCleanupCommand(
        ILogger<CleanupCommand> logger,
        IConfigService configService,
        CommandExecutor executor)
    {
        var command = new Command("instance", "Remove agent instance identity and user from Entra ID");
        
        var configOption = new Option<FileInfo?>(
            new[] { "--config", "-c" },
            "Path to configuration file")
        {
            ArgumentHelpName = "file"
        };

        var verboseOption = new Option<bool>(
            new[] { "--verbose", "-v" },
            description: "Enable verbose logging");

        command.AddOption(configOption);
        command.AddOption(verboseOption);

        command.SetHandler(async (configFile, verbose) =>
        {
            try
            {
                logger.LogInformation("Starting instance cleanup...");
                
                var config = await LoadConfigAsync(configFile, logger, configService);
                if (config == null) return;

                logger.LogInformation("");
                logger.LogInformation("Instance Cleanup Preview:");
                logger.LogInformation("============================");
                logger.LogInformation("Will delete the following resources:");
                
                if (!string.IsNullOrEmpty(config.AgenticAppId))
                    logger.LogInformation("    Agent Identity Application: {IdentityId}", config.AgenticAppId);
                if (!string.IsNullOrEmpty(config.AgenticUserId))
                    logger.LogInformation("    Agent User: {UserId}", config.AgenticUserId);
                logger.LogInformation("    Generated configuration file");
                logger.LogInformation("");

                Console.Write("Continue with instance cleanup? (y/N): ");
                var response = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (response != "y" && response != "yes")
                {
                    logger.LogInformation("Cleanup cancelled by user");
                    return;
                }

                // Delete agent identity application
                if (!string.IsNullOrEmpty(config.AgenticAppId))
                {
                    logger.LogInformation("Deleting agent identity application...");
                    await executor.ExecuteAsync("az", $"ad app delete --id {config.AgenticAppId}", null, true, false, CancellationToken.None);
                    logger.LogInformation("Agent identity application deleted");
                }

                // Delete agent user
                if (!string.IsNullOrEmpty(config.AgenticUserId))
                {
                    logger.LogInformation("Deleting agent user...");
                    await executor.ExecuteAsync("az", $"ad user delete --id {config.AgenticUserId}", null, true, false, CancellationToken.None);
                    logger.LogInformation("Agent user deleted");
                }

                // Clear instance-related fields from generated config while preserving blueprint data
                var generatedConfigPath = "a365.generated.config.json";
                if (File.Exists(generatedConfigPath))
                {
                    logger.LogInformation("Clearing instance data from generated configuration...");
                    
                    // Load current config
                    var generatedConfigJson = await File.ReadAllTextAsync(generatedConfigPath);
                    var generatedConfig = JsonSerializer.Deserialize<JsonElement>(generatedConfigJson);
                    
                    // Create new config with instance fields cleared
                    var updatedConfig = new Dictionary<string, object?>();
                    
                    // Copy all existing properties
                    foreach (var property in generatedConfig.EnumerateObject())
                    {
                        updatedConfig[property.Name] = JsonSerializer.Deserialize<object>(property.Value);
                    }
                    
                    // Clear instance-specific fields
                    updatedConfig["AgenticAppId"] = null;
                    updatedConfig["AgenticUserId"] = null;
                    updatedConfig["agentUserPrincipalName"] = null;
                    updatedConfig["agentIdentityConsentUrlGraph"] = null;
                    updatedConfig["agentIdentityConsentUrlBlueprint"] = null;
                    updatedConfig["consent1Granted"] = false;
                    updatedConfig["consent3Granted"] = false;
                    updatedConfig["lastUpdated"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
                    
                    // Save updated config
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var updatedJson = JsonSerializer.Serialize(updatedConfig, options);
                    await File.WriteAllTextAsync(generatedConfigPath, updatedJson);
                    
                    logger.LogInformation("Instance data cleared from generated configuration (blueprint data preserved)");
                }
                else
                {
                    logger.LogInformation("No generated configuration file found");
                }
                
                logger.LogInformation("Instance cleanup completed");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Instance cleanup failed: {Message}", ex.Message);
            }
        }, configOption, verboseOption);

        return command;
    }

    // Shared method for complete cleanup logic - used by both default handler and 'all' subcommand
    private static async Task ExecuteAllCleanupAsync(
        ILogger<CleanupCommand> logger,
        IConfigService configService,
        IBotConfigurator botConfigurator,
        CommandExecutor executor,
        GraphApiService graphApiService,
        FileInfo? configFile)
    {
        try
        {
            logger.LogInformation("Starting complete cleanup...");
            
            var config = await LoadConfigAsync(configFile, logger, configService);
            if (config == null) return;
            
            // Configure GraphApiService with custom client app ID if available
            if (!string.IsNullOrWhiteSpace(config.ClientAppId))
            {
                graphApiService.CustomClientAppId = config.ClientAppId;
            }

            logger.LogInformation("");
            logger.LogInformation("Complete Cleanup Preview:");
            logger.LogInformation("============================");
            logger.LogInformation("WARNING: ALL RESOURCES WILL BE DELETED:");
            if (!string.IsNullOrEmpty(config.AgentBlueprintId))
                logger.LogInformation("    Blueprint Application: {BlueprintId}", config.AgentBlueprintId);
            if (!string.IsNullOrEmpty(config.AgenticAppId))
                logger.LogInformation("    Agent Identity Application: {IdentityId}", config.AgenticAppId);
            if (!string.IsNullOrEmpty(config.AgenticUserId))
                logger.LogInformation("    Agent User: {UserId}", config.AgenticUserId);
            if (!string.IsNullOrEmpty(config.WebAppName))
                logger.LogInformation("    Web App: {WebAppName}", config.WebAppName);
            if (!string.IsNullOrEmpty(config.AppServicePlanName))
                logger.LogInformation("    App Service Plan: {PlanName}", config.AppServicePlanName);
            if (!string.IsNullOrEmpty(config.BotName))
                logger.LogInformation("    Azure Messaging Endpoint: {BotName}", config.BotName);
            logger.LogInformation("    Generated configuration file");
            logger.LogInformation("");

            Console.Write("Are you sure you want to DELETE ALL resources? (y/N): ");
            var response = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (response != "y" && response != "yes")
            {
                logger.LogInformation("Cleanup cancelled by user");
                return;
            }
            
            Console.Write("Type 'DELETE' to confirm: ");
            var confirmResponse = Console.ReadLine()?.Trim();
            if (confirmResponse != "DELETE")
            {
                logger.LogInformation("Cleanup cancelled - confirmation not received");
                return;
            }

            logger.LogInformation("Starting complete cleanup...");

            // 1. Delete agent blueprint application
            if (!string.IsNullOrEmpty(config.AgentBlueprintId))
            {
                logger.LogInformation("Deleting agent blueprint application...");
                var deleted = await graphApiService.DeleteAgentBlueprintAsync(
                    config.TenantId,
                    config.AgentBlueprintId);

                if (deleted)
                {
                    logger.LogInformation("Agent blueprint application deleted successfully");
                }
                else
                {
                    logger.LogWarning("Failed to delete agent blueprint application (will continue with other resources)");
                    logger.LogWarning("Local configuration will still be cleared at the end");
                }
            }

            // 2. Delete agent identity application
            if (!string.IsNullOrEmpty(config.AgenticAppId))
            {
                logger.LogInformation("Deleting agent identity application...");

                var deleted = await graphApiService.DeleteAgentIdentityAsync(
                    config.TenantId,
                    config.AgenticAppId);

                if (deleted)
                {
                    logger.LogInformation("Agent identity application deleted successfully");
                }
                else
                {
                    logger.LogWarning("Failed to delete agent identity application (will continue with other resources)");
                    logger.LogWarning("Local configuration will still be cleared at the end");
                }
            }

            // 3. Delete agent user
            if (!string.IsNullOrEmpty(config.AgenticUserId))
            {
                logger.LogInformation("Deleting agent user...");
                await executor.ExecuteAsync("az", $"ad user delete --id {config.AgenticUserId}", null, true, false, CancellationToken.None);
                logger.LogInformation("Agent user deleted");
            }

            // 4. Delete Azure resources
            if (!string.IsNullOrEmpty(config.WebAppName) && !string.IsNullOrEmpty(config.ResourceGroup))
            {
                logger.LogInformation("Deleting Azure resources...");
                
                // Add bot deletion if bot exists
                if (!string.IsNullOrEmpty(config.BotName))
                {
                    logger.LogInformation("Deleting messaging endpoint registration...");
                    if (string.IsNullOrEmpty(config.AgentBlueprintId))
                    {
                        logger.LogError("Agent Blueprint ID not found. Agent Blueprint ID is required for deleting endpoint registration.");
                    }
                    else
                    {
                        var endpointName = EndpointHelper.GetEndpointName(config.BotName);

                        var endpointRegistered = await botConfigurator.DeleteEndpointWithAgentBlueprintAsync(
                            endpointName,
                            config.Location,
                            config.AgentBlueprintId);

                        if (!endpointRegistered)
                        {
                            logger.LogWarning("Failed to delete blueprint messaging endpoint");
                        }
                    }
                }
                
                // Delete Web App
                logger.LogInformation("Deleting Web App: {WebAppName}...", config.WebAppName);
                await executor.ExecuteAsync("az", $"webapp delete --name {config.WebAppName} --resource-group {config.ResourceGroup} --subscription {config.SubscriptionId}", null, true, false, CancellationToken.None);
                logger.LogInformation("Web App deleted");
                
                // Wait for web app deletion to complete before deleting app service plan
                logger.LogInformation("Waiting for web app deletion to complete...");
                var maxRetries = 30; // 30 seconds max wait
                var retryCount = 0;
                var webAppDeleted = false;
                
                while (retryCount < maxRetries && !webAppDeleted)
                {
                    await Task.Delay(1000); // Wait 1 second
                    var checkResult = await executor.ExecuteAsync("az", 
                        $"webapp show --name {config.WebAppName} --resource-group {config.ResourceGroup} --subscription {config.SubscriptionId}", 
                        null, false, true, CancellationToken.None); // suppressErrorOutput = true to avoid logging expected errors
                    
                    if (checkResult.ExitCode != 0) // Resource not found = successfully deleted
                    {
                        webAppDeleted = true;
                        logger.LogInformation("Web app deletion confirmed");
                    }
                    retryCount++;
                }
                
                // Delete App Service Plan after web app is gone (with retry for conflicts)
                if (!string.IsNullOrEmpty(config.AppServicePlanName))
                {
                    logger.LogInformation("Deleting App Service Plan: {PlanName}...", config.AppServicePlanName);
                    
                    var planDeleted = false;
                    var planRetries = 5;
                    for (var i = 0; i < planRetries && !planDeleted; i++)
                    {
                        if (i > 0)
                        {
                            logger.LogInformation("Retrying app service plan deletion (attempt {Attempt}/{Max})...", i + 1, planRetries);
                            await Task.Delay(3000); // Wait 3 seconds between retries
                        }
                        
                        var planResult = await executor.ExecuteAsync("az", 
                            $"appservice plan delete --name {config.AppServicePlanName} --resource-group {config.ResourceGroup} --subscription {config.SubscriptionId} --yes", 
                            null, false, true, CancellationToken.None); // suppressErrorOutput to avoid logging conflict errors
                        
                        if (planResult.ExitCode == 0)
                        {
                            planDeleted = true;
                            logger.LogInformation("App Service Plan deleted");
                        }
                    }
                    
                    if (!planDeleted)
                    {
                        logger.LogWarning("App Service Plan deletion may not have completed successfully (conflict errors). It may need manual cleanup.");
                    }
                }
                
                logger.LogInformation("Azure resources deleted");
            }

            // 5. Backup and delete generated config file
            var generatedConfigPath = "a365.generated.config.json";
            if (File.Exists(generatedConfigPath))
            {
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var backupPath = $"a365.generated.config.backup-{timestamp}.json";
                
                logger.LogInformation("Backing up generated configuration to: {BackupPath}", backupPath);
                File.Copy(generatedConfigPath, backupPath);
                
                logger.LogInformation("Deleting generated configuration file...");
                File.Delete(generatedConfigPath);
                logger.LogInformation("Generated configuration deleted (backup saved)");
            }

            logger.LogInformation("Complete cleanup finished successfully!");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Complete cleanup failed: {Message}", ex.Message);
        }
    }

    private static async Task<Agent365Config?> LoadConfigAsync(
        FileInfo? configFile,
        ILogger<CleanupCommand> logger,
        IConfigService configService)
    {
        try
        {
            var configPath = configFile?.FullName ?? "a365.config.json";
            var config = await configService.LoadAsync(configPath);
            logger.LogInformation("Loaded configuration successfully from {ConfigFile}", configPath);
            return config;
        }
        catch (FileNotFoundException ex)
        {
            logger.LogError("Configuration file not found: {Message}", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load configuration: {Message}", ex.Message);
            return null;
        }
    }
}