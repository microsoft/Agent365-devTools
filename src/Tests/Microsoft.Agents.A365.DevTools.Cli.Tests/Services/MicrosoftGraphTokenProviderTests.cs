// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

[Collection("AuthTests")]
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

        string[]? requestedScopes = null;
        var provider = new MicrosoftGraphTokenProvider(_executor, _logger)
        {
            MsalTokenAcquirerOverride = (_, resolvedScopes, _, _) =>
            {
                requestedScopes = resolvedScopes;
                return Task.FromResult<string?>(msalToken);
            }
        };

        // Act
        var token = await provider.GetMgGraphAccessTokenAsync(
            tenantId, scopes, false, clientAppId, graphBaseUrl: "https://graph.example",
            authorityHost: "https://login.example");

        // Assert
        token.Should().Be(msalToken);
        requestedScopes.Should().Equal(
            new[] { "https://graph.example/AgentIdentityBlueprint.DeleteRestore.All" },
            because: "short scope names must be normalized to fully-qualified URIs by prepending the configured Graph base URL before being passed to MSAL");
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

    /// <summary>
    /// Tests for the IsInteractiveBrowserFailure + device-code retry path.
    /// This is the specific fix for 'a365 cleanup --agent-name' failing when PowerShell
    /// Connect-MgGraph's interactive browser auth fails in an embedded terminal.
    /// </summary>
    [Fact]
    public async Task GetMgGraphAccessTokenAsync_WhenPowerShellBrowserAuthFails_RetriesWithDeviceCode()
    {
        // Arrange
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var scopes = new[] { "User.Read" };
        var deviceCodeToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJkZXZpY2VDb2RlIn0.signature";
        var browserFailureError = "InteractiveBrowserCredential authentication failed: user cancelled";

        // First call (browser auth) fails with the embedded-terminal error; second call (device code) succeeds.
        var callCount = 0;
        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult(new CommandResult { ExitCode = 1, StandardOutput = string.Empty, StandardError = browserFailureError })
                    : Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = deviceCodeToken, StandardError = string.Empty });
            });

        var provider = new MicrosoftGraphTokenProvider(_executor, _logger)
        {
            MsalTokenAcquirerOverride = (_, _, _, _) => Task.FromResult<string?>(null)
        };

        // Act
        var token = await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, useDeviceCode: false);

        // Assert
        token.Should().Be(deviceCodeToken,
            because: "when PowerShell browser auth fails with 'InteractiveBrowserCredential authentication failed' " +
                     "(embedded terminal), the CLI must automatically retry with device code flow");
        callCount.Should().Be(2,
            because: "browser auth attempt (1) should be followed by a device-code retry attempt (2)");

        // The second call must include -UseDeviceCode
        await _executor.Received(1).ExecuteWithStreamingAsync(
            Arg.Any<string>(),
            Arg.Is<string>(args => args.Contains("-UseDeviceCode")),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<bool>(),
            Arg.Any<Func<string, string?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("InteractiveBrowserCredential authentication failed")]
    [InlineData("INTERACTIVEBROWSERCREDENTIAL AUTHENTICATION FAILED: user cancelled")]  // case-insensitive
    public async Task GetMgGraphAccessTokenAsync_WhenPowerShellBrowserAuthFails_DeviceCodeRetryIsCaseInsensitive(string stderr)
    {
        // Arrange — ensures IsInteractiveBrowserFailure uses OrdinalIgnoreCase as documented
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var scopes = new[] { "User.Read" };
        var deviceCodeToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJkZXZpY2VDb2RlIn0.signature";

        var callCount = 0;
        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult(new CommandResult { ExitCode = 1, StandardOutput = string.Empty, StandardError = stderr })
                    : Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = deviceCodeToken, StandardError = string.Empty });
            });

        var provider = new MicrosoftGraphTokenProvider(_executor, _logger)
        {
            MsalTokenAcquirerOverride = (_, _, _, _) => Task.FromResult<string?>(null)
        };

        // Act
        var token = await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, useDeviceCode: false);

        // Assert
        token.Should().Be(deviceCodeToken,
            because: "IsInteractiveBrowserFailure must match the error string case-insensitively");
    }

    [Fact]
    public async Task GetMgGraphAccessTokenAsync_WhenUseDeviceCodeAlreadyTrue_DoesNotRetryAgain()
    {
        // Arrange — ensures no double-retry when the caller already requested device code
        var tenantId = "12345678-1234-1234-1234-123456789abc";
        var scopes = new[] { "User.Read" };
        var browserFailureError = "InteractiveBrowserCredential authentication failed";

        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult { ExitCode = 1, StandardOutput = string.Empty, StandardError = browserFailureError }));

        var provider = new MicrosoftGraphTokenProvider(_executor, _logger)
        {
            MsalTokenAcquirerOverride = (_, _, _, _) => Task.FromResult<string?>(null)
        };

        // Act — caller already set useDeviceCode: true
        var token = await provider.GetMgGraphAccessTokenAsync(tenantId, scopes, useDeviceCode: true);

        // Assert
        token.Should().BeNull(
            because: "when useDeviceCode is already true the retry guard (!useDeviceCode) prevents an infinite loop");
        // Only one PowerShell call — no retry
        await _executor.Received(1).ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ── WAM wrong-tenant self-heal (issue #430) ───────────────────────────────

    private static string BuildJwt(object payload)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        var payloadB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"header.{payloadB64}.signature";
    }

    [Fact]
    public async Task GetMgGraphAccessTokenAsync_WhenMsalTokenHasWrongTid_ClearsFileAndRetries()
    {
        // Arrange
        var correctTenant = "aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa";
        var wrongTenant   = "bbbbbbbb-0000-0000-0000-bbbbbbbbbbbb";
        var clientAppId   = "87654321-4321-4321-4321-cba987654321";
        var scopes        = new[] { "User.Read" };

        // Write a dummy MSAL cache file to verify it gets deleted on mismatch.
        var msalCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AuthenticationConstants.ApplicationName,
            AuthenticationConstants.MsalCacheFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(msalCachePath)!);

        // Back up any pre-existing real MSAL cache so the test does not destroy it.
        string? originalCache = File.Exists(msalCachePath)
            ? await File.ReadAllTextAsync(msalCachePath)
            : null;

        try
        {
            await File.WriteAllTextAsync(msalCachePath, "stale-msal-cache");

            var callCount = 0;
            var provider = new MicrosoftGraphTokenProvider(_executor, _logger)
            {
                MsalTokenAcquirerOverride = (tid, _, _, _) =>
                {
                    callCount++;
                    // First call: wrong tenant (simulates WAM picking stale cached account)
                    // Second call: correct tenant (after cache clear WAM uses the right account)
                    var returnedTid = callCount == 1 ? wrongTenant : correctTenant;
                    return Task.FromResult<string?>(BuildJwt(new { tid = returnedTid }));
                }
            };

            // Act
            var token = await provider.GetMgGraphAccessTokenAsync(correctTenant, scopes, false, clientAppId);

            // Assert — retry was triggered and correct-tenant token returned
            callCount.Should().Be(2,
                because: "a tid mismatch on the first MSAL call must trigger exactly one retry");
            var returnedTid2 = JwtHelper.TryDecodeClaim(token, "tid");
            returnedTid2.Should().Be(correctTenant,
                because: "the token returned after self-heal must be for the configured tenant");

            // MSAL cache file must have been deleted to give WAM a clean slate
            File.Exists(msalCachePath).Should().BeFalse(
                because: "the stale MSAL cache must be removed so WAM re-evaluates account selection on retry");

            // Warning must have been logged
            _logger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Clearing cached credentials")),
                Arg.Any<Exception?>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            // Restore the original MSAL cache (or remove the test file if none existed before).
            if (originalCache is null)
            {
                if (File.Exists(msalCachePath)) File.Delete(msalCachePath);
            }
            else
            {
                await File.WriteAllTextAsync(msalCachePath, originalCache);
            }
        }
    }

    [Fact]
    public async Task GetMgGraphAccessTokenAsync_WhenTokenTidMatchesConfiguredTenant_NoRetryOccurs()
    {
        // Arrange — token already has the correct tid; self-heal must not fire.
        var correctTenant = "aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa";
        var clientAppId   = "87654321-4321-4321-4321-cba987654321";
        var scopes        = new[] { "User.Read" };

        var callCount = 0;
        var provider = new MicrosoftGraphTokenProvider(_executor, _logger)
        {
            MsalTokenAcquirerOverride = (_, _, _, _) =>
            {
                callCount++;
                return Task.FromResult<string?>(BuildJwt(new { tid = correctTenant }));
            }
        };

        // Act
        var token = await provider.GetMgGraphAccessTokenAsync(correctTenant, scopes, false, clientAppId);

        // Assert — exactly one MSAL call; no retry
        callCount.Should().Be(1,
            because: "when the returned tid matches the configured tenant no retry should occur");
        token.Should().NotBeNullOrEmpty();

        _logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Clearing cached credentials")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
