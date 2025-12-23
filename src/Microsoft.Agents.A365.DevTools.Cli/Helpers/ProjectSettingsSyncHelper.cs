// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using System.Collections.Generic;

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers;

/// <summary>
/// Helper methods for syncing project settings from the deployment project.
/// </summary>
public static class ProjectSettingsSyncHelper
{
    private const string DEFAULT_AUTHORITY_ENDPOINT = "https://login.microsoftonline.com";
    private const string DEFAULT_USER_AUTHORIZATION_SCOPE = "https://graph.microsoft.com/.default";
    // Messaging Bot API Application GUID
    private const string DEFAULT_SERVICE_CONNECTION_SCOPE = "5a807f24-c9de-44ee-a3a7-329e88a00ffc/.default";

    public static async Task ExecuteAsync(
        string a365ConfigPath,
        string a365GeneratedPath,
        IConfigService configService,
        PlatformDetector platformDetector,
        ILogger logger
    )
    {
        if (!File.Exists(a365GeneratedPath))
            throw new FileNotFoundException("a365.generated.config.json not found", a365GeneratedPath);

        // Load merged config via ConfigService
        var pkgConfig = await configService.LoadAsync(a365ConfigPath, a365GeneratedPath);

        var project = pkgConfig.DeploymentProjectPath;
        if (string.IsNullOrWhiteSpace(project) || !Directory.Exists(project))
        {
            logger.LogWarning("deploymentProjectPath is not set or does not exist in a365.config.json; skipping project settings sync.");
            return;
        }

        // Detect platform type (DotNet -> NodeJs -> Python -> Unknown)
        var platform = platformDetector.Detect(project);
        var appsettings = Path.Combine(project, "appsettings.json");
        var dotenv = Path.Combine(project, ".env");

        switch (platform)
        {
            case ProjectPlatform.DotNet:
            {
                // Create appsettings.json if missing
                if (!File.Exists(appsettings))
                {
                    await File.WriteAllTextAsync(appsettings, "{\n  \"Connections\": {}\n}\n");
                    logger.LogInformation("Created: {Path}", appsettings);
                }

                await UpdateDotnetAppsettingsAsync(appsettings, pkgConfig, logger);
                logger.LogInformation("Updated: {Path}", appsettings);
                break;
            }

            case ProjectPlatform.NodeJs:
            {
                if (!File.Exists(dotenv))
                {
                    await File.WriteAllTextAsync(dotenv, "");
                    logger.LogInformation("Created: {Path}", dotenv);
                }

                await UpdateNodeEnvAsync(dotenv, pkgConfig);
                logger.LogInformation("Updated: {Path}", dotenv);
                break;
            }

            case ProjectPlatform.Python:
            {
                if (!File.Exists(dotenv))
                {
                    await File.WriteAllTextAsync(dotenv, "");
                    logger.LogInformation("Created: {Path}", dotenv);
                }

                await UpdatePythonEnvAsync(dotenv, pkgConfig);
                logger.LogInformation("Updated: {Path}", dotenv);
                break;
            }

            default:
            {
                logger.LogWarning("Could not detect project platform in {ProjectPath}; no files updated.", project);
                return;
            }
        }

        logger.LogInformation("Stamped TenantId, ServiceConnection, and AgentBluePrint settings into {ProjectPath}", project);
    }

    /// <summary>
    /// Saves the bearer token to .env file for Python/Node.js samples or launchSettings.json for .NET samples
    /// </summary>
    public static async Task SaveBearerTokenToPlatformConfigAsync(
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
        await File.WriteAllLinesAsync(envPath, lines, new UTF8Encoding(false));
        
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
                        if (envVar.Name == AuthenticationConstants.BearerTokenEnvironmentVariable)
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
            await File.WriteAllTextAsync(launchSettingsPath, updatedJsonText, new UTF8Encoding(false));

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
                                    if (envVar.Name == AuthenticationConstants.BearerTokenEnvironmentVariable)
                                    {
                                        // Update BEARER_TOKEN with new value
                                        writer.WriteString(AuthenticationConstants.BearerTokenEnvironmentVariable, token);
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

    // ---------------------------
    // Writers
    // ---------------------------
    private static async Task UpdateDotnetAppsettingsAsync(
        string appsettingsPath,
        Agent365Config pkgConfig,
        ILogger logger)
    {
        var text = await File.ReadAllTextAsync(appsettingsPath);
        if (string.IsNullOrWhiteSpace(text)) text = "{ }";

        var root = JsonNode.Parse(
                   text,
                   nodeOptions: null,
                   documentOptions: new JsonDocumentOptions {
                       CommentHandling = JsonCommentHandling.Skip,
                       AllowTrailingCommas = true
                   }) as JsonObject
               ?? new JsonObject();

        static JsonObject RequireObj(JsonObject parent, string prop)
        {
            if (parent[prop] is not JsonObject o)
            {
                o = new JsonObject();
                parent[prop] = o;
            }
            return o;
        }

        // -- TokenValidation --
        var tokenValidation = RequireObj(root, "TokenValidation");
        tokenValidation["Enabled"] = false;

        var audiences = new JsonArray();
        if (!string.IsNullOrWhiteSpace(pkgConfig.AgentBlueprintId))
            audiences.Add(pkgConfig.AgentBlueprintId);
        tokenValidation["Audiences"] = audiences;

        if (!string.IsNullOrWhiteSpace(pkgConfig.TenantId))
            tokenValidation["TenantId"] = pkgConfig.TenantId;

        // -- AgentApplication --
        var agentApplication = RequireObj(root, "AgentApplication");
        agentApplication["StartTypingTimer"] = false;
        agentApplication["RemoveRecipientMention"] = false;
        agentApplication["NormalizeMentions"] = false;

         var userAuth = RequireObj(agentApplication, "UserAuthorization");
        userAuth["AutoSignin"] = false;

        var handlers = RequireObj(userAuth, "Handlers");
        var agentic = RequireObj(handlers, "agentic");
        agentic["Type"] = "AgenticUserAuthorization";

        var agenticSettings = RequireObj(agentic, "Settings");
        agenticSettings["AlternateBlueprintConnectionName"] = "ServiceConnection";
        var uaScopes = new JsonArray(DEFAULT_USER_AUTHORIZATION_SCOPE);
        agenticSettings["Scopes"] = uaScopes;
        
        // -- Connections --
        var connections = RequireObj(root, "Connections");
        var svc = RequireObj(connections, "ServiceConnection");
        var svcSettings = RequireObj(svc, "Settings");
        if (svcSettings["AuthType"] is null) svcSettings["AuthType"] = "ClientSecret";
        
        if (!string.IsNullOrWhiteSpace(pkgConfig.TenantId))
        {
            var authority = $"{DEFAULT_AUTHORITY_ENDPOINT}/{pkgConfig.TenantId}";
            svcSettings["AuthorityEndpoint"] = authority;
        }

        if (!string.IsNullOrWhiteSpace(pkgConfig.AgentBlueprintClientSecret))
        {
            // Decrypt the secret before writing to appsettings.json (must be plaintext for runtime use)
            var plaintextSecret = SecretProtectionHelper.UnprotectSecret(
                pkgConfig.AgentBlueprintClientSecret,
                pkgConfig.AgentBlueprintClientSecretProtected,
                logger);
            svcSettings["ClientSecret"] = plaintextSecret;
        }

        if (!string.IsNullOrWhiteSpace(pkgConfig.AgentBlueprintId))
        {
            svcSettings["ClientId"] = pkgConfig.AgentBlueprintId;
            svcSettings["AgentId"] = pkgConfig.AgentBlueprintId;
        }

        svcSettings["Scopes"] = new JsonArray(DEFAULT_SERVICE_CONNECTION_SCOPE);

        // -- ConnectionsMap --
        var connectionsMap = new JsonArray
        {
            new JsonObject
            {
                ["ServiceUrl"] = "*",
                ["Connection"] = "ServiceConnection"
            }
        };
        root["ConnectionsMap"] = connectionsMap;

        var updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(appsettingsPath, updated, new UTF8Encoding(false));
    }

    private static async Task UpdatePythonEnvAsync(
        string envPath,
        Agent365Config pkgConfig)
    {
        var lines = File.Exists(envPath)
            ? (await File.ReadAllLinesAsync(envPath)).ToList()
            : new List<string>();

        void Set(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var idx = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            var safe = $"{key}={EscapeEnv(value)}";
            if (idx >= 0) lines[idx] = safe;
            else lines.Add(safe);
        }

        // --- Service Connection ---
        if (!string.IsNullOrWhiteSpace(pkgConfig.AgentBlueprintId))
        {
            Set("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID", pkgConfig.AgentBlueprintId);
            Set("AGENT_ID", pkgConfig.AgentBlueprintId);
        }
        if (!string.IsNullOrWhiteSpace(pkgConfig.AgentBlueprintClientSecret))
            Set("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTSECRET", pkgConfig.AgentBlueprintClientSecret);
        if (!string.IsNullOrWhiteSpace(pkgConfig.TenantId))
            Set("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID", pkgConfig.TenantId);
        Set("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__SCOPES", DEFAULT_SERVICE_CONNECTION_SCOPE);

        // --- Agentic UserAuthorization (python env) ---
        Set("AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__AGENTIC__SETTINGS__TYPE",
            "AgenticUserAuthorization");
        Set("AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__AGENTIC__SETTINGS__ALT_BLUEPRINT_NAME",
            "SERVICE_CONNECTION");
        Set("AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__AGENTIC__SETTINGS__SCOPES",
            DEFAULT_USER_AUTHORIZATION_SCOPE);

        // --- ConnectionsMap[0] ---
        Set("CONNECTIONSMAP__0__SERVICEURL", "*");
        Set("CONNECTIONSMAP__0__CONNECTION", "SERVICE_CONNECTION");

        await File.WriteAllLinesAsync(envPath, lines, new UTF8Encoding(false));
    }

    private static async Task UpdateNodeEnvAsync(
        string envPath,
        Agent365Config pkgConfig)
    {
        var lines = File.Exists(envPath)
            ? (await File.ReadAllLinesAsync(envPath)).ToList()
            : new List<string>();

        void Set(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var idx = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            var safe = $"{key}={(value ?? "")}";
            if (idx >= 0) lines[idx] = safe;
            else lines.Add(safe);
        }

        // --- Service Connection ---
        if (!string.IsNullOrWhiteSpace(pkgConfig.AgentBlueprintId))
        {
            Set("connections__service_connection__settings__clientId", pkgConfig.AgentBlueprintId);
            Set("agent_id", pkgConfig.AgentBlueprintId);
        }

        if (!string.IsNullOrWhiteSpace(pkgConfig.AgentBlueprintClientSecret))
            Set("connections__service_connection__settings__clientSecret", pkgConfig.AgentBlueprintClientSecret);

        if (!string.IsNullOrWhiteSpace(pkgConfig.TenantId))
            Set("connections__service_connection__settings__tenantId", pkgConfig.TenantId);

        Set("connections__service_connection__settings__scopes", DEFAULT_SERVICE_CONNECTION_SCOPE);

        // --- Set service connection as default ---
        Set("connectionsMap__0__serviceUrl", "*");
        Set("connectionsMap__0__connection", "service_connection");

        // --- AgenticAuthentication Options ---
        Set("agentic_altBlueprintConnectionName", "service_connection");
        Set("agentic_scopes", DEFAULT_USER_AUTHORIZATION_SCOPE);
        Set("agentic_connectionName", "AgenticAuthConnection");

        await File.WriteAllLinesAsync(envPath, lines, new UTF8Encoding(false));
    }

    private static string EscapeEnv(string value)
    {
        // Keep as-is unless contains spaces or special chars; then quote
        // Most .env loaders accept raw secrets, but quoting is safe for ~all cases.
        if (value.Contains(' ') || value.Contains('#') || value.Contains('"'))
        {
            var escaped = value.Replace("\"", "\\\"");
            return $"\"{escaped}\"";
        }
        return value;
    }
}
