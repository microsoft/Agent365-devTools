// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using NSubstitute;
using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

[CollectionDefinition("AuthTests", DisableParallelization = true)]
public class AuthTestCollection { }

/// <summary>
/// Unit tests for AuthenticationService
/// </summary>
[Collection("AuthTests")]
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
            var scope = $"{McpConstants.WorkIQToolsProdAppId}/.default";
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
        var expectedScope = $"{McpConstants.WorkIQToolsProdAppId}/.default";
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
            var expectedScope = $"{McpConstants.WorkIQToolsProdAppId}/.default";
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
        var localManifestPath = Path.Combine(currentDir, McpConstants.ToolingManifestFileName);
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
            var expectedScope = $"{McpConstants.WorkIQToolsProdAppId}/.default";
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

    #region Browser Auth Platform Fallback Tests

    /// <summary>
    /// A TokenCredential that always throws the provided exception.
    /// Used to simulate MSAL browser auth failures without launching a browser.
    /// </summary>
    private sealed class ThrowingTokenCredential : TokenCredential
    {
        private readonly Exception _exception;

        public ThrowingTokenCredential(Exception exception) => _exception = exception;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw _exception;

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw _exception;
    }

    /// <summary>
    /// A TokenCredential that returns a fixed token without any interactive prompt.
    /// Used to simulate a successful device code flow in tests.
    /// </summary>
    private sealed class StubTokenCredential : TokenCredential
    {
        private readonly AccessToken _token;

        public StubTokenCredential(string token, DateTimeOffset expiresOn)
            => _token = new AccessToken(token, expiresOn);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => _token;

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(_token);
    }

    /// <summary>
    /// Subclass of AuthenticationService that overrides credential factory methods
    /// to inject test doubles without touching the file system or launching auth UIs.
    /// </summary>
    private sealed class TestableAuthenticationService : AuthenticationService
    {
        private readonly TokenCredential _browserCredential;
        private readonly TokenCredential _deviceCodeCredential;

        public TestableAuthenticationService(
            ILogger<AuthenticationService> logger,
            TokenCredential browserCredential,
            TokenCredential deviceCodeCredential)
            : base(logger)
        {
            _browserCredential = browserCredential;
            _deviceCodeCredential = deviceCodeCredential;
        }

        protected override TokenCredential CreateBrowserCredential(string clientId, string tenantId, string? loginHint = null)
            => _browserCredential;

        protected override TokenCredential CreateDeviceCodeCredential(string clientId, string tenantId)
            => _deviceCodeCredential;
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenBrowserThrowsPlatformNotSupported_FallsBackToDeviceCode()
    {
        // Arrange — browser credential throws PlatformNotSupportedException (macOS 15.x MSAL behavior)
        var inner = new PlatformNotSupportedException("macOS 15.3.1");
        var browserCredential = new ThrowingTokenCredential(
            new MsalAuthenticationFailedException("Browser authentication is not supported on this platform (macOS 15.3.1)", inner));

        var expectedToken = "device-code-fallback-token";
        var deviceCodeCredential = new StubTokenCredential(expectedToken, DateTimeOffset.UtcNow.AddHours(1));

        var logger = Substitute.For<ILogger<AuthenticationService>>();
        var sut = new TestableAuthenticationService(logger, browserCredential, deviceCodeCredential);

        try
        {
            // Act
            var result = await sut.GetAccessTokenAsync(
                "https://agent365.svc.cloud.microsoft",
                forceRefresh: true,
                useInteractiveBrowser: true);

            // Assert — device code fallback token returned
            result.Should().Be(expectedToken);
        }
        finally
        {
            sut.ClearCache();
        }
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenBrowserThrowsPlatformNotSupported_LogsWarning()
    {
        // Arrange
        var inner = new PlatformNotSupportedException("macOS 15.3.1");
        var browserCredential = new ThrowingTokenCredential(
            new MsalAuthenticationFailedException("Browser authentication is not supported on this platform (macOS 15.3.1)", inner));

        var deviceCodeCredential = new StubTokenCredential("token", DateTimeOffset.UtcNow.AddHours(1));

        var logger = Substitute.For<ILogger<AuthenticationService>>();
        var sut = new TestableAuthenticationService(logger, browserCredential, deviceCodeCredential);

        try
        {
            // Act
            await sut.GetAccessTokenAsync(
                "https://agent365.svc.cloud.microsoft",
                forceRefresh: true,
                useInteractiveBrowser: true);

            // Assert — warning logged for platform fallback
            logger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Browser authentication is not supported")),
                Arg.Any<Exception?>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            sut.ClearCache();
        }
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenBrowserThrowsPlatformNotSupportedAndDeviceCodeFails_ThrowsAzureAuthenticationException()
    {
        // Arrange — both browser and device code fail
        var inner = new PlatformNotSupportedException("macOS 15.3.1");
        var browserCredential = new ThrowingTokenCredential(
            new MsalAuthenticationFailedException("Browser auth failed", inner));

        var deviceCodeCredential = new ThrowingTokenCredential(
            new MsalAuthenticationFailedException("Device code auth also failed"));

        var logger = Substitute.For<ILogger<AuthenticationService>>();
        var sut = new TestableAuthenticationService(logger, browserCredential, deviceCodeCredential);

        // Act
        Func<Task> act = async () => await sut.GetAccessTokenAsync(
            "https://agent365.svc.cloud.microsoft",
            forceRefresh: true,
            useInteractiveBrowser: true);

        // Assert — outer error handler wraps as AzureAuthenticationException
        await act.Should().ThrowAsync<AzureAuthenticationException>();
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenBrowserThrowsNonPlatformException_DoesNotFallBack()
    {
        // Arrange — browser fails with a non-platform exception (e.g., user cancelled)
        var browserCredential = new ThrowingTokenCredential(
            new MsalAuthenticationFailedException("User cancelled authentication"));

        var deviceCodeCredential = new StubTokenCredential("should-not-be-used", DateTimeOffset.UtcNow.AddHours(1));

        var logger = Substitute.For<ILogger<AuthenticationService>>();
        var sut = new TestableAuthenticationService(logger, browserCredential, deviceCodeCredential);

        // Act
        Func<Task> act = async () => await sut.GetAccessTokenAsync(
            "https://agent365.svc.cloud.microsoft",
            forceRefresh: true,
            useInteractiveBrowser: true);

        // Assert — non-CAP errors must NOT trigger device code fallback; only AADSTS53003/53000 should
        await act.Should().ThrowAsync<AzureAuthenticationException>(
            because: "unrecognized auth errors (e.g. user cancelled) must not silently fall back " +
                     "to device code — only Conditional Access Policy errors qualify for automatic fallback");
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenBrowserThrowsMsalServiceExceptionWithUnrelatedCode_DoesNotFallBack()
    {
        // Arrange — browser fails with a real MsalServiceException but for an unrelated reason
        // (e.g. AADSTS70011 "invalid scope") — must NOT fall back to device code
        var msalServiceEx = new Microsoft.Identity.Client.MsalServiceException(
            "invalid_request",
            "AADSTS70011: The provided request must include a 'scope' input parameter.");
        var browserCredential = new ThrowingTokenCredential(
            new MsalAuthenticationFailedException("Invalid scope request.", msalServiceEx));

        var deviceCodeCredential = new StubTokenCredential("should-not-be-used", DateTimeOffset.UtcNow.AddHours(1));

        var logger = Substitute.For<ILogger<AuthenticationService>>();
        var sut = new TestableAuthenticationService(logger, browserCredential, deviceCodeCredential);

        // Act
        Func<Task> act = async () => await sut.GetAccessTokenAsync(
            "https://agent365.svc.cloud.microsoft",
            forceRefresh: true,
            useInteractiveBrowser: true);

        // Assert
        await act.Should().ThrowAsync<AzureAuthenticationException>(
            because: "only AADSTS53003 and AADSTS53000 qualify for automatic device code fallback; " +
                     "other MsalServiceException codes must surface as errors so the user can diagnose them");
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenDeviceCodeFailsWithCapError_ThrowsAzureAuthenticationException()
    {
        // Arrange — useInteractiveBrowser: false means device code is the first (and only) path.
        // If device code itself fails, there is no further fallback — the error must surface.
        var msalServiceEx = new Microsoft.Identity.Client.MsalServiceException(
            "access_denied",
            "AADSTS53003: Access has been blocked by Conditional Access policies.");
        var browserCredential = new StubTokenCredential("unused", DateTimeOffset.UtcNow.AddHours(1));
        var deviceCodeCredential = new ThrowingTokenCredential(
            new MsalAuthenticationFailedException(
                "Device code authentication failed.",
                msalServiceEx));

        var logger = Substitute.For<ILogger<AuthenticationService>>();
        var sut = new TestableAuthenticationService(logger, browserCredential, deviceCodeCredential);

        // Act
        Func<Task> act = async () => await sut.GetAccessTokenAsync(
            "https://agent365.svc.cloud.microsoft",
            forceRefresh: true,
            useInteractiveBrowser: false);

        // Assert
        await act.Should().ThrowAsync<AzureAuthenticationException>(
            because: "when device code itself fails there is no further fallback — " +
                     "the error must surface to the user rather than looping");
    }

    #endregion

    #region ResolveLoginHintFromCacheAsync

    private static string BuildJwt(object payload)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        // JWT uses Base64Url: replace standard Base64 chars to match real MSAL token format.
        var payloadB64Url = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"header.{payloadB64Url}.signature";
    }

    private void WriteTokenCache(string accessToken)
    {
        var cacheDir = Path.GetDirectoryName(_testCachePath)!;
        Directory.CreateDirectory(cacheDir);
        var cache = new
        {
            Tokens = new Dictionary<string, object>
            {
                ["key"] = new { AccessToken = accessToken, ExpiresOn = DateTime.UtcNow.AddHours(1), TenantId = "tid" }
            }
        };
        File.WriteAllText(_testCachePath, System.Text.Json.JsonSerializer.Serialize(cache));
    }

    [Fact]
    public async Task ResolveLoginHintFromCacheAsync_WhenCacheHasUpnClaim_ReturnsUpn()
    {
        WriteTokenCache(BuildJwt(new { upn = "user@test.com" }));

        var result = await _authService.ResolveLoginHintFromCacheAsync();

        result.Should().Be("user@test.com",
            because: "the upn claim in the cached JWT should be returned as the login hint");
    }

    [Fact]
    public async Task ResolveLoginHintFromCacheAsync_WhenCacheHasPreferredUsernameClaim_ReturnsIt()
    {
        WriteTokenCache(BuildJwt(new { preferred_username = "user@contoso.com" }));

        var result = await _authService.ResolveLoginHintFromCacheAsync();

        result.Should().Be("user@contoso.com",
            because: "preferred_username is the fallback claim when upn is absent");
    }

    [Fact]
    public async Task ResolveLoginHintFromCacheAsync_WhenCacheFileDoesNotExist_ReturnsNull()
    {
        _authService.ClearCache();

        var result = await _authService.ResolveLoginHintFromCacheAsync();

        result.Should().BeNull(because: "no cache file means no login hint can be resolved");
    }

    [Fact]
    public async Task ResolveLoginHintFromCacheAsync_WhenJwtHasNoUpnClaims_ReturnsNull()
    {
        WriteTokenCache(BuildJwt(new { sub = "some-subject", oid = "some-oid" }));

        var result = await _authService.ResolveLoginHintFromCacheAsync();

        result.Should().BeNull(because: "a JWT without upn or preferred_username cannot provide a login hint");
    }

    [Fact]
    public async Task ResolveLoginHintFromCacheAsync_WhenJwtIsMalformed_ReturnsNull()
    {
        WriteTokenCache("not-a-valid-jwt");

        var result = await _authService.ResolveLoginHintFromCacheAsync();

        result.Should().BeNull(because: "a malformed JWT should be swallowed and null returned");
    }

    #endregion

    #region WAM wrong-tenant self-heal (issue #430)

    // Builds a minimal JWT whose payload contains only the supplied tid claim.
    // Re-uses the existing BuildJwt helper (same Base64Url encoding).
    private static StubTokenCredential CredentialWithTid(string tid)
        => new(BuildJwt(new { tid }), DateTimeOffset.UtcNow.AddHours(1));

    /// <summary>
    /// TestableAuthenticationService variant that uses a call-indexed credential list.
    /// Each call to CreateBrowserCredential pops the next credential from the queue.
    /// </summary>
    private sealed class SequencedAuthenticationService : AuthenticationService
    {
        private readonly Queue<TokenCredential> _credentials;

        public SequencedAuthenticationService(
            ILogger<AuthenticationService> logger,
            IEnumerable<TokenCredential> credentials)
            : base(logger)
            => _credentials = new Queue<TokenCredential>(credentials);

        protected override TokenCredential CreateBrowserCredential(string clientId, string tenantId, string? loginHint = null)
            => _credentials.Count > 0 ? _credentials.Dequeue() : base.CreateBrowserCredential(clientId, tenantId, loginHint);
    }

    // Computes the MSAL cache path so tests can back it up and restore it.
    private static string MsalCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AuthenticationConstants.ApplicationName,
        AuthenticationConstants.MsalCacheFileName);

    [Fact]
    public async Task GetAccessTokenAsync_WhenFirstTokenHasWrongTid_ClearsCachesAndRetries()
    {
        // Arrange — first call returns wrong-tenant token; second returns correct tenant.
        var correctTenant = "aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa";
        var wrongTenant   = "bbbbbbbb-0000-0000-0000-bbbbbbbbbbbb";

        var logger = Substitute.For<ILogger<AuthenticationService>>();
        var sut = new SequencedAuthenticationService(logger, new[]
        {
            CredentialWithTid(wrongTenant),   // attempt 1 — WAM picked the wrong account
            CredentialWithTid(correctTenant)  // attempt 2 — after cache clear, correct account
        });

        // Back up any pre-existing real MSAL cache so the test does not destroy it.
        string? originalCache = File.Exists(MsalCachePath)
            ? await File.ReadAllTextAsync(MsalCachePath)
            : null;

        try
        {
            // Act
            var result = await sut.GetAccessTokenAsync(
                "https://graph.microsoft.com",
                tenantId: correctTenant,
                forceRefresh: true,
                useInteractiveBrowser: true);

            // Assert — the token returned contains the correct tid
            var tid = JwtHelper.TryDecodeClaim(result, "tid");
            tid.Should().Be(correctTenant,
                because: "after the self-heal retry the correct-tenant token must be returned");

            // A warning must have been logged to indicate that the wrong account was detected
            logger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Clearing cached credentials")),
                Arg.Any<Exception?>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            sut.ClearCache();
            if (originalCache is null) { if (File.Exists(MsalCachePath)) File.Delete(MsalCachePath); }
            else { await File.WriteAllTextAsync(MsalCachePath, originalCache); }
        }
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenBothAttemptsReturnWrongTid_ThrowsAzureAuthenticationException()
    {
        // Arrange — both calls return a token for the wrong tenant (worst-case: retry also wrong).
        var correctTenant = "aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa";
        var wrongTenant   = "bbbbbbbb-0000-0000-0000-bbbbbbbbbbbb";

        var logger = Substitute.For<ILogger<AuthenticationService>>();
        var sut = new SequencedAuthenticationService(logger, new[]
        {
            CredentialWithTid(wrongTenant),  // attempt 1
            CredentialWithTid(wrongTenant)   // attempt 2 — still wrong after cache clear
        });

        // Back up any pre-existing real MSAL cache so the test does not destroy it.
        string? originalCache = File.Exists(MsalCachePath)
            ? await File.ReadAllTextAsync(MsalCachePath)
            : null;

        try
        {
            // Act
            Func<Task> act = async () => await sut.GetAccessTokenAsync(
                "https://graph.microsoft.com",
                tenantId: correctTenant,
                forceRefresh: true,
                useInteractiveBrowser: true);

            // Assert
            await act.Should().ThrowAsync<AzureAuthenticationException>(
                because: "when both attempts return the wrong tenant the CLI must fail with a clear error " +
                         "rather than silently proceeding with a token that will cause 403 on every Graph call");
        }
        finally
        {
            sut.ClearCache();
            if (originalCache is null) { if (File.Exists(MsalCachePath)) File.Delete(MsalCachePath); }
            else { await File.WriteAllTextAsync(MsalCachePath, originalCache); }
        }
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenTokenTidMatchesConfiguredTenant_NoRetryOccurs()
    {
        // Arrange — token tid matches configured tenant; self-heal path must NOT fire.
        var correctTenant = "aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa";

        var logger = Substitute.For<ILogger<AuthenticationService>>();
        var sut = new SequencedAuthenticationService(logger, new[]
        {
            CredentialWithTid(correctTenant)  // only one credential queued — a retry would throw
        });

        try
        {
            // Act — should succeed without touching the second (non-existent) credential
            var result = await sut.GetAccessTokenAsync(
                "https://graph.microsoft.com",
                tenantId: correctTenant,
                forceRefresh: true,
                useInteractiveBrowser: true);

            // Assert — no warning about clearing caches
            logger.DidNotReceive().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Clearing cached credentials")),
                Arg.Any<Exception?>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            sut.ClearCache();
        }
    }

    #endregion
}
