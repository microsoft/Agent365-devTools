// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

public class ManifestHelperTests
{
    [Fact]
    public void CreateCompleteServerObject_WithAllParameters_ShouldCreateDictionary()
    {
        // Arrange
        var serverName = "Test Server";
        var uniqueName = "test-server";
        var url = "https://example.com/mcp";
        var scope = "McpServers.Mail.All";
        var audience = "api://test-app";

        // Act
        var result = ManifestHelper.CreateCompleteServerObject(serverName, uniqueName, url, scope, audience);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<Dictionary<string, object>>(result);
        
        var dict = (Dictionary<string, object>)result;
        Assert.Equal(serverName, dict["mcpServerName"]);
        Assert.Equal(uniqueName, dict["mcpServerUniqueName"]);
        Assert.Equal(url, dict["url"]);
        Assert.Equal(scope, dict["scope"]);
        Assert.Equal(audience, dict["audience"]);
    }

    [Fact]
    public void CreateCompleteServerObject_WithMinimalParameters_ShouldCreateValidDictionary()
    {
        // Arrange
        var serverName = "Minimal Server";

        // Act
        var result = ManifestHelper.CreateCompleteServerObject(serverName);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<Dictionary<string, object>>(result);
        
        var dict = (Dictionary<string, object>)result;
        Assert.Equal(serverName, dict["mcpServerName"]);
        Assert.Equal(serverName, dict["mcpServerUniqueName"]); // Should default to serverName
        Assert.False(dict.ContainsKey("url"));
        Assert.False(dict.ContainsKey("scope"));
        Assert.False(dict.ContainsKey("audience"));
    }

    [Fact]
    public void ExtractServerName_WithValidJson_ShouldReturnName()
    {
        // Arrange
        var json = """
        {
            "mcpServerName": "Test Server",
            "mcpServerUniqueName": "test-server"
        }
        """;

        var jsonElement = JsonDocument.Parse(json).RootElement;

        // Act
        var result = ManifestHelper.ExtractServerName(jsonElement);

        // Assert
        Assert.Equal("Test Server", result);
    }

    [Fact]
    public void ExtractServerName_WithMissingProperty_ShouldReturnNull()
    {
        // Arrange
        var json = """
        {
            "mcpServerUniqueName": "test-server"
        }
        """;

        var jsonElement = JsonDocument.Parse(json).RootElement;

        // Act
        var result = ManifestHelper.ExtractServerName(jsonElement);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExtractUniqueServerName_WithValidJson_ShouldReturnUniqueName()
    {
        // Arrange
        var json = """
        {
            "mcpServerName": "Test Server",
            "mcpServerUniqueName": "test-server-unique"
        }
        """;

        var jsonElement = JsonDocument.Parse(json).RootElement;

        // Act
        var result = ManifestHelper.ExtractUniqueServerName(jsonElement);

        // Assert
        Assert.Equal("test-server-unique", result);
    }

    [Fact]
    public void ConvertToServerObjects_WithValidJsonArray_ShouldReturnObjectList()
    {
        // Arrange
        var jsonArray = """
        [
            {
                "mcpServerName": "Server1",
                "mcpServerUniqueName": "server1"
            },
            {
                "mcpServerName": "Server2", 
                "mcpServerUniqueName": "server2"
            }
        ]
        """;

        var jsonElement = JsonDocument.Parse(jsonArray).RootElement;
        var jsonElements = jsonElement.EnumerateArray();

        // Act
        var result = ManifestHelper.ConvertToServerObjects(jsonElements);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.IsType<Dictionary<string, object>>(item));
    }

    [Fact]
    public void ConvertToServerObjects_WithEmptyArray_ShouldReturnEmptyList()
    {
        // Arrange
        var jsonArray = "[]";
        var jsonElement = JsonDocument.Parse(jsonArray).RootElement;
        var jsonElements = jsonElement.EnumerateArray();

        // Act
        var result = ManifestHelper.ConvertToServerObjects(jsonElements);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void GetManifestSerializerOptions_ShouldReturnConfiguredOptions()
    {
        // Act
        var options = ManifestHelper.GetManifestSerializerOptions();

        // Assert
        Assert.NotNull(options);
        Assert.True(options.WriteIndented);
        Assert.NotNull(options.Encoder);
    }

    [Fact]
    public void CreateServerObject_WithKnownServer_ShouldIncludeScopeAndAudience()
    {
        // Arrange
        var serverName = "MCP_MailTools";

        // Act
        var result = ManifestHelper.CreateServerObject(serverName);

        // Assert
        Assert.NotNull(result);
        var dict = (Dictionary<string, object>)result;
        Assert.Equal(serverName, dict["mcpServerName"]);
        Assert.Equal(serverName, dict["mcpServerUniqueName"]);
        Assert.Equal("McpServers.Mail.All", dict["scope"]);
        Assert.Equal("api://mcp-mailtools", dict["audience"]);
    }

    [Fact]
    public void CreateServerObject_WithUnknownServer_ShouldNotIncludeScopeAndAudience()
    {
        // Arrange
        var serverName = "UnknownServer";

        // Act
        var result = ManifestHelper.CreateServerObject(serverName);

        // Assert
        Assert.NotNull(result);
        var dict = (Dictionary<string, object>)result;
        Assert.Equal(serverName, dict["mcpServerName"]);
        Assert.Equal(serverName, dict["mcpServerUniqueName"]);
        
        // Scope and audience should not be included if they're null/empty
        Assert.False(dict.ContainsKey("scope") && !string.IsNullOrWhiteSpace(dict["scope"]?.ToString()));
        Assert.False(dict.ContainsKey("audience") && !string.IsNullOrWhiteSpace(dict["audience"]?.ToString()));
    }
}