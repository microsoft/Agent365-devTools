// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Agents.A365.DevTools.Cli.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Tests for subcommand validation logic.
/// Ensures prerequisites are validated before execution.
/// </summary>
public class SubcommandValidationTests
{
    private readonly ILogger _mockLogger;

    public SubcommandValidationTests()
    {
        _mockLogger = Substitute.For<ILogger>();
    }

    #region InfrastructureRequirementCheck Validation Tests

    [Fact]
    public async Task InfrastructureSubcommand_WithValidConfig_PassesValidation()
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
            Location = "westus",
            AppServicePlanSku = "F1"
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task InfrastructureSubcommand_WithMissingSubscriptionId_FailsValidation()
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
            Location = "westus",
            AppServicePlanSku = "F1"
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().Contain("subscriptionId");
    }

    [Fact]
    public async Task InfrastructureSubcommand_WithMissingResourceGroup_FailsValidation()
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
            Location = "westus",
            AppServicePlanSku = "F1"
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().Contain("resourceGroup");
    }

    [Fact]
    public async Task InfrastructureSubcommand_WithMultipleMissingFields_ReturnsAllErrors()
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
            Location = "westus",
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
    public async Task InfrastructureSubcommand_WhenNeedDeploymentFalse_SkipsValidation()
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
    }

    [Fact]
    public async Task InfrastructureSubcommand_WithInvalidSku_FailsValidation()
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
            Location = "westus",
            AppServicePlanSku = "INVALID_SKU"
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Invalid appServicePlanSku");
    }

    [Fact]
    public async Task InfrastructureSubcommand_WithB1Sku_PassesValidation()
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
            Location = "westus",
            AppServicePlanSku = "B1"
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Passed.Should().BeTrue();
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
        var check = new InfrastructureRequirementCheck();
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
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Passed.Should().BeTrue();
    }

    #endregion

    #region PermissionsSubcommand Validation Tests

    [Fact]
    public async Task PermissionsSubcommand_ValidateMcp_WithValidConfig_PassesValidation()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var manifestPath = Path.Combine(tempDir, McpConstants.ToolingManifestFileName);
        await File.WriteAllTextAsync(manifestPath, "{}");

        try
        {
            var config = new Agent365Config
            {
                AgentBlueprintId = "test-blueprint-id",
                DeploymentProjectPath = tempDir
            };

            // Act
            var errors = await ValidationHelper.ValidateMcpAsync(config);

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
        var manifestPath = Path.Combine(tempDir, McpConstants.ToolingManifestFileName);
        await File.WriteAllTextAsync(manifestPath, "{}");

        try
        {
            var config = new Agent365Config
            {
                AgentBlueprintId = "",
                DeploymentProjectPath = tempDir
            };

            // Act
            var errors = await ValidationHelper.ValidateMcpAsync(config);

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
            var errors = await ValidationHelper.ValidateMcpAsync(config);

            // Assert
            errors.Should().ContainSingle()
                .Which.Should().Contain("ToolingManifest.json");
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
        var errors = await ValidationHelper.ValidateBlueprintAsync(config);

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
        var errors = await ValidationHelper.ValidateBlueprintAsync(config);

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("Blueprint ID");
    }

    #endregion

    #region RequirementsSubcommand Validation Tests

    [Fact]
    public async Task RequirementsSubcommand_WithValidConfig_CompletesWithoutException()
    {
        // Arrange
        var mockLogger = Substitute.For<ILogger>();
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            SubscriptionId = "test-sub-id",
            ClientAppId = "test-client-app-id"
        };
        var listOfChecks = new List<IRequirementCheck>{new AlwaysPassRequirementCheck()};

        // Act & Assert - Should complete without throwing exceptions
        var result = await RequirementsSubcommand.RunRequirementChecksAsync(listOfChecks, config, mockLogger, null);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task RequirementsSubcommand_WithFailingCheck_ReturnsFalse()
    {
        // Arrange
        var mockLogger = Substitute.For<ILogger>();
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            SubscriptionId = "test-sub-id",
            ClientAppId = "test-client-app-id"
        };
        var listOfChecks = new List<IRequirementCheck> { new AlwaysFailRequirementCheck() };

        // Act & Assert - Should complete without throwing exceptions
        var result = await RequirementsSubcommand.RunRequirementChecksAsync(listOfChecks, config, mockLogger, null);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RequirementsSubcommand_WithCategoryFilter_CompletesWithoutException()
    {
        // Arrange
        var mockLogger = Substitute.For<ILogger>();
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            SubscriptionId = "test-sub-id"
        };
        var listOfChecks = new List<IRequirementCheck> { new AlwaysFailRequirementCheck() };

        // Act & Assert - Should complete without throwing exceptions since failing check is skipped
        var result = await RequirementsSubcommand.RunRequirementChecksAsync(listOfChecks, config, mockLogger, "Powershell");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task RequirementsSubcommand_WithNullCategory_CompletesWithoutException()
    {
        // Arrange
        var mockLogger = Substitute.For<ILogger>();
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            SubscriptionId = "test-sub-id",
            ClientAppId = "test-client-app-id"
        };
        var listOfChecks = new List<IRequirementCheck> { new AlwaysPassRequirementCheck() };

        // Act & Assert - Should complete without throwing exceptions
        var result = await RequirementsSubcommand.RunRequirementChecksAsync(listOfChecks, config, mockLogger, null);
        result.Should().BeTrue();
    }

    #endregion
}

