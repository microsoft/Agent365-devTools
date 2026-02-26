// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Requirements;

/// <summary>
/// Unit tests for LocationRequirementCheck
/// </summary>
public class LocationRequirementCheckTests
{
    private readonly ILogger _mockLogger;
    private readonly LocationRequirementCheck _check;

    public LocationRequirementCheckTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _check = new LocationRequirementCheck();
    }

    [Fact]
    public async Task CheckAsync_WithConfiguredLocation_ShouldReturnSuccess()
    {
        // Arrange
        var config = new Agent365Config { Location = "eastus" };

        // Act
        var result = await _check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeTrue("location is configured");
        result.IsWarning.Should().BeFalse();
        result.Details.Should().Contain("eastus");
        result.ErrorMessage.Should().BeNullOrEmpty();
        result.ResolutionGuidance.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CheckAsync_WithEmptyOrWhitespaceLocation_ShouldReturnFailure(string emptyLocation)
    {
        // Arrange
        var config = new Agent365Config { Location = emptyLocation };

        // Act
        var result = await _check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse("location is required for endpoint registration");
        result.ErrorMessage.Should().Be(ErrorMessages.EndpointLocationRequiredForCreate);
        result.ResolutionGuidance.Should().Contain(ErrorMessages.EndpointLocationAddToConfig);
        result.ResolutionGuidance.Should().Contain(ErrorMessages.EndpointLocationExample);
        result.Details.Should().Contain("needDeployment");
    }

    [Fact]
    public async Task CheckAsync_WithNeedDeploymentFalseAndNoLocation_ShouldReturnFailure()
    {
        // Arrange — external hosting scenario: no location in config
        var config = new Agent365Config
        {
            NeedDeployment = false,
            MessagingEndpoint = "https://myhost.example.com/api/messages"
            // Location intentionally omitted
        };

        // Act
        var result = await _check.CheckAsync(config, _mockLogger);

        // Assert
        result.Passed.Should().BeFalse("location is required for endpoint registration even without deployment");
        result.ErrorMessage.Should().Be(ErrorMessages.EndpointLocationRequiredForCreate);
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectName()
    {
        _check.Name.Should().Be("Location Configuration");
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectDescription()
    {
        _check.Description.Should().Contain("location");
        _check.Description.Should().Contain("endpoint registration");
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectCategory()
    {
        _check.Category.Should().Be("Configuration");
    }
}
