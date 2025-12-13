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
    /// <returns>Anonymous object representing the complete server configuration</returns>
    public static object CreateCompleteServerObject(string serverName, string? uniqueName = null, string? url = null, string? scope = null, string? audience = null)
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

                servers.Add(CreateCompleteServerObject(serverName, uniqueName, url, scope, audience));
            }
        }
        
        return servers;
    }

    /// <summary>
    /// Reads toolingManifest.json and returns the unique list of audiences required by all MCP servers.
    /// Strategy:
    ///  1) If a server entry has an explicit "audience" property, use it.
    ///  2) Otherwise, use McpConstants.ServerScopeMappings.GetScopeAndAudience(serverName).
    ///  3) If no servers found, returns empty array.
    /// </summary>
    public static async Task<string[]> GetRequiredAudiencesAsync(string manifestPath)
    {
        var audiences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var parsed = await ReadManifestAsync(manifestPath);
        if (parsed is null) return audiences.OrderBy(a => a).ToArray();

        var (servers, _) = parsed.Value;

        foreach (var element in servers)
        {
            // Prefer explicit "audience" in manifest
            if (element.TryGetProperty(McpConstants.ManifestProperties.Audience, out var audienceEl) &&
                audienceEl.ValueKind == JsonValueKind.String)
            {
                var aud = audienceEl.GetString();
                if (!string.IsNullOrWhiteSpace(aud))
                {
                    audiences.Add(aud);
                    continue;
                }
            }

            // Fallback to mapping
            var serverName = ExtractServerName(element);
            if (!string.IsNullOrWhiteSpace(serverName))
            {
                var (_, mappedAudience) = McpConstants.ServerScopeMappings.GetScopeAndAudience(serverName);
                if (!string.IsNullOrWhiteSpace(mappedAudience))
                {
                    audiences.Add(mappedAudience);
                }
            }
        }

        return audiences.OrderBy(a => a).ToArray();
    }

    /// <summary>
    /// Reads toolingManifest.json and returns the unique list of scopes required by all MCP servers.
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
                if (!string.IsNullOrWhiteSpace(s))
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
}
