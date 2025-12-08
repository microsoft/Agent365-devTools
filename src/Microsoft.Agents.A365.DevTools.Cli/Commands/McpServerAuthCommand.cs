// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// McpServerAuth command - Manages authentication and permissions for MCP servers
/// Supports On-Behalf-Of (OBO) authentication flow via Agent 365 Tools
/// </summary>
public class McpServerAuthCommand
{
    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        AuthenticationService authService,
        GraphApiService graphApiService)
    {
        var command = new Command(
            "mcpserverauth",
            "Manage authentication and permissions for MCP servers\n" +
            "Subcommands:\n" +
            "  gettoken        - Retrieve bearer tokens for MCP server access\n" +
            "  addpermissions  - Add MCP server permissions to an application");

        // Add subcommands
        command.AddCommand(CreateGetTokenSubcommand(logger, configService, authService));
        command.AddCommand(CreateAddPermissionsSubcommand(logger, configService, graphApiService));

        return command;
    }

    /// <summary>
    /// GetToken subcommand - Retrieves bearer tokens for MCP server authentication
    /// </summary>
    private static Command CreateGetTokenSubcommand(
        ILogger logger,
        IConfigService configService,
        AuthenticationService authService)
    {
        var command = new Command(
            "gettoken",
            "Retrieve bearer tokens for MCP server authentication\n" +
            "Reads ToolingManifest.json and acquires tokens with specified scopes");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Configuration file path");

        var appIdOption = new Option<string?>(
            ["--app-id"],
            description: "Application (client) ID to get token for. If not specified, uses the blueprint from config")
        {
            IsRequired = false
        };

        var manifestOption = new Option<FileInfo?>(
            ["--manifest", "-m"],
            description: "Path to ToolingManifest.json (defaults to current directory)");

        var scopesOption = new Option<string[]?>(
            ["--scopes"],
            description: "Specific scopes to request (e.g., McpServers.Mail.All McpServers.Calendar.All). If not specified, uses all scopes from ToolingManifest.json")
        {
            AllowMultipleArgumentsPerToken = true
        };

        var outputFormatOption = new Option<string>(
            ["--output", "-o"],
            getDefaultValue: () => "table",
            description: "Output format: table, json, or raw");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Show detailed output including full token");

        var forceRefreshOption = new Option<bool>(
            ["--force-refresh"],
            description: "Force token refresh even if cached token is valid");

        command.AddOption(configOption);
        command.AddOption(appIdOption);
        command.AddOption(manifestOption);
        command.AddOption(scopesOption);
        command.AddOption(outputFormatOption);
        command.AddOption(verboseOption);
        command.AddOption(forceRefreshOption);

        command.SetHandler(async (config, appId, manifest, scopes, outputFormat, verbose, forceRefresh) =>
        {
            try
            {
                logger.LogInformation("Retrieving bearer token for MCP servers...");
                logger.LogInformation("");

                // Check if config file exists or if --app-id was provided
                Agent365Config? setupConfig = null;
                if (File.Exists(config.FullName))
                {
                    // Load configuration if it exists
                    setupConfig = await configService.LoadAsync(config.FullName);
                }
                else if (string.IsNullOrWhiteSpace(appId))
                {
                    // Config doesn't exist and no --app-id provided
                    logger.LogError("Configuration file not found: {ConfigPath}", config.FullName);
                    logger.LogInformation("");
                    logger.LogInformation("To retrieve bearer tokens, you must either:");
                    logger.LogInformation("  1. Create a config file using: a365 config init");
                    logger.LogInformation("  2. Specify the application ID using: a365 mcpserverauth gettoken --app-id <your-app-id>");
                    logger.LogInformation("");
                    logger.LogInformation("Example: a365 mcpserverauth gettoken --app-id 12345678-1234-1234-1234-123456789abc --scopes McpServers.Mail.All");
                    Environment.Exit(1);
                    return;
                }

                // Determine manifest path
                var manifestPath = manifest?.FullName 
                    ?? Path.Combine(setupConfig?.DeploymentProjectPath ?? Environment.CurrentDirectory, "ToolingManifest.json");

                // Determine which scopes to request
                string[] requestedScopes;
                
                if (scopes != null && scopes.Length > 0)
                {
                    // User provided explicit scopes
                    requestedScopes = scopes;
                    logger.LogInformation("Using user-specified scopes: {Scopes}", string.Join(", ", requestedScopes));
                }
                else
                {
                    // Read scopes from ToolingManifest.json
                    if (!File.Exists(manifestPath))
                    {
                        logger.LogError("ToolingManifest.json not found at: {Path}", manifestPath);
                        logger.LogInformation("");
                        logger.LogInformation("Please ensure ToolingManifest.json exists in your project directory");
                        logger.LogInformation("or specify scopes explicitly with --scopes option.");
                        logger.LogInformation("");
                        logger.LogInformation("Example: a365 mcpserverauth gettoken --scopes McpServers.Mail.All McpServers.Calendar.All");
                        Environment.Exit(1);
                        return;
                    }

                    logger.LogInformation("Reading MCP server configuration from: {Path}", manifestPath);

                    // Parse ToolingManifest.json
                    var manifestJson = await File.ReadAllTextAsync(manifestPath);
                    var toolingManifest = JsonSerializer.Deserialize<ToolingManifest>(manifestJson);

                    if (toolingManifest?.McpServers == null || toolingManifest.McpServers.Length == 0)
                    {
                        logger.LogWarning("No MCP servers found in ToolingManifest.json");
                        logger.LogInformation("You can specify scopes explicitly with --scopes option.");
                        Environment.Exit(1);
                        return;
                    }

                    // Collect all unique scopes from manifest
                    var scopeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var server in toolingManifest.McpServers)
                    {
                        if (!string.IsNullOrWhiteSpace(server.Scope))
                        {
                            scopeSet.Add(server.Scope);
                        }
                    }

                    if (scopeSet.Count == 0)
                    {
                        logger.LogError("No scopes found in ToolingManifest.json");
                        logger.LogInformation("You can specify scopes explicitly with --scopes option.");
                        Environment.Exit(1);
                        return;
                    }

                    requestedScopes = scopeSet.ToArray();
                    logger.LogInformation("Collected {Count} unique scope(s) from manifest: {Scopes}", 
                        requestedScopes.Length, string.Join(", ", requestedScopes));
                }

                logger.LogInformation("");

                // Get the Agent 365 Tools resource App ID for the environment
                var environment = setupConfig?.Environment ?? "prod";
                var resourceAppId = ConfigConstants.GetAgent365ToolsResourceAppId(environment);
                logger.LogInformation("Agent 365 Tools Resource App ID: {AppId}", resourceAppId);
                logger.LogInformation("Requesting scopes: {Scopes}", string.Join(", ", requestedScopes));
                logger.LogInformation("");

                // Acquire token with explicit scopes
                logger.LogInformation("Acquiring access token with explicit scopes...");
                
                // Determine tenant ID (from config or detect from Azure CLI)
                string? tenantId = null;
                if (setupConfig != null && !string.IsNullOrWhiteSpace(setupConfig.TenantId))
                {
                    tenantId = setupConfig.TenantId;
                }
                else if (!string.IsNullOrWhiteSpace(appId))
                {
                    // When --app-id is used without config, we need to detect tenant
                    logger.LogWarning("No tenant ID in config. Tenant ID will be auto-detected from Azure CLI context.");
                    logger.LogInformation("For best results, either:");
                    logger.LogInformation("  1. Run 'az login' first to set Azure CLI context");
                    logger.LogInformation("  2. Or create a config file with: a365 config init");
                    logger.LogInformation("");
                    
                    // Leave null - AuthenticationService will handle tenant detection
                    tenantId = null;
                }
                
                try
                {
                    // Use GetAccessTokenWithScopesAsync for explicit scope control
                    // This will format scopes as api://{resourceAppId}/{scope}
                    var token = await authService.GetAccessTokenWithScopesAsync(
                        resourceAppId,
                        requestedScopes,
                        tenantId,
                        forceRefresh);

                    if (string.IsNullOrWhiteSpace(token))
                    {
                        logger.LogError("Failed to acquire token");
                        Environment.Exit(1);
                        return;
                    }

                    logger.LogInformation("[SUCCESS] Token acquired successfully with scopes: {Scopes}", 
                        string.Join(", ", requestedScopes));
                    logger.LogInformation("");

                    // Create a single result representing the consolidated token
                    var tokenResult = new McpServerTokenResult
                    {
                        ServerName = "Agent 365 Tools (All MCP Servers)",
                        Url = ConfigConstants.GetDiscoverEndpointUrl(environment),
                        Scope = string.Join(", ", requestedScopes),
                        Audience = resourceAppId,
                        Success = true,
                        Token = token,
                        ExpiresOn = DateTime.UtcNow.AddHours(1) // Estimate
                    };

                    var tokenResults = new List<McpServerTokenResult> { tokenResult };

                    // Display results based on output format
                    DisplayResults(tokenResults, outputFormat, verbose, logger);

                    logger.LogInformation("Token acquired successfully!");
                }
                catch (Exception ex)
                {
                    logger.LogError("Failed to acquire token: {Message}", ex.Message);
                    Environment.Exit(1);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to retrieve bearer token: {Message}", ex.Message);
                Environment.Exit(1);
            }
        }, configOption, appIdOption, manifestOption, scopesOption, outputFormatOption, verboseOption, forceRefreshOption);

        return command;
    }

    /// <summary>
    /// AddPermissions subcommand - Adds MCP server API permissions to a custom application
    /// </summary>
    private static Command CreateAddPermissionsSubcommand(
        ILogger logger,
        IConfigService configService,
        GraphApiService graphApiService)
    {
        var command = new Command(
            "addpermissions",
            "Add MCP server API permissions to a custom application\n" +
            "Configures required permissions in the application manifest");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Configuration file path");

        var manifestOption = new Option<FileInfo?>(
            ["--manifest", "-m"],
            description: "Path to ToolingManifest.json (defaults to current directory)");

        var appIdOption = new Option<string>(
            ["--app-id"],
            description: "Application (client) ID to add permissions to. If not specified, uses the blueprint from config")
        {
            IsRequired = false
        };

        var scopesOption = new Option<string[]?>(
            ["--scopes"],
            description: "Specific scopes to add (e.g., McpServers.Mail.All McpServers.Calendar.All). If not specified, uses all scopes from ToolingManifest.json")
        {
            AllowMultipleArgumentsPerToken = true
        };

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Show detailed output");

        var dryRunOption = new Option<bool>(
            ["--dry-run"],
            description: "Show what would be done without executing");

        command.AddOption(configOption);
        command.AddOption(manifestOption);
        command.AddOption(appIdOption);
        command.AddOption(scopesOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);

        command.SetHandler(async (config, manifest, appId, scopes, verbose, dryRun) =>
        {
            try
            {
                logger.LogInformation("Adding MCP server permissions to application...");
                logger.LogInformation("");

                // Check if config file exists or if --app-id was provided
                Agent365Config? setupConfig = null;
                if (File.Exists(config.FullName))
                {
                    // Load configuration if it exists
                    setupConfig = await configService.LoadAsync(config.FullName);
                }
                else if (string.IsNullOrWhiteSpace(appId))
                {
                    // Config doesn't exist and no --app-id provided
                    logger.LogError("Configuration file not found: {ConfigPath}", config.FullName);
                    logger.LogInformation("");
                    logger.LogInformation("To add MCP server permissions, you must either:");
                    logger.LogInformation("  1. Create a config file using: a365 config init");
                    logger.LogInformation("  2. Specify the application ID using: a365 mcpserverauth addpermissions --app-id <your-app-id>");
                    logger.LogInformation("");
                    logger.LogInformation("Example: a365 mcpserverauth addpermissions --app-id 12345678-1234-1234-1234-123456789abc --scopes McpServers.Mail.All");
                    Environment.Exit(1);
                    return;
                }

                // Determine target application ID
                string targetAppId;
                if (!string.IsNullOrWhiteSpace(appId))
                {
                    targetAppId = appId;
                    logger.LogInformation("Target Application ID (from --app-id): {AppId}", targetAppId);
                }
                else if (setupConfig != null && !string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
                {
                    targetAppId = setupConfig.AgentBlueprintId;
                    logger.LogInformation("Target Application ID (from config): {AppId}", targetAppId);
                }
                else
                {
                    logger.LogError("No application ID specified. Use --app-id or ensure AgentBlueprintId is set in config.");
                    logger.LogInformation("");
                    logger.LogInformation("Example: a365 mcpserverauth addpermissions --app-id <your-app-id>");
                    Environment.Exit(1);
                    return;
                }

                // Determine manifest path
                var manifestPath = manifest?.FullName 
                    ?? Path.Combine(setupConfig?.DeploymentProjectPath ?? Environment.CurrentDirectory, "ToolingManifest.json");

                // Determine which scopes to add
                string[] requestedScopes;
                HashSet<string> uniqueAudiences = new();
                
                if (scopes != null && scopes.Length > 0)
                {
                    // User provided explicit scopes
                    requestedScopes = scopes;
                    logger.LogInformation("Using user-specified scopes: {Scopes}", string.Join(", ", requestedScopes));
                    logger.LogInformation("");
                    
                    // For explicit scopes, we still need to read audiences from manifest
                    if (File.Exists(manifestPath))
                    {
                        var manifestJson = await File.ReadAllTextAsync(manifestPath);
                        var toolingManifest = JsonSerializer.Deserialize<ToolingManifest>(manifestJson);
                        
                        if (toolingManifest?.McpServers != null && toolingManifest.McpServers.Length > 0)
                        {
                            foreach (var server in toolingManifest.McpServers)
                            {
                                if (!string.IsNullOrWhiteSpace(server.Audience))
                                {
                                    uniqueAudiences.Add(server.Audience);
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Read scopes and audiences from ToolingManifest.json
                    if (!File.Exists(manifestPath))
                    {
                        logger.LogError("ToolingManifest.json not found at: {Path}", manifestPath);
                        logger.LogInformation("");
                        logger.LogInformation("Please ensure ToolingManifest.json exists in your project directory");
                        logger.LogInformation("or specify scopes explicitly with --scopes option.");
                        logger.LogInformation("");
                        logger.LogInformation("Example: a365 mcpserverauth addpermissions --scopes McpServers.Mail.All McpServers.Calendar.All");
                        Environment.Exit(1);
                        return;
                    }

                    logger.LogInformation("Reading MCP server configuration from: {Path}", manifestPath);

                    // Parse ToolingManifest.json
                    var manifestJson = await File.ReadAllTextAsync(manifestPath);
                    var toolingManifest = JsonSerializer.Deserialize<ToolingManifest>(manifestJson);

                    if (toolingManifest?.McpServers == null || toolingManifest.McpServers.Length == 0)
                    {
                        logger.LogWarning("No MCP servers found in ToolingManifest.json");
                        logger.LogInformation("You can specify scopes explicitly with --scopes option.");
                        Environment.Exit(1);
                        return;
                    }

                    // Collect all unique scopes and audiences from manifest
                    var scopeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    
                    foreach (var server in toolingManifest.McpServers)
                    {
                        if (!string.IsNullOrWhiteSpace(server.Scope))
                        {
                            scopeSet.Add(server.Scope);
                        }
                        
                        if (!string.IsNullOrWhiteSpace(server.Audience))
                        {
                            uniqueAudiences.Add(server.Audience);
                        }
                    }

                    if (scopeSet.Count == 0)
                    {
                        logger.LogError("No scopes found in ToolingManifest.json");
                        logger.LogInformation("You can specify scopes explicitly with --scopes option.");
                        Environment.Exit(1);
                        return;
                    }

                    requestedScopes = scopeSet.ToArray();
                    logger.LogInformation("Collected {Count} unique scope(s) from manifest: {Scopes}", 
                        requestedScopes.Length, string.Join(", ", requestedScopes));
                }

                if (uniqueAudiences.Count == 0)
                {
                    logger.LogWarning("No audiences found in ToolingManifest.json. Cannot determine resource application IDs.");
                    logger.LogInformation("Note: Each MCP server should have an 'audience' field specifying the resource API.");
                    logger.LogInformation("");
                    logger.LogInformation("Using Agent 365 Tools resource as fallback...");
                    var environment = setupConfig?.Environment ?? "prod";
                    uniqueAudiences.Add(ConfigConstants.GetAgent365ToolsResourceAppId(environment));
                }

                logger.LogInformation("Found {Count} unique audience(s): {Audiences}", 
                    uniqueAudiences.Count, string.Join(", ", uniqueAudiences));
                logger.LogInformation("");

                // Dry run mode
                if (dryRun)
                {
                    logger.LogInformation("DRY RUN: Add MCP Server Permissions");
                    logger.LogInformation("Would add the following permissions to application {AppId}:", targetAppId);
                    logger.LogInformation("");
                    
                    foreach (var audience in uniqueAudiences)
                    {
                        logger.LogInformation("Resource: {Audience}", audience);
                        logger.LogInformation("  Scopes: {Scopes}", string.Join(", ", requestedScopes));
                    }
                    
                    logger.LogInformation("");
                    logger.LogInformation("No changes made (dry run mode)");
                    return;
                }

                // Add permissions for each unique audience
                logger.LogInformation("Adding permissions to application...");
                logger.LogInformation("");

                // Determine tenant ID (from config or detect from Azure CLI)
                string tenantId = string.Empty;
                if (setupConfig != null && !string.IsNullOrWhiteSpace(setupConfig.TenantId))
                {
                    tenantId = setupConfig.TenantId;
                }
                else
                {
                    // When --app-id is used without config, we need to detect tenant
                    logger.LogWarning("No tenant ID in config. Tenant ID will be auto-detected from Azure CLI context.");
                    logger.LogInformation("For best results, either:");
                    logger.LogInformation("  1. Run 'az login' first to set Azure CLI context");
                    logger.LogInformation("  2. Or create a config file with: a365 config init");
                    logger.LogInformation("");
                    
                    // Leave empty string - GraphApiService will handle tenant detection
                    tenantId = string.Empty;
                }

                int successCount = 0;
                int failureCount = 0;

                foreach (var audience in uniqueAudiences)
                {
                    logger.LogInformation("Processing audience: {Audience}", audience);
                    
                    try
                    {
                        var success = await graphApiService.AddRequiredResourceAccessAsync(
                            tenantId,
                            targetAppId,
                            audience,
                            requestedScopes,
                            isDelegated: true);

                        if (success)
                        {
                            logger.LogInformation("  [SUCCESS] Successfully added permissions for {Audience}", audience);
                            successCount++;
                        }
                        else
                        {
                            logger.LogError("  [FAILED] Failed to add permissions for {Audience}", audience);
                            failureCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("  [ERROR] Exception adding permissions for {Audience}: {Message}", audience, ex.Message);
                        if (verbose)
                        {
                            logger.LogError("    {StackTrace}", ex.StackTrace);
                        }
                        failureCount++;
                    }
                    
                    logger.LogInformation("");
                }

                // Summary
                logger.LogInformation("=== Summary ===");
                logger.LogInformation("Succeeded: {SuccessCount}/{Total}", successCount, uniqueAudiences.Count);
                logger.LogInformation("Failed: {FailureCount}/{Total}", failureCount, uniqueAudiences.Count);
                logger.LogInformation("");

                if (failureCount == 0)
                {
                    logger.LogInformation("[SUCCESS] All permissions added successfully!");
                    logger.LogInformation("");
                    logger.LogInformation("Next steps:");
                    logger.LogInformation("  1. Review permissions in Azure Portal: https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/{AppId}", targetAppId);
                    logger.LogInformation("  2. Grant admin consent for the added permissions if required");
                    logger.LogInformation("  3. Test your application with the new permissions");
                }
                else
                {
                    logger.LogWarning("Some permissions failed to add. Review the errors above.");
                    Environment.Exit(1);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to add MCP server permissions: {Message}", ex.Message);
                Environment.Exit(1);
            }
        }, configOption, manifestOption, appIdOption, scopesOption, verboseOption, dryRunOption);

        return command;
    }

    private static void DisplayResults(
        List<McpServerTokenResult> results,
        string outputFormat,
        bool verbose,
        ILogger logger)
    {
        switch (outputFormat.ToLowerInvariant())
        {
            case "json":
                DisplayJsonResults(results, verbose);
                break;
            case "raw":
                DisplayRawResults(results, verbose);
                break;
            case "table":
            default:
                DisplayTableResults(results, verbose, logger);
                break;
        }
    }

    private static void DisplayTableResults(
        List<McpServerTokenResult> results,
        bool verbose,
        ILogger logger)
    {
        logger.LogInformation("=== MCP Server Bearer Tokens ===");
        logger.LogInformation("");

        foreach (var result in results)
        {
            logger.LogInformation("Server: {Name}", result.ServerName);
            logger.LogInformation("  URL: {Url}", result.Url ?? "(not specified)");
            logger.LogInformation("  Scope: {Scope}", result.Scope ?? "(not specified)");
            logger.LogInformation("  Audience: {Audience}", result.Audience ?? "(not specified)");

            if (result.Success)
            {
                logger.LogInformation("  Status: [SUCCESS]");
                logger.LogInformation("  Expires: ~{Expiry}", result.ExpiresOn?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown");

                if (!string.IsNullOrWhiteSpace(result.Token))
                {
                    logger.LogInformation("  Token: {Token}", result.Token);
                }
            }
            else
            {
                logger.LogInformation("  Status: [FAILED]");
                logger.LogInformation("  Error: {Error}", result.Error ?? "Unknown error");
            }

            logger.LogInformation("");
        }
    }

    private static void DisplayJsonResults(List<McpServerTokenResult> results, bool verbose)
    {
        var output = results.Select(r => new
        {
            serverName = r.ServerName,
            url = r.Url,
            scope = r.Scope,
            audience = r.Audience,
            success = r.Success,
            token = r.Token,
            expiresOn = r.ExpiresOn?.ToString("o"),
            error = r.Error
        });

        var json = JsonSerializer.Serialize(output, new JsonSerializerOptions 
        { 
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Console.WriteLine(json);
    }

    private static void DisplayRawResults(List<McpServerTokenResult> results, bool verbose)
    {
        foreach (var result in results)
        {
            if (result.Success && !string.IsNullOrWhiteSpace(result.Token))
            {
                if (verbose)
                {
                    Console.WriteLine($"# {result.ServerName}");
                    Console.WriteLine($"# Scope: {result.Scope}");
                    Console.WriteLine($"# Audience: {result.Audience}");
                }
                Console.WriteLine(result.Token);
                if (verbose)
                {
                    Console.WriteLine();
                }
            }
        }
    }

    private class McpServerTokenResult
    {
        public string ServerName { get; set; } = string.Empty;
        public string? Url { get; set; }
        public string? Scope { get; set; }
        public string? Audience { get; set; }
        public bool Success { get; set; }
        public string? Token { get; set; }
        public DateTime? ExpiresOn { get; set; }
        public string? Error { get; set; }
    }
}
