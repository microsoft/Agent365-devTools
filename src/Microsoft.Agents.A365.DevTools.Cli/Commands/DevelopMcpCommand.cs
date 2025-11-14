using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// Command for managing MCP server environments in Dataverse
/// </summary>
public static class DevelopMcpCommand
{
    /// <summary>
    /// Creates the develop-mcp command with subcommands for MCP server management in Dataverse
    /// </summary>
    public static Command CreateCommand(
        ILogger logger, 
        IAgent365ToolingService toolingService)
    {
        var developMcpCommand = new Command("develop-mcp", "Manage MCP servers in Dataverse environments");

        // Add common options
        var configOption = new Option<string>(
            ["--config", "-c"],
            getDefaultValue: () => "a365.config.json",
            description: "Configuration file path");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Enable verbose logging");

        developMcpCommand.AddOption(configOption);
        developMcpCommand.AddOption(verboseOption);

        // Add subcommands
        developMcpCommand.AddCommand(CreateListEnvironmentsSubcommand(logger, toolingService));
        developMcpCommand.AddCommand(CreateListServersSubcommand(logger, toolingService));
        developMcpCommand.AddCommand(CreatePublishSubcommand(logger, toolingService));
        developMcpCommand.AddCommand(CreateUnpublishSubcommand(logger, toolingService));
        developMcpCommand.AddCommand(CreateApproveSubcommand(logger, toolingService));
        developMcpCommand.AddCommand(CreateBlockSubcommand(logger, toolingService));

        return developMcpCommand;
    }

    /// <summary>
    /// Creates the list-environments subcommand
    /// </summary>
    private static Command CreateListEnvironmentsSubcommand(
        ILogger logger, 
        IAgent365ToolingService toolingService)
    {
        var command = new Command("list-environments", "List all Dataverse environments available for MCP server management");

        var configOption = new Option<string>(
            ["-c", "--config"],
            getDefaultValue: () => "a365.config.json",
            description: "Configuration file path"
        );
        command.AddOption(configOption);

        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "Show what would be done without executing"
        );
        command.AddOption(dryRunOption);

        command.SetHandler(async (configPath, dryRun) =>
        {
            logger.LogInformation("Starting list-environments operation...");

            if (dryRun)
            {
                logger.LogInformation("[DRY RUN] Would read config from {ConfigPath}", configPath);
                logger.LogInformation("[DRY RUN] Would query Dataverse environments endpoint");
                logger.LogInformation("[DRY RUN] Would display list of available environments");
                await Task.CompletedTask;
                return;
            }

            // Call service
            var environmentsResponse = await toolingService.ListEnvironmentsAsync();

            if (environmentsResponse == null || environmentsResponse.Environments.Length == 0)
            {
                logger.LogInformation("No Dataverse environments found");
                return;
            }

            // Display available environments
            logger.LogInformation("Available Dataverse Environments:");
            logger.LogInformation("==================================");

            foreach (var env in environmentsResponse.Environments)
            {
                var envId = env.GetEnvironmentId() ?? "Unknown";
                var envName = env.DisplayName ?? "Unknown";
                var envType = env.Type ?? "Unknown";

                logger.LogInformation("Environment ID: {EnvId}", envId);
                logger.LogInformation("   Name: {Name}", envName);
                logger.LogInformation("   Type: {Type}", envType);
                
                if (!string.IsNullOrWhiteSpace(env.Url))
                {
                    logger.LogInformation("   URL: {Url}", env.Url);
                }
                if (!string.IsNullOrWhiteSpace(env.Geo))
                {
                    logger.LogInformation("   Region: {Geo}", env.Geo);
                }
            }

            logger.LogInformation("Listed {Count} Dataverse environment(s)", environmentsResponse.Environments.Length);

        }, configOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Creates the list-servers subcommand
    /// </summary>
    private static Command CreateListServersSubcommand(
        ILogger logger, 
        IAgent365ToolingService toolingService)
    {
        var command = new Command("list-servers", "List MCP servers in a specific Dataverse environment");

        var envIdOption = new Option<string?>(
            ["--environment-id", "-e"],
            description: "Dataverse environment ID"
        );
        envIdOption.IsRequired = false; // Allow null so we can prompt
        command.AddOption(envIdOption);

        var configOption = new Option<string>(
            ["-c", "--config"],
            getDefaultValue: () => "a365.config.json",
            description: "Configuration file path"
        );
        command.AddOption(configOption);

        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "Show what would be done without executing"
        );
        command.AddOption(dryRunOption);

        command.SetHandler(async (envId, configPath, dryRun) =>
        {
            // Prompt for missing required argument
            if (string.IsNullOrWhiteSpace(envId))
            {
                Console.Write("Enter Dataverse environment ID: ");
                envId = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(envId))
                {
                    logger.LogError("Environment ID is required");
                    return;
                }
            }

            logger.LogInformation("Starting list-servers operation for environment {EnvId}...", envId);

            if (dryRun)
            {
                logger.LogInformation("[DRY RUN] Would read config from {ConfigPath}", configPath);
                logger.LogInformation("[DRY RUN] Would query MCP servers in environment {EnvId}", envId);
                logger.LogInformation("[DRY RUN] Would display list of MCP servers");
                await Task.CompletedTask;
                return;
            }

            // Call service
            var serversResponse = await toolingService.ListServersAsync(envId);

            if (serversResponse == null)
            {
                logger.LogError("Failed to list MCP servers in environment {EnvId}", envId);
                return;
            }

            // Log response details
            if (!string.IsNullOrWhiteSpace(serversResponse.Status))
            {
                logger.LogInformation("API Response Status: {Status}", serversResponse.Status);
            }
            if (!string.IsNullOrWhiteSpace(serversResponse.Message))
            {
                logger.LogInformation("API Response Message: {Message}", serversResponse.Message);
            }
            if (!string.IsNullOrWhiteSpace(serversResponse.Warning))
            {
                logger.LogWarning("API Warning: {Warning}", serversResponse.Warning);
            }

            var servers = serversResponse.GetServers();
            
            if (servers.Length == 0)
            {
                logger.LogInformation("No MCP servers found in environment {EnvId}", envId);
                return;
            }

            // Display MCP servers
            logger.LogInformation("MCP Servers in Environment {EnvId}:", envId);
            logger.LogInformation("======================================");

            foreach (var server in servers)
            {
                var serverName = server.McpServerName ?? "Unknown";
                var displayName = server.DisplayName ?? serverName;
                var url = server.Url ?? "Unknown";
                var status = server.Status ?? "Unknown";

                logger.LogInformation("{DisplayName}", displayName);
                if (!string.IsNullOrWhiteSpace(server.Name) && server.Name != displayName)
                {
                    logger.LogInformation("   Name: {Name}", server.Name);
                }
                if (!string.IsNullOrWhiteSpace(server.Id))
                {
                    logger.LogInformation("   ID: {Id}", server.Id);
                }
                logger.LogInformation("   URL: {Url}", url);
                logger.LogInformation("   Status: {Status}", status);
                
                if (!string.IsNullOrWhiteSpace(server.Description))
                {
                    logger.LogInformation("   Description: {Description}", server.Description);
                }
                if (!string.IsNullOrWhiteSpace(server.Version))
                {
                    logger.LogInformation("   Version: {Version}", server.Version);
                }
                if (server.PublishedDate.HasValue)
                {
                    logger.LogInformation("   Published: {PublishedDate:yyyy-MM-dd HH:mm:ss}", server.PublishedDate.Value);
                }
                if (!string.IsNullOrWhiteSpace(server.EnvironmentId))
                {
                    logger.LogInformation("   Environment ID: {EnvironmentId}", server.EnvironmentId);
                }
            }
            logger.LogInformation("Listed {Count} MCP server(s) in environment {EnvId}", servers.Length, envId);

        }, envIdOption, configOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Creates the publish subcommand
    /// </summary>
    private static Command CreatePublishSubcommand(
        ILogger logger, 
        IAgent365ToolingService toolingService)
    {
        var command = new Command("publish", "Publish an MCP server to a Dataverse environment");

        var envIdOption = new Option<string?>(
            ["--environment-id", "-e"],
            description: "Dataverse environment ID"
        );
        envIdOption.IsRequired = false; // Allow null so we can prompt
        command.AddOption(envIdOption);

        var serverNameOption = new Option<string?>(
            ["--server-name", "-s"],
            description: "MCP server name to publish"
        );
        serverNameOption.IsRequired = false; // Allow null so we can prompt
        command.AddOption(serverNameOption);

        var aliasOption = new Option<string?>(
            ["--alias", "-a"],
            description: "Alias for the MCP server"
        );
        command.AddOption(aliasOption);

        var displayNameOption = new Option<string?>(
            ["--display-name", "-d"],
            description: "Display name for the MCP server"
        );
        command.AddOption(displayNameOption);

        var configOption = new Option<string>(
            ["-c", "--config"],
            getDefaultValue: () => "a365.config.json",
            description: "Configuration file path"
        );
        command.AddOption(configOption);

        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "Show what would be done without executing"
        );
        command.AddOption(dryRunOption);

        command.SetHandler(async (envId, serverName, alias, displayName, configPath, dryRun) =>
        {
            // Prompt for missing required arguments
            if (string.IsNullOrWhiteSpace(envId))
            {
                Console.Write("Enter Dataverse environment ID: ");
                envId = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(envId))
                {
                    logger.LogError("Environment ID is required");
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(serverName))
            {
                Console.Write("Enter MCP server name to publish: ");
                serverName = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(serverName))
                {
                    logger.LogError("Server name is required");
                    return;
                }
            }

            logger.LogInformation("Starting publish operation for server {ServerName} in environment {EnvId}...", serverName, envId);

            if (dryRun)
            {
                logger.LogInformation("[DRY RUN] Would read config from {ConfigPath}", configPath);
                logger.LogInformation("[DRY RUN] Would publish MCP server {ServerName} to environment {EnvId}", serverName, envId);
                logger.LogInformation("[DRY RUN] Alias: {Alias}", alias ?? "[would prompt]");
                logger.LogInformation("[DRY RUN] Display Name: {DisplayName}", displayName ?? "[would prompt]");
                await Task.CompletedTask;
                return;
            }

            // Prompt for missing optional values
            if (string.IsNullOrWhiteSpace(alias))
            {
                Console.Write("Enter alias for the MCP server: ");
                alias = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(alias))
                {
                    logger.LogError("Alias is required");
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                Console.Write("Enter display name for the MCP server: ");
                displayName = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    logger.LogError("Display name is required");
                    return;
                }
            }

            // Create request
            var request = new PublishMcpServerRequest
            {
                Alias = alias,
                DisplayName = displayName
            };

            // Call service
            var response = await toolingService.PublishServerAsync(envId, serverName, request);

            if (response == null || !response.IsSuccess)
            {
                if (response?.Message != null)
                {
                    logger.LogError("Failed to publish MCP server {ServerName} to environment {EnvId}: {ErrorMessage}", serverName, envId, response.Message);
                }
                else
                {
                    logger.LogError("Failed to publish MCP server {ServerName} to environment {EnvId}: No response received", serverName, envId);
                }
                return;
            }

            logger.LogInformation("Successfully published MCP server {ServerName} to environment {EnvId}", serverName, envId);

        }, envIdOption, serverNameOption, aliasOption, displayNameOption, configOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Creates the unpublish subcommand
    /// </summary>
    private static Command CreateUnpublishSubcommand(
        ILogger logger, 
        IAgent365ToolingService toolingService)
    {
        var command = new Command("unpublish", "Unpublish an MCP server from a Dataverse environment");

        var envIdOption = new Option<string?>(
            ["--environment-id", "-e"],
            description: "Dataverse environment ID"
        );
        envIdOption.IsRequired = false; // Allow null so we can prompt
        command.AddOption(envIdOption);

        var serverNameOption = new Option<string?>(
            ["--server-name", "-s"],
            description: "MCP server name to unpublish"
        );
        serverNameOption.IsRequired = false; // Allow null so we can prompt
        command.AddOption(serverNameOption);

        var configOption = new Option<string>(
            ["-c", "--config"],
            getDefaultValue: () => "a365.config.json",
            description: "Configuration file path"
        );
        command.AddOption(configOption);

        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "Show what would be done without executing"
        );
        command.AddOption(dryRunOption);

        command.SetHandler(async (envId, serverName, configPath, dryRun) =>
        {
            // Prompt for missing required arguments
            if (string.IsNullOrWhiteSpace(envId))
            {
                Console.Write("Enter Dataverse environment ID: ");
                envId = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(envId))
                {
                    logger.LogError("Environment ID is required");
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(serverName))
            {
                Console.Write("Enter MCP server name to unpublish: ");
                serverName = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(serverName))
                {
                    logger.LogError("Server name is required");
                    return;
                }
            }

            logger.LogInformation("Starting unpublish operation for server {ServerName} in environment {EnvId}...", serverName, envId);

            if (dryRun)
            {
                logger.LogInformation("[DRY RUN] Would read config from {ConfigPath}", configPath);
                logger.LogInformation("[DRY RUN] Would unpublish MCP server {ServerName} from environment {EnvId}", serverName, envId);
                await Task.CompletedTask;
                return;
            }

            // Call service
            var success = await toolingService.UnpublishServerAsync(envId, serverName);

            if (!success)
            {
                logger.LogError("Failed to unpublish MCP server {ServerName} from environment {EnvId}", serverName, envId);
                return;
            }

            logger.LogInformation("Successfully unpublished MCP server {ServerName} from environment {EnvId}", serverName, envId);

        }, envIdOption, serverNameOption, configOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Creates the approve subcommand (not implemented)
    /// </summary>
    private static Command CreateApproveSubcommand(ILogger logger, IAgent365ToolingService toolingService)
    {
        var command = new Command("approve", "Approve an MCP server");

        var serverNameOption = new Option<string?>(
            ["--server-name", "-s"],
            description: "MCP server name to approve"
        );
        serverNameOption.IsRequired = false; // Allow null so we can prompt
        command.AddOption(serverNameOption);

        var configOption = new Option<string>(
            ["-c", "--config"],
            getDefaultValue: () => "a365.config.json",
            description: "Configuration file path"
        );
        command.AddOption(configOption);

        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "Show what would be done without executing"
        );
        command.AddOption(dryRunOption);

        command.SetHandler(async (serverName, configPath, dryRun) =>
        {
            // Prompt for missing required arguments
            if (string.IsNullOrWhiteSpace(serverName))
            {
                Console.Write("Enter MCP server name to approve: ");
                serverName = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(serverName))
                {
                    logger.LogError("Server name is required");
                    return;
                }
            }

            logger.LogInformation("Starting approve operation for server {ServerName}...", serverName);

            if (dryRun)
            {
                logger.LogInformation("[DRY RUN] Would read config from {ConfigPath}", configPath);
                logger.LogInformation("[DRY RUN] Would approve MCP server {ServerName}", serverName);
                await Task.CompletedTask;
                return;
            }

            // Call service
            var success = await toolingService.ApproveServerAsync(serverName);

            if (!success)
            {
                logger.LogError("Failed to approve MCP server {ServerName}", serverName);
                return;
            }

            logger.LogInformation("Successfully approved MCP server {ServerName}", serverName);

        }, serverNameOption, configOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Creates the block subcommand (not yet implemented)
    /// </summary>
    private static Command CreateBlockSubcommand(ILogger logger, IAgent365ToolingService toolingService)
    {
        var command = new Command("block", "Block an MCP server");

        var serverNameOption = new Option<string?>(
            ["--server-name", "-s"],
            description: "MCP server name to block"
        );
        serverNameOption.IsRequired = false; // Allow null so we can prompt
        command.AddOption(serverNameOption);

        var configOption = new Option<string>(
            ["-c", "--config"],
            getDefaultValue: () => "a365.config.json",
            description: "Configuration file path"
        );
        command.AddOption(configOption);

        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "Show what would be done without executing"
        );
        command.AddOption(dryRunOption);

        command.SetHandler(async (serverName, configPath, dryRun) =>
        {
            // Prompt for missing required arguments
            if (string.IsNullOrWhiteSpace(serverName))
            {
                Console.Write("Enter MCP server name to block: ");
                serverName = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(serverName))
                {
                    logger.LogError("Server name is required");
                    return;
                }
            }

            logger.LogInformation("Starting block operation for server {ServerName}...", serverName);

            if (dryRun)
            {
                logger.LogInformation("[DRY RUN] Would read config from {ConfigPath}", configPath);
                logger.LogInformation("[DRY RUN] Would block MCP server {ServerName}", serverName);
                await Task.CompletedTask;
                return;
            }

            // Call service
            var success = await toolingService.BlockServerAsync(serverName);

            if (!success)
            {
                logger.LogError("Failed to block MCP server {ServerName}", serverName);
                return;
            }

            logger.LogInformation("Successfully blocked MCP server {ServerName}", serverName);

        }, serverNameOption, configOption, dryRunOption);

        return command;
    }
}
