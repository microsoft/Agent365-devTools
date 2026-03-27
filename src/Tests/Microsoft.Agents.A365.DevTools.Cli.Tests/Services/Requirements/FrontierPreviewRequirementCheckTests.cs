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
        result.Details.Should().Contain("enrolled");
        result.ErrorMessage.Should().Contain("cannot be verified automatically");
        result.ResolutionGuidance.Should().BeNullOrEmpty("warning checks don't have resolution guidance");
    }

    [Fact]
    public async Task CheckAsync_ShouldLogMainWarningMessage()
    {
        // Arrange
        var check = new FrontierPreviewRequirementCheck();
        var config = new Agent365Config();

        // Act
        await check.CheckAsync(config, _mockLogger);

        // Assert — [WARN] prefix is included in the message (logged at Warning severity → yellow in formatter)
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("[WARN] Frontier Preview Program")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CheckAsync_ShouldLogRequirementName()
    {
        // Arrange
        var check = new FrontierPreviewRequirementCheck();
        var config = new Agent365Config();

        // Act
        await check.CheckAsync(config, _mockLogger);

        // Assert — warning is logged at Warning severity and includes the check name
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Frontier Preview Program")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CheckAsync_ShouldIncludePreviewContext()
    {
        // Arrange
        var check = new FrontierPreviewRequirementCheck();
        var config = new Agent365Config();

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        // Verify the result mentions the auto-verification limitation
        result.Details.Should().Contain("enrolled");
    }

    [Fact]
    public async Task CheckAsync_ShouldMentionDocumentationCheck()
    {
        // Arrange
        var check = new FrontierPreviewRequirementCheck();
        var config = new Agent365Config();

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        // Verify the details include a reference URL
        result.Details.Should().Contain("https://adoption.microsoft.com/copilot/frontier-program/");
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
