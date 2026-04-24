// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

public class CleanupCommand
{
    private const string AgenticUsersKey = "agentic users";
    private const string IdentitySpsKey = "identity SPs";

    /// <summary>
    /// Returns the base requirement checks for cleanup operations:
    /// Azure authentication only.
    /// </summary>
    public static List<Services.Requirements.IRequirementCheck> GetBaseChecks(AzureAuthValidator auth)
        => [new AzureAuthRequirementCheck(auth)];

    public static Command CreateCommand(
        ILogger<CleanupCommand> logger,
        IConfigService configService,
        ITeamsGraphBackendConfigurator backendConfigurator,
        CommandExecutor executor,
        AgentBlueprintService agentBlueprintService,
        IConfirmationProvider confirmationProvider,
        FederatedCredentialService federatedCredentialService,
        AzureAuthValidator authValidator,
        GraphApiService? graphApiService = null,
        IBootstrapConfigResolver? resolver = null)
    {
        var cleanupCommand = new Command("cleanup", "Clean up ALL resources (blueprint, instance, Azure) - use subcommands for granular cleanup");

        var agentNameOption = new Option<string?>(
            new[] { "--agent-name", "-n" },
            description: "Agent base name used with 'setup all --agent-name'. When provided, no config file is required.\n" +
                         "Loads resource IDs from generated config in the current directory first, then falls back to the global generated config if available.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Overrides auto-detection from 'az account show'. Use with --agent-name.");

        var yesOption = new Option<bool>(
            ["--yes", "-y"],
            description: "Skip confirmation prompts and proceed automatically");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Enable verbose logging");

        cleanupCommand.AddOption(agentNameOption);
        cleanupCommand.AddOption(tenantIdOption);
        cleanupCommand.AddOption(yesOption);
        cleanupCommand.AddOption(verboseOption);

        // Set default handler for 'a365 cleanup' (without subcommand) - cleans up everything
        cleanupCommand.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var configFile = new FileInfo("a365.config.json");
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var yes = context.ParseResult.GetValueForOption(yesOption);
            _ = context.ParseResult.GetValueForOption(verboseOption); // consumed by Program.cs startup via args

            // Generate correlation ID at workflow entry point
            var correlationId = HttpClientFactory.GenerateCorrelationId();
            logger.LogInformation("Starting cleanup (CorrelationId: {CorrelationId})", correlationId);

            Agent365Config? bootstrapConfig = null;
            if (!string.IsNullOrWhiteSpace(agentName))
            {
                bootstrapConfig = resolver != null
                    ? await resolver.ResolveAsync(agentName, tenantIdFlag, configFile, isCleanupMode: true, context.GetCancellationToken())
                    : await BuildBootstrapConfigForCleanupAsync(agentName, tenantIdFlag, executor, graphApiService, logger);
                if (bootstrapConfig is null)
                {
                    context.ExitCode = 1;
                    return;
                }
            }
            else
            {
                // No --agent-name and no static config file — fail fast with a clear exit code
                // so cleanup does not silently report success to scripts or CI.
                bootstrapConfig = await LoadConfigAsync(configFile, logger, configService);
                if (bootstrapConfig is null)
                {
                    context.ExitCode = 1;
                    return;
                }
            }

            IConfirmationProvider effectiveConfirmationProvider = yes
                ? new NonInteractiveConfirmationProvider()
                : confirmationProvider;

            await ExecuteAllCleanupAsync(logger, configService, executor, agentBlueprintService, effectiveConfirmationProvider, federatedCredentialService, configFile, graphApiService, correlationId: correlationId, configOverride: bootstrapConfig, ct: context.GetCancellationToken());
        });

        // Add subcommands for granular control
        cleanupCommand.AddCommand(CreateBlueprintCleanupCommand(logger, configService, backendConfigurator, executor, agentBlueprintService, confirmationProvider, federatedCredentialService, graphApiService: graphApiService, resolver: resolver));
        cleanupCommand.AddCommand(CreateAzureCleanupCommand(logger, configService, executor, authValidator, resolver: resolver));
        cleanupCommand.AddCommand(CreateInstanceCleanupCommand(logger, configService, executor, resolver: resolver));

        return cleanupCommand;
    }

    private static Command CreateBlueprintCleanupCommand(
        ILogger<CleanupCommand> logger,
        IConfigService configService,
        ITeamsGraphBackendConfigurator backendConfigurator,
        CommandExecutor executor,
        AgentBlueprintService agentBlueprintService,
        IConfirmationProvider confirmationProvider,
        FederatedCredentialService federatedCredentialService,
        string? correlationId = null,
        GraphApiService? graphApiService = null,
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command("blueprint", "Remove Entra ID blueprint application and service principal");

        var agentNameOption = new Option<string?>(
            new[] { "--agent-name", "-n" },
            description: "Agent base name. When provided, no config file is required.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Overrides auto-detection. Use with --agent-name.");

        var verboseOption = new Option<bool>(
            new[] { "--verbose", "-v" },
            description: "Enable verbose logging");

        var endpointOnlyOption = new Option<bool>(
            new[] { "--endpoint-only" },
            description: "Delete only the messaging endpoint, keep the blueprint application");

        var m365Option = new Option<bool>(
            new[] { "--m365" },
            description: "Only meaningful with --endpoint-only. When set, clears the messaging endpoint from " +
                        "Teams Graph via MCP Platform. Default is false (opt-in). Ignored (with a warning) " +
                        "for full blueprint cleanup, since deleting the blueprint application cascades to " +
                        "the backend configuration on the server side.");

        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(verboseOption);
        command.AddOption(endpointOnlyOption);
        command.AddOption(m365Option);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var configFile = new FileInfo("a365.config.json");
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var endpointOnly = context.ParseResult.GetValueForOption(endpointOnlyOption);
            var isM365 = context.ParseResult.GetValueForOption(m365Option);
            var ct = context.GetCancellationToken();

            try
            {
                Agent365Config? config;
                if (resolver != null)
                    config = await resolver.ResolveAsync(agentName, tenantIdFlag, configFile, isCleanupMode: true, ct);
                else
                    config = await LoadConfigAsync(configFile, logger, configService);
                if (config == null) { context.ExitCode = 1; return; }

                // Generate correlation ID at workflow entry point
                var correlationId = HttpClientFactory.GenerateCorrelationId();
                logger.LogInformation("Starting blueprint cleanup (CorrelationId: {CorrelationId})", correlationId);

                // Configure AgentBlueprintService with custom client app ID if available
                if (!string.IsNullOrWhiteSpace(config.ClientAppId))
                {
                    agentBlueprintService.CustomClientAppId = config.ClientAppId;
                }

                // If endpoint-only mode, only delete the messaging endpoint — gated on --m365.
                if (endpointOnly)
                {
                    if (!isM365)
                    {
                        SetupSubcommands.BlueprintSubcommand.LogNonM365EndpointGuidance(logger, "clear");
                        return;
                    }

                    await ExecuteEndpointOnlyCleanupAsync(logger, config, backendConfigurator, correlationId: correlationId);
                    return;
                }

                // Full cleanup path — --m365 has no effect here because blueprint deletion cascades
                // the backend configuration on the server side. Warn the user so they aren't misled.
                if (isM365)
                {
                    logger.LogWarning(
                        "--m365 has no effect on full blueprint cleanup. The Teams Graph backend " +
                        "configuration is removed automatically when the blueprint is deleted. " +
                        "Use 'a365 cleanup blueprint --endpoint-only --m365' to clear the endpoint " +
                        "while preserving the blueprint.");
                }

                // Full blueprint cleanup with cascade instance deletion
                logger.LogInformation("Starting blueprint cleanup...");

                if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
                {
                    logger.LogInformation("No blueprint application found to clean up");
                    return;
                }

                // Query for agent instances linked to this blueprint before showing preview
                logger.LogInformation("Querying for agent instances linked to blueprint...");
                List<AgentInstanceInfo> instances;
                try
                {
                    instances = (await agentBlueprintService.GetAgentInstancesForBlueprintAsync(
                        config.TenantId,
                        config.AgentBlueprintId))?.ToList() ?? new List<AgentInstanceInfo>();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to query agent instances for blueprint {BlueprintId}. Aborting cleanup.", config.AgentBlueprintId);
                    return;
                }

                // Show preview
                logger.LogInformation("");
                logger.LogInformation("Blueprint Cleanup Preview:");
                logger.LogInformation("=============================");
                logger.LogInformation("Will delete Entra ID application: {BlueprintId}", config.AgentBlueprintId);
                logger.LogInformation("  Name: {DisplayName}", config.AgentBlueprintDisplayName);

                if (!string.IsNullOrWhiteSpace(config.AgenticAppId))
                {
                    logger.LogInformation("");
                    logger.LogInformation("Will also delete Agent Identity Service Principal: {SpId}", config.AgenticAppId);
                }
                if (!string.IsNullOrWhiteSpace(config.AgentInstanceId))
                {
                    logger.LogInformation("");
                    logger.LogInformation("Will also deregister Agent Instance: {InstanceId}", config.AgentInstanceId);
                }
                if (instances.Count > 0)
                {
                    logger.LogInformation("");
                    logger.LogInformation("Will also delete {Count} agent instance(s) linked to this blueprint:", instances.Count);
                    foreach (var instance in instances)
                    {
                        logger.LogInformation("  Instance: {DisplayName} (SP: {SpId})", instance.DisplayName ?? "(unnamed)", instance.IdentitySpId);
                        if (!string.IsNullOrWhiteSpace(instance.AgentUserId))
                            logger.LogInformation("    Agentic user: {UserId}", instance.AgentUserId);
                    }
                }

                logger.LogInformation("");

                if (!await confirmationProvider.ConfirmAsync("Continue with blueprint cleanup? (y/N): "))
                {
                    logger.LogInformation("Cleanup cancelled by user");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(config.AgenticAppId))
                {
                    logger.LogInformation("Deleting agent identity service principal {SpId}...", config.AgenticAppId);
                    var identityDeleted = await agentBlueprintService.DeleteAgentIdentityAsync(
                        config.TenantId,
                        config.AgenticAppId);

                    if (identityDeleted)
                    {
                        logger.LogInformation("Agent identity service principal deleted");
                        config.AgenticAppId = string.Empty;
                        await configService.SaveStateAsync(config);
                    }
                    else
                    {
                        logger.LogWarning("Failed to delete agent identity service principal {SpId} -- will continue with cleanup", config.AgenticAppId);
                    }
                }

                if (!string.IsNullOrWhiteSpace(config.AgentRegistrationId))
                {
                    if (graphApiService is null)
                    {
                        logger.LogWarning("Agent registration deletion skipped (GraphApiService not available). Delete registration {RegistrationId} manually.", config.AgentRegistrationId);
                    }
                    else
                    {
                        logger.LogInformation("Deleting agent registration {RegistrationId} via Graph API...", config.AgentRegistrationId);
                        var registrationDeleted = await graphApiService.DeleteAgentRegistrationAsync(
                            config.TenantId,
                            config.AgentRegistrationId,
                            ct);

                        if (registrationDeleted)
                        {
                            logger.LogInformation("Agent registration deleted");
                            config.AgentRegistrationId = string.Empty;
                            await configService.SaveStateAsync(config);
                        }
                        else
                        {
                            logger.LogWarning("Failed to delete agent registration {RegistrationId} -- will continue with cleanup", config.AgentRegistrationId);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(config.AgentInstanceId))
                {
                    if (graphApiService is null)
                    {
                        logger.LogWarning("Agent instance deletion skipped (GraphApiService not available). Delete instance {InstanceId} manually via the M365 Admin Center.", config.AgentInstanceId);
                    }
                    else
                    {
                        logger.LogInformation("Deleting agent instance {InstanceId} from Agent Registry...", config.AgentInstanceId);
                        var instanceDeleted = await graphApiService.DeleteAgentInstanceAsync(
                            config.TenantId,
                            config.AgentInstanceId,
                            ct);

                        if (instanceDeleted)
                        {
                            logger.LogInformation("Agent instance deleted from registry");
                            config.AgentInstanceId = string.Empty;
                            await configService.SaveStateAsync(config);
                        }
                        else
                        {
                            logger.LogWarning("Failed to delete agent instance {InstanceId} -- will continue with blueprint deletion", config.AgentInstanceId);
                        }
                    }
                }

                // Delete instances first (warn and continue on failure)
                var failedResources = new Dictionary<string, List<string>>
                {
                    [AgenticUsersKey] = new List<string>(),
                    [IdentitySpsKey] = new List<string>()
                };

                foreach (var instance in instances)
                {
                    // Delete agentic user before identity SP
                    if (!string.IsNullOrWhiteSpace(instance.AgentUserId))
                    {
                        logger.LogInformation("Deleting agentic user {UserId} for instance {DisplayName}...",
                            instance.AgentUserId, instance.DisplayName ?? instance.IdentitySpId);

                        var userDeleted = await agentBlueprintService.DeleteAgentUserAsync(
                            config.TenantId,
                            instance.AgentUserId);

                        if (!userDeleted)
                        {
                            logger.LogWarning("Failed to delete agentic user {UserId} -- will continue", instance.AgentUserId);
                            failedResources[AgenticUsersKey].Add(instance.AgentUserId!);
                        }
                        else
                        {
                            logger.LogInformation("Agentic user deleted");
                        }
                    }

                    // Delete identity SP
                    logger.LogInformation("Deleting agent identity SP {SpId} for instance {DisplayName}...",
                        instance.IdentitySpId, instance.DisplayName ?? instance.IdentitySpId);

                    var spDeleted = await agentBlueprintService.DeleteAgentIdentityAsync(
                        config.TenantId,
                        instance.IdentitySpId);

                    if (!spDeleted)
                    {
                        logger.LogWarning("Failed to delete agent identity SP {SpId} -- will continue", instance.IdentitySpId);
                        failedResources[IdentitySpsKey].Add(instance.IdentitySpId);
                    }
                    else
                    {
                        logger.LogInformation("Agent identity SP deleted");
                    }
                }

                // Delete federated credentials first before deleting the blueprint
                logger.LogInformation("");
                logger.LogInformation("Deleting federated credentials from blueprint...");

                // Configure FederatedCredentialService with custom client app ID if available
                if (!string.IsNullOrWhiteSpace(config.ClientAppId))
                    federatedCredentialService.CustomClientAppId = config.ClientAppId;

                var ficsDeleted = await federatedCredentialService.DeleteAllFederatedCredentialsAsync(
                    config.TenantId,
                    config.AgentBlueprintId);

                if (!ficsDeleted)
                {
                    logger.LogWarning("Some federated credentials may not have been deleted successfully");
                    logger.LogWarning("Continuing with blueprint deletion...");
                }
                else
                {
                    logger.LogInformation("Federated credentials deleted successfully");
                }

                // Delete the agent blueprint
                logger.LogInformation("");
                logger.LogInformation("Deleting agent blueprint application...");
                var deleted = await agentBlueprintService.DeleteAgentBlueprintAsync(
                    config.TenantId,
                    config.AgentBlueprintId);

                if (!deleted)
                {
                    logger.LogWarning("");
                    logger.LogWarning("Blueprint deletion failed. The blueprint still exists in Entra ID.");
                    PrintOrphanSummary(logger, failedResources);
                    if (!HasOrphanedResources(failedResources))
                    {
                        logger.LogWarning("All agent instances were deleted. Retry 'a365 cleanup blueprint' or delete the blueprint manually via the Entra portal or Graph API.");
                    }
                    return;
                }

                logger.LogInformation("Agent blueprint application deleted successfully");

                // Teams Graph backend configuration is a child resource of the blueprint and is
                // removed on the server side when the blueprint is deleted. No separate clear
                // call is needed here. Use `a365 cleanup blueprint --endpoint-only --m365` to
                // clear just the backend configuration while preserving the blueprint.
                PrintOrphanSummary(logger, failedResources);

                // Clear configuration after successful blueprint deletion
                logger.LogInformation("");
                logger.LogInformation("Clearing blueprint data from local configuration...");

                config.AgentBlueprintId = string.Empty;
                config.AgentBlueprintClientSecret = string.Empty;
                config.AgenticAppId = string.Empty;
                config.AgentInstanceId = string.Empty;
                config.ResourceConsents.Clear();

                await configService.SaveStateAsync(config);
                logger.LogInformation("Local configuration cleared");

                if (!HasOrphanedResources(failedResources))
                {
                    logger.LogInformation("");
                    logger.LogInformation("Blueprint cleanup completed successfully!");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Blueprint cleanup failed");
            }
        });

        return command;
    }

    /// <summary>
    /// Returns the requirement checks for the <c>cleanup azure</c> subcommand.
    /// </summary>
    internal static List<Services.Requirements.IRequirementCheck> GetAzureCleanupChecks(AzureAuthValidator auth)
        => GetBaseChecks(auth);

    private static Command CreateAzureCleanupCommand(
        ILogger<CleanupCommand> logger,
        IConfigService configService,
        CommandExecutor executor,
        AzureAuthValidator authValidator,
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command("azure", "Remove Azure resources (App Service, App Service Plan)");

        var agentNameOption = new Option<string?>(
            new[] { "--agent-name", "-n" },
            description: "Agent base name. When provided, no config file is required.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Overrides auto-detection. Use with --agent-name.");

        var verboseOption = new Option<bool>(
            new[] { "--verbose", "-v" },
            description: "Enable verbose logging");

        var dryRunOption = new Option<bool>("--dry-run", "Show resources that would be deleted without making any changes");

        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var configFile = new FileInfo("a365.config.json");
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var ct = context.GetCancellationToken();
            try
            {
                Agent365Config? config;
                if (resolver != null)
                    config = await resolver.ResolveAsync(agentName, tenantIdFlag, configFile, isCleanupMode: true, ct);
                else
                    config = await LoadConfigAsync(configFile, logger, configService);
                if (config == null) { context.ExitCode = 1; return; }

                if (!dryRun)
                    logger.LogInformation("Starting Azure cleanup...");

                if (!dryRun)
                {
                    var checks = GetAzureCleanupChecks(authValidator);
                    await RequirementsSubcommand.RunChecksOrExitAsync(checks, config, logger, CancellationToken.None);
                }

                logger.LogInformation("");
                logger.LogInformation("Azure Cleanup Preview:");
                logger.LogInformation("=========================");
                if (!string.IsNullOrWhiteSpace(config.BotId))
                    logger.LogInformation("    Azure Bot: {BotId}", config.BotId);
                logger.LogInformation("");

                if (dryRun)
                {
                    logger.LogInformation("DRY RUN: No changes made.");
                    return;
                }

                Console.Write("Continue with Azure cleanup? (y/N): ");
                var response = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (response != "y" && response != "yes")
                {
                    logger.LogInformation("Cleanup cancelled by user");
                    return;
                }

                logger.LogInformation("No Azure Web App resources to clean up.");
                logger.LogInformation("Azure infrastructure is managed externally.");
                logger.LogInformation("Azure cleanup completed!");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Azure cleanup failed with exception");
            }
        });

        return command;
    }

    private static Command CreateInstanceCleanupCommand(
        ILogger<CleanupCommand> logger,
        IConfigService configService,
        CommandExecutor executor,
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command("instance", "Remove agent instance identity and user from Entra ID");

        var agentNameOption = new Option<string?>(
            new[] { "--agent-name", "-n" },
            description: "Agent base name. When provided, no config file is required.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Overrides auto-detection. Use with --agent-name.");

        var verboseOption = new Option<bool>(
            new[] { "--verbose", "-v" },
            description: "Enable verbose logging");

        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(verboseOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var configFile = new FileInfo("a365.config.json");
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var ct = context.GetCancellationToken();
            try
            {
                Agent365Config? config;
                if (resolver != null)
                    config = await resolver.ResolveAsync(agentName, tenantIdFlag, configFile, isCleanupMode: true, ct);
                else
                    config = await LoadConfigAsync(configFile, logger, configService);
                if (config == null) { context.ExitCode = 1; return; }

                logger.LogInformation("Starting instance cleanup...");

                logger.LogInformation("");
                logger.LogInformation("Instance Cleanup Preview:");
                logger.LogInformation("============================");
                logger.LogInformation("Will delete the following resources:");
                
                if (!string.IsNullOrWhiteSpace(config.AgenticAppId))
                    logger.LogInformation("    Agent Identity Service Principal: {SpId}", config.AgenticAppId);
                if (!string.IsNullOrWhiteSpace(config.AgenticUserId))
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

                // Delete agent identity service principal
                if (!string.IsNullOrWhiteSpace(config.AgenticAppId))
                {
                    logger.LogInformation("Deleting agent identity service principal...");
                    await executor.ExecuteAsync("az", $"ad app delete --id {config.AgenticAppId}", null, true, false, CancellationToken.None);
                    logger.LogInformation("Agent identity service principal deleted");
                }

                // Delete agent user
                if (!string.IsNullOrWhiteSpace(config.AgenticUserId))
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
        });

        return command;
    }

    // Shared method for complete cleanup logic - used by both default handler and 'all' subcommand
    private static async Task ExecuteAllCleanupAsync(
        ILogger<CleanupCommand> logger,
        IConfigService configService,
        CommandExecutor executor,
        AgentBlueprintService agentBlueprintService,
        IConfirmationProvider confirmationProvider,
        FederatedCredentialService federatedCredentialService,
        FileInfo? configFile,
        GraphApiService? graphApiService = null,
        string? correlationId = null,
        Agent365Config? configOverride = null,
        CancellationToken ct = default)
    {
        var cleanupSucceeded = false;
        var hasFailures = false;
        try
        {
            logger.LogInformation("Starting complete cleanup...");

            var config = configOverride ?? await LoadConfigAsync(configFile, logger, configService);
            if (config == null) return;
            
            // Configure AgentBlueprintService with custom client app ID if available
            if (!string.IsNullOrWhiteSpace(config.ClientAppId))
            {
                agentBlueprintService.CustomClientAppId = config.ClientAppId;
            }

            logger.LogInformation("");
            logger.LogInformation("Complete Cleanup Preview:");
            logger.LogInformation("============================");
            logger.LogInformation("WARNING: ALL RESOURCES WILL BE DELETED:");
            if (!string.IsNullOrWhiteSpace(config.AgentBlueprintId))
                logger.LogInformation("    Blueprint Application: {BlueprintId}", config.AgentBlueprintId);
            if (!string.IsNullOrWhiteSpace(config.AgentBlueprintServicePrincipalObjectId))
                logger.LogInformation("    Blueprint Service Principal: {SpId}", config.AgentBlueprintServicePrincipalObjectId);
            if (!string.IsNullOrWhiteSpace(config.AgenticAppId))
                logger.LogInformation("    Agent Identity Service Principal: {SpId}", config.AgenticAppId);
            if (!string.IsNullOrWhiteSpace(config.AgentRegistrationId))
                logger.LogInformation("    Agent Registration (AgentX): {RegistrationId}", config.AgentRegistrationId);
            if (!string.IsNullOrWhiteSpace(config.AgentInstanceId))
                logger.LogInformation("    Agent Registry Instance: {InstanceId}", config.AgentInstanceId);
            if (!string.IsNullOrWhiteSpace(config.AgenticUserId))
                logger.LogInformation("    Agent User: {UserId}", config.AgenticUserId);
            if (!string.IsNullOrWhiteSpace(config.BotName))
                logger.LogInformation("    Azure Messaging Endpoint: {BotName}", config.BotName);
            var previewLocalGen = Path.Combine(Environment.CurrentDirectory, "a365.generated.config.json");
            var previewGlobalGen = Path.Combine(ConfigService.GetGlobalConfigDirectory(), "a365.generated.config.json");
            if (File.Exists(previewLocalGen) || File.Exists(previewGlobalGen))
                logger.LogInformation("    Generated configuration file");
            logger.LogInformation("");

            if (!await confirmationProvider.ConfirmAsync("Are you sure you want to DELETE ALL resources? (y/N): "))
            {
                logger.LogInformation("Cleanup cancelled by user");
                return;
            }
            
            if (!await confirmationProvider.ConfirmWithTypedResponseAsync("Type 'DELETE' to confirm: ", "DELETE"))
            {
                logger.LogInformation("Cleanup cancelled - confirmation not received");
                return;
            }

            logger.LogInformation("Starting complete cleanup...");

            // 1a. For non-DW blueprint flow: delete AgentX agent registration before blueprint
            if (!string.IsNullOrWhiteSpace(config.AgentRegistrationId))
            {
                if (graphApiService is null)
                {
                    logger.LogWarning("Agent registration deletion skipped (GraphApiService not available). Delete registration {RegistrationId} manually.", config.AgentRegistrationId);
                    hasFailures = true;
                }
                else
                {
                    logger.LogInformation("Deleting agent registration {RegistrationId} via Graph API...", config.AgentRegistrationId);
                    var registrationDeleted = await graphApiService.DeleteAgentRegistrationAsync(
                        config.TenantId,
                        config.AgentRegistrationId,
                        ct);

                    if (registrationDeleted)
                    {
                        logger.LogInformation("Agent registration deleted");
                        config.AgentRegistrationId = string.Empty;
                    }
                    else
                    {
                        logger.LogWarning("Failed to delete agent registration {RegistrationId} -- will continue with blueprint deletion", config.AgentRegistrationId);
                        hasFailures = true;
                    }
                }
            }

            // 1b. For non-DW blueprint flow: delete Agent Registry instance before blueprint
            if (!string.IsNullOrWhiteSpace(config.AgentInstanceId))
            {
                if (graphApiService is null)
                {
                    logger.LogWarning("Agent instance deletion skipped (GraphApiService not available). Delete instance {InstanceId} manually via the M365 Admin Center.", config.AgentInstanceId);
                    hasFailures = true;
                }
                else
                {
                    logger.LogInformation("Deleting agent instance {InstanceId} from Agent Registry...", config.AgentInstanceId);
                    var instanceDeleted = await graphApiService.DeleteAgentInstanceAsync(
                        config.TenantId,
                        config.AgentInstanceId,
                        ct);

                    if (instanceDeleted)
                    {
                        logger.LogInformation("Agent instance deleted from registry");
                        config.AgentInstanceId = string.Empty;
                    }
                    else
                    {
                        logger.LogWarning("Failed to delete agent instance {InstanceId} -- will continue with blueprint deletion", config.AgentInstanceId);
                        hasFailures = true;
                    }
                }
            }

            // 1. Delete federated credentials from agent blueprint (if exists)
            if (!string.IsNullOrWhiteSpace(config.AgentBlueprintId))
            {
                logger.LogInformation("Deleting federated credentials from blueprint...");
                
                // Configure FederatedCredentialService with custom client app ID if available
                if (!string.IsNullOrWhiteSpace(config.ClientAppId))
                {
                    federatedCredentialService.CustomClientAppId = config.ClientAppId;
                }
                
                var ficsDeleted = await federatedCredentialService.DeleteAllFederatedCredentialsAsync(
                    config.TenantId,
                    config.AgentBlueprintId);

                if (!ficsDeleted)
                {
                    logger.LogWarning("Some federated credentials may not have been deleted successfully");
                    logger.LogWarning("Continuing with blueprint deletion...");
                    hasFailures = true;
                }
                else
                {
                    logger.LogInformation("Federated credentials deleted successfully");
                }
            }

            // 2. Delete agent blueprint application
            if (!string.IsNullOrWhiteSpace(config.AgentBlueprintId))
            {
                logger.LogInformation("Deleting agent blueprint application...");
                var deleted = await agentBlueprintService.DeleteAgentBlueprintAsync(
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
                    hasFailures = true;
                }
            }

            // 3. Delete agent identity service principal(s).
            // First delete the one recorded in config (fast path, no extra Graph query).
            // Then query Entra for any additional identities linked to the blueprint that
            // may not be in config — mirrors what 'cleanup blueprint' does, and handles the
            // case where AgenticAppId is missing (e.g. bootstrap cleanup without --agent-name).
            var deletedIdentityIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(config.AgenticAppId))
            {
                logger.LogInformation("Deleting agent identity service principal...");

                var deleted = await agentBlueprintService.DeleteAgentIdentityAsync(
                    config.TenantId,
                    config.AgenticAppId,
                    ct);

                if (deleted)
                {
                    deletedIdentityIds.Add(config.AgenticAppId);
                    logger.LogInformation("Agent identity service principal deleted successfully");
                }
                else
                {
                    logger.LogWarning("Failed to delete agent identity service principal (will continue with other resources)");
                    logger.LogWarning("Local configuration will still be cleared at the end");
                    hasFailures = true;
                }
            }

            // Discover any remaining linked identities via Entra (handles IDs missing from config).
            if (!string.IsNullOrWhiteSpace(config.AgentBlueprintId) && graphApiService != null)
            {
                try
                {
                    var linkedInstances = await agentBlueprintService.GetAgentInstancesForBlueprintAsync(
                        config.TenantId, config.AgentBlueprintId, ct);

                    foreach (var instance in linkedInstances)
                    {
                        if (string.IsNullOrWhiteSpace(instance.IdentitySpId) ||
                            deletedIdentityIds.Contains(instance.IdentitySpId))
                            continue;

                        logger.LogInformation("Deleting linked agent identity SP {SpId} ({DisplayName})...",
                            instance.IdentitySpId, instance.DisplayName ?? "(unnamed)");

                        var deleted = await agentBlueprintService.DeleteAgentIdentityAsync(
                            config.TenantId, instance.IdentitySpId, ct);

                        if (deleted)
                        {
                            deletedIdentityIds.Add(instance.IdentitySpId);
                            logger.LogInformation("Linked agent identity SP deleted");
                        }
                        else
                        {
                            logger.LogWarning("Failed to delete linked agent identity SP {SpId}", instance.IdentitySpId);
                            hasFailures = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Could not query linked agent identities from Entra (non-fatal): {Message}", ex.Message);
                }
            }

            // 4. Delete agent user
            if (!string.IsNullOrWhiteSpace(config.AgenticUserId))
            {
                logger.LogInformation("Deleting agent user...");
                await executor.ExecuteAsync("az", $"ad user delete --id {config.AgenticUserId}", null, true, false, CancellationToken.None);
                logger.LogInformation("Agent user deleted");
            }

            // 5. Messaging endpoint deletion is temporarily disabled.

            // Azure infrastructure cleanup removed — deploy command no longer manages Azure resources.

            // Mark cleanup as successful only if no failures occurred
            if (!hasFailures)
            {
                cleanupSucceeded = true;
                logger.LogInformation("Complete cleanup finished successfully!");
            }
            else
            {
                logger.LogWarning("Cleanup completed with some failures. Review warnings above.");
                logger.LogWarning("Generated configuration preserved. Fix issues and re-run cleanup if needed.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Complete cleanup failed: {Message}", ex.Message);
            logger.LogWarning("Generated configuration file preserved due to cleanup failure. Fix issues and re-run cleanup.");
        }
        finally
        {
            // Only clean up generated config if all cleanup steps succeeded
            if (cleanupSucceeded)
            {
                try
                {
                    var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                    
                    // Delete local generated config
                    var localGeneratedPath = "a365.generated.config.json";
                    if (File.Exists(localGeneratedPath))
                    {
                        var backupPath = $"a365.generated.config.backup-{timestamp}.json";
                        
                        logger.LogInformation("Backing up generated configuration to: {BackupPath}", backupPath);
                        File.Copy(localGeneratedPath, backupPath);
                        
                        logger.LogInformation("Deleting local generated configuration file...");
                        File.Delete(localGeneratedPath);
                        logger.LogInformation("Local generated configuration deleted (backup saved)");
                    }
                    
                    // Also delete global generated config (uses ConfigService for cross-platform path)
                    var globalGeneratedPath = Path.Combine(
                        ConfigService.GetGlobalConfigDirectory(),
                        "a365.generated.config.json");
                    
                    if (File.Exists(globalGeneratedPath))
                    {
                        var globalBackupPath = Path.Combine(
                            ConfigService.GetGlobalConfigDirectory(),
                            $"a365.generated.config.backup-{timestamp}.json");
                        
                        logger.LogInformation("Backing up global generated configuration to: {BackupPath}", globalBackupPath);
                        File.Copy(globalGeneratedPath, globalBackupPath);
                        
                        logger.LogInformation("Deleting global generated configuration file...");
                        File.Delete(globalGeneratedPath);
                        logger.LogInformation("Global generated configuration deleted (backup saved)");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to clean up generated configuration file: {Message}", ex.Message);
                }
            }
        }
    }

    /// <summary>
    /// Shared helper method to delete a messaging endpoint.
    /// Validates configuration, gets endpoint name, and calls the bot configurator to delete.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic messages</param>
    /// <param name="config">Configuration containing endpoint and blueprint information</param>
    /// <param name="backendConfigurator">Bot configurator service for endpoint operations</param>
    /// <param name="correlationId">Optional correlation ID for request tracing</param>
    /// <returns>True if endpoint was deleted successfully; false otherwise</returns>
    private static async Task<bool> DeleteMessagingEndpointAsync(
        ILogger<CleanupCommand> logger,
        Agent365Config config,
        ITeamsGraphBackendConfigurator backendConfigurator,
        string? correlationId = null)
    {
        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
        {
            logger.LogError("Agent Blueprint ID not found. Agent Blueprint ID is required for clearing the backend configuration.");
            return false;
        }

        logger.LogInformation("Clearing backend configuration...");

        var cleared = await backendConfigurator.ClearBackendConfigurationAsync(
            config.AgentBlueprintId,
            correlationId: correlationId);

        if (cleared)
        {
            logger.LogInformation("Backend configuration cleared successfully");
            return true;
        }

        logger.LogWarning("Failed to clear backend configuration");
        return false;
    }

    /// <summary>
    /// Executes endpoint-only cleanup — clears the Teams Graph backend configuration while
    /// preserving the blueprint application.
    /// </summary>
    private static async Task ExecuteEndpointOnlyCleanupAsync(
        ILogger<CleanupCommand> logger,
        Agent365Config config,
        ITeamsGraphBackendConfigurator backendConfigurator,
        string? correlationId = null)
    {
        logger.LogInformation("Starting endpoint-only cleanup...");

        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
        {
            logger.LogError("Agent Blueprint ID not found. Blueprint ID is required for endpoint deletion.");
            logger.LogInformation("Please ensure blueprint is configured before attempting endpoint cleanup.");
            return;
        }

        logger.LogInformation("");
        logger.LogInformation("Endpoint Cleanup Preview:");
        logger.LogInformation("============================");
        logger.LogInformation("Will clear messaging endpoint for blueprint: {BlueprintId}", config.AgentBlueprintId);
        logger.LogInformation("");

        Console.Write("Continue with endpoint cleanup? (y/N): ");
        var response = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (response != "y" && response != "yes")
        {
            logger.LogInformation("Cleanup cancelled by user");
            return;
        }

        var deleted = await DeleteMessagingEndpointAsync(logger, config, backendConfigurator, correlationId: correlationId);

        if (!deleted)
        {
            return;
        }

        logger.LogInformation("");
        logger.LogInformation("Endpoint cleanup completed successfully!");
        logger.LogInformation("");
    }

    /// <summary>
    /// Checks whether any instance deletions were recorded as failures.
    /// </summary>
    private static bool HasOrphanedResources(Dictionary<string, List<string>> failedResources)
    {
        return failedResources[AgenticUsersKey].Count + failedResources[IdentitySpsKey].Count > 0;
    }

    /// <summary>
    /// Prints a summary of orphaned Entra ID resources that could not be deleted.
    /// This should be called whenever instance deletions have failed, regardless of
    /// whether the blueprint deletion itself succeeded or failed.
    /// </summary>
    private static void PrintOrphanSummary(
        ILogger<CleanupCommand> logger,
        Dictionary<string, List<string>> failedResources)
    {
        if (!HasOrphanedResources(failedResources))
        {
            return;
        }

        logger.LogWarning("Blueprint cleanup encountered warnings.");
        logger.LogWarning("The following resources could not be deleted and remain orphaned in Entra ID:");
        foreach (var userId in failedResources[AgenticUsersKey])
            logger.LogWarning("  Orphaned agentic user: {ResourceId}", userId);
        foreach (var spId in failedResources[IdentitySpsKey])
            logger.LogWarning("  Orphaned identity SP: {ResourceId}", spId);
        logger.LogWarning("Delete them manually via the Entra portal or Graph API.");
    }

    /// <summary>
    /// Builds a cleanup config from the global generated config without requiring a static config file.
    /// Used when cleanup is invoked with <c>--agent-name</c> after a bootstrap setup.
    /// Loads resource IDs (blueprint, agent identity, registration) from the generated config saved
    /// to the global config directory by <c>setup all --agent-name</c>.
    /// </summary>
    private static async Task<Agent365Config?> BuildBootstrapConfigForCleanupAsync(
        string agentName,
        string? tenantIdFlag,
        CommandExecutor executor,
        GraphApiService? graphApiService,
        ILogger<CleanupCommand> logger)
    {
        // Step 1: Resolve tenant ID
        var tenantId = await SetupHelpers.ResolveBootstrapTenantIdAsync(tenantIdFlag, executor, logger);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            logger.LogError("Could not detect tenant ID. Sign in with 'az login' or pass --tenant-id.");
            return null;
        }

        // Step 2: Resolve client app ID.
        // Prefer a365.config.json when it exists locally and its tenant matches the current tenant.
        // Fall back to Entra lookup by well-known display name if the static config is absent or stale.
        var clientAppId = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
            tenantId,
            graphApiService,
            logger,
            CancellationToken.None,
            preferLocalConfig: true);
        if (!string.IsNullOrWhiteSpace(clientAppId) && graphApiService != null)
            graphApiService.CustomClientAppId = clientAppId;

        // Step 3: Resolve blueprint ID from Entra by display name (authoritative source).
        var blueprintDisplayName = $"{agentName} Blueprint";
        string? resolvedBlueprintId = null;
        if (graphApiService != null)
        {
            resolvedBlueprintId = await graphApiService.FindApplicationByDisplayNameAsync(
                tenantId, blueprintDisplayName);
            if (string.IsNullOrWhiteSpace(resolvedBlueprintId))
                logger.LogWarning("Blueprint '{Name}' not found in Entra — resource IDs may be incomplete.", blueprintDisplayName);
        }

        // Step 4: Load generated config.
        // Only take agentRegistrationId from the file when the blueprint IDs match,
        // confirming the file belongs to this agent.
        var localGeneratedPath = Path.Combine(Environment.CurrentDirectory, "a365.generated.config.json");
        var globalGeneratedPath = Path.Combine(ConfigService.GetGlobalConfigDirectory(), "a365.generated.config.json");
        var generatedConfigPath = File.Exists(localGeneratedPath) ? localGeneratedPath : globalGeneratedPath;

        string? agentRegistrationId = null;
        string? agenticAppId = null;
        string? agentBlueprintSpObjectId = null;
        string? configBlueprintId = null;

        if (File.Exists(generatedConfigPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(generatedConfigPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                configBlueprintId = SetupHelpers.GetJsonString(root, "agentBlueprintId");

                if (!string.IsNullOrWhiteSpace(resolvedBlueprintId) &&
                    string.Equals(resolvedBlueprintId, configBlueprintId, StringComparison.OrdinalIgnoreCase))
                {
                    agentRegistrationId = SetupHelpers.GetJsonString(root, "agentRegistrationId");
                    agenticAppId = SetupHelpers.GetJsonString(root, "AgenticAppId");
                    agentBlueprintSpObjectId = SetupHelpers.GetJsonString(root, "agentBlueprintServicePrincipalObjectId");
                    logger.LogInformation("Loaded resource IDs from {Path}", generatedConfigPath);
                }
                else if (!string.IsNullOrWhiteSpace(configBlueprintId) && !string.IsNullOrWhiteSpace(resolvedBlueprintId))
                {
                    logger.LogWarning(
                        "Generated config blueprint ID ({ConfigId}) does not match Entra-resolved ID ({ResolvedId}). Skipping resource IDs from file.",
                        configBlueprintId, resolvedBlueprintId);
                }
                else if (string.IsNullOrWhiteSpace(resolvedBlueprintId))
                {
                    // Entra lookup failed — fall back to file values for all IDs
                    agentRegistrationId = SetupHelpers.GetJsonString(root, "agentRegistrationId");
                    agenticAppId = SetupHelpers.GetJsonString(root, "AgenticAppId");
                    agentBlueprintSpObjectId = SetupHelpers.GetJsonString(root, "agentBlueprintServicePrincipalObjectId");
                    logger.LogInformation("Loaded resource IDs from {Path} (Entra lookup unavailable)", generatedConfigPath);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not read generated config at {Path}: {Message}", generatedConfigPath, ex.Message);
            }
        }
        else
        {
            logger.LogWarning("No generated config found at {Path}. Resource IDs may be missing — resources must be deleted manually.", generatedConfigPath);
        }

        var blueprintId = resolvedBlueprintId ?? configBlueprintId;

        var config = new Agent365Config
        {
            TenantId = tenantId,
            ClientAppId = clientAppId ?? string.Empty,
            AgentIdentityDisplayName = $"{agentName} Identity",
            AgentBlueprintDisplayName = blueprintDisplayName,
            AgentDescription = agentName,
            AiTeammate = false,
            UseBlueprint = true,
        };

        config.AgentBlueprintId = blueprintId;
        config.AgentBlueprintServicePrincipalObjectId = agentBlueprintSpObjectId;
        config.AgentRegistrationId = agentRegistrationId;
        config.AgenticAppId = agenticAppId;

        logger.LogInformation("Bootstrap cleanup config:");
        using (logger.Indent())
        {
            logger.LogInformation("TenantId:        {TenantId}", tenantId);
            logger.LogInformation("ClientAppId:     {ClientAppId}", clientAppId ?? "(not found)");
            logger.LogInformation("BlueprintId:     {BlueprintId}", blueprintId ?? "(not found)");
            logger.LogInformation("BlueprintSP:     {SpId}", agentBlueprintSpObjectId ?? "(not found)");
            logger.LogInformation("AgentIdentitySP: {SpId}", agenticAppId ?? "(not found)");
            logger.LogInformation("RegistrationId:  {RegId}", agentRegistrationId ?? "(not found)");
        }

        return config;
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
        catch (ConfigFileNotFoundException ex)
        {
            logger.LogError("{Message}", ex.IssueDescription);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load configuration: {Message}", ex.Message);
            return null;
        }
    }

}