// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using System.CommandLine;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// Command for managing MCP tool servers during agent development
/// </summary>
public static class DevelopCommand
{
    /// <summary>
    /// Creates the develop command with subcommands for MCP tool server management
    /// </summary>
    public static Command CreateCommand(ILogger logger, IConfigService configService, CommandExecutor commandExecutor, AuthenticationService authService)
    {
        var developCommand = new Command("develop", "Manage MCP tool servers for agent development");

        // Add common options
        var configOption = new Option<string>(
            ["--config", "-c"],
            getDefaultValue: () => "a365.config.json",
            description: "Configuration file path");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Enable verbose logging");

        developCommand.AddOption(configOption);
        developCommand.AddOption(verboseOption);

        // Add subcommands
        developCommand.AddCommand(CreateListAvailableSubcommand(logger, configService, commandExecutor, authService));
        developCommand.AddCommand(CreateListConfiguredSubcommand(logger, configService, commandExecutor));
        developCommand.AddCommand(CreateAddMcpServersSubcommand(logger, configService, authService, commandExecutor));
        developCommand.AddCommand(CreateRemoveMcpServersSubcommand(logger, configService, commandExecutor));

        return developCommand;
    }

    /// <summary>
    /// Creates the list-available subcommand to query Agent 365 Tools service
    /// </summary>
    private static Command CreateListAvailableSubcommand(ILogger logger, IConfigService configService, CommandExecutor commandExecutor, AuthenticationService authService)
    {
        var command = new Command("list-available", "List all MCP servers available in the catalog (what you can install)");

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

        var skipAuthOption = new Option<bool>(
            name: "--skip-auth",
            description: "Skip authentication (for testing only - will likely fail without valid auth)"
        );
        command.AddOption(skipAuthOption);

        command.SetHandler(async (configPath, dryRun, skipAuth) =>
        {
            logger.LogInformation("Starting list-available MCP Servers operation...");

            if (dryRun)
            {
                logger.LogInformation("[DRY RUN] Would read config from {ConfigPath}", configPath);
                logger.LogInformation("[DRY RUN] Would query endpoint directly for available MCP Servers");
                logger.LogInformation("[DRY RUN] Would display catalog of available MCP Servers");
                await Task.CompletedTask;
                return;
            }

            // Try direct endpoint call only (DiscoverEndpointUrl fallback disabled for testing)
            var success = await CallDiscoverToolServersAsync(configService, skipAuth, logger, authService);

            if (!success)
            {
                logger.LogError("Direct endpoint call failed. Please check your configuration.");
                return; // Exit without fallback
            }


            // Success - exit here
            return;

        }, configOption, dryRunOption, skipAuthOption);

        return command;
    }

    /// <summary>
    /// Call the discoverToolServers endpoint directly
    /// </summary>
    private static async Task<bool> CallDiscoverToolServersAsync(IConfigService configService, bool skipAuth, ILogger logger, AuthenticationService authService, bool skipLogs = false)
    {
        try
        {
            var config = configService.LoadAsync().Result;
            var discoverEndpointUrl = ConfigConstants.GetDiscoverEndpointUrl(config.Environment);

            logger.LogInformation("Calling discoverToolServers endpoint directly...");
            logger.LogInformation("Environment: {Env}", config.Environment);
            logger.LogInformation("Endpoint URL: {Url}", discoverEndpointUrl);

            // Get authentication token interactively (unless skip-auth is specified)
            string? authToken = null;
            if (!skipAuth)
            {
                logger.LogInformation("Getting authentication token...");
                
                // Determine the audience (App ID) based on the environment
                var audience = ConfigConstants.GetAgent365ToolsResourceAppId(config.Environment);
                
                logger.LogInformation("Environment: {Environment}, Audience: {Audience}", config.Environment, audience);

                authToken = await authService.GetAccessTokenAsync(audience);

                if (string.IsNullOrWhiteSpace(authToken))
                {
                    logger.LogError("Failed to acquire authentication token");
                    return false;
                }
                logger.LogInformation("Successfully acquired access token");
            }
            else
            {
                logger.LogWarning("Skipping authentication (--skip-auth flag). Request will likely fail without auth.");
            }

            // Use helper to create authenticated HTTP client
            using var httpClient = Services.Internal.HttpClientFactory.CreateAuthenticatedClient(authToken);

            // Call the endpoint directly (no environment ID needed in URL or query)
            logger.LogInformation("Making GET request to: {RequestUrl}", discoverEndpointUrl);

            var response = await httpClient.GetAsync(discoverEndpointUrl);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Failed to call discoverToolServers endpoint. Status: {Status}", response.StatusCode);
                var errorContent = await response.Content.ReadAsStringAsync();
                logger.LogError("Error response: {Error}", errorContent);
                return false;
            }

            var responseContent = await response.Content.ReadAsStringAsync();

            logger.LogInformation("Successfully received response from discoverToolServers endpoint");
            
            // Parse and display the MCP servers
            using var responseDoc = JsonDocument.Parse(responseContent);
            var responseRoot = responseDoc.RootElement;

            var catalogPath = Services.Internal.McpServerCatalogWriter.WriteCatalog(responseContent);
            logger.LogInformation("Catalog saved to {CatalogPath}", catalogPath);

            // Display available MCP servers
            Console.WriteLine();
            Console.WriteLine("Available MCP Servers (from catalog):");
            Console.WriteLine("=====================================");

            if (skipLogs == true)
            {
                return true;
            }

            if (responseRoot.TryGetProperty("mcpServers", out var serversElement) && serversElement.GetArrayLength() > 0)
            {
                foreach (var server in serversElement.EnumerateArray())
                {
                    if (server.TryGetProperty("mcpServerName", out var nameElement) &&
                        server.TryGetProperty("url", out var urlElement))
                    {
                        var serverName = nameElement.GetString() ?? "Unknown";
                        var serverUrl = urlElement.GetString() ?? "Unknown";

                        Console.WriteLine();
                        Console.WriteLine($"  {serverName}");
                        Console.WriteLine($"     URL: {serverUrl}");

                        // Display scope and audience if available
                        if (server.TryGetProperty("scope", out var scopeElement))
                        {
                            var scope = scopeElement.GetString();
                            if (!string.IsNullOrWhiteSpace(scope))
                            {
                                Console.WriteLine($"     Required Scope: {scope}");
                            }
                        }

                        if (server.TryGetProperty("audience", out var audienceElement))
                        {
                            var audience = audienceElement.GetString();
                            if (!string.IsNullOrWhiteSpace(audience))
                            {
                                Console.WriteLine($"     Audience: {audience}");
                            }
                        }
                    }
                }
                Console.WriteLine();
            }
            else
            {
                logger.LogInformation("No MCP servers found in response");
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to call discoverToolServers endpoint directly");
            return false;
        }
    }

    /// <summary>
    /// Creates the list-configured subcommand to show currently configured MCP Servers
    /// </summary>
    private static Command CreateListConfiguredSubcommand(ILogger logger, IConfigService configService, CommandExecutor commandExecutor)
    {
        var command = new Command("list-configured", "List currently configured MCP servers from your local ToolingManifest.json");

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
            logger.LogInformation("Starting list-configured MCP Servers operation...");

            if (dryRun)
            {
                logger.LogInformation("[DRY RUN] Would read ToolingManifest.json from your project");
                logger.LogInformation("[DRY RUN] Would display currently configured MCP servers");
                logger.LogInformation("[DRY RUN] Would show server names and URLs");
                await Task.CompletedTask;
                return;
            }

            // Load config to get deploymentProjectPath
            Agent365Config config;
            try
            {
                config = await configService.LoadAsync(configPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load configuration");
                return;
            }

            // Determine manifest path - use deploymentProjectPath if available
            string manifestPath;
            if (!string.IsNullOrEmpty(config.DeploymentProjectPath))
            {
                manifestPath = Path.Combine(config.DeploymentProjectPath, McpConstants.ToolingManifestFileName);
                logger.LogInformation("Using ToolingManifest.json from deployment project path: {Path}", manifestPath);
            }
            else
            {
                var currentDir = Directory.GetCurrentDirectory();
                manifestPath = Path.Combine(currentDir, McpConstants.ToolingManifestFileName);
                logger.LogWarning("No deploymentProjectPath in config, using current directory: {Path}", manifestPath);
            }

            if (!File.Exists(manifestPath))
            {
                logger.LogInformation("No {FileName} found at: {Path}", McpConstants.ToolingManifestFileName, manifestPath);
                logger.LogInformation("Use 'add-mcp-servers' to create and configure servers");
                return;
            }

            logger.LogInformation("Loading MCP servers from: {Path}", manifestPath);

            try
            {
                var jsonContent = await File.ReadAllTextAsync(manifestPath);
                using var manifestDoc = JsonDocument.Parse(jsonContent);
                var manifestRoot = manifestDoc.RootElement;

                if (!manifestRoot.TryGetProperty(McpConstants.ManifestProperties.McpServers, out var serversElement))
                {
                    logger.LogInformation("No '{PropertyName}' section found in {FileName}",
                        McpConstants.ManifestProperties.McpServers,
                        McpConstants.ToolingManifestFileName);
                    logger.LogInformation("Use 'add-mcp-servers' to configure servers");
                    return;
                }

                if (serversElement.ValueKind != JsonValueKind.Array)
                {
                    logger.LogError("'{PropertyName}' section is not an array", McpConstants.ManifestProperties.McpServers);
                    return;
                }

                var servers = serversElement.EnumerateArray().ToList();
                if (servers.Count == 0)
                {
                    logger.LogInformation("No MCP servers configured in {FileName}", McpConstants.ToolingManifestFileName);
                    logger.LogInformation("Use 'add-mcp-servers' to configure servers");
                    return;
                }

                logger.LogInformation("Found '{PropertyName}' section in {FileName}",
                    McpConstants.ManifestProperties.McpServers,
                    McpConstants.ToolingManifestFileName);
                logger.LogInformation("Configured MCP servers ({Count}):", servers.Count);

                foreach (var serverElement in servers)
                {
                    var serverName = serverElement.TryGetProperty(McpConstants.ManifestProperties.McpServerName, out var nameElement)
                        ? nameElement.GetString() ?? "unknown"
                        : "unknown";

                    // Construct URL based on server name if not explicitly provided
                    var serverUrl = serverElement.TryGetProperty(McpConstants.ManifestProperties.Url, out var urlElement)
                        && !string.IsNullOrEmpty(urlElement.GetString())
                        ? urlElement.GetString()
                        : String.Empty;

                    // Get scope and audience from manifest or mapping
                    var scope = "";
                    if (serverElement.TryGetProperty(McpConstants.ManifestProperties.Scope, out var scopeElement))
                    {
                        scope = scopeElement.GetString() ?? "";
                    }

                    var audience = "";
                    if (serverElement.TryGetProperty(McpConstants.ManifestProperties.Audience, out var audienceElement))
                    {
                        audience = audienceElement.GetString() ?? "";
                    }

                    // If scope/audience not in manifest, get from mapping
                    if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(audience))
                    {
                        var (mappedScope, mappedAudience) = McpConstants.ServerScopeMappings.GetScopeAndAudience(serverName);
                        if (string.IsNullOrWhiteSpace(scope)) scope = mappedScope ?? "";
                        if (string.IsNullOrWhiteSpace(audience)) audience = mappedAudience ?? "";
                    }

                    logger.LogInformation("  {Name}", serverName);
                    logger.LogInformation("     URL: {Url}", serverUrl);

                    if (!string.IsNullOrWhiteSpace(scope))
                    {
                        logger.LogInformation("     Required Scope: {Scope}", scope);
                    }
                    else
                    {
                        logger.LogInformation("     Required Scope: None specified");
                    }

                    if (!string.IsNullOrWhiteSpace(audience))
                    {
                        logger.LogInformation("     Audience: {Audience}", audience);
                    }
                    else
                    {
                        logger.LogInformation("     Audience: Not specified");
                    }

                    logger.LogInformation(""); // Empty line for readability
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read or parse {FileName}", McpConstants.ToolingManifestFileName);
                return;
            }
        }, configOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Creates the add-mcp-servers subcommand to add MCP Servers to local configuration
    /// </summary>
    private static Command CreateAddMcpServersSubcommand(ILogger logger, IConfigService configService, AuthenticationService authService, CommandExecutor commandExecutor)
    {
        var command = new Command("add-mcp-servers", "Add MCP Servers to the current agent configuration");

        var serversArgument = new Argument<string[]>(
            name: "servers",
            description: "Names of the MCP servers to add"
        );
        command.AddArgument(serversArgument);

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

        command.SetHandler(async (servers, configPath, dryRun) =>
        {
            logger.LogInformation("Starting add-mcp-servers operation...");

            var catalogPath = Services.Internal.McpServerCatalogWriter.GetCatalogPath();
            if (!File.Exists(catalogPath))
            {
                // Call the fetch logic (reuse from list-available, but no output)
                logger.LogInformation("Fetching latest MCP server catalog...");
                await CallDiscoverToolServersAsync(configService, false, logger, authService, skipLogs: true);
            }

            var catalogJson = await File.ReadAllTextAsync(catalogPath);
            using var doc = JsonDocument.Parse(catalogJson);
            var serversElement = doc.RootElement.GetProperty("mcpServers");
            var catalog = JsonSerializer.Deserialize<List<JsonElement>>(serversElement.GetRawText());

            if (catalog == null)
            {
                logger.LogError("Could not load MCP server catalog. Aborting.");
                return;
            }

            // Validate input
            if (servers == null || servers.Length == 0)
            {
                logger.LogError("No servers specified. Please provide at least one server name.");
                logger.LogInformation("Usage: a365 develop add-mcp-servers <server1> <server2> ...");
                return;
            }

            // Dry run mode
            if (dryRun)
            {
                logger.LogInformation("[DRY RUN] Would add the following MCP servers to configuration:");
                foreach (var serverName in servers)
                {
                    logger.LogInformation("[DRY RUN]   - {Server}", serverName);
                }
                logger.LogInformation("[DRY RUN] Would update {FileName}", McpConstants.ToolingManifestFileName);
                await Task.CompletedTask;
                return;
            }

            // Load config to get deploymentProjectPath
            Agent365Config config;
            try
            {
                config = await configService.LoadAsync(configPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load configuration");
                return;
            }

            // Determine manifest path - use deploymentProjectPath if available
            string manifestPath;
            if (!string.IsNullOrEmpty(config.DeploymentProjectPath))
            {
                manifestPath = Path.Combine(config.DeploymentProjectPath, McpConstants.ToolingManifestFileName);
                logger.LogInformation("Using ToolingManifest.json from deployment project path: {Path}", manifestPath);
            }
            else
            {
                var currentDir = Directory.GetCurrentDirectory();
                manifestPath = Path.Combine(currentDir, McpConstants.ToolingManifestFileName);
                logger.LogWarning("No deploymentProjectPath in config, using current directory: {Path}", manifestPath);
            }

            try
            {
                // Read existing manifest if present
                var existingServers = new List<object>();
                var existingServerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var readResult = await ManifestHelper.ReadManifestAsync(manifestPath);
                if (readResult != null)
                {
                    existingServers = ManifestHelper.ConvertToServerObjects(readResult.Value.servers);
                    existingServerNames = readResult.Value.serverNames;
                }

                var (updatedServers, addedCount, updatedCount) = UpsertMcpServersInManifest(
                    existingServers, 
                    existingServerNames, 
                    servers, 
                    catalog, 
                    logger);

                if (addedCount == 0 && updatedCount == 0)
                {
                    logger.LogInformation("No servers to add or update.");
                    return;
                }

                await ManifestHelper.WriteManifestAsync(manifestPath, updatedServers);

                logger.LogInformation("Successfully updated {FileName}", McpConstants.ToolingManifestFileName);
                logger.LogInformation("Summary: Added {Added} server(s), Updated {Updated} server(s)", addedCount, updatedCount);
                logger.LogInformation("Total servers in manifest: {Total}", updatedServers.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to add MCP servers to manifest");
                throw;
            }

        }, serversArgument, configOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Creates the remove-mcp-servers subcommand to remove MCP servers from local configuration
    /// </summary>
    private static Command CreateRemoveMcpServersSubcommand(ILogger logger, IConfigService configService, CommandExecutor commandExecutor)
    {
        var command = new Command("remove-mcp-servers", "Remove MCP Servers from the current agent configuration");

        var serversArgument = new Argument<string[]>(
            name: "servers",
            description: "Names of the MCP servers to remove"
        );
        command.AddArgument(serversArgument);

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

        command.SetHandler(async (servers, configPath, dryRun) =>
        {
            logger.LogInformation("Starting remove-mcp-servers operation...");

            // Validate input
            if (servers == null || servers.Length == 0)
            {
                logger.LogError("No servers specified. Please provide at least one server name.");
                logger.LogInformation("Usage: a365 develop remove-mcp-servers <server1> <server2> ...");
                return;
            }

            // Dry run mode
            if (dryRun)
            {
                logger.LogInformation("[DRY RUN] Would remove the following MCP servers from configuration:");
                foreach (var serverName in servers)
                {
                    logger.LogInformation("[DRY RUN]   - {Server}", serverName);
                }
                logger.LogInformation("[DRY RUN] Would update {FileName}", McpConstants.ToolingManifestFileName);
                await Task.CompletedTask;
                return;
            }

            // Load config to get deploymentProjectPath
            Agent365Config config;
            try
            {
                config = await configService.LoadAsync(configPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load configuration");
                return;
            }

            // Determine manifest path - use deploymentProjectPath if available
            string manifestPath;
            if (!string.IsNullOrEmpty(config.DeploymentProjectPath))
            {
                manifestPath = Path.Combine(config.DeploymentProjectPath, McpConstants.ToolingManifestFileName);
                logger.LogInformation("Using ToolingManifest.json from deployment project path: {Path}", manifestPath);
            }
            else
            {
                var currentDir = Directory.GetCurrentDirectory();
                manifestPath = Path.Combine(currentDir, McpConstants.ToolingManifestFileName);
                logger.LogWarning("No deploymentProjectPath in config, using current directory: {Path}", manifestPath);
            }

            if (!File.Exists(manifestPath))
            {
                logger.LogError("No {FileName} found at: {Path}", McpConstants.ToolingManifestFileName, manifestPath);
                logger.LogInformation("Nothing to remove.");
                return;
            }

            try
            {
                // Read existing manifest
                var manifestData = await ManifestHelper.ReadManifestAsync(manifestPath);

                if (!manifestData.HasValue)
                {
                    logger.LogError("Failed to read {FileName}", McpConstants.ToolingManifestFileName);
                    return;
                }

                logger.LogInformation("Loading {FileName} from: {Path}",
                    McpConstants.ToolingManifestFileName, manifestPath);

                var existingServers = ManifestHelper.ConvertToServerObjects(manifestData.Value.servers);
                var existingServerNames = manifestData.Value.serverNames;

                // Build set of servers to remove (case-insensitive)
                var serversToRemove = new HashSet<string>(servers, StringComparer.OrdinalIgnoreCase);
                var remainingServers = new List<object>();
                int removedCount = 0;

                // Filter servers
                foreach (var serverObj in existingServers)
                {
                    string? serverName = null;

                    // Handle Dictionary<string, object> (most likely type)
                    if (serverObj is Dictionary<string, object> dict && dict.TryGetValue("mcpServerName", out var nameValue))
                    {
                        serverName = nameValue as string;
                    }
                    // Handle JsonElement (if used elsewhere)
                    else if (serverObj is JsonElement jsonElement && jsonElement.TryGetProperty("mcpServerName", out var nameElement))
                    {
                        serverName = nameElement.GetString();
                    }

                    if (!string.IsNullOrEmpty(serverName) && serversToRemove.Contains(serverName))
                    {
                        logger.LogInformation("Removing server: {Server}", serverName);
                        removedCount++;
                        serversToRemove.Remove(serverName); // Track that we found it
                        continue; // Skip this server (don't add to remaining)
                    }

                    // Keep this server
                    remainingServers.Add(serverObj);
                }

                // Check for servers that weren't found
                int notFoundCount = serversToRemove.Count;
                foreach (var notFoundServer in serversToRemove)
                {
                    logger.LogWarning("Server '{Server}' not found in manifest. Skipping.", notFoundServer);
                }

                if (removedCount == 0)
                {
                    logger.LogInformation("No servers were removed. None of the specified servers were found in the manifest.");
                    return;
                }

                // Write updated manifest
                await ManifestHelper.WriteManifestAsync(manifestPath, remainingServers);

                logger.LogInformation("Successfully updated {FileName}", McpConstants.ToolingManifestFileName);
                logger.LogInformation("Summary: Removed {Removed} server(s), Not found {NotFound}",
                    removedCount, notFoundCount);
                logger.LogInformation("Total servers remaining in manifest: {Total}", remainingServers.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to remove MCP servers from manifest");
                throw;
            }

        }, serversArgument, configOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Upserts MCP servers in the manifest by updating existing servers with latest catalog information
    /// and adding new servers that don't exist yet.
    /// </summary>
    /// <param name="existingServers">Current servers in the manifest</param>
    /// <param name="existingServerNames">Set of existing server names for fast lookup</param>
    /// <param name="serversToProcess">Server names to add or update</param>
    /// <param name="catalog">MCP server catalog with latest server definitions</param>
    /// <param name="logger">Logger for progress reporting</param>
    /// <returns>Tuple of (updatedServers, addedCount, updatedCount)</returns>
    private static (List<object> updatedServers, int addedCount, int updatedCount) UpsertMcpServersInManifest(
        List<object> existingServers,
        HashSet<string> existingServerNames,
        string[] serversToProcess,
        List<JsonElement> catalog,
        ILogger logger)
    {
        int addedCount = 0;
        int updatedCount = 0;
        var updatedServers = new List<object>();

        // Process existing servers first (for upsert behavior)
        foreach (var existingServer in existingServers)
        {
            if (existingServer is Dictionary<string, object> serverDict && 
                serverDict.TryGetValue("mcpServerName", out var nameObj) &&
                nameObj is string existingServerName)
            {
                // Check if this existing server should be updated
                if (serversToProcess.Any(s => string.Equals(s?.Trim(), existingServerName?.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    // Update this server with latest catalog info
                    var catalogEntry = catalog.FirstOrDefault(s =>
                        s.TryGetProperty("mcpServerName", out var nameElement) &&
                        string.Equals(nameElement.GetString()?.Trim(), existingServerName?.Trim(), StringComparison.OrdinalIgnoreCase)
                    );

                    if (catalogEntry.ValueKind != JsonValueKind.Undefined)
                    {
                        var url = catalogEntry.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                        var scope = catalogEntry.TryGetProperty("scope", out var scopeElement) ? scopeElement.GetString() : null;
                        var audience = catalogEntry.TryGetProperty("audience", out var audienceElement) ? audienceElement.GetString() : null;

                        var updatedServerObject = ManifestHelper.CreateCompleteServerObject(existingServerName, existingServerName, url, scope, audience);
                        updatedServers.Add(updatedServerObject);
                        updatedCount++;
                        logger.LogInformation("Updated existing server: {Server}", existingServerName);
                    }
                    else
                    {
                        // Keep existing server as-is if not found in catalog
                        updatedServers.Add(existingServer);
                        logger.LogWarning("Server '{Server}' not found in catalog, keeping existing configuration", existingServerName);
                    }
                }
                else
                {
                    // Keep existing server that's not being updated
                    updatedServers.Add(existingServer);
                }
            }
            else
            {
                // Keep malformed existing servers as-is
                updatedServers.Add(existingServer);
            }
        }

        // Add new servers that don't exist yet
        foreach (var serverName in serversToProcess)
        {
            if (string.IsNullOrWhiteSpace(serverName))
            {
                logger.LogWarning("Skipping empty server name");
                continue;
            }

            if (existingServerNames.Contains(serverName))
            {
                // Already processed in update loop above
                continue;
            }

            logger.LogInformation("Adding new server: {Server}", serverName);
            addedCount++;

            // Get complete info from catalog
            var catalogEntry = catalog.FirstOrDefault(s =>
                s.TryGetProperty("mcpServerName", out var nameElement) &&
                string.Equals(nameElement.GetString()?.Trim(), serverName?.Trim(), StringComparison.OrdinalIgnoreCase)
            );

            if (catalogEntry.ValueKind == JsonValueKind.Undefined)
            {
                logger.LogWarning("Server '{Server}' not found in catalog, adding with minimal configuration", serverName);
                var minimalServerObject = ManifestHelper.CreateCompleteServerObject(serverName, serverName, null, null, null);
                updatedServers.Add(minimalServerObject);
                continue;
            }

            var url = catalogEntry.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            var scope = catalogEntry.TryGetProperty("scope", out var scopeElement) ? scopeElement.GetString() : null;
            var audience = catalogEntry.TryGetProperty("audience", out var audienceElement) ? audienceElement.GetString() : null;

            var serverObject = ManifestHelper.CreateCompleteServerObject(serverName, serverName, url, scope, audience);
            updatedServers.Add(serverObject);
        }

        return (updatedServers, addedCount, updatedCount);
    }
}