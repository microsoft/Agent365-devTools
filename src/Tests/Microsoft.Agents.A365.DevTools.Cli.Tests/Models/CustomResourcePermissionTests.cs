// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Models;

public class CustomResourcePermissionTests
{
    [Fact]
    public void Validate_ValidPermission_ReturnsTrue()
    {
        // Arrange
        var permission = new CustomResourcePermission
        {
            ResourceAppId = "00000003-0000-0000-c000-000000000000",
            ResourceName = "Microsoft Graph",
            Scopes = new List<string> { "User.Read", "Mail.Send" }
        };

        // Act
        var (isValid, errors) = permission.Validate();

        // Assert
        isValid.Should().BeTrue();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_EmptyResourceAppId_ReturnsError()
    {
        // Arrange
        var permission = new CustomResourcePermission
        {
            ResourceAppId = "",
            ResourceName = "Test API",
            Scopes = new List<string> { "read" }
        };

        // Act
        var (isValid, errors) = permission.Validate();

        // Assert
        isValid.Should().BeFalse();
        errors.Should().Contain("resourceAppId is required");
    }

    [Fact]
    public void Validate_InvalidGuidFormat_ReturnsError()
    {
        // Arrange
        var permission = new CustomResourcePermission
        {
            ResourceAppId = "not-a-valid-guid",
            ResourceName = "Test API",
            Scopes = new List<string> { "read" }
        };

        // Act
        var (isValid, errors) = permission.Validate();

        // Assert
        isValid.Should().BeFalse();
        errors.Should().ContainSingle(e => e.Contains("resourceAppId must be a valid GUID format"));
    }

    [Fact]
    public void Validate_NullResourceName_IsValid()
    {
        // Arrange
        var permission = new CustomResourcePermission
        {
            ResourceAppId = "00000003-0000-0000-c000-000000000000",
            ResourceName = null,
            Scopes = new List<string> { "read" }
        };

        // Act
        var (isValid, errors) = permission.Validate();

        // Assert
        isValid.Should().BeTrue();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_EmptyResourceName_IsValid()
    {
        // Arrange
        var permission = new CustomResourcePermission
        {
            ResourceAppId = "00000003-0000-0000-c000-000000000000",
            ResourceName = "",
            Scopes = new List<string> { "read" }
        };

        // Act
        var (isValid, errors) = permission.Validate();

        // Assert
        isValid.Should().BeTrue();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_EmptyScopesList_ReturnsError()
    {
        // Arrange
        var permission = new CustomResourcePermission
        {
            ResourceAppId = "00000003-0000-0000-c000-000000000000",
            ResourceName = "Test API",
            Scopes = new List<string>()
        };

        // Act
        var (isValid, errors) = permission.Validate();

        // Assert
        isValid.Should().BeFalse();
        errors.Should().Contain("At least one scope is required");
    }

    [Fact]
    public void Validate_NullScopesList_ReturnsError()
    {
        // Arrange
        var permission = new CustomResourcePermission
        {
            ResourceAppId = "00000003-0000-0000-c000-000000000000",
            ResourceName = "Test API",
            Scopes = null!
        };

        // Act
        var (isValid, errors) = permission.Validate();

        // Assert
        isValid.Should().BeFalse();
        errors.Should().Contain("At least one scope is required");
    }

    [Fact]
    public void Validate_ScopesContainEmptyString_ReturnsError()
    {
        // Arrange
        var permission = new CustomResourcePermission
        {
            ResourceAppId = "00000003-0000-0000-c000-000000000000",
            ResourceName = "Test API",
            Scopes = new List<string> { "User.Read", "", "Mail.Send" }
        };

        // Act
        var (isValid, errors) = permission.Validate();

        // Assert
        isValid.Should().BeFalse();
        errors.Should().ContainSingle(e => e.Contains("Scopes cannot contain empty values"));
    }

    [Fact]
    public void Validate_ScopesContainWhitespace_ReturnsError()
    {
        // Arrange
        var permission = new CustomResourcePermission
        {
            ResourceAppId = "00000003-0000-0000-c000-000000000000",
            ResourceName = "Test API",
            Scopes = new List<string> { "User.Read", "   ", "Mail.Send" }
        };

        // Act
        var (isValid, errors) = permission.Validate();

        // Assert
        isValid.Should().BeFalse();
        errors.Should().ContainSingle(e => e.Contains("Scopes cannot contain empty values"));
    }

    [Fact]
    public void Validate_DuplicateScopes_ReturnsError()
    {
        // Arrange
        var permission = new CustomResourcePermission
        {
            ResourceAppId = "00000003-0000-0000-c000-000000000000",
            ResourceName = "Test API",
            Scopes = new List<string> { "User.Read", "Mail.Send", "User.Read" }
        };

        // Act
        var (isValid, errors) = permission.Validate();

        // Assert
        isValid.Should().BeFalse();
        errors.Should().ContainSingle(e => e.Contains("Duplicate scopes found: User.Read"));
    }

    [Fact]
    public void Validate_DuplicateScopesCaseInsensitive_ReturnsError()
    {
        // Arrange
        var permission = new CustomResourcePermission
        {
            ResourceAppId = "00000003-0000-0000-c000-000000000000",
            ResourceName = "Test API",
            Scopes = new List<string> { "User.Read", "mail.send", "MAIL.SEND" }
        };

        // Act
        var (isValid, errors) = permission.Validate();

        // Assert
        isValid.Should().BeFalse();
        errors.Should().ContainSingle(e => e.Contains("Duplicate scopes found"));
    }

    [Fact]
    public void Validate_ScopesWithWhitespaceAreTrimmed_ReturnsError()
    {
        // Arrange
        var permission = new CustomResourcePermission
        {
            ResourceAppId = "00000003-0000-0000-c000-000000000000",
            ResourceName = "Test API",
            Scopes = new List<string> { " User.Read ", "User.Read" }
        };

        // Act
        var (isValid, errors) = permission.Validate();

        // Assert
        isValid.Should().BeFalse();
        errors.Should().ContainSingle(e => e.Contains("Duplicate scopes found"));
    }

    [Fact]
    public void Validate_MultipleErrors_ReturnsAllErrors()
    {
        // Arrange
        var permission = new CustomResourcePermission
        {
            ResourceAppId = "invalid-guid",
            ResourceName = null,  // ResourceName is optional now
            Scopes = new List<string>()
        };

        // Act
        var (isValid, errors) = permission.Validate();

        // Assert
        isValid.Should().BeFalse();
        errors.Should().HaveCount(2);
        errors.Should().Contain(e => e.Contains("resourceAppId must be a valid GUID"));
        errors.Should().Contain("At least one scope is required");
    }

    [Theory]
    [InlineData("00000003-0000-0000-c000-000000000000")]
    [InlineData("12345678-1234-1234-1234-123456789012")]
    [InlineData("{ABCDEF01-2345-6789-ABCD-EF0123456789}")]
    public void Validate_ValidGuidFormats_Succeeds(string guid)
    {
        // Arrange
        var permission = new CustomResourcePermission
        {
            ResourceAppId = guid,
            ResourceName = "Test API",
            Scopes = new List<string> { "read" }
        };

        // Act
        var (isValid, errors) = permission.Validate();

        // Assert
        isValid.Should().BeTrue();
        errors.Should().BeEmpty();
    }
}
