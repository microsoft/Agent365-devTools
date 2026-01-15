// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class InteractiveGraphAuthServiceTests
{
    /// <summary>
    /// This test ensures that all required Graph API scopes are present in the RequiredScopes array.
    /// If any of these scopes are removed, the test will fail to prevent accidental permission reduction.
    /// 
    /// These scopes are critical for Agent Blueprint creation and inheritable permissions configuration:
    /// - Application.ReadWrite.All: Required for creating and managing app registrations
    /// - AgentIdentityBlueprint.ReadWrite.All: Required for Agent Blueprint operations
    /// - AgentIdentityBlueprint.UpdateAuthProperties.All: Required for updating blueprint auth properties
    /// - User.Read: Basic user profile access for authentication context
    /// </summary>
    [Fact]
    public void RequiredScopes_MustContainAllEssentialPermissions()
    {
        // Arrange
        var expectedScopes = new[]
        {
            "https://graph.microsoft.com/Application.ReadWrite.All",
            "https://graph.microsoft.com/AgentIdentityBlueprint.ReadWrite.All", 
            "https://graph.microsoft.com/AgentIdentityBlueprint.UpdateAuthProperties.All",
            "https://graph.microsoft.com/User.Read"
        };

        var logger = Substitute.For<ILogger<InteractiveGraphAuthService>>();
        var service = new InteractiveGraphAuthService(logger, "12345678-1234-1234-1234-123456789abc");

        // Act - Use reflection to access the private static RequiredScopes field
        var requiredScopesField = typeof(InteractiveGraphAuthService)
            .GetField("RequiredScopes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        Assert.NotNull(requiredScopesField);
        var actualScopes = (string[])requiredScopesField.GetValue(null)!;

        // Assert
        Assert.NotNull(actualScopes);
        Assert.Equal(expectedScopes.Length, actualScopes.Length);
        
        foreach (var expectedScope in expectedScopes)
        {
            Assert.Contains(expectedScope, actualScopes);
        }
    }

    [Fact]
    public void Constructor_WithValidGuidClientAppId_ShouldSucceed()
    {
        // Arrange
        var logger = Substitute.For<ILogger<InteractiveGraphAuthService>>();
        var validGuid = "12345678-1234-1234-1234-123456789abc";

        // Act & Assert - Should not throw
        var service = new InteractiveGraphAuthService(logger, validGuid);
        Assert.NotNull(service);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithNullOrEmptyClientAppId_ShouldThrowArgumentNullException(string? clientAppId)
    {
        // Arrange
        var logger = Substitute.For<ILogger<InteractiveGraphAuthService>>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new InteractiveGraphAuthService(logger, clientAppId!));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    [InlineData("invalid-format")]
    public void Constructor_WithInvalidGuidClientAppId_ShouldThrowArgumentException(string clientAppId)
    {
        // Arrange
        var logger = Substitute.For<ILogger<InteractiveGraphAuthService>>();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => new InteractiveGraphAuthService(logger, clientAppId));
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrowArgumentNullException()
    {
        // Arrange
        var validGuid = "12345678-1234-1234-1234-123456789abc";

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new InteractiveGraphAuthService(null!, validGuid));
    }

    #region WAM Configuration Tests (GitHub Issues #146 and #151)

    /// <summary>
    /// Verifies that MsalBrowserCredential can be constructed with valid parameters.
    /// </summary>
    [Fact]
    public void MsalBrowserCredential_WithValidParameters_ShouldConstruct()
    {
        // Arrange
        var clientId = "12345678-1234-1234-1234-123456789abc";
        var tenantId = "87654321-4321-4321-4321-cba987654321";
        var redirectUri = "http://localhost:8400";

        // Act
        var credential = new MsalBrowserCredential(clientId, tenantId, redirectUri);

        // Assert
        Assert.NotNull(credential);
    }

    /// <summary>
    /// Verifies that MsalBrowserCredential throws on null client ID.
    /// </summary>
    [Fact]
    public void MsalBrowserCredential_WithNullClientId_ShouldThrow()
    {
        // Arrange
        var tenantId = "87654321-4321-4321-4321-cba987654321";

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new MsalBrowserCredential(null!, tenantId));
    }

    /// <summary>
    /// Verifies that MsalBrowserCredential throws on null tenant ID.
    /// </summary>
    [Fact]
    public void MsalBrowserCredential_WithNullTenantId_ShouldThrow()
    {
        // Arrange
        var clientId = "12345678-1234-1234-1234-123456789abc";

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new MsalBrowserCredential(clientId, null!));
    }

    /// <summary>
    /// Integration test for WAM configuration - can be run manually to verify the fix.
    /// This test is skipped by default in automated runs as it requires user interaction.
    /// 
    /// To run manually: dotnet test --filter "Category=Integration"
    /// </summary>
    [Fact(Skip = "Integration test requires manual verification on Windows 10/11")]
    [Trait("Category", "Integration")]
    public void MsalBrowserCredential_ManualTest_ShouldUseWAMOnWindows()
    {
        // This test is marked as Integration and should be skipped in CI/CD pipelines.
        // To verify the WAM fix works:
        //
        // 1. Run this command on Windows 10/11:
        //    a365 setup all
        //
        // 2. Expected behavior on Windows:
        //    - Native WAM dialog appears (Windows Account Manager)
        //    - No browser window opens
        //    - WAM broker redirect URI auto-configured: ms-appx-web://microsoft.aad.brokerplugin/{clientId}
        //    - No "window handle" error
        //    - No AADSTS50011 redirect URI mismatch error
        //
        // 3. Expected behavior on macOS/Linux:
        //    - System browser opens for authentication
        //    - Uses localhost redirect URI
        //
        // 4. The implementation uses MSAL with:
        //    PublicClientApplicationBuilder.Create(clientId)
        //        .WithAuthority(...)
        //        .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))  // WAM enabled
        //        .WithParentActivityOrWindow(() => windowHandle)  // P/Invoke for console apps
        //        .Build()
        
        Assert.True(true, "Manual verification required");
    }

    #endregion
}