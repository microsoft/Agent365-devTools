// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Tests must run sequentially because they modify a shared cache file
/// at ~/.a365/mos-token-cache.json.
/// </summary>
[CollectionDefinition("MosTokenCacheTests", DisableParallelization = true)]
public class MosTokenCacheTestCollection { }

[Collection("MosTokenCacheTests")]
public class MosTokenServiceCacheTests : IDisposable
{
    private readonly ILogger<MosTokenService> _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly MosTokenService _service;
    private readonly string _cacheFilePath;
    private readonly string? _originalCacheContent;

    public MosTokenServiceCacheTests()
    {
        _mockLogger = Substitute.For<ILogger<MosTokenService>>();
        _mockConfigService = Substitute.For<IConfigService>();
        _service = new MosTokenService(_mockLogger, _mockConfigService);

        var cacheDir = FileHelper.GetSecureCrossOsDirectory();
        _cacheFilePath = Path.Combine(cacheDir, "mos-token-cache.json");

        // Backup any existing cache file to restore after tests
        _originalCacheContent = File.Exists(_cacheFilePath)
            ? File.ReadAllText(_cacheFilePath)
            : null;
    }

    public void Dispose()
    {
        // Restore original cache state
        if (_originalCacheContent != null)
        {
            File.WriteAllText(_cacheFilePath, _originalCacheContent);
        }
        else if (File.Exists(_cacheFilePath))
        {
            File.Delete(_cacheFilePath);
        }
    }

    [Fact]
    public async Task AcquireTokenAsync_CachedTokenWithFutureUtcExpiry_ReturnsCachedToken()
    {
        // Arrange - cache a token that expires 1 hour from now (UTC)
        var futureUtc = DateTime.UtcNow.AddHours(1);
        WriteCacheFile("prod", "cached-valid-token", futureUtc);

        // Act
        var result = await _service.AcquireTokenAsync("prod");

        // Assert - cached token returned without loading config
        result.Should().Be("cached-valid-token");
        await _mockConfigService.DidNotReceive().LoadAsync();
    }

    [Fact]
    public async Task AcquireTokenAsync_CachedTokenWithPastUtcExpiry_DoesNotReturnCachedToken()
    {
        // Arrange - cache a token that expired 1 hour ago (UTC)
        var pastUtc = DateTime.UtcNow.AddHours(-1);
        WriteCacheFile("prod", "expired-token", pastUtc);
        SetupConfigForCacheMiss();

        // Act
        var result = await _service.AcquireTokenAsync("prod", cancellationToken: CancelledToken());

        // Assert - expired token not returned, config was loaded (cache miss)
        result.Should().NotBe("expired-token");
        await _mockConfigService.Received(1).LoadAsync();
    }

    [Fact]
    public async Task AcquireTokenAsync_CachedTokenUtcZSuffix_ParsedAsUtcNotLocalTime()
    {
        // Regression test for #277. Without DateTimeStyles.AdjustToUniversal,
        // DateTime.TryParse converts the "Z" suffix to local time. On IST (+5:30):
        //   Stored:  "...12:00:00Z"  Parsed: DateTime(17:30, Kind=Local)
        //   UtcNow(14:00) < 17:28 -> TRUE -> stale token returned (bug)
        // Only catches the regression on UTC+ machines; see the Kind test below
        // for the CI-reliable counterpart.
        var expiredUtc = DateTime.UtcNow.AddHours(-3);
        WriteCacheFile("prod", "stale-tz-token", expiredUtc);
        SetupConfigForCacheMiss();

        var result = await _service.AcquireTokenAsync("prod", cancellationToken: CancelledToken());

        result.Should().NotBe("stale-tz-token");
        await _mockConfigService.Received(1).LoadAsync();
    }

    [Fact]
    public void TryParseUtcTimestamp_WithAdjustToUniversal_ParsedAsUtcKindNotLocalTime()
    {
        // CI-reliable regression test for #277. On a UTC machine the buggy code
        // produces Kind=Local with the same tick value as Kind=Utc, so comparison
        // passes anyway — the service-level test above misses it. Checking Kind
        // directly catches the regression on every machine including UTC CI runners.
        const string utcZTimestamp = "2026-01-01T12:00:00.0000000Z";

        var parsed = DateTime.TryParse(
            utcZTimestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal,
            out var result);

        parsed.Should().BeTrue();
        result.Kind.Should().Be(DateTimeKind.Utc);
        result.Hour.Should().Be(12);
    }

    [Fact]
    public async Task AcquireTokenAsync_CachedTokenWithin2MinuteBuffer_DoesNotReturnCachedToken()
    {
        // Arrange - token expiring in 90 seconds (within 2-minute safety buffer)
        var almostExpiredUtc = DateTime.UtcNow.AddSeconds(90);
        WriteCacheFile("prod", "almost-expired-token", almostExpiredUtc);
        SetupConfigForCacheMiss();

        // Act
        var result = await _service.AcquireTokenAsync("prod", cancellationToken: CancelledToken());

        // Assert - token within buffer not returned
        result.Should().NotBe("almost-expired-token");
        await _mockConfigService.Received(1).LoadAsync();
    }

    [Fact]
    public async Task AcquireTokenAsync_CachedTokenForDifferentEnvironment_DoesNotReturnToken()
    {
        // Arrange - cache a valid token for "sdf" but request "prod"
        var futureUtc = DateTime.UtcNow.AddHours(1);
        WriteCacheFile("sdf", "sdf-only-token", futureUtc);
        SetupConfigForCacheMiss();

        // Act
        var result = await _service.AcquireTokenAsync("prod", cancellationToken: CancelledToken());

        // Assert - token for wrong environment not returned
        result.Should().NotBe("sdf-only-token");
        await _mockConfigService.Received(1).LoadAsync();
    }

    [Fact]
    public async Task AcquireTokenAsync_NoCacheFile_LoadsConfig()
    {
        // Arrange - ensure no cache file exists
        if (File.Exists(_cacheFilePath))
        {
            File.Delete(_cacheFilePath);
        }

        SetupConfigForCacheMiss();

        // Act
        var result = await _service.AcquireTokenAsync("prod", cancellationToken: CancelledToken());

        // Assert - no cache, falls through to config loading
        await _mockConfigService.Received(1).LoadAsync();
    }

    /// <summary>
    /// Write a MOS token cache JSON file with an ISO 8601 UTC expiry timestamp,
    /// matching the format produced by MosTokenService.CacheToken().
    /// </summary>
    private void WriteCacheFile(string environment, string token, DateTime expiryUtc)
    {
        var cacheDir = Path.GetDirectoryName(_cacheFilePath)!;
        Directory.CreateDirectory(cacheDir);

        // Use the same format as CacheToken: expiry.ToUniversalTime().ToString("o")
        // This produces "2026-02-18T17:00:00.0000000Z" with the "Z" suffix
        var isoExpiry = expiryUtc.ToUniversalTime().ToString("o");
        var json = $$"""
            {
                "{{environment}}": {
                    "token": "{{token}}",
                    "expiry": "{{isoExpiry}}"
                }
            }
            """;
        File.WriteAllText(_cacheFilePath, json);
    }

    private void SetupConfigForCacheMiss()
    {
        var config = new Agent365Config { TenantId = "test-tenant", ClientAppId = "test-client" };
        _mockConfigService.LoadAsync().Returns(Task.FromResult(config));
    }

    private static CancellationToken CancelledToken()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts.Token;
    }
}
