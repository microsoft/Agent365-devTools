// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class MicrosoftGraphTokenProviderTests
{
    private readonly ILogger<MicrosoftGraphTokenProvider> _logger;
    private readonly CommandExecutor _executor;

    public MicrosoftGraphTokenProviderTests()
    {
        _logger = Substitute.For<ILogger<MicrosoftGraphTokenProvider>>();
        _executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
    }

    [Fact]
    public async Task GetMgGraphAccessTokenAsync_WithValidClientAppId_IncludesClientIdInScript()
    {
        // Arrange
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var scopes = new[] { "User.Read", "Mail.Read" };
        var clientAppId = "87654321-4321-4321-4321-cba987654321";
        var expectedToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature";

        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(),
            Arg.Is<string>(args => args.Contains($"-ClientId '{clientAppId}'")),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = expectedToken, StandardError = string.Empty });

        // MSAL is primary but we skip it here to test PS-path behavior (ClientId in script)
        var provider = new MicrosoftGraphTokenProvider(_executor, _logger)
        {
            MsalTokenAcquirerOverride = (_, _, _, _) => Task.FromResult<string?>(null)
        };

        // Act
        var token = await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, false, clientAppId);

        // Assert
        token.Should().Be(expectedToken);
        await _executor.Received(1).ExecuteWithStreamingAsync(
            Arg.Is<string>(cmd => cmd == "pwsh" || cmd == "powershell"),
            Arg.Is<string>(args => args.Contains($"-ClientId '{clientAppId}'")),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMgGraphAccessTokenAsync_WithoutClientAppId_OmitsClientIdParameter()
    {
        // Arrange
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var scopes = new[] { "User.Read" };
        var expectedToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature";

        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(),
            Arg.Is<string>(args => !args.Contains("-ClientId")),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = expectedToken, StandardError = string.Empty });

        var provider = new MicrosoftGraphTokenProvider(_executor, _logger);

        // Act
        var token = await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, false, null);

        // Assert
        token.Should().Be(expectedToken);
        await _executor.Received(1).ExecuteWithStreamingAsync(
            Arg.Any<string>(),
            Arg.Is<string>(args => !args.Contains("-ClientId")),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    [InlineData("invalid-format")]
    public async Task GetMgGraphAccessTokenAsync_WithInvalidClientAppId_ThrowsArgumentException(string invalidClientAppId)
    {
        // Arrange
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var scopes = new[] { "User.Read" };
        var provider = new MicrosoftGraphTokenProvider(_executor, _logger);

        // Act & Assert
        var act = async () => await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, false, invalidClientAppId);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Client App ID must be a valid GUID format*");
    }

    [Fact]
    public async Task GetMgGraphAccessTokenAsync_WithNullScopes_ThrowsArgumentNullException()
    {
        // Arrange
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var provider = new MicrosoftGraphTokenProvider(_executor, _logger);

        // Act & Assert
        var act = async () => await provider.GetMgGraphAccessTokenAsync(tenantId, null!, false);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GetMgGraphAccessTokenAsync_WithEmptyScopes_ThrowsArgumentException()
    {
        // Arrange
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var scopes = Array.Empty<string>();
        var provider = new MicrosoftGraphTokenProvider(_executor, _logger);

        // Act & Assert
        var act = async () => await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, false);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*At least one scope is required*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetMgGraphAccessTokenAsync_WithInvalidTenantId_ThrowsArgumentException(string? invalidTenantId)
    {
        // Arrange
        var scopes = new[] { "User.Read" };
        var provider = new MicrosoftGraphTokenProvider(_executor, _logger);

        // Act & Assert
        var act = async () => await provider.GetMgGraphAccessTokenAsync(invalidTenantId!, scopes, false);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GetMgGraphAccessTokenAsync_WhenExecutionFails_ReturnsNull()
    {
        // Arrange
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var scopes = new[] { "User.Read" };

        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 1, StandardOutput = string.Empty, StandardError = "PowerShell error" });

        var provider = new MicrosoftGraphTokenProvider(_executor, _logger);

        // Act
        var token = await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, false);

        // Assert
        token.Should().BeNull();
    }

    [Fact]
    public async Task GetMgGraphAccessTokenAsync_WithValidToken_ReturnsToken()
    {
        // Arrange
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var scopes = new[] { "User.Read" };
        var expectedToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature";

        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = expectedToken, StandardError = string.Empty });

        var provider = new MicrosoftGraphTokenProvider(_executor, _logger);

        // Act
        var token = await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, false);

        // Assert
        token.Should().Be(expectedToken);
    }

    [Fact]
    public async Task GetMgGraphAccessTokenAsync_WhenMsalSucceeds_ReturnsMsalTokenWithoutCallingPowerShell()
    {
        // Arrange
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var scopes = new[] { "AgentIdentityBlueprint.DeleteRestore.All" };
        var clientAppId = "87654321-4321-4321-4321-cba987654321";
        var msalToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJzZWxsYWsifQ.signature";

        var provider = new MicrosoftGraphTokenProvider(_executor, _logger)
        {
            MsalTokenAcquirerOverride = (_, _, _, _) => Task.FromResult<string?>(msalToken)
        };

        // Act
        var token = await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, false, clientAppId);

        // Assert
        token.Should().Be(msalToken);
        await _executor.DidNotReceive().ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMgGraphAccessTokenAsync_WhenMsalFails_FallsBackToPowerShell()
    {
        // Arrange
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var scopes = new[] { "AgentIdentityBlueprint.DeleteRestore.All" };
        var clientAppId = "87654321-4321-4321-4321-cba987654321";
        var psToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJmYWxsYmFjayJ9.signature";

        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = psToken, StandardError = string.Empty });

        var provider = new MicrosoftGraphTokenProvider(_executor, _logger)
        {
            MsalTokenAcquirerOverride = (_, _, _, _) => Task.FromResult<string?>(null) // MSAL fails
        };

        // Act
        var token = await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, false, clientAppId);

        // Assert
        token.Should().Be(psToken);
        await _executor.Received(1).ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMgGraphAccessTokenAsync_WhenMsalSucceeds_SecondCallReturnsCachedToken()
    {
        // Arrange
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var scopes = new[] { "AgentIdentityBlueprint.DeleteRestore.All" };
        var clientAppId = "87654321-4321-4321-4321-cba987654321";
        // Valid JWT with a future exp claim (year 2099)
        var msalToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJzZWxsYWsiLCJleHAiOjQwNzA5MDg4MDB9.signature";
        var callCount = 0;

        var provider = new MicrosoftGraphTokenProvider(_executor, _logger)
        {
            MsalTokenAcquirerOverride = (_, _, _, _) =>
            {
                callCount++;
                return Task.FromResult<string?>(msalToken);
            }
        };

        // Act
        var token1 = await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, false, clientAppId);
        var token2 = await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, false, clientAppId);

        // Assert
        token1.Should().Be(msalToken);
        token2.Should().Be(msalToken);
        callCount.Should().Be(1, "second call should return cached token without re-invoking MSAL");
    }

    [Theory]
    [InlineData("User.Read'; Invoke-Expression 'malicious'")]
    [InlineData("User.Read\"; Invoke-Expression \"malicious\"")]
    [InlineData("User.Read`; dangerous")]
    public async Task GetMgGraphAccessTokenAsync_WithDangerousCharactersInScopes_ThrowsArgumentException(string dangerousScope)
    {
        // Arrange
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var scopes = new[] { dangerousScope };
        var provider = new MicrosoftGraphTokenProvider(_executor, _logger);

        // Act & Assert
        var act = async () => await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, false);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Scope contains invalid characters*");
    }

    [Fact]
    public async Task GetMgGraphAccessTokenAsync_EscapesSingleQuotesInClientAppId()
    {
        // Arrange - This scenario should not happen in practice since validation catches non-GUID formats
        // but we test escaping logic is applied correctly
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var scopes = new[] { "User.Read" };
        var clientAppId = "87654321-4321-4321-4321-cba987654321";
        var expectedToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature";

        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(),
            Arg.Is<string>(args => !args.Contains("''")), // Should not have escaped quotes for valid GUID
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = expectedToken, StandardError = string.Empty });

        // MSAL is primary but we skip it here to test PS-path escaping behavior
        var provider = new MicrosoftGraphTokenProvider(_executor, _logger)
        {
            MsalTokenAcquirerOverride = (_, _, _, _) => Task.FromResult<string?>(null)
        };

        // Act
        var token = await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, false, clientAppId);

        // Assert
        token.Should().Be(expectedToken);
    }

    [Fact]
    public async Task GetMgGraphAccessTokenAsync_WithForceRefresh_BypassesCache()
    {
        // Arrange
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var scopes = new[] { "AgentIdentityBlueprint.DeleteRestore.All" };
        var clientAppId = "87654321-4321-4321-4321-cba987654321";
        // Valid JWT with a future exp claim (year 2099)
        var msalToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJzZWxsYWsiLCJleHAiOjQwNzA5MDg4MDB9.signature";
        var callCount = 0;

        var provider = new MicrosoftGraphTokenProvider(_executor, _logger)
        {
            MsalTokenAcquirerOverride = (_, _, _, _) =>
            {
                callCount++;
                return Task.FromResult<string?>(msalToken);
            }
        };

        // Prime the cache with a first call
        await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, false, clientAppId);
        callCount.Should().Be(1);

        // Act — second call with forceRefresh: true should bypass the cache
        var token = await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, false, clientAppId, forceRefresh: true);

        // Assert
        token.Should().Be(msalToken);
        callCount.Should().Be(2,
            because: "forceRefresh: true must evict the cached token and re-invoke MSAL, " +
                     "ensuring a stale CAE-revoked token is not reused");
    }
}
