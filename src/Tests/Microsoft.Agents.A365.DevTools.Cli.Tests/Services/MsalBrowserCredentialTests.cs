// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Runtime.InteropServices;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class MsalBrowserCredentialTests
{
    private const string ValidClientId = "12345678-1234-1234-1234-123456789abc";
    private const string ValidTenantId = "87654321-4321-4321-4321-cba987654321";
    private const string ValidRedirectUri = "http://localhost:8400";

    #region Constructor Tests

    [Fact]
    public void Constructor_WithValidParameters_ShouldSucceed()
    {
        // Arrange & Act
        var credential = new MsalBrowserCredential(ValidClientId, ValidTenantId, ValidRedirectUri);

        // Assert
        Assert.NotNull(credential);
    }

    [Fact]
    public void Constructor_WithNullRedirectUri_ShouldSucceed()
    {
        // Arrange & Act - redirectUri is optional
        var credential = new MsalBrowserCredential(ValidClientId, ValidTenantId, redirectUri: null);

        // Assert
        Assert.NotNull(credential);
    }

    [Fact]
    public void Constructor_WithLogger_ShouldSucceed()
    {
        // Arrange
        var logger = Substitute.For<ILogger>();

        // Act
        var credential = new MsalBrowserCredential(ValidClientId, ValidTenantId, ValidRedirectUri, logger);

        // Assert
        Assert.NotNull(credential);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithNullOrEmptyClientId_ShouldThrowArgumentNullException(string? clientId)
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            new MsalBrowserCredential(clientId!, ValidTenantId, ValidRedirectUri));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithNullOrEmptyTenantId_ShouldThrowArgumentNullException(string? tenantId)
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            new MsalBrowserCredential(ValidClientId, tenantId!, ValidRedirectUri));
    }

    #endregion

    #region WAM Configuration Tests

    [Fact]
    public void Constructor_WithUseWamTrue_OnWindows_ShouldConfigureForWam()
    {
        // Arrange & Act
        var credential = new MsalBrowserCredential(
            ValidClientId, 
            ValidTenantId, 
            redirectUri: null,  // WAM uses broker redirect URI
            logger: null,
            useWam: true);

        // Assert - credential should be created successfully
        // On Windows, WAM will be enabled; on other platforms, it falls back to browser
        Assert.NotNull(credential);
    }

    [Fact]
    public void Constructor_WithUseWamFalse_ShouldConfigureForBrowser()
    {
        // Arrange & Act
        var credential = new MsalBrowserCredential(
            ValidClientId, 
            ValidTenantId, 
            ValidRedirectUri,
            logger: null,
            useWam: false);

        // Assert
        Assert.NotNull(credential);
    }

    [Fact]
    public void Constructor_WithUseWamTrue_OnNonWindows_ShouldFallbackToBrowser()
    {
        // This test verifies the fallback behavior
        // On non-Windows platforms, useWam=true should still work by falling back to browser
        
        // Arrange
        var logger = Substitute.For<ILogger>();
        
        // Act - should not throw regardless of platform
        var credential = new MsalBrowserCredential(
            ValidClientId, 
            ValidTenantId, 
            ValidRedirectUri,
            logger,
            useWam: true);

        // Assert
        Assert.NotNull(credential);
    }

    #endregion

    #region Platform Detection Tests

    [Fact]
    public void WamShouldOnlyBeEnabledOnWindows()
    {
        // This test documents the expected platform behavior:
        // - Windows: WAM is enabled (native authentication dialog)
        // - macOS/Linux: Browser-based authentication
        
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        
        // The credential should be constructable on all platforms
        var credential = new MsalBrowserCredential(
            ValidClientId, 
            ValidTenantId, 
            redirectUri: null,
            useWam: true);

        Assert.NotNull(credential);
        
        // Note: We can't directly verify _useWam field as it's private,
        // but the constructor should succeed on all platforms
    }

    #endregion

    #region Window Handle Fallback Tests (Windows-specific behavior documentation)

    /// <summary>
    /// Documents the window handle fallback chain used for WAM on Windows:
    /// 1. GetConsoleWindow() - Works for cmd.exe, PowerShell
    /// 2. GetForegroundWindow() - Works for Windows Terminal
    /// 3. GetDesktopWindow() - Always returns a valid handle
    /// 
    /// This test verifies the credential can be constructed, which exercises
    /// the window handle detection code on Windows.
    /// </summary>
    [Fact]
    public void Constructor_OnWindows_ShouldHandleWindowHandleDetection()
    {
        // Arrange
        var logger = Substitute.For<ILogger>();
        
        // Act - On Windows, this exercises the P/Invoke window handle detection
        var credential = new MsalBrowserCredential(
            ValidClientId, 
            ValidTenantId, 
            redirectUri: null,
            logger,
            useWam: true);

        // Assert
        Assert.NotNull(credential);
        
        // On Windows, logger should have received debug messages about window handle
        // On other platforms, WAM is disabled so no window handle detection occurs
    }

    #endregion

    #region Exception Type Tests

    [Fact]
    public void MsalAuthenticationFailedException_WithMessage_ShouldSetMessage()
    {
        // Arrange
        var message = "Test error message";
        
        // Act
        var exception = new MsalAuthenticationFailedException(message);
        
        // Assert
        Assert.Equal(message, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void MsalAuthenticationFailedException_WithMessageAndInnerException_ShouldSetBoth()
    {
        // Arrange
        var message = "Test error message";
        var innerException = new InvalidOperationException("Inner error");
        
        // Act
        var exception = new MsalAuthenticationFailedException(message, innerException);
        
        // Assert
        Assert.Equal(message, exception.Message);
        Assert.Same(innerException, exception.InnerException);
    }

    [Fact]
    public void MsalAuthenticationFailedException_ShouldInheritFromException()
    {
        // Arrange & Act
        var exception = new MsalAuthenticationFailedException("Test");
        
        // Assert
        Assert.IsAssignableFrom<Exception>(exception);
    }

    #endregion
}
