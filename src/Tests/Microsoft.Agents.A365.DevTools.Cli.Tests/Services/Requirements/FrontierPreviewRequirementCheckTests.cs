// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Requirements;

/// <summary>
/// Unit tests for FrontierPreviewRequirementCheck
/// </summary>
public class FrontierPreviewRequirementCheckTests
{
    private readonly ILogger _mockLogger;

    public FrontierPreviewRequirementCheckTests()
    {
        _mockLogger = Substitute.For<ILogger>();
    }

    [Fact]
    public async Task CheckAsync_ShouldReturnWarning_WithDetails()
    {
        // Arrange
        var check = new FrontierPreviewRequirementCheck();
        var config = new Agent365Config();

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeTrue("check should pass to allow user to proceed despite warning");
        result.IsWarning.Should().BeTrue("check should be flagged as a warning");
        result.Details.Should().NotBeNullOrEmpty();
        result.Details.Should().Contain("ensure");
        result.Details.Should().Contain("enrolled");
        result.ErrorMessage.Should().Contain("Cannot automatically verify");
        result.ResolutionGuidance.Should().BeNullOrEmpty("warning checks don't have resolution guidance");
    }

    [Fact]
    public async Task CheckAsync_ShouldLogDocumentationLink()
    {
        // Arrange
        var check = new FrontierPreviewRequirementCheck();
        var config = new Agent365Config();

        // Act
        await check.CheckAsync(config, _mockLogger);

        // Assert
        // Verify the logger was called with the documentation link
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("https://learn.microsoft.com/en-us/microsoft-agent-365/developer/")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CheckAsync_ShouldLogFrontierProgramLink()
    {
        // Arrange
        var check = new FrontierPreviewRequirementCheck();
        var config = new Agent365Config();

        // Act
        await check.CheckAsync(config, _mockLogger);

        // Assert
        // Verify the logger was called with the Frontier program link
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("https://adoption.microsoft.com/en-us/copilot/frontier-program/")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CheckAsync_ShouldLogLearnMoreText()
    {
        // Arrange
        var check = new FrontierPreviewRequirementCheck();
        var config = new Agent365Config();

        // Act
        await check.CheckAsync(config, _mockLogger);

        // Assert
        // Verify the logger was called with "Learn more:" text
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Learn more:")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CheckAsync_ShouldLogGuidanceForAlreadyEnrolled()
    {
        // Arrange
        var check = new FrontierPreviewRequirementCheck();
        var config = new Agent365Config();

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        // Verify the details mention ability to proceed
        result.Details.Should().Contain("ensure");
        result.Details.Should().Contain("proceeding");
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectName()
    {
        // Arrange
        var check = new FrontierPreviewRequirementCheck();

        // Act & Assert
        check.Name.Should().Be("Frontier Preview Program");
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectDescription()
    {
        // Arrange
        var check = new FrontierPreviewRequirementCheck();

        // Act & Assert
        check.Description.Should().Contain("Frontier Preview Program");
        check.Description.Should().Contain("early access");
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectCategory()
    {
        // Arrange
        var check = new FrontierPreviewRequirementCheck();

        // Act & Assert
        check.Category.Should().Be("Tenant Enrollment");
    }
}
