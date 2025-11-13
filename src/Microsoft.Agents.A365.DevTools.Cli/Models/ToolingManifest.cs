// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Model representing the complete tooling manifest configuration
/// Contains all MCP server definitions with their scope requirements
/// </summary>
public class ToolingManifest
{
    /// <summary>
    /// JSON schema reference for validation (optional)
    /// </summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }
    
    /// <summary>
    /// Version of the manifest format
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.1";
    
    /// <summary>
    /// Array of MCP server configurations
    /// </summary>
    [JsonPropertyName("mcpServers")]
    public McpServerConfig[] McpServers { get; set; } = Array.Empty<McpServerConfig>();

    /// <summary>
    /// Gets all unique scopes required across all MCP servers
    /// </summary>
    /// <returns>Array of unique scope strings</returns>
    public string[] GetAllRequiredScopes()
    {
        return McpServers
            .Where(server => !string.IsNullOrWhiteSpace(server.Scope))
            .Select(server => server.Scope!)
            .Distinct()
            .OrderBy(scope => scope)
            .ToArray();
    }

    /// <summary>
    /// Gets all unique audiences used across all MCP servers
    /// </summary>
    /// <returns>Array of unique audience strings</returns>
    public string[] GetAllAudiences()
    {
        return McpServers
            .Where(server => !string.IsNullOrWhiteSpace(server.Audience))
            .Select(server => server.Audience!)
            .Distinct()
            .OrderBy(audience => audience)
            .ToArray();
    }

    /// <summary>
    /// Finds an MCP server configuration by name
    /// </summary>
    /// <param name="serverName">The server name to search for</param>
    /// <returns>The server configuration or null if not found</returns>
    public McpServerConfig? FindServerByName(string serverName)
    {
        return McpServers.FirstOrDefault(server => 
            string.Equals(server.McpServerName, serverName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(server.McpServerUniqueName, serverName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the required scope for a specific server
    /// </summary>
    /// <param name="serverName">The server name to get scope for</param>
    /// <returns>Required scope for the server or null if not found</returns>
    public string? GetServerScope(string serverName)
    {
        var server = FindServerByName(serverName);
        return server?.Scope;
    }

    /// <summary>
    /// Validates the manifest configuration
    /// </summary>
    /// <returns>True if valid, false otherwise</returns>
    public bool IsValid()
    {
        // Check that we have at least one server
        if (McpServers.Length == 0)
            return false;

        // Check that all servers are valid
        return McpServers.All(server => server.IsValid());
    }

    /// <summary>
    /// Gets validation errors for the manifest
    /// </summary>
    /// <returns>Array of validation error messages</returns>
    public string[] GetValidationErrors()
    {
        var errors = new List<string>();

        if (McpServers.Length == 0)
        {
            errors.Add("Manifest must contain at least one MCP server");
        }

        for (int i = 0; i < McpServers.Length; i++)
        {
            var server = McpServers[i];
            if (!server.IsValid())
            {
                errors.Add($"Server at index {i} is invalid: missing required fields");
            }
        }

        // Check for duplicate server names
        var duplicateNames = McpServers
            .GroupBy(s => s.McpServerName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var duplicateName in duplicateNames)
        {
            errors.Add($"Duplicate server name found: {duplicateName}");
        }

        // Check for duplicate unique names
        var duplicateUniqueNames = McpServers
            .GroupBy(s => s.McpServerUniqueName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var duplicateUniqueName in duplicateUniqueNames)
        {
            errors.Add($"Duplicate server unique name found: {duplicateUniqueName}");
        }

        return errors.ToArray();
    }
}