// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using NSubstitute;
using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Unit tests for AuthenticationService
/// </summary>
public class AuthenticationServiceTests : IDisposable
{
    private readonly ILogger<AuthenticationService> _mockLogger;
    private readonly string _testCachePath;
    private readonly AuthenticationService _authService;

    public AuthenticationServiceTests()
    {
        _mockLogger = Substitute.For<ILogger<AuthenticationService>>();
        _authService = new AuthenticationService(_mockLogger);
        
        // Get the actual cache path that the service uses
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _testCachePath = Path.Combine(appDataPath, "Microsoft.Agents.A365.DevTools.Cli", "auth-token.json");
    }

    public void Dispose()
    {
        // Clean up test cache
        _authService.ClearCache();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ClearCache_WhenCacheExists_RemovesFile()
    {
        // Arrange
        var cacheDir = Path.GetDirectoryName(_testCachePath)!;
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(_testCachePath, "test content");

        // Act
        _authService.ClearCache();

        // Assert
        File.Exists(_testCachePath).Should().BeFalse();
    }

    [Fact]
    public void ClearCache_WhenCacheDoesNotExist_DoesNotThrow()
    {
        // Arrange
        if (File.Exists(_testCachePath))
        {
            File.Delete(_testCachePath);
        }

        // Act
        Action act = () => _authService.ClearCache();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_CreatesAuthenticationService_Successfully()
    {
        // Act
        var service = new AuthenticationService(_mockLogger);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_CreatesCacheDirectory_IfNotExists()
    {
        // Arrange
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cacheDir = Path.Combine(appDataPath, "Microsoft.Agents.A365.DevTools.Cli");

        // Act
        _ = new AuthenticationService(_mockLogger);

        // Assert
        Directory.Exists(cacheDir).Should().BeTrue();
    }

    [Fact]
    public void ResolveScopesForResource_WithSingleScopeManifest_ShouldReturnCorrectScope()
    {
        // Arrange
        var manifest = new ToolingManifest
        {
            McpServers = new[]
            {
                new McpServerConfig
                {
                    McpServerName = "mcp_MailTools",
                    Url = "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools",
                    Scope = "McpServers.Mail.All",
                    Audience = "api://mcp-mail"
                }
            }
        };

        var manifestJson = JsonSerializer.Serialize(manifest);
        var tempManifestPath = Path.GetTempFileName();
        File.WriteAllText(tempManifestPath, manifestJson);

        try
        {
            // Act
            var mailScopes = _authService.ResolveScopesForResource(
                "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools",
                tempManifestPath);

            // Assert
            Assert.Single(mailScopes);
            Assert.Equal("McpServers.Mail.All", mailScopes[0]);
        }
        finally
        {
            if (File.Exists(tempManifestPath))
                File.Delete(tempManifestPath);
        }
    }

    [Fact]
    public void ResolveScopesForResource_WithNullOrEmptyScopes_ShouldReturnDefaultScope()
    {
        // Arrange
        var manifest = new ToolingManifest
        {
            McpServers = new[]
            {
                new McpServerConfig
                {
                    McpServerName = "server-no-scope",
                    Url = "https://test.example.com/no-scope",
                    Scope = null,
                    Audience = "api://no-scope"
                }
            }
        };

        var manifestJson = JsonSerializer.Serialize(manifest);
        var tempManifestPath = Path.GetTempFileName();
        File.WriteAllText(tempManifestPath, manifestJson);

        try
        {
            // Act
            var noScopeResult = _authService.ResolveScopesForResource(
                "https://test.example.com/no-scope", tempManifestPath);

            // Assert - Should return default Power Platform scope when no MCP scopes are found
            Assert.Single(noScopeResult);
            var scope = $"{McpConstants.Agent365ToolsProdAppId}/.default";
            Assert.Equal(scope, noScopeResult[0]);
        }
        finally
        {
            if (File.Exists(tempManifestPath))
                File.Delete(tempManifestPath);
        }
    }

    // Note: Testing GetAccessTokenAsync requires interactive browser authentication
    // which is not suitable for automated unit tests. This should be tested with integration tests
    // or manual testing.
}
