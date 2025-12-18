// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Tests for subcommand validation logic.
/// Ensures prerequisites are validated before execution.
/// </summary>
public class SubcommandValidationTests
{
    private readonly IAzureValidator _mockAzureValidator;
    private readonly IClientAppValidator _mockClientAppValidator;

    public SubcommandValidationTests()
    {
        _mockAzureValidator = Substitute.For<IAzureValidator>();
        _mockClientAppValidator = Substitute.For<IClientAppValidator>();
    }

    #region InfrastructureSubcommand Validation Tests

    [Fact]
    public async Task InfrastructureSubcommand_WithValidConfig_PassesValidation()
    {
        // Arrange
        var config = new Agent365Config
        {
            NeedDeployment = true,
            SubscriptionId = "test-sub-id",
            ResourceGroup = "test-rg",
            AppServicePlanName = "test-plan",
            WebAppName = "test-webapp",
            Location = "westus",
            AppServicePlanSku = "F1" // Use F1 to avoid B1 quota warning
        };

        // Act
        var errors = await InfrastructureSubcommand.ValidateAsync(config, _mockAzureValidator);

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task InfrastructureSubcommand_WithMissingSubscriptionId_FailsValidation()
    {
        // Arrange
        var config = new Agent365Config
        {
            NeedDeployment = true,
            SubscriptionId = "",
            ResourceGroup = "test-rg",
            AppServicePlanName = "test-plan",
            WebAppName = "test-webapp",
            Location = "westus",
            AppServicePlanSku = "F1" // Use F1 to avoid B1 quota warning
        };

        // Act
        var errors = await InfrastructureSubcommand.ValidateAsync(config, _mockAzureValidator);

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("subscriptionId");
    }

    [Fact]
    public async Task InfrastructureSubcommand_WithMissingResourceGroup_FailsValidation()
    {
        // Arrange
        var config = new Agent365Config
        {
            NeedDeployment = true,
            SubscriptionId = "test-sub-id",
            ResourceGroup = "",
            AppServicePlanName = "test-plan",
            WebAppName = "test-webapp",
            Location = "westus",
            AppServicePlanSku = "F1" // Use F1 to avoid B1 quota warning
        };

        // Act
        var errors = await InfrastructureSubcommand.ValidateAsync(config, _mockAzureValidator);

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("resourceGroup");
    }

    [Fact]
    public async Task InfrastructureSubcommand_WithMultipleMissingFields_ReturnsAllErrors()
    {
        // Arrange
        var config = new Agent365Config
        {
            NeedDeployment = true,
            SubscriptionId = "",
            ResourceGroup = "",
            AppServicePlanName = "",
            WebAppName = "test-webapp",
            Location = "westus",
            AppServicePlanSku = "F1" // Use F1 to avoid B1 quota warning
        };

        // Act
        var errors = await InfrastructureSubcommand.ValidateAsync(config, _mockAzureValidator);

        // Assert
        errors.Should().HaveCount(3);
        errors.Should().Contain(e => e.Contains("subscriptionId"));
        errors.Should().Contain(e => e.Contains("resourceGroup"));
        errors.Should().Contain(e => e.Contains("appServicePlanName"));
    }

    [Fact]
    public async Task InfrastructureSubcommand_WhenNeedDeploymentFalse_SkipsValidation()
    {
        // Arrange
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
        var errors = await InfrastructureSubcommand.ValidateAsync(config, _mockAzureValidator);

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task InfrastructureSubcommand_WithInvalidSku_FailsValidation()
    {
        // Arrange
        var config = new Agent365Config
        {
            NeedDeployment = true,
            SubscriptionId = "test-sub-id",
            ResourceGroup = "test-rg",
            AppServicePlanName = "test-plan",
            WebAppName = "test-webapp",
            Location = "westus",
            AppServicePlanSku = "INVALID_SKU"
        };

        // Act
        var errors = await InfrastructureSubcommand.ValidateAsync(config, _mockAzureValidator);

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("Invalid appServicePlanSku");
    }

    [Fact]
    public async Task InfrastructureSubcommand_WithB1Sku_PassesValidation()
    {
        // Arrange
        var config = new Agent365Config
        {
            NeedDeployment = true,
            SubscriptionId = "test-sub-id",
            ResourceGroup = "test-rg",
            AppServicePlanName = "test-plan",
            WebAppName = "test-webapp",
            Location = "westus",
            AppServicePlanSku = "B1"
        };

        // Act
        var errors = await InfrastructureSubcommand.ValidateAsync(config, _mockAzureValidator);

        // Assert - B1 quota warning is now logged at execution time, not during validation
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("B1")]
    [InlineData("B2")]
    [InlineData("S1")]
    [InlineData("P1V2")]
    [InlineData("P1V3")]
    public async Task InfrastructureSubcommand_WithValidSku_PassesValidationOrWarning(string sku)
    {
        // Arrange
        var config = new Agent365Config
        {
            NeedDeployment = true,
            SubscriptionId = "test-sub-id",
            ResourceGroup = "test-rg",
            AppServicePlanName = "test-plan",
            WebAppName = "test-webapp",
            Location = "westus",
            AppServicePlanSku = sku
        };

        // Act
        var errors = await InfrastructureSubcommand.ValidateAsync(config, _mockAzureValidator);

        // Assert - All valid SKUs pass validation (B1 quota warning is logged at execution time)
        errors.Should().BeEmpty();
    }

    #endregion

    #region BlueprintSubcommand Validation Tests

    [Fact]
    public async Task BlueprintSubcommand_WithValidConfig_PassesValidation()
    {
        // Arrange
        var config = new Agent365Config
        {
            ClientAppId = "12345678-1234-1234-1234-123456789012"
        };

        // Act
        var errors = await BlueprintSubcommand.ValidateAsync(config, _mockAzureValidator, _mockClientAppValidator);

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task BlueprintSubcommand_WithMissingClientAppId_FailsValidation()
    {
        // Arrange
        var config = new Agent365Config
        {
            ClientAppId = ""
        };

        // Act
        var errors = await BlueprintSubcommand.ValidateAsync(config, _mockAzureValidator, _mockClientAppValidator);

        // Assert
        errors.Should().HaveCountGreaterThan(0);
        errors.Should().Contain(e => e.Contains("clientAppId"));
        errors.Should().Contain(e => e.Contains("learn.microsoft.com"));
    }

    #endregion

    #region PermissionsSubcommand Validation Tests

    [Fact]
    public async Task PermissionsSubcommand_ValidateMcp_WithValidConfig_PassesValidation()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var manifestPath = Path.Combine(tempDir, "toolingManifest.json");
        await File.WriteAllTextAsync(manifestPath, "{}");

        try
        {
            var config = new Agent365Config
            {
                AgentBlueprintId = "test-blueprint-id",
                DeploymentProjectPath = tempDir
            };

            // Act
            var errors = await PermissionsSubcommand.ValidateMcpAsync(config);

            // Assert
            errors.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task PermissionsSubcommand_ValidateMcp_WithMissingBlueprintId_FailsValidation()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var manifestPath = Path.Combine(tempDir, "toolingManifest.json");
        await File.WriteAllTextAsync(manifestPath, "{}");

        try
        {
            var config = new Agent365Config
            {
                AgentBlueprintId = "",
                DeploymentProjectPath = tempDir
            };

            // Act
            var errors = await PermissionsSubcommand.ValidateMcpAsync(config);

            // Assert
            errors.Should().ContainSingle()
                .Which.Should().Contain("Blueprint ID");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task PermissionsSubcommand_ValidateMcp_WithMissingManifest_FailsValidation()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var config = new Agent365Config
            {
                AgentBlueprintId = "test-blueprint-id",
                DeploymentProjectPath = tempDir
            };

            // Act
            var errors = await PermissionsSubcommand.ValidateMcpAsync(config);

            // Assert
            errors.Should().ContainSingle()
                .Which.Should().Contain("toolingManifest.json");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task PermissionsSubcommand_ValidateBot_WithValidConfig_PassesValidation()
    {
        // Arrange
        var config = new Agent365Config
        {
            AgentBlueprintId = "test-blueprint-id"
        };

        // Act
        var errors = await PermissionsSubcommand.ValidateBotAsync(config);

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task PermissionsSubcommand_ValidateBot_WithMissingBlueprintId_FailsValidation()
    {
        // Arrange
        var config = new Agent365Config
        {
            AgentBlueprintId = ""
        };

        // Act
        var errors = await PermissionsSubcommand.ValidateBotAsync(config);

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("Blueprint ID");
    }

    #endregion
}

