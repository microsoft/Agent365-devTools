// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// VersionCheck tests must run sequentially because they modify global environment variables.
/// Running in parallel would cause race conditions when tests set/unset environment variables.
/// </summary>
[CollectionDefinition("VersionCheckTests", DisableParallelization = true)]
public class VersionCheckTestCollection
{
    // This class is never instantiated. It exists only to define the collection.
}

[Collection("VersionCheckTests")]
public class VersionCheckServiceTests
{
    private readonly ILogger<VersionCheckService> _logger;
    private readonly VersionCheckService _versionCheckService;

    public VersionCheckServiceTests()
    {
        _logger = Substitute.For<ILogger<VersionCheckService>>();
        _versionCheckService = new VersionCheckService(_logger);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenRunningInCiCd_ReturnsNoUpdate()
    {
        // Arrange
        Environment.SetEnvironmentVariable("CI", "true");

        try
        {
            // Act
            var result = await _versionCheckService.CheckForUpdatesAsync();

            // Assert
            result.UpdateAvailable.Should().BeFalse();
            result.CurrentVersion.Should().NotBeNullOrEmpty();
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("CI", null);
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Func<Task> act = async () => await _versionCheckService.CheckForUpdatesAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WithTimeout_HandlesGracefully()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));

        // Act & Assert - Should either complete successfully or throw OperationCanceledException
        try
        {
            await _versionCheckService.CheckForUpdatesAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // This is acceptable behavior for timeout
        }
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]  // Patch update available
    [InlineData("1.0.0", "1.1.0", true)]  // Minor update available
    [InlineData("1.0.0", "2.0.0", true)]  // Major update available
    [InlineData("1.0.0", "1.0.0", false)] // Same version
    [InlineData("1.0.1", "1.0.0", false)] // Current is newer
    [InlineData("1.1.0-preview.1", "1.1.0-preview.2", true)]  // Preview update
    [InlineData("1.1.0-preview.100", "1.1.0-preview.50", false)] // Current preview is newer
    public void ParseVersion_ComparesVersionsCorrectly(string current, string latest, bool expectedNewerAvailable)
    {
        // Act - ParseVersion moved to VersionCheckHelper (internal, accessible to test assembly)
        var currentVersion = VersionCheckHelper.ParseVersion(current);
        var latestVersion = VersionCheckHelper.ParseVersion(latest);

        var isNewer = latestVersion > currentVersion;

        // Assert
        isNewer.Should().Be(expectedNewerAvailable);
    }

    [Theory]
    // Stable user — no preview above GA: latest stable is 1.2.0, preview is older → no nudge
    [InlineData("1.2.0", new[] { "1.1.165-preview", "1.2.0" }, "1.2.0", null)]
    // Stable user — newer preview above GA: 1.3.0-preview.1 exists → nudge with preview version
    [InlineData("1.2.0", new[] { "1.2.0", "1.3.0-preview.1" }, "1.2.0", "1.3.0-preview.1")]
    // Preview user — GA is above preview: pick GA, no secondary nudge
    [InlineData("1.1.165-preview", new[] { "1.1.165-preview", "1.2.0" }, "1.2.0", null)]
    // Preview user — newer preview above GA: pick highest preview, no secondary nudge
    [InlineData("1.1.165-preview", new[] { "1.1.165-preview", "1.2.0", "1.3.0-preview.1" }, "1.3.0-preview.1", null)]
    public void SelectLatestVersions_AppliesChannelAwareFiltering(
        string currentVersion, string[] nugetVersions, string expectedPrimary, string? expectedNewerPreview)
    {
        // Act
        var (primary, newerPreview) = VersionCheckHelper.SelectLatestVersions(nugetVersions, currentVersion);

        // Assert
        primary.Should().Be(expectedPrimary,
            because: "the primary latest should respect the channel of the current version");
        newerPreview.Should().Be(expectedNewerPreview,
            because: "the informational preview nudge should only appear for stable users when a newer preview exists");
    }

    [Theory]
    [InlineData("CI")]
    [InlineData("TF_BUILD")]
    [InlineData("GITHUB_ACTIONS")]
    [InlineData("JENKINS_HOME")]
    [InlineData("GITLAB_CI")]
    public async Task CheckForUpdatesAsync_DetectsCiEnvironments(string envVar)
    {
        // Arrange
        Environment.SetEnvironmentVariable(envVar, "true");

        try
        {
            // Act
            var result = await _versionCheckService.CheckForUpdatesAsync();

            // Assert
            result.UpdateAvailable.Should().BeFalse();
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }
}
