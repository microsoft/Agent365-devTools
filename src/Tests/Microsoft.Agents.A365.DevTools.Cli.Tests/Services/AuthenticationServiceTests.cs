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

    [Fact]
    public void ResolveScopesForResource_WithMultipleServersOnSameHost_ShouldReturnAllScopes()
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
                },
                new McpServerConfig
                {
                    McpServerName = "mcp_CalendarTools",
                    Url = "https://agent365.svc.cloud.microsoft/agents/servers/mcp_CalendarTools",
                    Scope = "McpServers.Calendar.All",
                    Audience = "api://mcp-calendar"
                }
            }
        };

        var manifestJson = JsonSerializer.Serialize(manifest);
        var tempManifestPath = Path.GetTempFileName();
        File.WriteAllText(tempManifestPath, manifestJson);

        try
        {
            // Act
            var scopes = _authService.ResolveScopesForResource(
                "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools",
                tempManifestPath);

            // Assert
            scopes.Should().HaveCount(2);
            scopes.Should().Contain("McpServers.Mail.All");
            scopes.Should().Contain("McpServers.Calendar.All");
        }
        finally
        {
            if (File.Exists(tempManifestPath))
                File.Delete(tempManifestPath);
        }
    }

    [Fact]
    public void ResolveScopesForResource_WithDifferentHosts_ShouldReturnOnlyMatchingScopes()
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
                },
                new McpServerConfig
                {
                    McpServerName = "mcp_OtherHost",
                    Url = "https://different-host.example.com/api/mcp",
                    Scope = "McpServers.Other.All",
                    Audience = "api://mcp-other"
                }
            }
        };

        var manifestJson = JsonSerializer.Serialize(manifest);
        var tempManifestPath = Path.GetTempFileName();
        File.WriteAllText(tempManifestPath, manifestJson);

        try
        {
            // Act
            var scopes = _authService.ResolveScopesForResource(
                "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools",
                tempManifestPath);

            // Assert
            scopes.Should().ContainSingle();
            scopes.Should().Contain("McpServers.Mail.All");
            scopes.Should().NotContain("McpServers.Other.All");
        }
        finally
        {
            if (File.Exists(tempManifestPath))
                File.Delete(tempManifestPath);
        }
    }

    [Fact]
    public void ResolveScopesForResource_WithInvalidUrlInManifest_ShouldSkipInvalidAndContinue()
    {
        // Arrange
        var manifest = new ToolingManifest
        {
            McpServers = new[]
            {
                new McpServerConfig
                {
                    McpServerName = "mcp_InvalidUrl",
                    Url = "not-a-valid-url",
                    Scope = "McpServers.Invalid.All",
                    Audience = "api://mcp-invalid"
                },
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
            var scopes = _authService.ResolveScopesForResource(
                "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools",
                tempManifestPath);

            // Assert
            scopes.Should().ContainSingle();
            scopes.Should().Contain("McpServers.Mail.All");
        }
        finally
        {
            if (File.Exists(tempManifestPath))
                File.Delete(tempManifestPath);
        }
    }

    [Fact]
    public void ResolveScopesForResource_WithMissingManifestFile_ShouldReturnDefaultScope()
    {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"NonExistent_{Guid.NewGuid()}.json");

        // Act
        var scopes = _authService.ResolveScopesForResource(
            "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools",
            nonExistentPath);

        // Assert
        scopes.Should().ContainSingle();
        var expectedScope = $"{McpConstants.Agent365ToolsProdAppId}/.default";
        scopes[0].Should().Be(expectedScope);
    }

    [Fact]
    public void ResolveScopesForResource_WithEmptyMcpServers_ShouldReturnDefaultScope()
    {
        // Arrange
        var manifest = new ToolingManifest
        {
            McpServers = Array.Empty<McpServerConfig>()
        };

        var manifestJson = JsonSerializer.Serialize(manifest);
        var tempManifestPath = Path.GetTempFileName();
        File.WriteAllText(tempManifestPath, manifestJson);

        try
        {
            // Act
            var scopes = _authService.ResolveScopesForResource(
                "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools",
                tempManifestPath);

            // Assert
            scopes.Should().ContainSingle();
            var expectedScope = $"{McpConstants.Agent365ToolsProdAppId}/.default";
            scopes[0].Should().Be(expectedScope);
        }
        finally
        {
            if (File.Exists(tempManifestPath))
                File.Delete(tempManifestPath);
        }
    }

    [Fact]
    public void ResolveScopesForResource_WithDuplicateScopes_ShouldReturnUniqueScopes()
    {
        // Arrange
        var manifest = new ToolingManifest
        {
            McpServers = new[]
            {
                new McpServerConfig
                {
                    McpServerName = "mcp_MailTools1",
                    Url = "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools1",
                    Scope = "McpServers.Mail.All",
                    Audience = "api://mcp-mail"
                },
                new McpServerConfig
                {
                    McpServerName = "mcp_MailTools2",
                    Url = "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools2",
                    Scope = "McpServers.Mail.All",
                    Audience = "api://mcp-mail"
                },
                new McpServerConfig
                {
                    McpServerName = "mcp_CalendarTools",
                    Url = "https://agent365.svc.cloud.microsoft/agents/servers/mcp_CalendarTools",
                    Scope = "McpServers.Calendar.All",
                    Audience = "api://mcp-calendar"
                }
            }
        };

        var manifestJson = JsonSerializer.Serialize(manifest);
        var tempManifestPath = Path.GetTempFileName();
        File.WriteAllText(tempManifestPath, manifestJson);

        try
        {
            // Act
            var scopes = _authService.ResolveScopesForResource(
                "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools1",
                tempManifestPath);

            // Assert
            scopes.Should().HaveCount(2);
            scopes.Should().Contain("McpServers.Mail.All");
            scopes.Should().Contain("McpServers.Calendar.All");
        }
        finally
        {
            if (File.Exists(tempManifestPath))
                File.Delete(tempManifestPath);
        }
    }

    [Fact]
    public void ResolveScopesForResource_WithCaseInsensitiveHostMatch_ShouldMatchCorrectly()
    {
        // Arrange
        var manifest = new ToolingManifest
        {
            McpServers = new[]
            {
                new McpServerConfig
                {
                    McpServerName = "mcp_MailTools",
                    Url = "https://AGENT365.SVC.CLOUD.MICROSOFT/agents/servers/mcp_MailTools",
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
            var scopes = _authService.ResolveScopesForResource(
                "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools",
                tempManifestPath);

            // Assert
            scopes.Should().ContainSingle();
            scopes.Should().Contain("McpServers.Mail.All");
        }
        finally
        {
            if (File.Exists(tempManifestPath))
                File.Delete(tempManifestPath);
        }
    }

    [Fact]
    public void ResolveScopesForResource_WithNoManifestPath_ShouldLookForLocalManifest()
    {
        // Arrange
        var currentDir = Environment.CurrentDirectory;
        var localManifestPath = Path.Combine(currentDir, "ToolingManifest.json");
        var manifestCreated = false;

        try
        {
            // Only create if it doesn't exist to avoid overwriting
            if (!File.Exists(localManifestPath))
            {
                var manifest = new ToolingManifest
                {
                    McpServers = new[]
                    {
                        new McpServerConfig
                        {
                            McpServerName = "mcp_TestLocal",
                            Url = "https://test-local.example.com/api/mcp",
                            Scope = "McpServers.TestLocal.All",
                            Audience = "api://mcp-test"
                        }
                    }
                };
                var manifestJson = JsonSerializer.Serialize(manifest);
                File.WriteAllText(localManifestPath, manifestJson);
                manifestCreated = true;
            }

            // Act
            var scopes = _authService.ResolveScopesForResource(
                "https://test-local.example.com/api/mcp");

            // Assert - Should either find the local manifest or return default
            scopes.Should().NotBeNull();
            scopes.Should().NotBeEmpty();
        }
        finally
        {
            // Clean up only if we created it
            if (manifestCreated && File.Exists(localManifestPath))
            {
                File.Delete(localManifestPath);
            }
        }
    }

    [Fact]
    public void ResolveScopesForResource_WithMalformedJson_ShouldReturnDefaultScope()
    {
        // Arrange
        var tempManifestPath = Path.GetTempFileName();
        File.WriteAllText(tempManifestPath, "{ invalid json content }");

        try
        {
            // Act
            var scopes = _authService.ResolveScopesForResource(
                "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools",
                tempManifestPath);

            // Assert
            scopes.Should().ContainSingle();
            var expectedScope = $"{McpConstants.Agent365ToolsProdAppId}/.default";
            scopes[0].Should().Be(expectedScope);
        }
        finally
        {
            if (File.Exists(tempManifestPath))
                File.Delete(tempManifestPath);
        }
    }

    #region ValidateScopesForResource Tests

    [Fact]
    public void ValidateScopesForResource_WithValidResource_ShouldReturnTrue()
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
            var isValid = _authService.ValidateScopesForResource(
                "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools",
                tempManifestPath);

            // Assert
            isValid.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(tempManifestPath))
                File.Delete(tempManifestPath);
        }
    }

    [Fact]
    public void ValidateScopesForResource_WithMissingManifest_ShouldReturnTrue()
    {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"NonExistent_{Guid.NewGuid()}.json");

        // Act
        var isValid = _authService.ValidateScopesForResource(
            "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools",
            nonExistentPath);

        // Assert - Should return true because it falls back to default scope
        isValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateScopesForResource_WithNullResourceUrl_ShouldHandleGracefully()
    {
        // Act
        var isValid = _authService.ValidateScopesForResource(null!);

        // Assert - Should not throw and handle gracefully
        // The method returns true by default, but this tests it doesn't crash
        isValid.Should().BeTrue();
    }

    #endregion

    #region GetAccessTokenWithScopesAsync Validation Tests

    [Fact]
    public async Task GetAccessTokenWithScopesAsync_WithNullResourceAppId_ShouldThrowArgumentException()
    {
        // Arrange
        var scopes = new[] { "McpServers.Mail.All" };

        // Act
        Func<Task> act = async () => await _authService.GetAccessTokenWithScopesAsync(
            null!, scopes);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Resource App ID cannot be empty*");
    }

    [Fact]
    public async Task GetAccessTokenWithScopesAsync_WithEmptyResourceAppId_ShouldThrowArgumentException()
    {
        // Arrange
        var scopes = new[] { "McpServers.Mail.All" };

        // Act
        Func<Task> act = async () => await _authService.GetAccessTokenWithScopesAsync(
            "", scopes);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Resource App ID cannot be empty*");
    }

    [Fact]
    public async Task GetAccessTokenWithScopesAsync_WithWhitespaceResourceAppId_ShouldThrowArgumentException()
    {
        // Arrange
        var scopes = new[] { "McpServers.Mail.All" };

        // Act
        Func<Task> act = async () => await _authService.GetAccessTokenWithScopesAsync(
            "   ", scopes);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Resource App ID cannot be empty*");
    }

    [Fact]
    public async Task GetAccessTokenWithScopesAsync_WithNullScopes_ShouldThrowArgumentException()
    {
        // Arrange
        var resourceAppId = "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1";

        // Act
        Func<Task> act = async () => await _authService.GetAccessTokenWithScopesAsync(
            resourceAppId, null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*At least one scope must be specified*");
    }

    [Fact]
    public async Task GetAccessTokenWithScopesAsync_WithEmptyScopes_ShouldThrowArgumentException()
    {
        // Arrange
        var resourceAppId = "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1";
        var scopes = Array.Empty<string>();

        // Act
        Func<Task> act = async () => await _authService.GetAccessTokenWithScopesAsync(
            resourceAppId, scopes);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*At least one scope must be specified*");
    }

    #endregion

    #region ClearCache Additional Tests

    [Fact]
    public void ClearCache_WithMultipleTokensCached_ShouldClearAll()
    {
        // Arrange
        var cacheDir = Path.GetDirectoryName(_testCachePath)!;
        Directory.CreateDirectory(cacheDir);

        var tokenCache = new
        {
            Tokens = new Dictionary<string, object>
            {
                ["resource1"] = new { AccessToken = "token1", ExpiresOn = DateTime.UtcNow.AddHours(1), TenantId = "tenant1" },
                ["resource2"] = new { AccessToken = "token2", ExpiresOn = DateTime.UtcNow.AddHours(1), TenantId = "tenant2" }
            }
        };

        var cacheJson = JsonSerializer.Serialize(tokenCache);
        File.WriteAllText(_testCachePath, cacheJson);

        // Act
        _authService.ClearCache();

        // Assert
        File.Exists(_testCachePath).Should().BeFalse();
    }

    [Fact]
    public void ClearCache_MultipleTimes_ShouldNotThrow()
    {
        // Arrange
        var cacheDir = Path.GetDirectoryName(_testCachePath)!;
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(_testCachePath, "test content");

        // Act
        Action act = () =>
        {
            _authService.ClearCache();
            _authService.ClearCache();
            _authService.ClearCache();
        };

        // Assert
        act.Should().NotThrow();
        File.Exists(_testCachePath).Should().BeFalse();
    }

    #endregion

    // Note: Testing GetAccessTokenAsync requires interactive browser authentication
    // which is not suitable for automated unit tests. This should be tested with integration tests
    // or manual testing.
}
