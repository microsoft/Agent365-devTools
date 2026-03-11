// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

[Collection("VersionCheckTests")]
public class NoticeServiceTests : IDisposable
{
    private readonly ILogger<NoticeService> _logger;
    private readonly NoticeService _service;

    public NoticeServiceTests()
    {
        _logger = Substitute.For<ILogger<NoticeService>>();
        _service = new NoticeService(_logger);
    }

    public void Dispose()
    {
        // Clean up cache file written by tests so state does not leak
        var path = NoticeService.GetCacheFilePath();
        if (File.Exists(path))
            File.Delete(path);
    }

    // ---------------------------------------------------------------------------
    // CI/CD skip
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CheckForNoticeAsync_WhenRunningInCiCd_ReturnsNoNotice()
    {
        Environment.SetEnvironmentVariable("CI", "true");
        try
        {
            var result = await _service.CheckForNoticeAsync();
            result.HasNotice.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CI", null);
        }
    }

    // ---------------------------------------------------------------------------
    // Notice evaluation (cache-injected to avoid real network calls)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CheckForNoticeAsync_WhenCacheHasNoMessage_ReturnsNoNotice()
    {
        WriteCacheFile(new NoticeCache(DateTimeOffset.UtcNow, new Notice(null, null, null)));

        var result = await _service.CheckForNoticeAsync();

        result.HasNotice.Should().BeFalse();
    }

    [Fact]
    public async Task CheckForNoticeAsync_WhenNoticeIsExpired_ReturnsNoNotice()
    {
        WriteCacheFile(new NoticeCache(
            DateTimeOffset.UtcNow,
            new Notice("Critical issue!", null, DateTimeOffset.UtcNow.AddDays(-1))));

        var result = await _service.CheckForNoticeAsync();

        result.HasNotice.Should().BeFalse();
    }

    [Fact]
    public async Task CheckForNoticeAsync_WhenNoticeHasNoExpiry_ReturnsNotice()
    {
        WriteCacheFile(new NoticeCache(
            DateTimeOffset.UtcNow,
            new Notice("Critical issue - upgrade now.", null, null)));

        var result = await _service.CheckForNoticeAsync();

        result.HasNotice.Should().BeTrue();
        result.Message.Should().Be("Critical issue - upgrade now.");
    }

    [Fact]
    public async Task CheckForNoticeAsync_WhenNoticeHasFutureExpiry_ReturnsNotice()
    {
        WriteCacheFile(new NoticeCache(
            DateTimeOffset.UtcNow,
            new Notice("Security advisory.", null, DateTimeOffset.UtcNow.AddDays(30))));

        var result = await _service.CheckForNoticeAsync();

        result.HasNotice.Should().BeTrue();
        result.Message.Should().Be("Security advisory.");
    }

    [Fact]
    public async Task CheckForNoticeAsync_WhenCurrentVersionMeetsMinimum_ReturnsNoNotice()
    {
        // Any real build version is above 0.0.1
        WriteCacheFile(new NoticeCache(
            DateTimeOffset.UtcNow,
            new Notice("Please upgrade.", "0.0.1", null)));

        var result = await _service.CheckForNoticeAsync();

        result.HasNotice.Should().BeFalse();
    }

    [Fact]
    public async Task CheckForNoticeAsync_WhenCurrentVersionBelowMinimum_ReturnsNotice()
    {
        // Any realistic build version is below 99.99.99
        WriteCacheFile(new NoticeCache(
            DateTimeOffset.UtcNow,
            new Notice("Please upgrade to v99.99.99.", "99.99.99", null)));

        var result = await _service.CheckForNoticeAsync();

        result.HasNotice.Should().BeTrue();
        result.Message.Should().Be("Please upgrade to v99.99.99.");
        result.UpdateCommand.Should().Contain("dotnet tool update")
            .And.Contain("Microsoft.Agents.A365.DevTools.Cli");
    }

    // ---------------------------------------------------------------------------
    // Cache file path
    // ---------------------------------------------------------------------------

    [Fact]
    public void GetCacheFilePath_ReturnsPathWithExpectedFileName()
    {
        Path.GetFileName(NoticeService.GetCacheFilePath()).Should().Be("notice.cache.json");
    }

    // ---------------------------------------------------------------------------
    // Cancellation
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CheckForNoticeAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await _service.CheckForNoticeAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static void WriteCacheFile(NoticeCache cache)
    {
        var path = NoticeService.GetCacheFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(cache));
    }
}
