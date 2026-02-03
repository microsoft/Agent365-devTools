// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

/// <summary>
/// Unit tests for SetupHelpers.DisplaySetupSummary method
/// </summary>
public class SetupHelpersDisplaySummaryTests
{
    private readonly ILogger _mockLogger;
    private readonly List<string> _logMessages;

    public SetupHelpersDisplaySummaryTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _logMessages = new List<string>();

        // Capture log messages for verification
        _mockLogger.When(x => x.Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>()))
            .Do(callInfo =>
            {
                var state = callInfo.ArgAt<object>(2);
                if (state != null)
                {
                    _logMessages.Add(state.ToString() ?? string.Empty);
                }
            });
    }

    [Fact]
    public void DisplaySetupSummary_WithGraphPermissionsError_ShouldShowRecoveryAction()
    {
        // Arrange
        var results = new SetupResults
        {
            BlueprintCreated = true,
            GraphInheritablePermissionsError = "Test error"
        };
        results.Warnings.Add($"Microsoft Graph inheritable permissions: {results.GraphInheritablePermissionsError}");

        // Act
        SetupHelpers.DisplaySetupSummary(results, _mockLogger);

        // Assert - Verify recovery action is shown
        _logMessages.Should().Contain(m => m.Contains("Graph Inheritable Permissions"));
        _logMessages.Should().Contain(m => m.Contains("a365 setup blueprint"));
    }

    [Fact]
    public void DisplaySetupSummary_WithNoGraphPermissionsError_ShouldNotShowRecoveryAction()
    {
        // Arrange
        var results = new SetupResults
        {
            BlueprintCreated = true,
            GraphInheritablePermissionsError = null
        };

        // Act
        SetupHelpers.DisplaySetupSummary(results, _mockLogger);

        // Assert - Should not show Graph recovery action
        _logMessages.Should().NotContain(m => m.Contains("Graph Inheritable Permissions: Run"));
    }

    [Fact]
    public void DisplaySetupSummary_WithWarningsButNoGraphError_ShouldShowWarningsSection()
    {
        // Arrange
        var results = new SetupResults
        {
            BlueprintCreated = true,
            GraphInheritablePermissionsError = null
        };
        results.Warnings.Add("Some other warning");

        // Act
        SetupHelpers.DisplaySetupSummary(results, _mockLogger);

        // Assert
        _logMessages.Should().Contain(m => m.Contains("Warnings:"));
        _logMessages.Should().Contain(m => m.Contains("Some other warning"));
    }

    [Fact]
    public void DisplaySetupSummary_WithGraphErrorAndOtherWarnings_ShouldShowBothRecoveryActions()
    {
        // Arrange
        var results = new SetupResults
        {
            BlueprintCreated = true,
            GraphInheritablePermissionsError = "Permission denied"
        };
        results.Warnings.Add($"Microsoft Graph inheritable permissions: {results.GraphInheritablePermissionsError}");

        // Act
        SetupHelpers.DisplaySetupSummary(results, _mockLogger);

        // Assert
        _logMessages.Should().Contain(m => m.Contains("Setup completed successfully with warnings"));
        _logMessages.Should().Contain(m => m.Contains("Recovery Actions:"));
        _logMessages.Should().Contain(m => m.Contains("Graph Inheritable Permissions"));
    }

    [Fact]
    public void DisplaySetupSummary_WithErrors_ShouldShowErrorRecoveryActions()
    {
        // Arrange
        var results = new SetupResults
        {
            BlueprintCreated = false,
            McpPermissionsConfigured = false,
            BotApiPermissionsConfigured = false
        };
        results.Errors.Add("Blueprint creation failed");

        // Act
        SetupHelpers.DisplaySetupSummary(results, _mockLogger);

        // Assert
        _logMessages.Should().Contain(m => m.Contains("Setup completed with errors"));
        _logMessages.Should().Contain(m => m.Contains("Recovery Actions:"));
    }

    [Fact]
    public void DisplaySetupSummary_AllSuccessful_ShouldNotShowWarningsOrErrors()
    {
        // Arrange
        var results = new SetupResults
        {
            InfrastructureCreated = true,
            BlueprintCreated = true,
            McpPermissionsConfigured = true,
            BotApiPermissionsConfigured = true,
            MessagingEndpointRegistered = true,
            InheritablePermissionsConfigured = true,
            GraphInheritablePermissionsConfigured = true,
            GraphInheritablePermissionsError = null
        };

        // Act
        SetupHelpers.DisplaySetupSummary(results, _mockLogger);

        // Assert
        _logMessages.Should().Contain(m => m.Contains("Setup completed successfully"));
        _logMessages.Should().Contain(m => m.Contains("All components configured correctly"));
        _logMessages.Should().NotContain(m => m.Contains("Recovery Actions:"));
    }

    [Fact]
    public void DisplaySetupSummary_WithGraphError_ShouldIndicatePartialSuccess()
    {
        // Arrange
        var results = new SetupResults
        {
            InfrastructureCreated = true,
            BlueprintCreated = true,
            McpPermissionsConfigured = true,
            BotApiPermissionsConfigured = true,
            MessagingEndpointRegistered = true,
            GraphInheritablePermissionsError = "Failed"
        };
        results.Warnings.Add($"Microsoft Graph inheritable permissions: {results.GraphInheritablePermissionsError}");

        // Act
        SetupHelpers.DisplaySetupSummary(results, _mockLogger);

        // Assert - Should indicate success with warnings, not failure
        _logMessages.Should().Contain(m => m.Contains("Setup completed successfully with warnings"));
        _logMessages.Should().NotContain(m => m.Contains("Setup completed with errors"));
    }
}
