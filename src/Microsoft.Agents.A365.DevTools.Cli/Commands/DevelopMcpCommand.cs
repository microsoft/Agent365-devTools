// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using static Microsoft.Agents.A365.DevTools.Cli.Helpers.PackageMCPServerHelper;

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

        // Add minimal options - config is optional and not advertised (for internal developers only)
        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Enable verbose logging");

        developMcpCommand.AddOption(verboseOption);

        // Add subcommands
        developMcpCommand.AddCommand(CreateListEnvironmentsSubcommand(logger, toolingService));
        developMcpCommand.AddCommand(CreateListServersSubcommand(logger, toolingService));
        developMcpCommand.AddCommand(CreatePublishSubcommand(logger, toolingService));
        developMcpCommand.AddCommand(CreateUnpublishSubcommand(logger, toolingService));
        developMcpCommand.AddCommand(CreateApproveSubcommand(logger, toolingService));
        developMcpCommand.AddCommand(CreateBlockSubcommand(logger, toolingService));
        developMcpCommand.AddCommand(CreatePackageMCPServerSubCommand(logger, toolingService));

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

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Enable verbose logging"
        );
        command.AddOption(verboseOption);

        command.SetHandler(async (configPath, dryRun, verbose) =>
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
            logger.LogDebug("API call completed - received response with {Count} environment(s)", 
                environmentsResponse?.Environments?.Length ?? 0);

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
                
                // Show additional details in debug mode
                if (!string.IsNullOrWhiteSpace(env.TenantId))
                {
                    logger.LogDebug("   Tenant ID: {TenantId}", env.TenantId);
                }
            }

            logger.LogInformation("Listed {Count} Dataverse environment(s)", environmentsResponse.Environments.Length);

        }, configOption, dryRunOption, verboseOption);

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

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Enable verbose logging"
        );
        command.AddOption(verboseOption);

        command.SetHandler(async (envId, configPath, dryRun, verbose) =>
        {
            try
            {
                // Validate and prompt for missing required argument with security checks
                if (string.IsNullOrWhiteSpace(envId))
                {
                    envId = InputValidator.PromptAndValidateRequiredInput("Enter Dataverse environment ID: ", "Environment ID");
                    if (string.IsNullOrWhiteSpace(envId))
                    {
                        logger.LogError("Environment ID is required");
                        return;
                    }
                }
                else
                {
                    // Validate provided environment ID
                    envId = InputValidator.ValidateInput(envId, "Environment ID");
                    if (envId == null)
                    {
                        logger.LogError("Invalid environment ID format");
                        return;
                    }
                }
            }
            catch (ArgumentException ex)
            {
                logger.LogError("Input validation failed: {Message}", ex.Message);
                return;
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

        }, envIdOption, configOption, dryRunOption, verboseOption);

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
            try
            {
                // Validate and prompt for missing required arguments with security checks
                if (string.IsNullOrWhiteSpace(envId))
                {
                    envId = InputValidator.PromptAndValidateRequiredInput("Enter Dataverse environment ID: ", "Environment ID");
                    if (string.IsNullOrWhiteSpace(envId))
                    {
                        logger.LogError("Environment ID is required");
                        return;
                    }
                }
                else
                {
                    // Validate provided environment ID
                    envId = InputValidator.ValidateInput(envId, "Environment ID");
                    if (envId == null)
                    {
                        logger.LogError("Invalid environment ID format");
                        return;
                    }
                }

                if (string.IsNullOrWhiteSpace(serverName))
                {
                    serverName = InputValidator.PromptAndValidateRequiredInput("Enter MCP server name to publish: ", "Server name", 100);
                    if (string.IsNullOrWhiteSpace(serverName))
                    {
                        logger.LogError("Server name is required");
                        return;
                    }
                }
                else
                {
                    // Validate provided server name
                    serverName = InputValidator.ValidateInput(serverName, "Server name");
                    if (serverName == null)
                    {
                        logger.LogError("Invalid server name format");
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

                // Validate and prompt for missing optional values with security checks
                if (string.IsNullOrWhiteSpace(alias))
                {
                    alias = InputValidator.PromptAndValidateRequiredInput("Enter alias for the MCP server: ", "Alias", 50);
                    if (string.IsNullOrWhiteSpace(alias))
                    {
                        logger.LogError("Alias is required");
                        return;
                    }
                }
                else
                {
                    // Validate provided alias
                    alias = InputValidator.ValidateInput(alias, "Alias", maxLength: 50);
                    if (alias == null)
                    {
                        logger.LogError("Invalid alias format");
                        return;
                    }
                }

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = InputValidator.PromptAndValidateRequiredInput("Enter display name for the MCP server: ", "Display name", 100);
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        logger.LogError("Display name is required");
                        return;
                    }
                }
                else
                {
                    // Validate provided display name
                    displayName = InputValidator.ValidateInput(displayName, "Display name", maxLength: 100);
                    if (displayName == null)
                    {
                        logger.LogError("Invalid display name format");
                        return;
                    }
                }
            }
            catch (ArgumentException ex)
            {
                logger.LogError("Input validation failed: {Message}", ex.Message);
                return;
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
            try
            {
                // Validate and prompt for missing required arguments with security checks
                if (string.IsNullOrWhiteSpace(envId))
                {
                    envId = InputValidator.PromptAndValidateRequiredInput("Enter Dataverse environment ID: ", "Environment ID");
                    if (string.IsNullOrWhiteSpace(envId))
                    {
                        logger.LogError("Environment ID is required");
                        return;
                    }
                }
                else
                {
                    // Validate provided environment ID
                    envId = InputValidator.ValidateInput(envId, "Environment ID");
                    if (envId == null)
                    {
                        logger.LogError("Invalid environment ID format");
                        return;
                    }
                }

                if (string.IsNullOrWhiteSpace(serverName))
                {
                    serverName = InputValidator.PromptAndValidateRequiredInput("Enter MCP server name to unpublish: ", "Server name", 100);
                    if (string.IsNullOrWhiteSpace(serverName))
                    {
                        logger.LogError("Server name is required");
                        return;
                    }
                }
                else
                {
                    // Validate provided server name
                    serverName = InputValidator.ValidateInput(serverName, "Server name");
                    if (serverName == null)
                    {
                        logger.LogError("Invalid server name format");
                        return;
                    }
                }
            }
            catch (ArgumentException ex)
            {
                logger.LogError("Input validation failed: {Message}", ex.Message);
                return;
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
    /// Creates the approve subcommand
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
            try
            {
                // Validate and prompt for missing required arguments with security checks
                if (string.IsNullOrWhiteSpace(serverName))
                {
                    serverName = InputValidator.PromptAndValidateRequiredInput("Enter MCP server name to approve: ", "Server name", 100);
                    if (string.IsNullOrWhiteSpace(serverName))
                    {
                        logger.LogError("Server name is required");
                        return;
                    }
                }
                else
                {
                    // Validate provided server name
                    serverName = InputValidator.ValidateInput(serverName, "Server name");
                    if (serverName == null)
                    {
                        logger.LogError("Invalid server name format");
                        return;
                    }
                }
            }
            catch (ArgumentException ex)
            {
                logger.LogError("Input validation failed: {Message}", ex.Message);
                return;
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
    /// Creates the block subcommand
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
            try
            {
                // Validate and prompt for missing required arguments with security checks
                if (string.IsNullOrWhiteSpace(serverName))
                {
                    serverName = InputValidator.PromptAndValidateRequiredInput("Enter MCP server name to block: ", "Server name", 100);
                    if (string.IsNullOrWhiteSpace(serverName))
                    {
                        logger.LogError("Server name is required");
                        return;
                    }
                }
                else
                {
                    // Validate provided server name
                    serverName = InputValidator.ValidateInput(serverName, "Server name");
                    if (serverName == null)
                    {
                        logger.LogError("Invalid server name format");
                        return;
                    }
                }
            }
            catch (ArgumentException ex)
            {
                logger.LogError("Input validation failed: {Message}", ex.Message);
                return;
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

    /// <summary>
    /// Creates the package generation subcommand
    /// </summary>
    private static Command CreatePackageMCPServerSubCommand(ILogger logger, IAgent365ToolingService toolingService)
    {
        var command = new Command("package-mcp-server", "Generate MCP server package for submission on Microsoft admin center");

        var serverNameOption = new Option<string>("--server-name", "MCP server name") { IsRequired = true };
        var developerNameOption = new Option<string>("--developer-name", "Publisher/developer display name") { IsRequired = true };
        var iconUrlOption = new Option<string>("--icon-url", "Public URL to a PNG icon for the MCP server") { IsRequired = true };
        var outputPathOption = new Option<string>("--output-path", "Target directory for the generated ZIP package") { IsRequired = true };
        var dryRunOption = new Option<bool>(name: "--dry-run", description: "Show what would be done without executing");
        var configOption = new Option<string>(["-c", "--config"], getDefaultValue: () => "a365.config.json", description: "Configuration file path");

        command.AddOption(serverNameOption);
        command.AddOption(developerNameOption);
        command.AddOption(iconUrlOption);
        command.AddOption(outputPathOption);
        command.AddOption(dryRunOption);
        command.AddOption(configOption);

        command.SetHandler(async (serverName, developerName, iconUrl, outputPath, dryRun) =>
        {
            if (dryRun)
            {
                logger.LogInformation("[DRY RUN] Would query MCP servers management endpoint to fetch details of the MCP server");
                logger.LogInformation("[DRY RUN] Fetch the icon from the provided url");
                logger.LogInformation("[DRY RUN] Build the package content and put it in the target directory");
                await Task.CompletedTask;
                return;
            }

            logger.LogInformation("Starting package creation...");

            try
            {
                var serverInfo = await toolingService.GetServerInfoAsync(serverName);
                var manifest = PackageMCPServerHelper.GenerateManifestJson(serverInfo, developerName, logger);
                var zipFilePath = PackageMCPServerHelper.BuildPackage(manifest, serverInfo, iconUrl, outputPath);
                logger.LogInformation("Package was created successfully at {zipFilePath}", zipFilePath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Package creation failed");
            }

        }, serverNameOption, developerNameOption, iconUrlOption, outputPathOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Validates and sanitizes user input following Azure CLI security patterns
    /// </summary>
    private static class InputValidator
    {
        private static readonly char[] InvalidChars = ['<', '>', '"', '|', '\0', '\u0001', '\u0002', '\u0003', '\u0004', '\u0005', '\u0006', '\u0007', '\u0008', '\u0009', '\u000a', '\u000b', '\u000c', '\u000d', '\u000e', '\u000f', '\u0010', '\u0011', '\u0012', '\u0013', '\u0014', '\u0015', '\u0016', '\u0017', '\u0018', '\u0019', '\u001a', '\u001b', '\u001c', '\u001d', '\u001e', '\u001f'];

        /// <summary>
        /// Prompts for and validates a required string input
        /// </summary>
        public static string? PromptAndValidateRequiredInput(string promptText, string fieldName, int maxLength = 255)
        {
            Console.Write(promptText);
            var input = Console.ReadLine()?.Trim();
            
            return ValidateInput(input, fieldName, isRequired: true, maxLength);
        }

        /// <summary>
        /// Prompts for and validates an optional string input
        /// </summary>
        public static string? PromptAndValidateOptionalInput(string promptText, string fieldName, int maxLength = 255)
        {
            Console.Write(promptText);
            var input = Console.ReadLine()?.Trim();
            
            return ValidateInput(input, fieldName, isRequired: false, maxLength);
        }

        /// <summary>
        /// Validates string input following Azure CLI security patterns
        /// </summary>
        public static string? ValidateInput(string? input, string fieldName, bool isRequired = true, int maxLength = 255)
        {
            // Handle null or empty input
            if (string.IsNullOrWhiteSpace(input))
            {
                return isRequired ? null : string.Empty;
            }

            // Trim and validate length
            input = input.Trim();
            if (input.Length > maxLength)
            {
                throw new ArgumentException($"{fieldName} cannot exceed {maxLength} characters");
            }

            // Check for dangerous characters that could be used in injection attacks
            if (input.IndexOfAny(InvalidChars) != -1)
            {
                throw new ArgumentException($"{fieldName} contains invalid characters");
            }

            // Additional validation for environment ID (must be reasonable identifier)
            if (fieldName.Equals("Environment ID", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsValidEnvironmentId(input))
                {
                    throw new ArgumentException("Environment ID must be a valid identifier (GUID or alphanumeric with hyphens)");
                }
            }

            // Additional validation for server names (alphanumeric, hyphens, underscores only)
            if (fieldName.Equals("Server name", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsValidServerName(input))
                {
                    throw new ArgumentException("Server name can only contain alphanumeric characters, hyphens, and underscores");
                }
            }

            return input;
        }

        /// <summary>
        /// Validates environment ID format (GUID or reasonable test identifier)
        /// </summary>
        private static bool IsValidEnvironmentId(string input)
        {
            // Accept GUID format (production case)
            if (Guid.TryParse(input, out _))
                return true;

            // Accept alphanumeric identifiers with hyphens for test scenarios
            // Must start with alphanumeric character and contain only safe characters
            if (string.IsNullOrWhiteSpace(input))
                return false;

            if (!char.IsLetterOrDigit(input[0]))
                return false;

            return input.All(c => char.IsLetterOrDigit(c) || c == '-');
        }

        /// <summary>
        /// Validates GUID format for strict GUID requirements
        /// </summary>
        private static bool IsValidGuidFormat(string input)
        {
            return Guid.TryParse(input, out _);
        }

        /// <summary>
        /// Validates server name format (alphanumeric, hyphens, underscores)
        /// </summary>
        private static bool IsValidServerName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            // Must start with alphanumeric character
            if (!char.IsLetterOrDigit(input[0]))
                return false;

            // Can contain only letters, digits, hyphens, and underscores
            return input.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
        }
    }
}
