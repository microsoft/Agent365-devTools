// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Unit tests for RequirementsSubcommand with custom test requirement checks.
/// These tests validate that the subcommand correctly processes passing and failing checks.
/// </summary>
public class RequirementsSubcommandTests
{
    private readonly ILogger _mockLogger;
    private readonly IConfigService _mockConfigService;

    public RequirementsSubcommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockConfigService = Substitute.For<IConfigService>();
    }

    #region Test Requirement Check Tests

    [Fact]
    public async Task AlwaysPassRequirementCheck_ShouldAlwaysReturnSuccess()
    {
        // Arrange
        var check = new AlwaysPassRequirementCheck();
        var config = new Agent365Config();

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeTrue();
        result.ErrorMessage.Should().BeNullOrEmpty();
        result.Details.Should().Contain("always passes");
    }

    [Fact]
    public async Task AlwaysFailRequirementCheck_ShouldAlwaysReturnFailure()
    {
        // Arrange
        var check = new AlwaysFailRequirementCheck();
        var config = new Agent365Config();

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("always fails");
        result.ResolutionGuidance.Should().NotBeNullOrEmpty();
        result.Details.Should().Contain("Test failure details");
    }

    [Fact]
    public void AlwaysPassRequirementCheck_ShouldHaveCorrectMetadata()
    {
        // Arrange
        var check = new AlwaysPassRequirementCheck();

        // Act & Assert
        check.Name.Should().Be("Test Always Pass Check");
        check.Description.Should().Be("Test requirement check that always passes");
        check.Category.Should().Be("Test");
    }

    [Fact]
    public void AlwaysFailRequirementCheck_ShouldHaveCorrectMetadata()
    {
        // Arrange
        var check = new AlwaysFailRequirementCheck();

        // Act & Assert
        check.Name.Should().Be("Test Always Fail Check");
        check.Description.Should().Be("Test requirement check that always fails");
        check.Category.Should().Be("Test");
    }

    #endregion

    #region RequirementCheckResult Tests

    [Fact]
    public void RequirementCheckResult_Success_ShouldCreateSuccessResult()
    {
        // Act
        var result = RequirementCheckResult.Success("Test details");

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeTrue();
        result.Details.Should().Be("Test details");
        result.ErrorMessage.Should().BeNullOrEmpty();
        result.ResolutionGuidance.Should().BeNullOrEmpty();
    }

    [Fact]
    public void RequirementCheckResult_Failure_ShouldCreateFailureResult()
    {
        // Act
        var result = RequirementCheckResult.Failure(
            "Test error",
            "Test resolution",
            "Test details");

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().Be("Test error");
        result.ResolutionGuidance.Should().Be("Test resolution");
        result.Details.Should().Be("Test details");
    }

    [Fact]
    public void RequirementCheckResult_SuccessWithoutDetails_ShouldHaveNullDetails()
    {
        // Act
        var result = RequirementCheckResult.Success();

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeTrue();
        result.Details.Should().BeNull();
    }

    [Fact]
    public void RequirementCheckResult_FailureWithoutDetails_ShouldHaveNullDetails()
    {
        // Act
        var result = RequirementCheckResult.Failure(
            "Test error",
            "Test resolution");

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse();
        result.Details.Should().BeNull();
    }

    #endregion

    #region Multiple Check Execution Tests

    [Fact]
    public async Task MultipleChecks_AllPass_ShouldReturnTrue()
    {
        // Arrange
        var checks = new List<IRequirementCheck>
        {
            new AlwaysPassRequirementCheck(),
            new AlwaysPassRequirementCheck()
        };
        var config = new Agent365Config();

        // Act
        var results = new List<RequirementCheckResult>();
        foreach (var check in checks)
        {
            var result = await check.CheckAsync(config, _mockLogger);
            results.Add(result);
        }

        var allPassed = results.All(r => r.Passed);

        // Assert
        allPassed.Should().BeTrue();
        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task MultipleChecks_SomeFail_ShouldReturnFalse()
    {
        // Arrange
        var checks = new List<IRequirementCheck>
        {
            new AlwaysPassRequirementCheck(),
            new AlwaysFailRequirementCheck(),
            new AlwaysPassRequirementCheck()
        };
        var config = new Agent365Config();

        // Act
        var results = new List<RequirementCheckResult>();
        foreach (var check in checks)
        {
            var result = await check.CheckAsync(config, _mockLogger);
            results.Add(result);
        }

        var allPassed = results.All(r => r.Passed);
        var passedCount = results.Count(r => r.Passed);
        var failedCount = results.Count(r => !r.Passed);

        // Assert
        allPassed.Should().BeFalse();
        passedCount.Should().Be(2);
        failedCount.Should().Be(1);
        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task MultipleChecks_AllFail_ShouldReturnFalse()
    {
        // Arrange
        var checks = new List<IRequirementCheck>
        {
            new AlwaysFailRequirementCheck(),
            new AlwaysFailRequirementCheck()
        };
        var config = new Agent365Config();

        // Act
        var results = new List<RequirementCheckResult>();
        foreach (var check in checks)
        {
            var result = await check.CheckAsync(config, _mockLogger);
            results.Add(result);
        }

        var allPassed = results.All(r => r.Passed);
        var failedCount = results.Count(r => !r.Passed);

        // Assert
        allPassed.Should().BeFalse();
        failedCount.Should().Be(2);
        results.Should().HaveCount(2);
    }

    #endregion
}
