// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers;

/// <summary>
/// Helper methods for working with ToolingManifest.json
/// </summary>
public static class ManifestHelper
{
    /// <summary>
    /// Gets the default JSON serializer options for manifest files
    /// Uses indented formatting and relaxed JSON escaping for better readability
    /// </summary>
    public static JsonSerializerOptions GetManifestSerializerOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            // UnsafeRelaxedJsonEscaping allows Unicode characters without escaping
            // This makes the JSON more readable in text editors
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }

    /// <summary>
    /// Extracts server name from a JSON element
    /// </summary>
    /// <param name="serverElement">JSON element containing server data</param>
    /// <returns>Server name or null if not found</returns>
    public static string? ExtractServerName(JsonElement serverElement)
    {
        if (serverElement.TryGetProperty(McpConstants.ManifestProperties.McpServerName, out var nameElement))
        {
            return nameElement.GetString();
        }
        return null;
    }

    /// <summary>
    /// Extracts unique server name from a JSON element, falling back to server name if not present
    /// </summary>
    /// <param name="serverElement">JSON element containing server data</param>
    /// <returns>Unique server name or null if not found</returns>
    public static string? ExtractUniqueServerName(JsonElement serverElement)
    {
        if (serverElement.TryGetProperty(McpConstants.ManifestProperties.McpServerUniqueName, out var uniqueNameElement))
        {
            return uniqueNameElement.GetString();
        }
        
        // Fall back to server name if unique name not present
        return ExtractServerName(serverElement);
    }

    /// <summary>
    /// Creates a server object with name and unique name
    /// </summary>
    /// <param name="serverName">Name of the server</param>
    /// <param name="uniqueName">Unique name (optional, defaults to serverName)</param>
    /// <returns>Anonymous object representing the server</returns>
    public static object CreateServerObject(string serverName, string? uniqueName = null)
    {
        // Get scope and audience from mapping
        var (scope, audience) = McpConstants.ServerScopeMappings.GetScopeAndAudience(serverName);
        
        return CreateCompleteServerObject(serverName, uniqueName, null, scope, audience);
    }

    /// <summary>
    /// Creates a complete server object with scope and audience information
    /// </summary>
    /// <param name="serverName">Name of the server</param>
    /// <param name="uniqueName">Unique name (optional, defaults to serverName)</param>
    /// <param name="url">Server URL (optional)</param>
    /// <param name="scope">Required Entra scope</param>
    /// <param name="audience">Token audience for this server</param>
    /// <param name="publisher">Publisher of the server (e.g. "Microsoft")</param>
    /// <returns>Anonymous object representing the complete server configuration</returns>
    public static object CreateCompleteServerObject(string serverName, string? uniqueName = null, string? url = null, string? scope = null, string? audience = null, string? publisher = null)
    {
        var serverObj = new Dictionary<string, object>
        {
            [McpConstants.ManifestProperties.McpServerName] = serverName,
            [McpConstants.ManifestProperties.McpServerUniqueName] = uniqueName ?? serverName
        };

        if (!string.IsNullOrWhiteSpace(url))
        {
            serverObj[McpConstants.ManifestProperties.Url] = url;
        }

        if (!string.IsNullOrWhiteSpace(scope))
        {
            serverObj[McpConstants.ManifestProperties.Scope] = scope;
        }

        if (!string.IsNullOrWhiteSpace(audience))
        {
            serverObj[McpConstants.ManifestProperties.Audience] = audience;
        }

        if (!string.IsNullOrWhiteSpace(publisher))
        {
            serverObj[McpConstants.ManifestProperties.Publisher] = publisher;
        }

        return serverObj;
    }

    /// <summary>
    /// Serializes a list of servers to JSON and writes to file
    /// </summary>
    /// <param name="manifestPath">Path to the manifest file</param>
    /// <param name="servers">List of server objects</param>
    public static async Task WriteManifestAsync(string manifestPath, IEnumerable<object> servers)
    {
        var manifest = new Dictionary<string, object>
        {
            [McpConstants.ManifestProperties.McpServers] = servers
        };

        var jsonOptions = GetManifestSerializerOptions();
        var manifestJson = JsonSerializer.Serialize(manifest, jsonOptions);
        await File.WriteAllTextAsync(manifestPath, manifestJson);
    }

    /// <summary>
    /// Reads and parses the manifest file, returning the servers array
    /// </summary>
    /// <param name="manifestPath">Path to the manifest file</param>
    /// <returns>Tuple containing parsed servers and their names, or null if file doesn't exist</returns>
    public static async Task<(List<JsonElement> servers, HashSet<string> serverNames)?> ReadManifestAsync(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var jsonContent = await File.ReadAllTextAsync(manifestPath);
        using var manifestDoc = JsonDocument.Parse(jsonContent);
        var manifestRoot = manifestDoc.RootElement;

        if (!manifestRoot.TryGetProperty(McpConstants.ManifestProperties.McpServers, out var serversElement)
            || serversElement.ValueKind != JsonValueKind.Array)
        {
            return (new List<JsonElement>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var servers = new List<JsonElement>();
        var serverNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var serverElement in serversElement.EnumerateArray())
        {
            servers.Add(serverElement.Clone());
            
            var serverName = ExtractServerName(serverElement);
            if (!string.IsNullOrEmpty(serverName))
            {
                serverNames.Add(serverName);
            }
        }

        return (servers, serverNames);
    }

    /// <summary>
    /// Converts JsonElement servers to server objects
    /// </summary>
    /// <param name="jsonElements">List of JSON elements</param>
    /// <returns>List of server objects</returns>
    public static List<object> ConvertToServerObjects(IEnumerable<JsonElement> jsonElements)
    {
        var servers = new List<object>();
        
        foreach (var element in jsonElements)
        {
            var serverName = ExtractServerName(element);
            var uniqueName = ExtractUniqueServerName(element);
            
            if (!string.IsNullOrEmpty(serverName))
            {
                // Extract additional fields if present
                string? url = null;
                if (element.TryGetProperty(McpConstants.ManifestProperties.Url, out var urlElement))
                {
                    url = urlElement.GetString();
                }

                string? scope = null;
                if (element.TryGetProperty(McpConstants.ManifestProperties.Scope, out var scopeElement) 
                    && scopeElement.ValueKind == JsonValueKind.String)
                {
                    scope = scopeElement.GetString();
                }

                string? audience = null;
                if (element.TryGetProperty(McpConstants.ManifestProperties.Audience, out var audienceElement))
                {
                    audience = audienceElement.GetString();
                }

                string? publisher = null;
                if (element.TryGetProperty(McpConstants.ManifestProperties.Publisher, out var publisherElement))
                {
                    publisher = publisherElement.GetString();
                }

                servers.Add(CreateCompleteServerObject(serverName, uniqueName, url, scope, audience, publisher));
            }
        }
        
        return servers;
    }

    /// <summary>
    /// Reads ToolingManifest.json and returns the unique list of scopes required by all MCP servers.
    /// Strategy:
    ///  1) If a server entry has an explicit "scope" property, use it.
    ///  2) Otherwise, use McpConstants.ServerScopeMappings.GetScopeAndAudience(serverName).
    ///  3) Always include "McpServersMetadata.Read.All".
    /// </summary>
    public static async Task<string[]> GetRequiredScopesAsync(string manifestPath)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "McpServersMetadata.Read.All"
        };

        var parsed = await ReadManifestAsync(manifestPath);
        if (parsed is null) return scopes.OrderBy(s => s).ToArray();

        var (servers, _) = parsed.Value;

        foreach (var element in servers)
        {
            // Prefer explicit "scope" in manifest
            if (element.TryGetProperty(McpConstants.ManifestProperties.Scope, out var scopeEl) &&
                scopeEl.ValueKind == JsonValueKind.String)
            {
                var s = scopeEl.GetString();
                if (!string.IsNullOrWhiteSpace(s) &&
                    !string.Equals(s, "null", StringComparison.OrdinalIgnoreCase))
                {
                    AddScopeString(scopes, s);
                    continue;
                }
            }

            // Fallback to mapping
            var serverName = ExtractServerName(element);
            if (!string.IsNullOrWhiteSpace(serverName))
            {
                var (mappedScope, _) = McpConstants.ServerScopeMappings.GetScopeAndAudience(serverName);
                if (!string.IsNullOrWhiteSpace(mappedScope))
                {
                    AddScopeString(scopes, mappedScope);
                }
            }
        }

        return scopes.OrderBy(s => s).ToArray();

        static void AddScopeString(HashSet<string> set, string scopeValue)
        {
            // Accept either a single scope or a space-delimited scope string
            var parts = scopeValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in parts) set.Add(p);
        }
    }

    /// <summary>
    /// Reads ToolingManifest.json and returns scopes grouped by their audience (resourceAppId).
    /// Supports V1 (shared ATG AppId), V2 (per-server AppId), and mixed manifests.
    /// Fallback rules when audience is missing or in legacy api:// format:
    ///   → falls back to <see cref="McpConstants.WorkIQToolsProdAppId"/> (ATG AppId).
    /// </summary>
    /// <param name="manifestPath">Path to ToolingManifest.json</param>
    /// <param name="excludeLegacyAtg">
    /// When true, omits all entries whose resolved audience is the shared ATG AppId.
    /// Only pass true when V2 SDK is confirmed live (--remove-legacy-scopes flag).
    /// </param>
    /// <returns>Dictionary of resourceAppId → ordered scopes array</returns>
    public static async Task<Dictionary<string, string[]>> GetScopesByAudienceAsync(
        string manifestPath,
        bool excludeLegacyAtg = false)
    {
        var atgAppId = McpConstants.WorkIQToolsProdAppId;
        var scopesByAudience = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // McpServersMetadata.Read.All is always required and belongs to the ATG AppId
        if (!excludeLegacyAtg)
        {
            scopesByAudience[atgAppId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "McpServersMetadata.Read.All"
            };
        }

        var parsed = await ReadManifestAsync(manifestPath);
        if (parsed is null)
            return ToDictionary(scopesByAudience);

        var (servers, _) = parsed.Value;

        foreach (var element in servers)
        {
            // Resolve scope — prefer manifest field, fall back to static mapping
            string? scope = null;
            if (element.TryGetProperty(McpConstants.ManifestProperties.Scope, out var scopeEl) &&
                scopeEl.ValueKind == JsonValueKind.String)
            {
                var raw = scopeEl.GetString();
                if (!string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
                    scope = raw;
            }
            if (string.IsNullOrWhiteSpace(scope))
            {
                var serverName = ExtractServerName(element);
                if (!string.IsNullOrWhiteSpace(serverName))
                {
                    var (mappedScope, _) = McpConstants.ServerScopeMappings.GetScopeAndAudience(serverName);
                    scope = mappedScope;
                }
            }
            if (string.IsNullOrWhiteSpace(scope)) continue;

            // Resolve audience — fall back to ATG AppId when missing or old api:// format
            string? audience = null;
            if (element.TryGetProperty(McpConstants.ManifestProperties.Audience, out var audienceEl))
                audience = audienceEl.GetString();

            if (string.IsNullOrWhiteSpace(audience) ||
                audience.StartsWith("api://", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(audience, "default", StringComparison.OrdinalIgnoreCase))
            {
                audience = atgAppId;
            }

            if (excludeLegacyAtg &&
                string.Equals(audience, atgAppId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!scopesByAudience.TryGetValue(audience, out var scopeSet))
            {
                scopeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                scopesByAudience[audience] = scopeSet;
            }

            AddScopeString(scopeSet, scope);
        }

        return ToDictionary(scopesByAudience);

        static Dictionary<string, string[]> ToDictionary(Dictionary<string, HashSet<string>> src) =>
            src.ToDictionary(
                k => k.Key,
                v => v.Value.OrderBy(s => s).ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads ToolingManifest.json and returns a mapping of audience → server unique names.
    /// Used by get-token to associate each acquired token with the right per-server env var key.
    /// </summary>
    public static async Task<Dictionary<string, List<string>>> GetServerNamesByAudienceAsync(string manifestPath)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var parsed = await ReadManifestAsync(manifestPath);
        if (parsed is null) return result;

        var (servers, _) = parsed.Value;

        foreach (var element in servers)
        {
            string? audience = null;
            if (element.TryGetProperty(McpConstants.ManifestProperties.Audience, out var audienceEl))
                audience = audienceEl.GetString();

            // Skip all forms that resolve to the shared ATG audience — these are handled via the
            // shared BEARER_TOKEN env var, not per-server BEARER_TOKEN_{AUDIENCE} entries.
            if (string.IsNullOrWhiteSpace(audience) ||
                audience.StartsWith("api://", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(audience, "default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(audience, McpConstants.WorkIQToolsProdAppId, StringComparison.OrdinalIgnoreCase))
                continue;

            var serverName = ExtractUniqueServerName(element);
            if (string.IsNullOrWhiteSpace(serverName) &&
                element.TryGetProperty("mcpServerName", out var serverNameEl))
            {
                serverName = serverNameEl.GetString();
            }

            if (string.IsNullOrWhiteSpace(serverName)) continue;

            if (!result.TryGetValue(audience, out var names))
            {
                names = new List<string>();
                result[audience] = names;
            }
            names.Add(serverName);
        }

        return result;
    }
}
