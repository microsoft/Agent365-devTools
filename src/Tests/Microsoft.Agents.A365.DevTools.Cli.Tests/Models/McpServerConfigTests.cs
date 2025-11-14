// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Models;

public class McpServerConfigTests
{
    [Fact]
    public void McpServerConfig_DefaultValues_ShouldBeEmpty()
    {
        // Arrange & Act
        var config = new McpServerConfig();

        // Assert
        Assert.Equal(string.Empty, config.McpServerName);
        Assert.Null(config.McpServerUniqueName);
        Assert.Null(config.Url);
        Assert.Null(config.Scope);
        Assert.Null(config.Audience);
        Assert.Null(config.Description);
        Assert.Null(config.Capabilities);
    }

    [Fact]
    public void McpServerConfig_IsValid_WithRequiredFields_ShouldReturnTrue()
    {
        // Arrange
        var config = new McpServerConfig
        {
            McpServerName = "Test Server",
            Url = "https://api.example.com/mcp/test"
        };

        // Act & Assert
        Assert.True(config.IsValid());
    }

    [Theory]
    [InlineData("", "https://api.example.com/mcp/test")]
    [InlineData("Test Server", "")]
    [InlineData("", "")]
    [InlineData(null, "https://api.example.com/mcp/test")]
    [InlineData("Test Server", null)]
    public void McpServerConfig_IsValid_WithMissingRequiredFields_ShouldReturnFalse(string? name, string? url)
    {
        // Arrange
        var config = new McpServerConfig
        {
            McpServerName = name ?? string.Empty,
            Url = url
        };

        // Act & Assert
        Assert.False(config.IsValid());
    }

    [Fact]
    public void McpServerConfig_ToString_WithScopes_ShouldIncludeScopeInfo()
    {
        // Arrange
        var config = new McpServerConfig
        {
            McpServerName = "Test Server",
            Scope = "McpServers.Mail.All"
        };

        // Act
        var result = config.ToString();

        // Assert
        Assert.Equal("Test Server (Scope: McpServers.Mail.All)", result);
    }

    [Fact]
    public void McpServerConfig_ToString_WithoutScopes_ShouldIndicateNoScopes()
    {
        // Arrange
        var config = new McpServerConfig
        {
            McpServerName = "Test Server",
            Scope = null
        };

        // Act
        var result = config.ToString();

        // Assert
        Assert.Equal("Test Server (No scope required)", result);
    }

    [Fact]
    public void McpServerConfig_JsonSerialization_ShouldRoundTrip()
    {
        // Arrange
        var original = new McpServerConfig
        {
            McpServerName = "Test Server",
            McpServerUniqueName = "test-server",
            Url = "https://example.com/mcp",
            Scope = "McpServers.Mail.All",
            Audience = "api://test-app",
            Description = "A test MCP server",
            Capabilities = new[] { "tools", "resources" }
        };

        // Act
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<McpServerConfig>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.McpServerName, deserialized.McpServerName);
        Assert.Equal(original.McpServerUniqueName, deserialized.McpServerUniqueName);
        Assert.Equal(original.Url, deserialized.Url);
        Assert.Equal(original.Scope, deserialized.Scope);
        Assert.Equal(original.Audience, deserialized.Audience);
        Assert.Equal(original.Description, deserialized.Description);
        Assert.Equal(original.Capabilities, deserialized.Capabilities);
    }

    [Fact]
    public void McpServerConfig_JsonSerialization_ShouldUseCorrectPropertyNames()
    {
        // Arrange
        var config = new McpServerConfig
        {
            McpServerName = "Test Server",
            McpServerUniqueName = "test-server",
            Scope = "McpServers.Mail.All",
            Audience = "api://test"
        };

        // Act
        var json = JsonSerializer.Serialize(config);

        // Assert
        Assert.Contains("\"mcpServerName\":", json);
        Assert.Contains("\"mcpServerUniqueName\":", json);
        Assert.Contains("\"scope\":", json);
        Assert.Contains("\"audience\":", json);
    }

    [Fact]
    public void McpServerConfig_SingleScopeSchema_ShouldSerializeCorrectly()
    {
        // Arrange
        var config = new McpServerConfig
        {
            McpServerName = "mcp_MailTools",
            Url = "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools",
            Scope = "McpServers.Mail.All",
            Audience = "api://mcp-mail"
        };

        // Act
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        // Assert
        Assert.Contains("\"scope\": \"McpServers.Mail.All\"", json);
        // Should NOT contain old schema properties
        Assert.DoesNotContain("requiredScopes", json);
    }

    [Fact]
    public void McpServerConfig_SingleScopeSchema_ShouldDeserializeCorrectly()
    {
        // Arrange
        var json = """
        {
          "mcpServerName": "mcp_CalendarTools",
          "url": "https://agent365.svc.cloud.microsoft/agents/servers/mcp_CalendarTools",
          "scope": "McpServers.Calendar.All",
          "audience": "api://mcp-calendar"
        }
        """;

        // Act
        var config = JsonSerializer.Deserialize<McpServerConfig>(json);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("mcp_CalendarTools", config.McpServerName);
        Assert.Equal("https://agent365.svc.cloud.microsoft/agents/servers/mcp_CalendarTools", config.Url);
        Assert.Equal("McpServers.Calendar.All", config.Scope);
        Assert.Equal("api://mcp-calendar", config.Audience);
    }
}