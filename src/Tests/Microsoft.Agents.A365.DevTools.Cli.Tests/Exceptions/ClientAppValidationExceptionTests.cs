// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Exceptions;

/// <summary>
/// Unit tests for ClientAppValidationException factory methods.
/// </summary>
public class ClientAppValidationExceptionTests
{
    private const string TestClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6";
    private const string TestTenantId = "12345678-1234-1234-1234-123456789012";

    #region AppNotFound Tests

    [Fact]
    public void AppNotFound_CreatesExceptionWithCorrectProperties()
    {
        // Act
        var exception = ClientAppValidationException.AppNotFound(TestClientAppId, TestTenantId);

        // Assert
        exception.Should().NotBeNull();
        exception.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        exception.IssueDescription.Should().Be("Client app not found in tenant");
        exception.ErrorDetails.Should().HaveCount(2);
        exception.ErrorDetails[0].Should().Contain(TestClientAppId);
        exception.ErrorDetails[0].Should().Contain(TestTenantId);
        exception.MitigationSteps.Should().HaveCount(4);
        exception.MitigationSteps.Should().Contain(s => s.Contains("a365.config.json"));
        exception.Context.Should().ContainKey("clientAppId");
        exception.Context.Should().ContainKey("tenantId");
        exception.Context["clientAppId"].Should().Be(TestClientAppId);
        exception.Context["tenantId"].Should().Be(TestTenantId);
    }

    [Fact]
    public void AppNotFound_IncludesDocumentationReference()
    {
        // Act
        var exception = ClientAppValidationException.AppNotFound(TestClientAppId, TestTenantId);

        // Assert
        exception.MitigationSteps.Should().Contain(s => 
            s.Contains(ConfigConstants.Agent365CliDocumentationUrl));
    }

    #endregion

    #region MissingPermissions Tests

    [Fact]
    public void MissingPermissions_WithSinglePermission_CreatesExceptionWithCorrectProperties()
    {
        // Arrange
        var missingPermissions = new List<string> { "Application.ReadWrite.All" };

        // Act
        var exception = ClientAppValidationException.MissingPermissions(TestClientAppId, missingPermissions);

        // Assert
        exception.Should().NotBeNull();
        exception.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        exception.IssueDescription.Should().Be("Client app is missing required API permissions");
        exception.ErrorDetails.Should().HaveCount(1);
        exception.ErrorDetails[0].Should().Contain("Application.ReadWrite.All");
        exception.MitigationSteps.Should().HaveCount(5);
        exception.MitigationSteps.Should().Contain(s => s.Contains("Azure Portal"));
        exception.Context.Should().ContainKey("clientAppId");
        exception.Context.Should().ContainKey("missingPermissions");
        exception.Context["clientAppId"].Should().Be(TestClientAppId);
    }

    [Fact]
    public void MissingPermissions_WithMultiplePermissions_ListsAllMissingPermissions()
    {
        // Arrange
        var missingPermissions = new List<string> 
        { 
            "Application.ReadWrite.All", 
            "Directory.Read.All",
            "DelegatedPermissionGrant.ReadWrite.All"
        };

        // Act
        var exception = ClientAppValidationException.MissingPermissions(TestClientAppId, missingPermissions);

        // Assert
        exception.ErrorDetails[0].Should().Contain("Application.ReadWrite.All");
        exception.ErrorDetails[0].Should().Contain("Directory.Read.All");
        exception.ErrorDetails[0].Should().Contain("DelegatedPermissionGrant.ReadWrite.All");
        exception.Context["missingPermissions"].Should().Contain("Application.ReadWrite.All");
        exception.Context["missingPermissions"].Should().Contain("Directory.Read.All");
        exception.Context["missingPermissions"].Should().Contain("DelegatedPermissionGrant.ReadWrite.All");
    }

    [Fact]
    public void MissingPermissions_IncludesDetailedSetupInstructions()
    {
        // Arrange
        var missingPermissions = new List<string> { "Application.ReadWrite.All" };

        // Act
        var exception = ClientAppValidationException.MissingPermissions(TestClientAppId, missingPermissions);

        // Assert
        exception.MitigationSteps.Should().Contain(s => s.Contains("API permissions"));
        exception.MitigationSteps.Should().Contain(s => s.Contains("admin consent"));
        exception.MitigationSteps.Should().Contain(s => 
            s.Contains(ConfigConstants.Agent365CliDocumentationUrl));
    }

    #endregion

    #region MissingAdminConsent Tests

    [Fact]
    public void MissingAdminConsent_CreatesExceptionWithCorrectProperties()
    {
        // Act
        var exception = ClientAppValidationException.MissingAdminConsent(TestClientAppId);

        // Assert
        exception.Should().NotBeNull();
        exception.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        exception.IssueDescription.Should().Be("Admin consent not granted for client app");
        exception.ErrorDetails.Should().HaveCount(3, because: "MissingAdminConsent includes: (1) permissions-configured check, (2) per-user consent warning, (3) Global Administrator requirement");
        exception.ErrorDetails[0].Should().Contain("permissions are configured");
        exception.ErrorDetails[1].Should().Contain("per-user consent");
        exception.ErrorDetails[2].Should().Contain("Global Administrator");
        exception.MitigationSteps.Should().HaveCount(4, because: "MissingAdminConsent provides 4 mitigation steps: run setup requirements, run setup admin, share consent URL, and contact IT admin");
        exception.Context.Should().ContainKey("clientAppId");
        exception.Context["clientAppId"].Should().Be(TestClientAppId);
    }

    [Fact]
    public void MissingAdminConsent_IncludesConsentGrantInstructions()
    {
        // Act
        var exception = ClientAppValidationException.MissingAdminConsent(TestClientAppId);

        // Assert
        exception.MitigationSteps.Should().Contain(s => s.Contains("Grant admin consent"));
        exception.MitigationSteps.Should().Contain(s => 
            s.Contains(ConfigConstants.Agent365CliDocumentationUrl));
    }

    [Fact]
    public void BuildAdminConsentUrl_EncodesRedirectUri()
    {
        // Act
        var consentUrl = ClientAppValidationException.BuildAdminConsentUrl(TestClientAppId, TestTenantId);

        // Assert
        consentUrl.Should().NotBeNull();
        consentUrl.Should().Contain($"client_id={TestClientAppId}", because: "the client ID must be preserved in the admin consent URL query string");
        consentUrl.Should().Contain(
            $"redirect_uri={Uri.EscapeDataString("https://login.microsoftonline.com/common/oauth2/nativeclient")}",
            because: "redirect_uri is a URL-valued query parameter and must be encoded so the consent link remains valid when copied through shells, logs, and browsers");
        consentUrl.Should().NotContain(
            "&redirect_uri=https://login.microsoftonline.com/common/oauth2/nativeclient",
            because: "an unescaped redirect URI contains reserved characters that can corrupt the admin consent query string");
    }

    #endregion

    #region ValidationFailed Tests

    [Fact]
    public void ValidationFailed_WithClientAppId_CreatesExceptionWithCorrectProperties()
    {
        // Arrange
        var issueDescription = "Custom validation issue";
        var errorDetails = new List<string> { "Error 1", "Error 2" };

        // Act
        var exception = ClientAppValidationException.ValidationFailed(
            issueDescription, 
            errorDetails, 
            TestClientAppId);

        // Assert
        exception.Should().NotBeNull();
        exception.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        exception.IssueDescription.Should().Be(issueDescription);
        exception.ErrorDetails.Should().HaveCount(2);
        exception.ErrorDetails[0].Should().Be("Error 1");
        exception.ErrorDetails[1].Should().Be("Error 2");
        exception.MitigationSteps.Should().HaveCount(4);
        exception.Context.Should().ContainKey("clientAppId");
        exception.Context["clientAppId"].Should().Be(TestClientAppId);
    }

    [Fact]
    public void ValidationFailed_WithoutClientAppId_CreatesExceptionWithoutContext()
    {
        // Arrange
        var issueDescription = "Custom validation issue";
        var errorDetails = new List<string> { "Error 1" };

        // Act
        var exception = ClientAppValidationException.ValidationFailed(
            issueDescription, 
            errorDetails, 
            clientAppId: null);

        // Assert
        exception.Should().NotBeNull();
        exception.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        exception.IssueDescription.Should().Be(issueDescription);
        exception.ErrorDetails.Should().HaveCount(1);
        exception.Context.Should().BeEmpty();
    }

    [Fact]
    public void ValidationFailed_IncludesGenericMitigationSteps()
    {
        // Arrange
        var issueDescription = "Custom validation issue";
        var errorDetails = new List<string> { "Error 1" };

        // Act
        var exception = ClientAppValidationException.ValidationFailed(
            issueDescription, 
            errorDetails);

        // Assert
        exception.MitigationSteps.Should().Contain(s => s.Contains("az login"));
        exception.MitigationSteps.Should().Contain(s => s.Contains("Azure Portal"));
        exception.MitigationSteps.Should().Contain(s => 
            s.Contains(ConfigConstants.Agent365CliDocumentationUrl));
    }

    #endregion

    #region General Exception Properties Tests

    [Fact]
    public void AllFactoryMethods_UseConsistentErrorCode()
    {
        // Arrange & Act
        var appNotFound = ClientAppValidationException.AppNotFound(TestClientAppId, TestTenantId);
        var missingPermissions = ClientAppValidationException.MissingPermissions(
            TestClientAppId, 
            new List<string> { "Application.ReadWrite.All" });
        var missingConsent = ClientAppValidationException.MissingAdminConsent(TestClientAppId);
        var validationFailed = ClientAppValidationException.ValidationFailed(
            "Issue", 
            new List<string> { "Detail" });

        // Assert
        appNotFound.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        missingPermissions.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        missingConsent.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        validationFailed.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
    }

    [Fact]
    public void AllFactoryMethods_ProvideNonEmptyMitigationSteps()
    {
        // Arrange & Act
        var appNotFound = ClientAppValidationException.AppNotFound(TestClientAppId, TestTenantId);
        var missingPermissions = ClientAppValidationException.MissingPermissions(
            TestClientAppId, 
            new List<string> { "Application.ReadWrite.All" });
        var missingConsent = ClientAppValidationException.MissingAdminConsent(TestClientAppId);
        var validationFailed = ClientAppValidationException.ValidationFailed(
            "Issue", 
            new List<string> { "Detail" });

        // Assert
        appNotFound.MitigationSteps.Should().NotBeEmpty();
        missingPermissions.MitigationSteps.Should().NotBeEmpty();
        missingConsent.MitigationSteps.Should().NotBeEmpty();
        validationFailed.MitigationSteps.Should().NotBeEmpty();
    }

    #endregion
}
