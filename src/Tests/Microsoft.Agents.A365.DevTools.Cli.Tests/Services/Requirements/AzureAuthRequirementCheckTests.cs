// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Requirements;

/// <summary>
/// Unit tests for AzureAuthRequirementCheck
/// </summary>
public class AzureAuthRequirementCheckTests
{
    private readonly AzureAuthValidator _mockAuthValidator;
    private readonly ILogger _mockLogger;

    public AzureAuthRequirementCheckTests()
    {
        var mockExecutor = Substitute.ForPartsOf<CommandExecutor>(NullLogger<CommandExecutor>.Instance);
        _mockAuthValidator = Substitute.ForPartsOf<AzureAuthValidator>(NullLogger<AzureAuthValidator>.Instance, mockExecutor);
        _mockLogger = Substitute.For<ILogger>();
    }

    [Fact]
    public async Task CheckAsync_WhenAuthenticationSucceeds_ShouldReturnSuccess()
    {
        // Arrange
        var check = new AzureAuthRequirementCheck(_mockAuthValidator);
        var config = new Agent365Config { SubscriptionId = "test-sub-id" };

        _mockAuthValidator.ValidateAuthenticationAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeTrue();
        result.ErrorMessage.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task CheckAsync_WhenAuthenticationFails_ShouldReturnFailure()
    {
        // Arrange
        var check = new AzureAuthRequirementCheck(_mockAuthValidator);
        var config = new Agent365Config { SubscriptionId = "test-sub-id" };

        _mockAuthValidator.ValidateAuthenticationAsync(Arg.Any<string?>())
            .Returns(false);

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Azure CLI authentication failed");
        result.ResolutionGuidance.Should().Contain("az login");
    }

    [Fact]
    public async Task CheckAsync_ShouldPassSubscriptionIdToValidator()
    {
        // Arrange
        var check = new AzureAuthRequirementCheck(_mockAuthValidator);
        var config = new Agent365Config { SubscriptionId = "specific-sub-id" };

        _mockAuthValidator.ValidateAuthenticationAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        await check.CheckAsync(config, _mockLogger);

        // Assert
        await _mockAuthValidator.Received(1).ValidateAuthenticationAsync("specific-sub-id", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_WithEmptySubscriptionId_ShouldPassEmptyStringToValidator()
    {
        // Arrange
        var check = new AzureAuthRequirementCheck(_mockAuthValidator);
        var config = new Agent365Config();

        _mockAuthValidator.ValidateAuthenticationAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        await check.CheckAsync(config, _mockLogger);

        // Assert
        await _mockAuthValidator.Received(1).ValidateAuthenticationAsync(string.Empty, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectName()
    {
        // Arrange
        var check = new AzureAuthRequirementCheck(_mockAuthValidator);

        // Act & Assert
        check.Name.Should().Be("Azure Authentication");
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectCategory()
    {
        // Arrange
        var check = new AzureAuthRequirementCheck(_mockAuthValidator);

        // Act & Assert
        check.Category.Should().Be("Azure");
    }

    [Fact]
    public void Constructor_WithNullValidator_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        var act = () => new AzureAuthRequirementCheck(null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("authValidator");
    }
}
