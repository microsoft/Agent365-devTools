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
        developMcpCommand.AddCommand(CreatePublishSubcommand(logger, toolingService, graphApiService));
        developMcpCommand.AddCommand(CreateUnpublishSubcommand(logger, toolingService));
        developMcpCommand.AddCommand(CreateApproveSubcommand(logger, toolingService));
        developMcpCommand.AddCommand(CreateBlockSubcommand(logger, toolingService));
        developMcpCommand.AddCommand(CreatePackageMCPServerSubCommand(logger, toolingService));
        developMcpCommand.AddCommand(CreateRegisterExternalMcpServerSubcommand(logger, toolingService, graphApiService));

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

        command.SetHandler(async (dryRun, verbose) =>
        {
            logger.LogInformation("Starting list-environments operation...");

            if (dryRun)
            {
                logger.LogInformation("[DRY RUN] Would read config from a365.config.json");
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

        }, dryRunOption, verboseOption);

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

        command.SetHandler(async (envId, dryRun, verbose) =>
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
                logger.LogInformation("[DRY RUN] Would read config from a365.config.json");
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

        }, envIdOption, dryRunOption, verboseOption);

        return command;
    }

    /// <summary>
    /// Creates the publish subcommand
    /// </summary>
    private static Command CreatePublishSubcommand(
        ILogger logger,
        IAgent365ToolingService toolingService,
        GraphApiService? graphApiService)
    {
        var command = new Command("publish", "Publish an MCP server to a Dataverse environment.");

        var envIdOption = new Option<string?>(
            ["--environment-id", "-e"],
            description: "Dataverse environment ID");
        envIdOption.IsRequired = false; // Allow null so we can prompt
        command.AddOption(envIdOption);

        var serverNameOption = new Option<string?>(
            ["--server-name", "-s"],
            description: "MCP server name to publish");
        serverNameOption.IsRequired = false;
        command.AddOption(serverNameOption);

        var aliasOption = new Option<string?>(
            ["--alias", "-a"],
            description: "Alias for the MCP server");
        command.AddOption(aliasOption);

        var displayNameOption = new Option<string?>(
            ["--display-name", "-d"],
            description: "Display name for the MCP server (max 30 chars)");
        command.AddOption(displayNameOption);

        var publisherNameOption = new Option<string?>(
            ["--publisher-name", "-p"],
            description: "Publisher name for the MCP Server. Required for custom (user-created) MCP servers; ignored for 1p Microsoft-owned servers (e.g. msdyn_DataverseMCPServer) which always publish as 'Microsoft'.");
        command.AddOption(publisherNameOption);

        var yesOption = new Option<bool>(
            ["--yes", "-y"],
            description: "Skip the interactive 'Proceed with publish? (y/N)' confirmation.");
        command.AddOption(yesOption);

        var dryRunOption = new Option<bool>("--dry-run", "Show what would be done without executing");
        command.AddOption(dryRunOption);

        // Verbose is handled globally in Program.cs (sets LogLevel.Debug); declared here so the parser accepts -v.
        command.AddOption(new Option<bool>(["--verbose", "-v"], description: "Enable verbose logging"));

        command.SetHandler(async (context) =>
        {
            var args = new RawPublishArgs(
                EnvironmentId: context.ParseResult.GetValueForOption(envIdOption),
                ServerName: context.ParseResult.GetValueForOption(serverNameOption),
                Alias: context.ParseResult.GetValueForOption(aliasOption),
                DisplayName: context.ParseResult.GetValueForOption(displayNameOption),
                PublisherName: context.ParseResult.GetValueForOption(publisherNameOption),
                Yes: context.ParseResult.GetValueForOption(yesOption),
                DryRun: context.ParseResult.GetValueForOption(dryRunOption));

            var executor = new PublishCommandExecutor(logger, toolingService, graphApiService);
            var success = await executor.ExecuteAsync(args, context.GetCancellationToken());
            if (!success)
            {
                context.ExitCode = 1;
            }
        });

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

        command.SetHandler(async (envId, serverName, dryRun, verbose) =>
        {
            _ = verbose;
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
                logger.LogInformation("[DRY RUN] Would read config from a365.config.json");
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

        }, envIdOption, serverNameOption, dryRunOption, verboseOption);

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

        command.SetHandler(async (serverName, dryRun, verbose) =>
        {
            _ = verbose;
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
                logger.LogInformation("[DRY RUN] Would read config from a365.config.json");
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

        }, serverNameOption, dryRunOption, verboseOption);

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

        command.SetHandler(async (serverName, dryRun, verbose) =>
        {
            _ = verbose;
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
                logger.LogInformation("[DRY RUN] Would read config from a365.config.json");
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

        }, serverNameOption, dryRunOption, verboseOption);

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
        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Enable verbose logging"
        );

        command.AddOption(serverNameOption);
        command.AddOption(developerNameOption);
        command.AddOption(iconUrlOption);
        command.AddOption(outputPathOption);
        command.AddOption(dryRunOption);
        command.AddOption(verboseOption);

        command.SetHandler(async (serverName, developerName, iconUrl, outputPath, dryRun, verbose) =>
        {
            _ = verbose;
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

        }, serverNameOption, developerNameOption, iconUrlOption, outputPathOption, dryRunOption, verboseOption);

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

        var serverNameOption = new Option<string?>(["--server-name", "-s"], description: "MCP server name (max 20 chars, must start with 'ext_', e.g. ext_MyServer)");
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

        var remoteScopesOption = new Option<string?>("--remote-scopes", description: "Scopes for the remote MCP server (e.g., 'api://{appId-guid}/{scopeName}' such as 'api://00000000-0000-0000-0000-000000000000/access_as_user')");
        command.AddOption(remoteScopesOption);

        var serviceTreeIdOption = new Option<string?>("--service-tree-id", description: "ServiceTree ID for Entra app registration (required in Microsoft corporate tenants)");
        command.AddOption(serviceTreeIdOption);

        var secretLifetimeMonthsOption = new Option<int?>(["--secret-lifetime-months", "-l"], description: "Lifetime in months (1-24) for generated client secrets on the created Entra apps. Default is 2 years. Set a value that is smaller than the appManagementPolicies cap in your tenant.");
        command.AddOption(secretLifetimeMonthsOption);

        var publisherOption = new Option<string?>("--publisher", description: "Publisher name (required, used in MOS package metadata)");
        command.AddOption(publisherOption);

        var descriptionOption = new Option<string?>("--description", description: "Server description (required, used in MOS package metadata)");
        command.AddOption(descriptionOption);

        var dryRunOption = new Option<bool>("--dry-run", description: "Show what would be done without executing");
        command.AddOption(dryRunOption);

        // Verbose is handled globally in Program.cs (sets LogLevel.Debug); declared here so the parser accepts -v.
        command.AddOption(new Option<bool>(["--verbose", "-v"], description: "Enable verbose logging"));

        command.SetHandler(async (context) =>
        {
            var args = new RawRegisterArgs(
                ServerName: context.ParseResult.GetValueForOption(serverNameOption),
                ServerUrl: context.ParseResult.GetValueForOption(serverUrlOption),
                AuthType: context.ParseResult.GetValueForOption(authTypeOption),
                IdpAuthUrl: context.ParseResult.GetValueForOption(idpAuthUrlOption),
                IdpTokenUrl: context.ParseResult.GetValueForOption(idpTokenUrlOption),
                IdpScopes: context.ParseResult.GetValueForOption(idpScopesOption),
                IdpClientId: context.ParseResult.GetValueForOption(idpClientIdOption),
                IdpClientSecret: context.ParseResult.GetValueForOption(idpClientSecretOption),
                ApiKeyLocation: context.ParseResult.GetValueForOption(apiKeyLocationOption),
                ApiKeyName: context.ParseResult.GetValueForOption(apiKeyNameOption),
                ToolsInput: context.ParseResult.GetValueForOption(toolsOption),
                InputFile: context.ParseResult.GetValueForOption(inputFileOption),
                RemoteScopes: context.ParseResult.GetValueForOption(remoteScopesOption),
                TenantId: null,
                ServiceTreeId: context.ParseResult.GetValueForOption(serviceTreeIdOption),
                SecretLifetimeMonths: context.ParseResult.GetValueForOption(secretLifetimeMonthsOption),
                PublisherName: context.ParseResult.GetValueForOption(publisherOption),
                Description: context.ParseResult.GetValueForOption(descriptionOption),
                DryRun: context.ParseResult.GetValueForOption(dryRunOption));

            var executor = new RegisterCommandExecutor(logger, toolingService, graphApiService);
            var success = await executor.ExecuteAsync(args, context.GetCancellationToken());
            if (!success)
            {
                context.ExitCode = 1;
            }
        });

        return command;
    }

    internal static void WriteLabel(string label)
    {
        var prevColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write(label);
        Console.ForegroundColor = prevColor;
    }

    internal static string? RemoveTcPrefix(string uri)
    {
        var lastSlash = uri.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            var lastSegment = uri.Substring(lastSlash + 1);
            if (lastSegment.StartsWith("tc-", StringComparison.Ordinal))
            {
                return uri.Substring(0, lastSlash + 1) + lastSegment.Substring(3);
            }
        }

        return null;
    }

    internal static string? AddTcPrefix(string uri)
    {
        var lastSlash = uri.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            var lastSegment = uri.Substring(lastSlash + 1);
            if (!lastSegment.StartsWith("tc-", StringComparison.Ordinal))
            {
                return uri.Substring(0, lastSlash + 1) + "tc-" + lastSegment;
            }
        }

        return null;
    }

    internal static string[] BuildRedirectUriList(string original, string? tcVariant, string? nonTcVariant)
    {
        var uris = new HashSet<string>(StringComparer.Ordinal) { original };
        if (tcVariant != null)
        {
            uris.Add(tcVariant);
        }

        if (nonTcVariant != null)
        {
            uris.Add(nonTcVariant);
        }

        return uris.ToArray();
    }

    /// <summary>
    /// Validates and sanitizes user input following Azure CLI security patterns
    /// </summary>
    internal static class InputValidator
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
