// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
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
    private const string DEFAULT_SERVICE_CONNECTION_SCOPE = "https://api.botframework.com/.default";

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

                await UpdateDotnetAppsettingsAsync(appsettings, pkgConfig);
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

    // ---------------------------
    // Writers
    // ---------------------------
    private static async Task UpdateDotnetAppsettingsAsync(
        string appsettingsPath,
        Agent365Config pkgConfig)
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
            svcSettings["ClientSecret"] = pkgConfig.AgentBlueprintClientSecret;
        }

        if (!string.IsNullOrWhiteSpace(pkgConfig.AgentBlueprintId))
        {
            svcSettings["ClientId"] = pkgConfig.AgentBlueprintId;
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
            Set("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID", pkgConfig.AgentBlueprintId);
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
            Set("connections__service_connection__settings__clientId", pkgConfig.AgentBlueprintId);

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
