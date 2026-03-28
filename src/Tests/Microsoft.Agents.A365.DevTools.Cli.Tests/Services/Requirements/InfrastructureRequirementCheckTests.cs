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
/// Unit tests for InfrastructureRequirementCheck
/// </summary>
public class InfrastructureRequirementCheckTests
{
    private readonly ILogger _mockLogger;

    public InfrastructureRequirementCheckTests()
    {
        _mockLogger = Substitute.For<ILogger>();
    }

    [Fact]
    public async Task CheckAsync_WhenNeedDeploymentFalse_ShouldReturnSuccess()
    {
        // Arrange
        var check = new InfrastructureRequirementCheck();
        var config = new Agent365Config
        {
            NeedDeployment = false,
            SubscriptionId = "",
            ResourceGroup = "",
            AppServicePlanName = "",
            WebAppName = "",
            Location = ""
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Passed.Should().BeTrue();
        result.ErrorMessage.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task CheckAsync_WithAllRequiredFields_ShouldReturnSuccess()
    {
        // Arrange
        var check = new InfrastructureRequirementCheck();
        var config = new Agent365Config
        {
            NeedDeployment = true,
            SubscriptionId = "test-sub-id",
            ResourceGroup = "test-rg",
            AppServicePlanName = "test-plan",
            WebAppName = "test-webapp",
            Location = "eastus",
            AppServicePlanSku = "B1"
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Passed.Should().BeTrue();
        result.ErrorMessage.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task CheckAsync_WithMissingSubscriptionId_ShouldReturnFailure()
    {
        // Arrange
        var check = new InfrastructureRequirementCheck();
        var config = new Agent365Config
        {
            NeedDeployment = true,
            SubscriptionId = "",
            ResourceGroup = "test-rg",
            AppServicePlanName = "test-plan",
            WebAppName = "test-webapp",
            Location = "eastus",
            AppServicePlanSku = "B1"
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().Contain("subscriptionId");
        result.ResolutionGuidance.Should().Contain("a365.config.json");
    }

    [Fact]
    public async Task CheckAsync_WithMissingResourceGroup_ShouldReturnFailure()
    {
        // Arrange
        var check = new InfrastructureRequirementCheck();
        var config = new Agent365Config
        {
            NeedDeployment = true,
            SubscriptionId = "test-sub-id",
            ResourceGroup = "",
            AppServicePlanName = "test-plan",
            WebAppName = "test-webapp",
            Location = "eastus",
            AppServicePlanSku = "B1"
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().Contain("resourceGroup");
    }

    [Fact]
    public async Task CheckAsync_WithMissingAppServicePlanName_ShouldReturnFailure()
    {
        // Arrange
        var check = new InfrastructureRequirementCheck();
        var config = new Agent365Config
        {
            NeedDeployment = true,
            SubscriptionId = "test-sub-id",
            ResourceGroup = "test-rg",
            AppServicePlanName = "",
            WebAppName = "test-webapp",
            Location = "eastus",
            AppServicePlanSku = "B1"
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().Contain("appServicePlanName");
    }

    [Fact]
    public async Task CheckAsync_WithMultipleMissingFields_ShouldIncludeAllErrorsInMessage()
    {
        // Arrange
        var check = new InfrastructureRequirementCheck();
        var config = new Agent365Config
        {
            NeedDeployment = true,
            SubscriptionId = "",
            ResourceGroup = "",
            AppServicePlanName = "",
            WebAppName = "test-webapp",
            Location = "eastus",
            AppServicePlanSku = "F1"
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().Contain("subscriptionId");
        result.ErrorMessage.Should().Contain("resourceGroup");
        result.ErrorMessage.Should().Contain("appServicePlanName");
    }

    [Fact]
    public async Task CheckAsync_WithInvalidSku_ShouldReturnFailure()
    {
        // Arrange
        var check = new InfrastructureRequirementCheck();
        var config = new Agent365Config
        {
            NeedDeployment = true,
            SubscriptionId = "test-sub-id",
            ResourceGroup = "test-rg",
            AppServicePlanName = "test-plan",
            WebAppName = "test-webapp",
            Location = "eastus",
            AppServicePlanSku = "INVALID_SKU"
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Invalid appServicePlanSku");
        result.ErrorMessage.Should().Contain("INVALID_SKU");
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("B1")]
    [InlineData("B2")]
    [InlineData("B3")]
    [InlineData("S1")]
    [InlineData("P1V2")]
    [InlineData("P1V3")]
    public async Task CheckAsync_WithValidSku_ShouldReturnSuccess(string sku)
    {
        // Arrange
        var check = new InfrastructureRequirementCheck();
        var config = new Agent365Config
        {
            NeedDeployment = true,
            SubscriptionId = "test-sub-id",
            ResourceGroup = "test-rg",
            AppServicePlanName = "test-plan",
            WebAppName = "test-webapp",
            Location = "eastus",
            AppServicePlanSku = sku
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectName()
    {
        // Arrange
        var check = new InfrastructureRequirementCheck();

        // Act & Assert
        check.Name.Should().Be("Infrastructure Configuration");
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectCategory()
    {
        // Arrange
        var check = new InfrastructureRequirementCheck();

        // Act & Assert
        check.Category.Should().Be("Configuration");
    }
}
