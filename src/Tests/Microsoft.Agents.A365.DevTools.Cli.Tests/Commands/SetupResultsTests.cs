// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Unit tests for SetupResults and BlueprintCreationResult classes
/// </summary>
public class SetupResultsTests
{
    #region SetupResults Tests

    [Fact]
    public void SetupResults_DefaultValues_ShouldBeFalse()
    {
        // Arrange & Act
        var results = new SetupResults();

        // Assert
        results.InfrastructureCreated.Should().BeFalse();
        results.BlueprintCreated.Should().BeFalse();
        results.McpPermissionsConfigured.Should().BeFalse();
        results.BotApiPermissionsConfigured.Should().BeFalse();
        results.MessagingEndpointRegistered.Should().BeFalse();
        results.InheritablePermissionsConfigured.Should().BeFalse();
        results.GraphInheritablePermissionsConfigured.Should().BeFalse();
        results.GraphInheritablePermissionsError.Should().BeNull();
    }

    [Fact]
    public void SetupResults_GraphInheritablePermissionsError_CanBeSet()
    {
        // Arrange
        var results = new SetupResults();

        // Act
        results.GraphInheritablePermissionsError = "Test error message";

        // Assert
        results.GraphInheritablePermissionsError.Should().Be("Test error message");
    }

    [Fact]
    public void SetupResults_ErrorsCollection_ShouldBeEmptyByDefault()
    {
        // Arrange & Act
        var results = new SetupResults();

        // Assert
        results.Errors.Should().NotBeNull();
        results.Errors.Should().BeEmpty();
        results.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void SetupResults_WarningsCollection_ShouldBeEmptyByDefault()
    {
        // Arrange & Act
        var results = new SetupResults();

        // Assert
        results.Warnings.Should().NotBeNull();
        results.Warnings.Should().BeEmpty();
        results.HasWarnings.Should().BeFalse();
    }

    [Fact]
    public void SetupResults_HasErrors_ShouldReturnTrueWhenErrorsExist()
    {
        // Arrange
        var results = new SetupResults();

        // Act
        results.Errors.Add("Test error");

        // Assert
        results.HasErrors.Should().BeTrue();
    }

    [Fact]
    public void SetupResults_HasWarnings_ShouldReturnTrueWhenWarningsExist()
    {
        // Arrange
        var results = new SetupResults();

        // Act
        results.Warnings.Add("Test warning");

        // Assert
        results.HasWarnings.Should().BeTrue();
    }

    [Fact]
    public void SetupResults_GraphInheritablePermissionsError_WithWarning_ShouldTrackBoth()
    {
        // Arrange
        var results = new SetupResults();

        // Act - simulate the AllSubcommand behavior when Graph inheritable permissions fail
        results.GraphInheritablePermissionsError = "Test error message";
        results.Warnings.Add($"Microsoft Graph inheritable permissions: {results.GraphInheritablePermissionsError}");

        // Assert
        results.GraphInheritablePermissionsError.Should().NotBeNull();
        results.HasWarnings.Should().BeTrue();
        results.Warnings.Should().Contain(w => w.Contains("Microsoft Graph inheritable permissions"));
    }

    #endregion

    #region BlueprintCreationResult Tests

    [Fact]
    public void BlueprintCreationResult_DefaultValues_ShouldBeFalse()
    {
        // Arrange & Act
        var result = new BlueprintCreationResult();

        // Assert
        result.BlueprintCreated.Should().BeFalse();
        result.BlueprintAlreadyExisted.Should().BeFalse();
        result.EndpointRegistered.Should().BeFalse();
        result.EndpointAlreadyExisted.Should().BeFalse();
        result.EndpointRegistrationAttempted.Should().BeFalse();
        result.GraphInheritablePermissionsFailed.Should().BeFalse();
        result.GraphInheritablePermissionsError.Should().BeNull();
    }

    [Fact]
    public void BlueprintCreationResult_GraphInheritablePermissionsFailed_CanBeSet()
    {
        // Arrange & Act
        var result = new BlueprintCreationResult
        {
            BlueprintCreated = true,
            GraphInheritablePermissionsFailed = true,
            GraphInheritablePermissionsError = "Failed to set inheritable permissions: Insufficient permissions"
        };

        // Assert
        result.BlueprintCreated.Should().BeTrue();
        result.GraphInheritablePermissionsFailed.Should().BeTrue();
        result.GraphInheritablePermissionsError.Should().Be("Failed to set inheritable permissions: Insufficient permissions");
    }

    [Fact]
    public void BlueprintCreationResult_SuccessfulBlueprintWithGraphPermissionsFailure_ShouldAllowPartialSuccess()
    {
        // Arrange & Act
        // This simulates the scenario where blueprint creation succeeds but Graph inheritable permissions fail
        var result = new BlueprintCreationResult
        {
            BlueprintCreated = true,
            BlueprintAlreadyExisted = false,
            EndpointRegistered = true,
            EndpointAlreadyExisted = false,
            EndpointRegistrationAttempted = true,
            GraphInheritablePermissionsFailed = true,
            GraphInheritablePermissionsError = "AgentIdentityBlueprint.UpdateAuthProperties.All permission missing"
        };

        // Assert - Blueprint should still be considered created even with Graph permission failure
        result.BlueprintCreated.Should().BeTrue();
        result.GraphInheritablePermissionsFailed.Should().BeTrue();
        result.GraphInheritablePermissionsError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void BlueprintCreationResult_SuccessfulBlueprintWithGraphPermissionsSuccess_ShouldNotHaveError()
    {
        // Arrange & Act
        var result = new BlueprintCreationResult
        {
            BlueprintCreated = true,
            BlueprintAlreadyExisted = false,
            EndpointRegistered = true,
            EndpointAlreadyExisted = false,
            EndpointRegistrationAttempted = true,
            GraphInheritablePermissionsFailed = false,
            GraphInheritablePermissionsError = null
        };

        // Assert
        result.BlueprintCreated.Should().BeTrue();
        result.GraphInheritablePermissionsFailed.Should().BeFalse();
        result.GraphInheritablePermissionsError.Should().BeNull();
    }

    [Fact]
    public void BlueprintCreationResult_ExistingBlueprint_WithGraphPermissionsFailure_ShouldTrackProperly()
    {
        // Arrange & Act
        // This simulates the scenario where an existing blueprint is found but Graph inheritable permissions fail
        var result = new BlueprintCreationResult
        {
            BlueprintCreated = true,
            BlueprintAlreadyExisted = true,
            EndpointRegistered = false,
            EndpointAlreadyExisted = true,
            EndpointRegistrationAttempted = true,
            GraphInheritablePermissionsFailed = true,
            GraphInheritablePermissionsError = "Consent not granted for required scopes"
        };

        // Assert
        result.BlueprintCreated.Should().BeTrue();
        result.BlueprintAlreadyExisted.Should().BeTrue();
        result.GraphInheritablePermissionsFailed.Should().BeTrue();
    }

    #endregion
}
