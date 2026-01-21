// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

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

        // Act
        Func<Task> act = async () => await _versionCheckService.CheckForUpdatesAsync(cts.Token);

        // Assert - Either completes successfully or throws OperationCanceledException
        try
        {
            await act();
            // If we get here, the call completed successfully
            true.Should().BeTrue();
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
        // This tests the internal version comparison logic indirectly
        // by observing the behavior through ParseVersion
        var currentVersion = InvokePrivateMethod<Version>(_versionCheckService, "ParseVersion", current);
        var latestVersion = InvokePrivateMethod<Version>(_versionCheckService, "ParseVersion", latest);

        // Act
        var isNewer = latestVersion > currentVersion;

        // Assert
        isNewer.Should().Be(expectedNewerAvailable);
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

    // Helper method to invoke private methods for testing
    private T InvokePrivateMethod<T>(object obj, string methodName, params object[] parameters)
    {
        var method = obj.GetType().GetMethod(methodName, 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (T)method!.Invoke(obj, parameters)!;
    }
}
