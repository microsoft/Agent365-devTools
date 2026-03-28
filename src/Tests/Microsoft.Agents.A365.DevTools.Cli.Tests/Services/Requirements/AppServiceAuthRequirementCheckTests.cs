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
/// Unit tests for AppServiceAuthRequirementCheck
/// </summary>
public class AppServiceAuthRequirementCheckTests
{
    private readonly AzureAuthValidator _mockAuthValidator;
    private readonly ILogger _mockLogger;

    public AppServiceAuthRequirementCheckTests()
    {
        var mockExecutor = Substitute.ForPartsOf<CommandExecutor>(NullLogger<CommandExecutor>.Instance);
        _mockAuthValidator = Substitute.ForPartsOf<AzureAuthValidator>(NullLogger<AzureAuthValidator>.Instance, mockExecutor);
        _mockLogger = Substitute.For<ILogger>();
    }

    [Fact]
    public async Task CheckAsync_WhenTokenAcquisitionSucceeds_ShouldReturnSuccess()
    {
        // Arrange
        var check = new AppServiceAuthRequirementCheck(_mockAuthValidator);
        var config = new Agent365Config { SubscriptionId = "test-sub-id" };

        _mockAuthValidator.GetAppServiceTokenAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeTrue();
        result.IsWarning.Should().BeFalse();
        result.ErrorMessage.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task CheckAsync_WhenTokenAcquisitionFails_ShouldReturnFailure()
    {
        // Arrange
        var check = new AppServiceAuthRequirementCheck(_mockAuthValidator);
        var config = new Agent365Config { SubscriptionId = "test-sub-id" };

        _mockAuthValidator.GetAppServiceTokenAsync(Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().Contain("App Service token is expired or revoked");
        result.ResolutionGuidance.Should().Contain("az logout");
    }

    [Fact]
    public async Task CheckAsync_ShouldCallGetAppServiceTokenAsync()
    {
        // Arrange
        var check = new AppServiceAuthRequirementCheck(_mockAuthValidator);
        var config = new Agent365Config();

        _mockAuthValidator.GetAppServiceTokenAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        await check.CheckAsync(config, _mockLogger);

        // Assert
        await _mockAuthValidator.Received(1).GetAppServiceTokenAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectName()
    {
        var check = new AppServiceAuthRequirementCheck(_mockAuthValidator);
        check.Name.Should().Be("App Service Authentication");
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectCategory()
    {
        var check = new AppServiceAuthRequirementCheck(_mockAuthValidator);
        check.Category.Should().Be("Azure");
    }

    [Fact]
    public void Constructor_WithNullValidator_ShouldThrowArgumentNullException()
    {
        var act = () => new AppServiceAuthRequirementCheck(null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("auth");
    }
}
