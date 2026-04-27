// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Text.Json;
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
        IAgent365ToolingService toolingService,
        GraphApiService? graphApiService = null)
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
        developMcpCommand.AddCommand(CreateRegisterExternalMcpServerSubcommand(logger, toolingService, graphApiService));
        developMcpCommand.AddCommand(CreateDeleteExternalMcpServerSubcommand(logger, toolingService, graphApiService));

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
    /// Creates the register-external-mcp-server subcommand
    /// </summary>
    private static Command CreateRegisterExternalMcpServerSubcommand(
        ILogger logger,
        IAgent365ToolingService toolingService,
        GraphApiService? graphApiService)
    {
        var command = new Command("register-external-mcp-server", "Register an external MCP server with Entra, ExternalIDP, or NoAuth authentication");

        var serverNameOption = new Option<string?>(["--server-name", "-s"], description: "MCP server name (max 22 chars). If no '<prefix>_' is present, 'ext_' is auto-prepended.");
        command.AddOption(serverNameOption);

        var serverUrlOption = new Option<string?>(["--server-url", "-u"], description: "Remote MCP server URL");
        command.AddOption(serverUrlOption);

        var authTypeOption = new Option<string?>(["--auth-type", "-a"], description: "Authentication type: EntraOAuth, ExternalOAuth, APIKey, or NoAuth");
        command.AddOption(authTypeOption);

        // ExternalOAuth-specific options
        var idpAuthUrlOption = new Option<string?>("--idp-authorization-url", description: "External OAuth authorization URL (required for ExternalOAuth)");
        command.AddOption(idpAuthUrlOption);

        var idpTokenUrlOption = new Option<string?>("--idp-token-url", description: "External OAuth token URL (required for ExternalOAuth)");
        command.AddOption(idpTokenUrlOption);

        var idpScopesOption = new Option<string?>("--idp-scopes", description: "External OAuth scopes (required for ExternalOAuth)");
        command.AddOption(idpScopesOption);

        var idpClientIdOption = new Option<string?>("--idp-client-id", description: "External OAuth client ID (required for ExternalOAuth)");
        command.AddOption(idpClientIdOption);

        var idpClientSecretOption = new Option<string?>("--idp-client-secret", description: "External OAuth client secret (required for ExternalOAuth)");
        command.AddOption(idpClientSecretOption);

        // APIKey-specific options
        var apiKeyLocationOption = new Option<string?>("--api-key-location", description: "API key location: Header or Query (required for APIKey)");
        command.AddOption(apiKeyLocationOption);

        var apiKeyNameOption = new Option<string?>("--api-key-name", description: "API key parameter/header name, e.g. 'X-API-Key' or 'token' (required for APIKey)");
        command.AddOption(apiKeyNameOption);

        var toolsOption = new Option<string?>("--tools", description: "Comma-separated list of tool names exposed by this server (e.g., 'tool1,tool2,tool3')");
        command.AddOption(toolsOption);

        var inputFileOption = new Option<string?>(["--input-file", "-f"], description: "Path to JSON file with register parameters (see sample: register-external-mcp-server-sample.json)");
        command.AddOption(inputFileOption);

        var remoteScopesOption = new Option<string?>("--remote-scopes", description: "Scopes for the remote MCP server (e.g., 'api://myapp/.default')");
        command.AddOption(remoteScopesOption);

        var tenantIdOption = new Option<string?>(["--tenant-id", "-t"], description: "Entra tenant ID for app registration (defaults to current az login tenant)");
        command.AddOption(tenantIdOption);

        var serviceTreeIdOption = new Option<string?>("--service-tree-id", description: "ServiceTree ID for Entra app registration (required in Microsoft corporate tenants)");
        command.AddOption(serviceTreeIdOption);

        var configOption = new Option<string>(["-c", "--config"], getDefaultValue: () => "a365.config.json", description: "Configuration file path");
        command.AddOption(configOption);

        var publisherOption = new Option<string?>("--publisher", description: "Publisher name (required, used in MOS package metadata)");
        command.AddOption(publisherOption);

        var descriptionOption = new Option<string?>("--description", description: "Server description (required, used in MOS package metadata)");
        command.AddOption(descriptionOption);

        var forceOption = new Option<bool>("--force", description: "Force re-creation of Entra apps and connectors");
        command.AddOption(forceOption);

        var dryRunOption = new Option<bool>("--dry-run", description: "Show what would be done without executing");
        command.AddOption(dryRunOption);

        var verboseOption = new Option<bool>(["--verbose", "-v"], description: "Enable verbose logging");
        command.AddOption(verboseOption);

        command.SetHandler(async (context) =>
        {
            var serverName = context.ParseResult.GetValueForOption(serverNameOption);
            var serverUrl = context.ParseResult.GetValueForOption(serverUrlOption);
            var authType = context.ParseResult.GetValueForOption(authTypeOption);
            var idpAuthUrl = context.ParseResult.GetValueForOption(idpAuthUrlOption);
            var idpTokenUrl = context.ParseResult.GetValueForOption(idpTokenUrlOption);
            var idpScopes = context.ParseResult.GetValueForOption(idpScopesOption);
            var idpClientId = context.ParseResult.GetValueForOption(idpClientIdOption);
            var idpClientSecret = context.ParseResult.GetValueForOption(idpClientSecretOption);
            var apiKeyLocation = context.ParseResult.GetValueForOption(apiKeyLocationOption);
            var apiKeyName = context.ParseResult.GetValueForOption(apiKeyNameOption);
            var toolsInput = context.ParseResult.GetValueForOption(toolsOption);
            var inputFile = context.ParseResult.GetValueForOption(inputFileOption);
            var remoteScopes = context.ParseResult.GetValueForOption(remoteScopesOption);
            var userTenantId = context.ParseResult.GetValueForOption(tenantIdOption);
            var serviceTreeId = context.ParseResult.GetValueForOption(serviceTreeIdOption);
            var publisherName = context.ParseResult.GetValueForOption(publisherOption);
            var serverDescription = context.ParseResult.GetValueForOption(descriptionOption);
            var force = context.ParseResult.GetValueForOption(forceOption);
            var configPath = context.ParseResult.GetValueForOption(configOption)!;
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);

            // Load input file if provided, and use file values as defaults for CLI options not explicitly set
            RegisterExternalMcpServerInput? inputFileData = null;
            if (!string.IsNullOrWhiteSpace(inputFile))
            {
                if (!File.Exists(inputFile))
                {
                    logger.LogError("Input file not found: {InputFile}", inputFile);
                    return;
                }

                try
                {
                    var jsonContent = await File.ReadAllTextAsync(inputFile);
                    inputFileData = JsonSerializer.Deserialize<RegisterExternalMcpServerInput>(jsonContent);
                }
                catch (JsonException ex)
                {
                    logger.LogError("Failed to parse input file '{InputFile}': {Error}", inputFile, ex.Message);
                    return;
                }

                if (inputFileData is not null)
                {
                    logger.LogDebug("Loaded input file: {InputFile}", inputFile);

                    // Apply file values as defaults where CLI options were not provided
                    serverName ??= inputFileData.ServerName;
                    serverUrl ??= inputFileData.ServerUrl;
                    authType ??= inputFileData.AuthType;
                    remoteScopes ??= inputFileData.RemoteScopes;
                    userTenantId ??= inputFileData.TenantId;
                    serviceTreeId ??= inputFileData.ServiceTreeId;
                    publisherName ??= inputFileData.PublisherName;
                    serverDescription ??= inputFileData.Description;
                    force = force || inputFileData.Force;

                    // ExternalOAuth fields
                    if (inputFileData.ExternalOAuth is not null)
                    {
                        idpAuthUrl ??= inputFileData.ExternalOAuth.AuthorizationUrl;
                        idpTokenUrl ??= inputFileData.ExternalOAuth.TokenUrl;
                        idpScopes ??= inputFileData.ExternalOAuth.Scopes;
                        idpClientId ??= inputFileData.ExternalOAuth.ClientId;
                        idpClientSecret ??= inputFileData.ExternalOAuth.ClientSecret;
                    }

                    // APIKey fields
                    if (inputFileData.ApiKey is not null)
                    {
                        apiKeyLocation ??= inputFileData.ApiKey.Location;
                        apiKeyName ??= inputFileData.ApiKey.Name;
                    }
                }
            }

            var isEntra = false;
            var isExternalIdp = false;
            var isNoAuth = false;
            var isApiKey = false;
            List<string>? toolList = null;
            Dictionary<string, string>? toolDescriptions = null;

            try
            {
                // Validate required inputs
                if (string.IsNullOrWhiteSpace(serverName))
                {
                    serverName = InputValidator.PromptAndValidateRequiredInput("Enter MCP server name to register: ", "Server name", 100);
                    if (string.IsNullOrWhiteSpace(serverName)) { logger.LogError("Server name is required"); return; }
                }
                else
                {
                    serverName = InputValidator.ValidateInput(serverName, "Server name");
                    if (serverName == null) { logger.LogError("Invalid server name format"); return; }
                }

                // Auto-prepend "ext_" if the server name doesn't contain a prefix (no '_' found)
                if (!serverName.Contains('_'))
                {
                    serverName = $"ext_{serverName}";
                    logger.LogDebug("Server name auto-prefixed to '{ServerName}' (no prefix detected)", serverName);
                }

                // Validate server name length (max 27 chars including prefix)
                const int maxServerNameLength = 22;
                if (serverName.Length > maxServerNameLength)
                {
                    logger.LogError("Server name '{ServerName}' is {Length} characters, exceeding the maximum of {Max} characters (including prefix)", serverName, serverName.Length, maxServerNameLength);
                    return;
                }

                if (string.IsNullOrWhiteSpace(serverUrl))
                {
                    serverUrl = InputValidator.PromptAndValidateRequiredInput("Enter remote MCP server URL: ", "Server URL", 500);
                    if (string.IsNullOrWhiteSpace(serverUrl)) { logger.LogError("Server URL is required"); return; }
                }

                // Validate auth type
                if (string.IsNullOrWhiteSpace(authType))
                {
                    authType = InputValidator.PromptAndValidateRequiredInput("Enter authentication type (EntraOAuth, ExternalOAuth, APIKey, or NoAuth): ", "Auth type", 20);
                    if (string.IsNullOrWhiteSpace(authType)) { logger.LogError("Auth type is required"); return; }
                }

                // Normalize legacy auth type names
                if (authType.Equals("Entra", StringComparison.OrdinalIgnoreCase)) authType = "EntraOAuth";
                if (authType.Equals("ExternalIDP", StringComparison.OrdinalIgnoreCase)) authType = "ExternalOAuth";

                isEntra = authType.Equals("EntraOAuth", StringComparison.OrdinalIgnoreCase);
                isExternalIdp = authType.Equals("ExternalOAuth", StringComparison.OrdinalIgnoreCase);
                isNoAuth = authType.Equals("NoAuth", StringComparison.OrdinalIgnoreCase);
                isApiKey = authType.Equals("APIKey", StringComparison.OrdinalIgnoreCase);
                if (!isEntra && !isExternalIdp && !isNoAuth && !isApiKey)
                {
                    logger.LogError("Invalid auth type '{AuthType}'. Must be 'EntraOAuth', 'ExternalOAuth', 'APIKey', or 'NoAuth'", authType);
                    return;
                }

                // For ExternalOAuth, collect IDP details
                if (isExternalIdp)
                {
                    if (string.IsNullOrWhiteSpace(idpAuthUrl))
                    {
                        idpAuthUrl = InputValidator.PromptAndValidateRequiredInput("Enter external OAuth authorization URL: ", "Authorization URL", 500);
                        if (string.IsNullOrWhiteSpace(idpAuthUrl)) { logger.LogError("Authorization URL is required for ExternalOAuth"); return; }
                    }

                    if (string.IsNullOrWhiteSpace(idpTokenUrl))
                    {
                        idpTokenUrl = InputValidator.PromptAndValidateRequiredInput("Enter external OAuth token URL: ", "Token URL", 500);
                        if (string.IsNullOrWhiteSpace(idpTokenUrl)) { logger.LogError("Token URL is required for ExternalOAuth"); return; }
                    }

                    if (string.IsNullOrWhiteSpace(idpScopes))
                    {
                        idpScopes = InputValidator.PromptAndValidateRequiredInput("Enter external OAuth scopes: ", "Scopes", 500);
                        if (string.IsNullOrWhiteSpace(idpScopes)) { logger.LogError("Scopes are required for ExternalOAuth"); return; }
                    }

                    if (string.IsNullOrWhiteSpace(idpClientId))
                    {
                        idpClientId = InputValidator.PromptAndValidateRequiredInput("Enter external OAuth client ID: ", "Client ID", 100);
                        if (string.IsNullOrWhiteSpace(idpClientId)) { logger.LogError("Client ID is required for ExternalOAuth"); return; }
                    }

                    if (string.IsNullOrWhiteSpace(idpClientSecret))
                    {
                        idpClientSecret = InputValidator.PromptAndValidateRequiredInput("Enter external OAuth client secret: ", "Client secret", 500);
                        if (string.IsNullOrWhiteSpace(idpClientSecret)) { logger.LogError("Client secret is required for ExternalOAuth"); return; }
                    }
                }

                // For APIKey, collect key location and name
                if (isApiKey)
                {
                    if (string.IsNullOrWhiteSpace(apiKeyLocation))
                    {
                        apiKeyLocation = InputValidator.PromptAndValidateRequiredInput("Enter API key location (Header or Query): ", "API key location", 10);
                        if (string.IsNullOrWhiteSpace(apiKeyLocation)) { logger.LogError("API key location is required for APIKey"); return; }
                    }

                    if (!apiKeyLocation.Equals("Header", StringComparison.OrdinalIgnoreCase) &&
                        !apiKeyLocation.Equals("Query", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogError("Invalid API key location '{Location}'. Must be 'Header' or 'Query'", apiKeyLocation);
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(apiKeyName))
                    {
                        var prompt = apiKeyLocation.Equals("Header", StringComparison.OrdinalIgnoreCase)
                            ? "Enter API key header name (e.g., 'X-API-Key'): "
                            : "Enter API key query parameter name (e.g., 'token'): ";
                        apiKeyName = InputValidator.PromptAndValidateRequiredInput(prompt, "API key name", 100);
                        if (string.IsNullOrWhiteSpace(apiKeyName)) { logger.LogError("API key name is required for APIKey"); return; }
                    }
                }

                // Tool names: CLI --tools > input file tools > interactive prompt
                if (string.IsNullOrWhiteSpace(toolsInput) && inputFileData?.Tools is not null && inputFileData.Tools.Count > 0)
                {
                    // Use tools from input file (both names and descriptions)
                    toolList = inputFileData.Tools.Select(t => t.Name).ToList();
                    toolDescriptions = new Dictionary<string, string>();
                    foreach (var tool in inputFileData.Tools)
                    {
                        if (!string.IsNullOrWhiteSpace(tool.Description))
                        {
                            toolDescriptions[tool.Name] = tool.Description;
                        }
                    }

                    logger.LogDebug("Tools loaded from input file: {Tools}", string.Join(", ", toolList));
                }
                else
                {
                    // CLI --tools or interactive prompt
                    if (string.IsNullOrWhiteSpace(toolsInput))
                    {
                        toolsInput = InputValidator.PromptAndValidateRequiredInput("Enter comma-separated list of tool names: ", "Tool names", 2000);
                        if (string.IsNullOrWhiteSpace(toolsInput)) { logger.LogError("At least one tool name is required"); return; }
                    }

                    toolList = toolsInput!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (toolList.Count == 0)
                    {
                        logger.LogError("At least one tool name is required");
                        return;
                    }

                    logger.LogDebug("Tools to register: {Tools}", string.Join(", ", toolList));
                }

                if (toolList.Count == 0)
                {
                    logger.LogError("At least one tool name is required");
                    return;
                }

                // Collect descriptions for tools that don't yet have one (interactive prompt fallback)
                toolDescriptions ??= new Dictionary<string, string>();
                foreach (var tool in toolList)
                {
                    if (!toolDescriptions.ContainsKey(tool))
                    {
                        var desc = InputValidator.PromptAndValidateRequiredInput($"Enter description for tool '{tool}': ", $"Description for tool '{tool}'", 200);
                        if (string.IsNullOrWhiteSpace(desc)) { logger.LogError("Tool description is required for '{Tool}'", tool); return; }
                        toolDescriptions[tool] = desc;
                    }
                }

                // Publisher name is required
                if (string.IsNullOrWhiteSpace(publisherName))
                {
                    publisherName = InputValidator.PromptAndValidateRequiredInput("Enter publisher name: ", "Publisher name", 200);
                    if (string.IsNullOrWhiteSpace(publisherName)) { logger.LogError("Publisher name is required"); return; }
                }

                // Server description is required
                if (string.IsNullOrWhiteSpace(serverDescription))
                {
                    serverDescription = InputValidator.PromptAndValidateRequiredInput("Enter server description: ", "Server description", 500);
                    if (string.IsNullOrWhiteSpace(serverDescription)) { logger.LogError("Server description is required"); return; }
                }

                // Remote scopes are optional — if empty, the Remote Proxy connector uses NoAuth
                // Skip for NoAuth and APIKey since no scopes are needed
                if (!isNoAuth && !isApiKey && string.IsNullOrWhiteSpace(remoteScopes))
                {
                    Console.Write("Enter scopes for the remote MCP server (leave empty for no auth): ");
                    remoteScopes = Console.ReadLine()?.Trim() ?? string.Empty;
                }
            }
            catch (ArgumentException ex)
            {
                logger.LogError("Input validation failed: {Message}", ex.Message);
                return;
            }

            // Display registration summary
            Console.WriteLine();
            Console.WriteLine("Registration Summary");
            Console.WriteLine("====================");
            Console.WriteLine($"  Server Name:    {serverName}");
            Console.WriteLine($"  Server URL:     {serverUrl}");
            Console.WriteLine($"  Auth Type:      {authType}");
            Console.WriteLine($"  Publisher:      {publisherName}");
            Console.WriteLine($"  Description:    {serverDescription}");
            if (toolList is not null)
            {
                Console.WriteLine($"  Tools:");
                foreach (var tool in toolList)
                {
                    var desc = toolDescriptions?.GetValueOrDefault(tool);
                    Console.WriteLine(desc is not null ? $"    - {tool}: {desc}" : $"    - {tool}");
                }
            }

            if (!isNoAuth && !isApiKey && !string.IsNullOrWhiteSpace(remoteScopes))
            {
                Console.WriteLine($"  Remote Scopes:  {remoteScopes}");
            }

            if (isExternalIdp)
            {
                Console.WriteLine($"  IDP Auth URL:   {idpAuthUrl}");
                Console.WriteLine($"  IDP Token URL:  {idpTokenUrl}");
                Console.WriteLine($"  IDP Scopes:     {idpScopes}");
                Console.WriteLine($"  IDP Client ID:  {idpClientId}");
            }

            if (isApiKey)
            {
                Console.WriteLine($"  API Key Location: {apiKeyLocation}");
                Console.WriteLine($"  API Key Name:     {apiKeyName}");
            }

            if (force)
            {
                Console.WriteLine($"  Force:          true");
            }

            Console.WriteLine();

            if (dryRun)
            {
                logger.LogInformation("[DRY RUN] Would create Entra apps and register MCP server {ServerName}", serverName);
                return;
            }

            await toolingService.LogRegisterUsageAsync(
                serverName, authType, toolList?.Count ?? 0);

            Console.WriteLine($"Registering MCP server '{serverName}'...");

            // Step 1: Create Entra app(s) and get secrets

            // Auto-detect tenant ID from az account if not provided
            var tenantId = userTenantId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                try
                {
                    var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = isWindows ? "cmd.exe" : "az",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    if (isWindows)
                    {
                        psi.ArgumentList.Add("/c");
                        psi.ArgumentList.Add("az");
                    }

                    psi.ArgumentList.Add("account");
                    psi.ArgumentList.Add("show");
                    psi.ArgumentList.Add("--query");
                    psi.ArgumentList.Add("tenantId");
                    psi.ArgumentList.Add("-o");
                    psi.ArgumentList.Add("tsv");

                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        var output = await proc.StandardOutput.ReadToEndAsync();
                        await proc.WaitForExitAsync();
                        tenantId = output?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(tenantId))
                        {
                            logger.LogDebug("Auto-detected tenant ID from az account: {TenantId}", tenantId);
                        }
                    }
                }
                catch
                {
                    logger.LogDebug("Could not auto-detect tenant ID from az account");
                }
            }

            Models.AddMcpServerAuthMetadata? authMetadata = null;
            string? remoteProxyAppObjectId = null;
            string? a365AppObjectId = null;

            // A365 Proxy app is always created (including NoAuth — needed for the A365 Proxy connector)
            if (graphApiService == null)
            {
                logger.LogError("Graph API service is not available. Cannot create Entra applications.");
                return;
            }

            // Force mode: delete existing Entra apps before recreating
            if (force)
            {
                logger.LogDebug("Force mode: looking up existing Entra apps to delete...");

                var existingA365ObjectId = await graphApiService.GetAppObjectIdByDisplayNameAsync(tenantId, $"{serverName}-A365Proxy");
                if (!string.IsNullOrWhiteSpace(existingA365ObjectId))
                {
                    logger.LogDebug("Deleting existing A365 Proxy app: {ObjectId}", existingA365ObjectId);
                    await graphApiService.DeleteEntraAppAsync(tenantId, existingA365ObjectId);
                }

                if (isEntra)
                {
                    var existingRemoteObjectId = await graphApiService.GetAppObjectIdByDisplayNameAsync(tenantId, $"{serverName}-RemoteProxy");
                    if (!string.IsNullOrWhiteSpace(existingRemoteObjectId))
                    {
                        logger.LogDebug("Deleting existing Remote Proxy app: {ObjectId}", existingRemoteObjectId);
                        await graphApiService.DeleteEntraAppAsync(tenantId, existingRemoteObjectId);
                    }
                }

                var existingPpmiObjectId = await graphApiService.GetAppObjectIdByDisplayNameAsync(tenantId, $"{serverName} - BYO");
                if (!string.IsNullOrWhiteSpace(existingPpmiObjectId))
                {
                    logger.LogDebug("Deleting existing PPMI (MCPServer) app: {ObjectId}", existingPpmiObjectId);
                    await graphApiService.DeleteEntraAppAsync(tenantId, existingPpmiObjectId);
                }
            }

            logger.LogDebug("Creating Entra application for A365 Proxy...");
            var a365App = await graphApiService.CreateEntraAppAsync(tenantId, $"{serverName}-A365Proxy", serviceTreeId: serviceTreeId);
            if (a365App == null)
            {
                logger.LogError("Failed to create Entra application for A365 Proxy");
                return;
            }

            var a365Secret = await graphApiService.AddAppPasswordAsync(tenantId, a365App.Value.ObjectId);
            if (string.IsNullOrWhiteSpace(a365Secret))
            {
                logger.LogError("Failed to create secret for A365 Proxy Entra application");
                return;
            }

            logger.LogDebug("Created A365 Proxy app: {ClientId}", a365App.Value.ClientId);
            a365AppObjectId = a365App.Value.ObjectId;

            if (isNoAuth || isApiKey)
            {
                // NoAuth/APIKey: only A365 Proxy app needed, no Remote Proxy app
                authMetadata = new Models.AddMcpServerAuthMetadata
                {
                    ClientApp1Id = a365App.Value.ClientId,
                    ClientApp1Secret = a365Secret,
                };
            }
            else
            {
                string clientApp2Id;
                string clientApp2Secret;

                if (isEntra)
                {
                    // Entra flow: create second Entra app for RemoteProxy
                    logger.LogDebug("Creating Entra application for Remote Proxy...");
                    var remoteApp = await graphApiService.CreateEntraAppAsync(tenantId, $"{serverName}-RemoteProxy", serviceTreeId: serviceTreeId);
                    if (remoteApp == null)
                    {
                        logger.LogError("Failed to create Entra application for Remote Proxy");
                        return;
                    }

                    var remoteSecret = await graphApiService.AddAppPasswordAsync(tenantId, remoteApp.Value.ObjectId);
                    if (string.IsNullOrWhiteSpace(remoteSecret))
                    {
                        logger.LogError("Failed to create secret for Remote Proxy Entra application");
                        return;
                    }

                    logger.LogDebug("Created Remote Proxy app: {ClientId}", remoteApp.Value.ClientId);
                    clientApp2Id = remoteApp.Value.ClientId;
                    clientApp2Secret = remoteSecret;
                    remoteProxyAppObjectId = remoteApp.Value.ObjectId;
                }
                else
                {
                    // ExternalIDP flow: use user-provided client ID and secret for RemoteProxy
                    clientApp2Id = idpClientId!;
                    clientApp2Secret = idpClientSecret!;
                }

                authMetadata = new Models.AddMcpServerAuthMetadata
                {
                    ClientApp1Id = a365App.Value.ClientId,
                    ClientApp1Secret = a365Secret,
                    ClientApp2Id = clientApp2Id,
                    ClientApp2Secret = clientApp2Secret,
                };
            }

            // Create Copilot (VS Code) Entra app — same pattern as A365 Proxy
            string? copilotAppClientId = null;
            string? copilotAppObjectId = null;

            if (force)
            {
                var existingCopilotObjectId = await graphApiService.GetAppObjectIdByDisplayNameAsync(tenantId, $"{serverName}-Copilot");
                if (!string.IsNullOrWhiteSpace(existingCopilotObjectId))
                {
                    logger.LogDebug("Deleting existing Copilot app: {ObjectId}", existingCopilotObjectId);
                    await graphApiService.DeleteEntraAppAsync(tenantId, existingCopilotObjectId);
                }
            }

            logger.LogDebug("Creating Entra application for Copilot (VS Code)...");
            var copilotApp = await graphApiService.CreateEntraAppAsync(tenantId, $"{serverName}-Copilot", serviceTreeId: serviceTreeId);
            if (copilotApp != null)
            {
                copilotAppClientId = copilotApp.Value.ClientId;
                copilotAppObjectId = copilotApp.Value.ObjectId;
                logger.LogDebug("Created Copilot app: {ClientId}", copilotAppClientId);

                var copilotRedirectUri = $"ms-appx-web://MicrosoftAAD.BrokerPlugin/{copilotAppClientId}";
                try
                {
                    await graphApiService.UpdateAppRedirectUrisAsync(tenantId, copilotAppObjectId, new[] { copilotRedirectUri });
                    logger.LogDebug("Set Copilot redirect URI: {Uri}", copilotRedirectUri);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Failed to set redirect URI on Copilot app: {Error}", ex.Message);
                }
            }
            else
            {
                logger.LogWarning("Failed to create Copilot Entra app. Continuing without it.");
            }

            // Track warnings for non-fatal failures during registration
            var warnings = new List<string>();

            // Step 2: Call Add MCP server API
            logger.LogDebug("Adding MCP server {ServerName}...", serverName);
            var addRequest = new Models.AddMcpServerRequest
            {
                ServerName = serverName,
                ServerUrl = serverUrl,
                ToolList = toolList,
                ToolDescriptions = toolDescriptions?.Count > 0 ? toolDescriptions : null,
                AuthType = authType,
                AuthMetadata = authMetadata,
                ExternalIdp = isExternalIdp ? new Models.ExternalIdpDetails
                {
                    AuthorizationUrl = idpAuthUrl,
                    TokenUrl = idpTokenUrl,
                    Scopes = idpScopes,
                } : null,
                ApiKeyDetails = isApiKey ? new Models.ApiKeyDetails
                {
                    Location = apiKeyLocation,
                    Name = apiKeyName,
                } : null,
                RemoteServerScopes = remoteScopes,
                PublisherName = publisherName,
                Description = serverDescription,
                CopilotClientAppId = copilotAppClientId,
                Force = force,
            };

            Models.AddMcpServerResponse? addResponse;
            try
            {
                addResponse = await toolingService.AddMcpServerAsync(addRequest);
            }
            catch (Exception ex)
            {
                logger.LogError("Failed to register MCP server '{ServerName}': {Error}", serverName, ex.Message);
                logger.LogDebug("Exception details: {Exception}", ex.ToString());
                return;
            }

            if (addResponse == null || !addResponse.IsSuccess)
            {
                var errorMsg = addResponse?.Message ?? "No response received";
                logger.LogError("Failed to add MCP server {ServerName}: {Error}", serverName, errorMsg);
                return;
            }

            logger.LogDebug("Successfully added MCP server {ServerName}", serverName);

            var a365RedirectUri = addResponse.Server?.A365ProxyRedirectUri;
            var remoteRedirectUri = addResponse.Server?.RemoteMCPServerProxyRedirectUri;

            // Step 3: Update redirect URIs on Entra apps
            // A365 Proxy always uses Entra AAD auth, so it always needs the redirect URI
            if (!string.IsNullOrWhiteSpace(a365RedirectUri) && a365AppObjectId != null)
            {
                try
                {
                    var a365OriginalUri = RemoveTcPrefix(a365RedirectUri);
                    var a365Uris = a365OriginalUri != null ? new[] { a365RedirectUri, a365OriginalUri } : new[] { a365RedirectUri };
                    logger.LogDebug("A365 Proxy Redirect URIs: {RedirectUris}", string.Join(", ", a365Uris));
                    logger.LogDebug("Updating redirect URIs on A365 Proxy Entra app...");
                    await graphApiService!.UpdateAppRedirectUrisAsync(tenantId, a365AppObjectId, a365Uris);
                }
                catch (Exception ex)
                {
                    var msg = $"Failed to update redirect URIs on A365 Proxy app: {ex.Message}";
                    logger.LogWarning(msg);
                    warnings.Add(msg);
                }
            }
            else if (a365AppObjectId != null)
            {
                var msg = "A365 Proxy redirect URI was not returned by the server. Redirect URI configuration skipped.";
                logger.LogWarning(msg);
                warnings.Add(msg);
            }

            if (isEntra && !string.IsNullOrWhiteSpace(remoteRedirectUri) && remoteProxyAppObjectId != null)
            {
                try
                {
                    var remoteOriginalUri = RemoveTcPrefix(remoteRedirectUri);
                    var remoteUris = remoteOriginalUri != null ? new[] { remoteRedirectUri, remoteOriginalUri } : new[] { remoteRedirectUri };
                    logger.LogDebug("Remote MCP Proxy Redirect URIs: {RedirectUris}", string.Join(", ", remoteUris));
                    logger.LogDebug("Updating redirect URIs on Remote Proxy Entra app...");
                    await graphApiService!.UpdateAppRedirectUrisAsync(tenantId, remoteProxyAppObjectId, remoteUris);
                }
                catch (Exception ex)
                {
                    var msg = $"Failed to update redirect URIs on Remote Proxy app: {ex.Message}";
                    logger.LogWarning(msg);
                    warnings.Add(msg);
                }
            }
            else if (isEntra)
            {
                var msg = "Remote MCP Proxy redirect URI was not returned by the server. Redirect URI configuration skipped.";
                logger.LogWarning(msg);
                warnings.Add(msg);
            }

            // Add API permissions on RemoteProxy app for the remote server scopes
            if (isEntra && remoteProxyAppObjectId != null && !string.IsNullOrWhiteSpace(remoteScopes))
            {
                try
                {
                    // Parse resource app ID and scope name from "api://{appId}/{scopeName}"
                    var scopeUri = remoteScopes.Trim();
                    string? resourceAppId = null;
                    string? scopeName = null;

                    if (scopeUri.StartsWith("api://", StringComparison.OrdinalIgnoreCase))
                    {
                        var path = scopeUri.Substring("api://".Length);
                        var slashIndex = path.IndexOf('/');
                        if (slashIndex > 0)
                        {
                            resourceAppId = path.Substring(0, slashIndex);
                            scopeName = path.Substring(slashIndex + 1);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(resourceAppId) && !string.IsNullOrWhiteSpace(scopeName))
                    {
                        logger.LogDebug("Looking up scope '{ScopeName}' on resource app {ResourceAppId}...", scopeName, resourceAppId);
                        var remoteScopeId = await graphApiService!.GetOAuth2PermissionScopeIdAsync(tenantId, resourceAppId, scopeName);
                        if (remoteScopeId.HasValue)
                        {
                            logger.LogDebug("Adding API permission for remote scope on RemoteProxy app...");
                            await graphApiService.AddRequiredResourceAccessAsync(
                                tenantId, remoteProxyAppObjectId, resourceAppId, remoteScopeId.Value);
                        }
                        else
                        {
                            var msg = $"Could not find scope '{scopeName}' on resource app {resourceAppId}. API permission not added to RemoteProxy app.";
                            logger.LogWarning(msg);
                            warnings.Add(msg);
                        }
                    }
                    else
                    {
                        var msg = $"Could not parse resource app ID and scope from '{remoteScopes}'. Expected format: api://{{appId}}/{{scopeName}}";
                        logger.LogWarning(msg);
                        warnings.Add(msg);
                    }
                }
                catch (Exception ex)
                {
                    var msg = $"Failed to add API permissions on RemoteProxy app: {ex.Message}";
                    logger.LogWarning(msg);
                    warnings.Add(msg);
                }
            }

            // Step 4: Configure PPMI app scopes and A365 Proxy API permissions
            var ppmiAppClientId = addResponse.Server?.PpmiAppClientId;
            if (!string.IsNullOrWhiteSpace(ppmiAppClientId))
            {
                logger.LogDebug("PPMI app provisioned: {PpmiAppClientId}", ppmiAppClientId);
                logger.LogDebug("Waiting for PPMI app to replicate in Entra ID (this may take up to 60 seconds)...");

                // Look up PPMI app objectId (PPMI-created apps have Graph API replication lag)
                var ppmiObjectId = await graphApiService!.GetAppObjectIdByClientIdAsync(tenantId, ppmiAppClientId);
                if (!string.IsNullOrWhiteSpace(ppmiObjectId))
                {
                    // Set Application ID URI on the PPMI app
                    try
                    {
                        var identifierUri = McpConstants.BuildPpmiIdentifierUri(toolingService.Environment, tenantId, serverName);
                        logger.LogDebug("Setting Application ID URI: {Uri}", identifierUri);
                        await graphApiService.SetIdentifierUriAsync(tenantId, ppmiObjectId, identifierUri);
                    }
                    catch (Exception ex)
                    {
                        var msg = $"Failed to set Application ID URI on PPMI app: {ex.Message}";
                        logger.LogWarning(msg);
                        warnings.Add(msg);
                    }

                    // Add user_impersonation scope (required for PPMI OBO token flow)
                    Guid? uiScopeId = null;
                    try
                    {
                        logger.LogDebug("Adding 'user_impersonation' scope to PPMI app...");
                        uiScopeId = await graphApiService.AddOAuth2PermissionScopeAsync(
                            tenantId,
                            ppmiObjectId,
                            "user_impersonation",
                            "Allow the application to access resources on behalf of the signed-in user");
                    }
                    catch (Exception ex)
                    {
                        var msg = $"Failed to add 'user_impersonation' scope to PPMI app: {ex.Message}";
                        logger.LogWarning(msg);
                        warnings.Add(msg);
                    }

                    // Add <serverName>.All scope to the PPMI app
                    Guid? serverAllScopeId = null;
                    var scopeName = $"{serverName}.All";
                    try
                    {
                        logger.LogDebug("Adding scope '{ScopeName}' to PPMI app...", scopeName);
                        serverAllScopeId = await graphApiService.AddOAuth2PermissionScopeAsync(
                            tenantId, ppmiObjectId, scopeName, $"Full access to {serverName}");
                    }
                    catch (Exception ex)
                    {
                        var msg = $"Failed to add '{scopeName}' scope to PPMI app: {ex.Message}";
                        logger.LogWarning(msg);
                        warnings.Add(msg);
                    }

                    // Add API permissions on A365 Proxy app for both PPMI scopes
                    var ppmiScopeIds = new List<Guid>();
                    if (uiScopeId.HasValue) ppmiScopeIds.Add(uiScopeId.Value);
                    if (serverAllScopeId.HasValue) ppmiScopeIds.Add(serverAllScopeId.Value);

                    if (ppmiScopeIds.Count > 0 && a365AppObjectId != null)
                    {
                        try
                        {
                            logger.LogDebug("Adding API permissions on A365 Proxy app for PPMI scopes...");
                            await graphApiService.AddRequiredResourceAccessAsync(
                                tenantId, a365AppObjectId, ppmiAppClientId, ppmiScopeIds);
                        }
                        catch (Exception ex)
                        {
                            var msg = $"Failed to add API permissions on A365 Proxy app: {ex.Message}";
                            logger.LogWarning(msg);
                            warnings.Add(msg);
                        }
                    }
                    else if (a365AppObjectId != null)
                    {
                        var msg = "No PPMI scopes were created. API permissions not added to A365 Proxy app.";
                        logger.LogWarning(msg);
                        warnings.Add(msg);
                    }

                    // Add same PPMI API permissions on Copilot app
                    if (ppmiScopeIds.Count > 0 && copilotAppObjectId != null)
                    {
                        try
                        {
                            logger.LogDebug("Adding API permissions on Copilot app for PPMI scopes...");
                            await graphApiService.AddRequiredResourceAccessAsync(
                                tenantId, copilotAppObjectId, ppmiAppClientId, ppmiScopeIds);
                        }
                        catch (Exception ex)
                        {
                            var msg = $"Failed to add API permissions on Copilot app: {ex.Message}";
                            logger.LogWarning(msg);
                            warnings.Add(msg);
                        }
                    }
                }
                else
                {
                    var msg = $"Could not find PPMI app {ppmiAppClientId} in Entra after waiting. Scope configuration skipped.";
                    logger.LogWarning(msg);
                    warnings.Add(msg);
                }
            }
            else
            {
                var msg = "PPMI app was not provisioned by the server. Scope configuration skipped.";
                logger.LogWarning(msg);
                warnings.Add(msg);
            }

            // Step 5: Show completion summary
            if (warnings.Count == 0)
            {
                var prevColor = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"MCP server '{serverName}' has been registered successfully.");
                Console.ForegroundColor = prevColor;
            }
            else
            {
                var prevColor = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"MCP server '{serverName}' was registered with {warnings.Count} warning(s):");
                Console.ForegroundColor = prevColor;
                Console.WriteLine();
                foreach (var w in warnings)
                {
                    logger.LogWarning("  - {Warning}", w);
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Please ask your tenant admin to approve MCP server '{serverName}'.");
            if (isExternalIdp && !string.IsNullOrWhiteSpace(remoteRedirectUri))
            {
                Console.WriteLine();
                Console.WriteLine($"Redirect URI: {remoteRedirectUri}");
                Console.WriteLine($"Please add this redirect URI to your external IDP application ({idpClientId}).");
            }
        });

        return command;
    }

    /// <summary>
    /// Removes the "tc-" prefix from the last path segment of a redirect URI to get the original URI.
    /// </summary>
    private static string? RemoveTcPrefix(string tcPrefixedUri)
    {
        var lastSlash = tcPrefixedUri.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            var lastSegment = tcPrefixedUri.Substring(lastSlash + 1);
            if (lastSegment.StartsWith("tc-", StringComparison.Ordinal))
            {
                return tcPrefixedUri.Substring(0, lastSlash + 1) + lastSegment.Substring(3);
            }
        }

        return null;
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

    /// <summary>
    /// Creates the delete-external-mcp-server subcommand
    /// </summary>
    private static Command CreateDeleteExternalMcpServerSubcommand(
        ILogger logger,
        IAgent365ToolingService toolingService,
        GraphApiService? graphApiService)
    {
        var command = new Command("delete-external-mcp-server", "Delete a registered external MCP server");

        var serverNameOption = new Option<string>(["-s", "--server-name"], description: "Name of the MCP server to delete") { IsRequired = true };
        command.AddOption(serverNameOption);

        var tenantIdOption = new Option<string?>(["-t", "--tenant-id"], description: "Azure AD tenant ID (auto-detected from az CLI if not specified)");
        command.AddOption(tenantIdOption);

        var forceOption = new Option<bool>("--force", description: "Force deletion even if the server is approved");
        command.AddOption(forceOption);

        var configOption = new Option<string>(["-c", "--config"], getDefaultValue: () => "a365.config.json", description: "Configuration file path");
        command.AddOption(configOption);

        var verboseOption = new Option<bool>(["--verbose", "-v"], description: "Enable verbose logging");
        command.AddOption(verboseOption);

        command.SetHandler(async (context) =>
        {
            var serverName = context.ParseResult.GetValueForOption(serverNameOption)!;
            var userTenantId = context.ParseResult.GetValueForOption(tenantIdOption);
            var force = context.ParseResult.GetValueForOption(forceOption);

            var tenantId = userTenantId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("az")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    psi.ArgumentList.Add("account");
                    psi.ArgumentList.Add("show");
                    psi.ArgumentList.Add("--query");
                    psi.ArgumentList.Add("tenantId");
                    psi.ArgumentList.Add("-o");
                    psi.ArgumentList.Add("tsv");

                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        var output = await proc.StandardOutput.ReadToEndAsync();
                        await proc.WaitForExitAsync();
                        tenantId = output?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(tenantId))
                        {
                            logger.LogDebug("Auto-detected tenant ID from az account: {TenantId}", tenantId);
                        }
                    }
                }
                catch
                {
                    logger.LogDebug("Could not auto-detect tenant ID from az account");
                }
            }

            if (string.IsNullOrWhiteSpace(tenantId))
            {
                logger.LogError("Tenant ID is required. Pass --tenant-id or sign in via az login.");
                context.ExitCode = 1;
                return;
            }

            Console.WriteLine($"Deleting MCP server '{serverName}'...");

            var deleteResponse = await toolingService.DeleteMcpServerAsync(serverName, force);
            if (deleteResponse == null)
            {
                logger.LogError("Failed to delete MCP server {ServerName}", serverName);
                context.ExitCode = 1;
                return;
            }

            if (!deleteResponse.IsSuccess)
            {
                logger.LogError("Failed to delete MCP server {ServerName}: {Message}", serverName, deleteResponse.Message);
                context.ExitCode = 1;
                return;
            }

            // Delete Entra apps returned by the backend
            var appIds = deleteResponse.AppIds;
            if (graphApiService != null && appIds != null && appIds.Count > 0)
            {
                Console.WriteLine($"Cleaning up {appIds.Count} Entra app(s)...");
                foreach (var app in appIds)
                {
                    if (string.IsNullOrWhiteSpace(app.AppId))
                        continue;

                    try
                    {
                        var objectId = await graphApiService.GetAppObjectIdByClientIdAsync(tenantId, app.AppId);
                        if (!string.IsNullOrWhiteSpace(objectId))
                        {
                            logger.LogDebug("Deleting Entra app '{AppName}' (clientId: {AppId}, objectId: {ObjectId})", app.AppName, app.AppId, objectId);
                            await graphApiService.DeleteEntraAppAsync(tenantId, objectId);
                            Console.WriteLine($"  Deleted: {app.AppName} ({app.AppId})");
                        }
                        else
                        {
                            logger.LogDebug("Entra app '{AppName}' (clientId: {AppId}) not found - may have been already deleted", app.AppName, app.AppId);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning("Failed to delete Entra app '{AppName}' ({AppId}): {Error}", app.AppName, app.AppId, ex.Message);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(deleteResponse.MosTitleId))
            {
                Console.WriteLine(deleteResponse.MosTitleDeleted
                    ? $"MOS title '{deleteResponse.MosTitleId}' deleted."
                    : $"WARNING: MOS title '{deleteResponse.MosTitleId}' was NOT deleted. Manual cleanup may be required.");
            }

            Console.WriteLine($"MCP server '{serverName}' has been deleted successfully.");
        });

        return command;
    }
}
