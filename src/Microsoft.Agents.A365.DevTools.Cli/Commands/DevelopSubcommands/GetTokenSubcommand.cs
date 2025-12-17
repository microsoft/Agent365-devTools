// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.DevelopSubcommands;

/// <summary>
/// GetToken subcommand - Retrieves bearer tokens for MCP server authentication
/// </summary>
internal static class GetTokenSubcommand
{
    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        AuthenticationService authService)
    {
        var command = new Command(
            "gettoken",
            "Retrieve bearer tokens for MCP server authentication\n" +
            "Scopes are read from ToolingManifest.json or specified via command line");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Configuration file path");

        var appIdOption = new Option<string?>(
            ["--app-id"],
            description: "Application (client) ID to get token for. If not specified, uses the client app ID from config")
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
                    logger.LogInformation("  2. Specify the application ID using: a365 develop gettoken --app-id <your-app-id>");
                    logger.LogInformation("");
                    logger.LogInformation("Example: a365 develop gettoken --app-id 12345678-1234-1234-1234-123456789abc --scopes McpServers.Mail.All");
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
                        logger.LogInformation("Example: a365 develop gettoken --scopes McpServers.Mail.All McpServers.Calendar.All");
                        Environment.Exit(1);
                        return;
                    }

                    logger.LogInformation("Reading MCP server configuration from: {Path}", manifestPath);

                    // Use ManifestHelper to extract scopes (includes fallback to mappings and McpServersMetadata.Read.All)
                    requestedScopes = await ManifestHelper.GetRequiredScopesAsync(manifestPath);

                    if (requestedScopes.Length == 0)
                    {
                        logger.LogError("No scopes found in ToolingManifest.json");
                        logger.LogInformation("You can specify scopes explicitly with --scopes option.");
                        Environment.Exit(1);
                        return;
                    }

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
                string? tenantId = await TenantDetectionHelper.DetectTenantIdAsync(setupConfig, logger);
                
                try
                {
                    // Determine which client app to use for authentication
                    string? clientAppId = null;
                    if (!string.IsNullOrWhiteSpace(appId))
                    {
                        // User specified --app-id: use it as the client (caller) application
                        clientAppId = appId;
                        logger.LogInformation("Using custom client application: {ClientAppId}", clientAppId);
                    }
                    else if (setupConfig != null && !string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
                    {
                        // Use client app from config
                        clientAppId = setupConfig.ClientAppId;
                        logger.LogInformation("Using client application from config: {ClientAppId}", clientAppId);
                    }
                    else
                    {
                        throw new InvalidOperationException("No client application ID specified. Use --app-id or ensure ClientAppId is set in config.");
                    }
                    
                    logger.LogInformation("");
                    
                    // Use GetAccessTokenWithScopesAsync for explicit scope control
                    var token = await authService.GetAccessTokenWithScopesAsync(
                        resourceAppId,
                        requestedScopes,
                        tenantId,
                        forceRefresh,
                        clientAppId,
                        useInteractiveBrowser: true);

                    if (string.IsNullOrWhiteSpace(token))
                    {
                        logger.LogError("Failed to acquire token");
                        Environment.Exit(1);
                        return;
                    }

                logger.LogInformation("[SUCCESS] Token acquired successfully with scopes: {Scopes}", 
                    string.Join(", ", requestedScopes));
                logger.LogInformation("");

                var tokenCachePath = Path.Combine(
                    ConfigService.GetGlobalConfigDirectory(),
                    AuthenticationConstants.TokenCacheFileName);

                // Create a single result representing the consolidated token
                var tokenResult = new McpServerTokenResult
                {
                    ServerName = "Agent 365 Tools (All MCP Servers)",
                    Url = ConfigConstants.GetDiscoverEndpointUrl(environment),
                    Scope = string.Join(", ", requestedScopes),
                    Audience = resourceAppId,
                    Success = true,
                    Token = token,
                    ExpiresOn = DateTime.UtcNow.AddHours(1), // Estimate
                    CacheFilePath = tokenCachePath
                };

                var tokenResults = new List<McpServerTokenResult> { tokenResult };

                // Display results based on output format
                DisplayResults(tokenResults, outputFormat, verbose, logger);

                // Save bearer token to project configuration files
                if (setupConfig != null)
                {
                    await SaveBearerTokenToPlatformConfigAsync(token, setupConfig, logger);
                }
                else
                {
                    // No config file: user must manually copy the token
                    logger.LogInformation("");
                    logger.LogInformation("Note: To use this token in your samples, manually add it to:");
                    logger.LogInformation("  - .NET projects: Properties/launchSettings.json > profiles > environmentVariables > BEARER_TOKEN");
                    logger.LogInformation("  - Python/Node.js projects: .env file as BEARER_TOKEN={Token}", token);
                    logger.LogInformation("");
                }

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

                if (!string.IsNullOrWhiteSpace(result.CacheFilePath))
                {
                    logger.LogInformation("  Cached at: {CacheFilePath}", result.CacheFilePath);
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
            error = r.Error,
            cacheFilePath = r.CacheFilePath
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
        public string? CacheFilePath { get; set; }
    }

    /// <summary>
    /// Saves the bearer token to .env file for Python/Node.js samples or launchSettings.json for .NET samples
    /// </summary>
    private static async Task SaveBearerTokenToPlatformConfigAsync(
        string token,
        Agent365Config config,
        ILogger logger)
    {
        try
        {
            // Determine project directory from config
            var projectDir = config.DeploymentProjectPath;
            if (string.IsNullOrWhiteSpace(projectDir))
            {
                projectDir = Environment.CurrentDirectory;
                logger.LogDebug("deploymentProjectPath not configured, using current directory for token update");
            }

            // Resolve to absolute path
            if (!Path.IsPathRooted(projectDir))
            {
                projectDir = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, projectDir));
            }

            if (!Directory.Exists(projectDir))
            {
                logger.LogWarning("Project directory does not exist: {Path}. Skipping token update.", projectDir);
                return;
            }

            // Detect platform type using PlatformDetector
            var cleanLoggerFactory = LoggerFactoryHelper.CreateCleanLoggerFactory();
            var platformDetector = new PlatformDetector(
                cleanLoggerFactory.CreateLogger<PlatformDetector>());
            var platform = platformDetector.Detect(projectDir);

            // Handle token saving based on platform type
            if (platform == ProjectPlatform.DotNet)
            {
                await SaveBearerTokenToLaunchSettingsAsync(token, projectDir, logger);
            }
            else if (platform == ProjectPlatform.Python || platform == ProjectPlatform.NodeJs)
            {
                await SaveBearerTokenToDotEnvAsync(token, projectDir, platform, logger);
            }
            else
            {
                logger.LogDebug("Project type is {Platform}, skipping bearer token update (only applies to .NET/Python/Node.js)", platform);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save bearer token: {Message}", ex.Message);
            logger.LogInformation("You can manually add the token to your project configuration");
        }
    }

    /// <summary>
    /// Saves the bearer token to .env file for Python and Node.js projects
    /// </summary>
    private static async Task SaveBearerTokenToDotEnvAsync(
        string token,
        string projectDir,
        ProjectPlatform platform,
        ILogger logger)
    {
        var envPath = Path.Combine(projectDir, ".env");
        
        if (!File.Exists(envPath))
        {
            logger.LogDebug(".env file not found at {Path}, skipping token update for {Platform} project", envPath, platform);
            logger.LogInformation("To use the bearer token in your {Platform} application, add it to .env file:", platform);
            logger.LogInformation("  Create .env file in your project directory with: BEARER_TOKEN=<your bearer token>");
            return;
        }

        // Read existing .env content
        var lines = (await File.ReadAllLinesAsync(envPath)).ToList();

        // Update or add BEARER_TOKEN
        var bearerTokenLine = $"{AuthenticationConstants.BearerTokenEnvironmentVariable}={token}";
        var existingIndex = lines.FindIndex(l => 
            l.StartsWith($"{AuthenticationConstants.BearerTokenEnvironmentVariable}=", StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            lines[existingIndex] = bearerTokenLine;
            logger.LogInformation("Updated BEARER_TOKEN in {Path}", envPath);
        }
        else
        {
            lines.Add(bearerTokenLine);
            logger.LogInformation("Added BEARER_TOKEN to {Path}", envPath);
        }

        // Write back to .env file
        await File.WriteAllLinesAsync(envPath, lines, new System.Text.UTF8Encoding(false));
        
        logger.LogInformation("Bearer token saved to .env file for {Platform} sample", platform);
        logger.LogInformation("  Path: {Path}", envPath);
        logger.LogInformation("  The token can now be used by your {Platform} application", platform);
    }

    /// <summary>
    /// Saves the bearer token to launchSettings.json for .NET projects
    /// </summary>
    private static async Task SaveBearerTokenToLaunchSettingsAsync(
        string token,
        string projectDir,
        ILogger logger)
    {
        // Check for Properties/launchSettings.json
        var launchSettingsPath = Path.Combine(projectDir, "Properties", "launchSettings.json");
        
        if (!File.Exists(launchSettingsPath))
        {
            logger.LogDebug("launchSettings.json not found at {Path}, skipping token update for .NET project", launchSettingsPath);
            logger.LogInformation("To use the bearer token in your .NET application, add it to launchSettings.json:");
            logger.LogInformation("  Properties/launchSettings.json > profiles > [profile-name] > environmentVariables > BEARER_TOKEN");
            return;
        }

        try
        {
            // Read and parse existing launchSettings.json
            var jsonText = await File.ReadAllTextAsync(launchSettingsPath);
            var launchSettings = JsonSerializer.Deserialize<JsonElement>(jsonText);

            if (!launchSettings.TryGetProperty("profiles", out var profiles))
            {
                logger.LogWarning("No profiles found in launchSettings.json");
                return;
            }

            // Check if any profile has BEARER_TOKEN defined
            var profilesWithBearerToken = new List<string>();
            foreach (var profile in profiles.EnumerateObject())
            {
                if (profile.Value.TryGetProperty("environmentVariables", out var envVars) &&
                    envVars.ValueKind == JsonValueKind.Object)
                {
                    foreach (var envVar in envVars.EnumerateObject())
                    {
                        if (envVar.Name == "BEARER_TOKEN")
                        {
                            profilesWithBearerToken.Add(profile.Name);
                            break;
                        }
                    }
                }
            }

            if (profilesWithBearerToken.Count == 0)
            {
                logger.LogInformation("No profiles found with BEARER_TOKEN in {Path}", launchSettingsPath);
                logger.LogInformation("To use the bearer token, add BEARER_TOKEN to a profile's environmentVariables:");
                logger.LogInformation("  \"environmentVariables\": {{ \"BEARER_TOKEN\": \"\" }}");
                return;
            }

            // Build updated JSON with BEARER_TOKEN in environment variables
            var updatedJson = UpdateLaunchSettingsWithToken(launchSettings, token);

            // Write back to file with indentation
            var options = new JsonSerializerOptions { WriteIndented = true };
            var updatedJsonText = JsonSerializer.Serialize(updatedJson, options);
            await File.WriteAllTextAsync(launchSettingsPath, updatedJsonText, new System.Text.UTF8Encoding(false));

            logger.LogInformation("Updated BEARER_TOKEN in {Path}", launchSettingsPath);
            logger.LogInformation("Bearer token saved to launchSettings.json for .NET sample");
            logger.LogInformation("  Path: {Path}", launchSettingsPath);
            logger.LogInformation("  Updated {Count} profile(s): {Profiles}", 
                profilesWithBearerToken.Count, 
                string.Join(", ", profilesWithBearerToken));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse launchSettings.json: {Message}", ex.Message);
            logger.LogInformation("You can manually add BEARER_TOKEN to launchSettings.json environmentVariables");
        }
    }

    /// <summary>
    /// Updates the launchSettings JSON structure with the bearer token only in profiles that already have BEARER_TOKEN defined
    /// </summary>
    private static JsonElement UpdateLaunchSettingsWithToken(JsonElement launchSettings, string token)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            foreach (var property in launchSettings.EnumerateObject())
            {
                if (property.Name == "profiles" && property.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("profiles");
                    writer.WriteStartObject();

                    // only update BEARER_TOKEN if it already exists
                    foreach (var profile in property.Value.EnumerateObject())
                    {
                        writer.WritePropertyName(profile.Name);
                        writer.WriteStartObject();

                        // Write all properties for this profile
                        foreach (var profileProp in profile.Value.EnumerateObject())
                        {
                            if (profileProp.Name == "environmentVariables" && profileProp.Value.ValueKind == JsonValueKind.Object)
                            {
                                writer.WritePropertyName("environmentVariables");
                                writer.WriteStartObject();

                                // Copy existing environment variables, updating BEARER_TOKEN only if it exists
                                foreach (var envVar in profileProp.Value.EnumerateObject())
                                {
                                    if (envVar.Name == "BEARER_TOKEN")
                                    {
                                        // Update BEARER_TOKEN with new value
                                        writer.WriteString("BEARER_TOKEN", token);
                                    }
                                    else
                                    {
                                        writer.WritePropertyName(envVar.Name);
                                        envVar.Value.WriteTo(writer);
                                    }
                                }

                                writer.WriteEndObject();
                            }
                            else
                            {
                                writer.WritePropertyName(profileProp.Name);
                                profileProp.Value.WriteTo(writer);
                            }
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndObject();
                }
                else
                {
                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        stream.Position = 0;
        return JsonSerializer.Deserialize<JsonElement>(stream);
    }
}
