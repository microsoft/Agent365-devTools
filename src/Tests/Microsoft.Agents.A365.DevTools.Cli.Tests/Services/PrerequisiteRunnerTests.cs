// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Unit tests for PrerequisiteRunner
/// </summary>
public class PrerequisiteRunnerTests
{
    private readonly ILogger _mockLogger;
    private readonly Agent365Config _config;

    public PrerequisiteRunnerTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _config = new Agent365Config { TenantId = "test-tenant", SubscriptionId = "test-sub" };
    }

    [Fact]
    public async Task RunAsync_WithEmptyChecks_ShouldReturnTrue()
    {
        // Arrange
        var runner = new PrerequisiteRunner();
        var checks = new List<IRequirementCheck>();

        // Act
        var result = await runner.RunAsync(checks, _config, _mockLogger);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WithAllPassingChecks_ShouldReturnTrue()
    {
        // Arrange
        var runner = new PrerequisiteRunner();
        var checks = new List<IRequirementCheck>
        {
            new AlwaysPassRequirementCheck(),
            new AlwaysPassRequirementCheck()
        };

        // Act
        var result = await runner.RunAsync(checks, _config, _mockLogger);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WithOneFailingCheck_ShouldReturnFalse()
    {
        // Arrange
        var runner = new PrerequisiteRunner();
        var checks = new List<IRequirementCheck>
        {
            new AlwaysPassRequirementCheck(),
            new AlwaysFailRequirementCheck()
        };

        // Act
        var result = await runner.RunAsync(checks, _config, _mockLogger);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_WithFailingCheck_ShouldLogError()
    {
        // Arrange
        var runner = new PrerequisiteRunner();
        var checks = new List<IRequirementCheck> { new AlwaysFailRequirementCheck() };

        // Act
        await runner.RunAsync(checks, _config, _mockLogger);

        // Assert - should log an error for the failing check
        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task RunAsync_WithMultipleFailingChecks_ShouldReturnFalseAndLogAll()
    {
        // Arrange
        var runner = new PrerequisiteRunner();
        var checks = new List<IRequirementCheck>
        {
            new AlwaysFailRequirementCheck(),
            new AlwaysFailRequirementCheck()
        };

        // Act
        var result = await runner.RunAsync(checks, _config, _mockLogger);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_WithWarningCheck_ShouldReturnTrueAndLogWarning()
    {
        // Arrange
        var runner = new PrerequisiteRunner();
        var mockCheck = Substitute.For<IRequirementCheck>();
        mockCheck.Name.Returns("Warning Check");
        mockCheck.CheckAsync(Arg.Any<Agent365Config>(), Arg.Any<ILogger>(), Arg.Any<CancellationToken>())
            .Returns(RequirementCheckResult.Warning("This is a warning", "Warning details"));

        var checks = new List<IRequirementCheck> { mockCheck };

        // Act
        var result = await runner.RunAsync(checks, _config, _mockLogger);

        // Assert
        result.Should().BeTrue("a warning does not block execution");
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task RunAsync_ChecksAreRunInOrder()
    {
        // Arrange
        var runner = new PrerequisiteRunner();
        var executionOrder = new List<string>();

        var check1 = Substitute.For<IRequirementCheck>();
        check1.Name.Returns("Check1");
        check1.CheckAsync(Arg.Any<Agent365Config>(), Arg.Any<ILogger>(), Arg.Any<CancellationToken>())
            .Returns(_ => { executionOrder.Add("Check1"); return Task.FromResult(RequirementCheckResult.Success()); });

        var check2 = Substitute.For<IRequirementCheck>();
        check2.Name.Returns("Check2");
        check2.CheckAsync(Arg.Any<Agent365Config>(), Arg.Any<ILogger>(), Arg.Any<CancellationToken>())
            .Returns(_ => { executionOrder.Add("Check2"); return Task.FromResult(RequirementCheckResult.Success()); });

        var checks = new List<IRequirementCheck> { check1, check2 };

        // Act
        await runner.RunAsync(checks, _config, _mockLogger);

        // Assert
        executionOrder.Should().Equal("Check1", "Check2");
    }
}
