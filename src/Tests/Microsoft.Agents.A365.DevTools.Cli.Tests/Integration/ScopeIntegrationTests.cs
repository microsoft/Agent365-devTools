// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Integration;

/// <summary>
/// Integration tests to verify that scope and audience information flows correctly
/// through the MCP server configuration system
/// </summary>
public class ScopeIntegrationTests
{
    [Fact]
    public void EndToEnd_CreateServerObject_ShouldProduceValidManifestWithScope()
    {
        // Arrange
        var serverName = "MCP_MailTools";
        var expectedScope = "McpServers.Mail.All";
        var expectedAudience = "api://mcp-mailtools";

        // Act - Create server object as the CLI would
        var serverObject = ManifestHelper.CreateServerObject(serverName);
        
        // Serialize to JSON as would happen when writing to ToolingManifest.json
        var json = JsonSerializer.Serialize(serverObject, ManifestHelper.GetManifestSerializerOptions());
        
        // Parse back to verify structure
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Assert - Verify all expected fields are present and correct
        Assert.True(root.TryGetProperty("mcpServerName", out var nameElement));
        Assert.Equal(serverName, nameElement.GetString());
        
        Assert.True(root.TryGetProperty("scope", out var scopeElement));
        Assert.Equal(expectedScope, scopeElement.GetString());
        
        Assert.True(root.TryGetProperty("audience", out var audienceElement));
        Assert.Equal(expectedAudience, audienceElement.GetString());
    }

    [Fact]
    public void EndToEnd_AllMappedServers_ShouldHaveValidScopeAndAudience()
    {
        // Arrange - Get all mapped servers
        var serverMappings = McpConstants.ServerScopeMappings.ServerToScope;

        foreach (var kvp in serverMappings)
        {
            var serverName = kvp.Key;
            var (expectedScope, expectedAudience) = kvp.Value;
            
            // Act - Create server object for each mapped server
            var serverObject = ManifestHelper.CreateServerObject(serverName);
            var json = JsonSerializer.Serialize(serverObject);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Assert - Each server should have correct scope and audience
            Assert.True(root.TryGetProperty("scope", out var scopeElement), 
                $"Server {serverName} should have a scope property");
            Assert.Equal(expectedScope, scopeElement.GetString());
            
            Assert.True(root.TryGetProperty("audience", out var audienceElement), 
                $"Server {serverName} should have an audience property");
            Assert.Equal(expectedAudience, audienceElement.GetString());
        }
    }

    [Fact]
    public void EndToEnd_CreateManifestWithMultipleServers_ShouldContainAllScopesAndAudiences()
    {
        // Arrange
        var serverNames = new[] { "MCP_MailTools", "MCP_CalendarTools", "MCP_NLWeb" };
        var servers = new List<object>();

        // Act - Create multiple server objects
        foreach (var serverName in serverNames)
        {
            servers.Add(ManifestHelper.CreateServerObject(serverName));
        }

        // Create a manifest structure
        var manifest = new { mcpServers = servers };
        var json = JsonSerializer.Serialize(manifest, ManifestHelper.GetManifestSerializerOptions());
        
        // Parse back
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Assert - Verify manifest structure and all servers have scope/audience
        Assert.True(root.TryGetProperty("mcpServers", out var serversElement));
        Assert.Equal(JsonValueKind.Array, serversElement.ValueKind);
        Assert.Equal(serverNames.Length, serversElement.GetArrayLength());

        var serverArray = serversElement.EnumerateArray().ToArray();
        for (int i = 0; i < serverNames.Length; i++)
        {
            var server = serverArray[i];
            var expectedServerName = serverNames[i];
            var (expectedScope, expectedAudience) = McpConstants.ServerScopeMappings.GetScopeAndAudience(expectedServerName);

            Assert.True(server.TryGetProperty("mcpServerName", out var nameElement));
            Assert.Equal(expectedServerName, nameElement.GetString());
            
            Assert.True(server.TryGetProperty("scope", out var scopeElement));
            Assert.Equal(expectedScope, scopeElement.GetString());
            
            Assert.True(server.TryGetProperty("audience", out var audienceElement));
            Assert.Equal(expectedAudience, audienceElement.GetString());
        }
    }
}