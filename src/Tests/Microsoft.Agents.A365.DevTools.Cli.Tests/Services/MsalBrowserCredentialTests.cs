// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Azure.Core;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
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

    [Theory]
    [InlineData("https://login.microsoftonline.us/tenant-id")]
    [InlineData("https://login.microsoftonline.com/tenant-id")]
    public void Constructor_WithCustomAuthority_ShouldSucceed(string authority)
    {
        // Custom authority is used for government clouds (gcch/dod) where
        // AzureCloudInstance.AzurePublic is not appropriate.
        var credential = new MsalBrowserCredential(
            ValidClientId, ValidTenantId, redirectUri: null, authority: authority);

        Assert.NotNull(credential);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithNullOrWhitespaceAuthority_UsesDefaultAzurePublicAuthority(string? authority)
    {
        // Null/whitespace authority falls back to AzureCloudInstance.AzurePublic + tenantId.
        var credential = new MsalBrowserCredential(
            ValidClientId, ValidTenantId, redirectUri: null, authority: authority);

        Assert.NotNull(credential);
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
    public void SelectAuthenticationMode_FirstPartyAppOnWindows_UsesWam()
    {
        var mode = MsalBrowserCredential.SelectAuthenticationMode(
            AuthenticationConstants.WellKnownClientAppId,
            useWam: true,
            isWindows: true);

        Assert.Equal(MsalBrowserCredential.InteractiveAuthenticationMode.Wam, mode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SelectAuthenticationMode_FirstPartyAppWithoutWam_UsesDeviceCode(
        bool useWam,
        bool isWindows)
    {
        var mode = MsalBrowserCredential.SelectAuthenticationMode(
            AuthenticationConstants.WellKnownClientAppId,
            useWam,
            isWindows);

        Assert.Equal(
            MsalBrowserCredential.InteractiveAuthenticationMode.DeviceCode,
            mode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SelectAuthenticationMode_CustomAppWithoutWam_UsesSystemBrowser(
        bool useWam,
        bool isWindows)
    {
        var mode = MsalBrowserCredential.SelectAuthenticationMode(
            ValidClientId,
            useWam,
            isWindows);

        Assert.Equal(
            MsalBrowserCredential.InteractiveAuthenticationMode.SystemBrowser,
            mode);
    }

    [Fact]
    public void SelectAuthenticationMode_CustomAppOnWindowsWithWam_PreservesWam()
    {
        var mode = MsalBrowserCredential.SelectAuthenticationMode(
            ValidClientId,
            useWam: true,
            isWindows: true);

        mode.Should().Be(
            MsalBrowserCredential.InteractiveAuthenticationMode.Wam,
            because: "tenant-owned custom apps already use WAM on Windows and the FPA fix must not change that behavior");
    }

    [Fact]
    public async Task GetTokenAsync_FirstPartyWithoutWam_UsesDeviceCodeOnly()
    {
        var acquirer = CreateEmptyAcquirer();
        var expected = CreateAccessToken();
        acquirer.DeviceCodeResult = expected;
        var credential = new MsalBrowserCredential(
            AuthenticationConstants.WellKnownClientAppId,
            ValidTenantId,
            MsalBrowserCredential.InteractiveAuthenticationMode.DeviceCode,
            acquirer);

        var result = await credential.GetTokenAsync(
            new TokenRequestContext(["https://graph.microsoft.com/Application.Read.All"]),
            CancellationToken.None);

        result.Token.Should().Be(expected.Token,
            because: "the FPA system-browser request is rejected with AADSTS70007 in WSL/non-Windows environments, while device code avoids that response-mode incompatibility");
        acquirer.DeviceCodeCalls.Should().Be(1,
            because: "the non-WAM FPA path must invoke device code exactly once");
        acquirer.SystemBrowserCalls.Should().Be(0,
            because: "the FPA must not repeat the system-browser request that Entra rejects with AADSTS70007");
        acquirer.WamCalls.Should().Be(0,
            because: "WAM is unavailable in WSL, macOS, and Linux");
    }

    [Fact]
    public async Task GetTokenAsync_CustomAppWithoutWam_UsesSystemBrowserOnly()
    {
        var acquirer = CreateEmptyAcquirer();
        var expected = CreateAccessToken();
        acquirer.SystemBrowserResult = expected;
        var credential = new MsalBrowserCredential(
            ValidClientId,
            ValidTenantId,
            MsalBrowserCredential.InteractiveAuthenticationMode.SystemBrowser,
            acquirer);

        var result = await credential.GetTokenAsync(
            new TokenRequestContext(["https://graph.microsoft.com/User.Read"]),
            CancellationToken.None);

        result.Token.Should().Be(expected.Token,
            because: "tenant-owned custom apps retain their registered localhost browser callback flow");
        acquirer.SystemBrowserCalls.Should().Be(1,
            because: "a custom app without WAM must keep its registered browser callback flow");
        acquirer.DeviceCodeCalls.Should().Be(0,
            because: "the FPA-specific fallback must not alter custom-app authentication");
        acquirer.WamCalls.Should().Be(0,
            because: "this scenario explicitly represents a platform without WAM");
    }

    [Fact]
    public async Task GetTokenAsync_FirstPartyWithWam_UsesWamOnly()
    {
        var acquirer = CreateEmptyAcquirer();
        acquirer.OperatingSystemAccountFailure = new MsalUiRequiredException(
            "interaction_required",
            "Interactive WAM sign-in is required.");
        var expected = CreateAccessToken();
        acquirer.WamResult = expected;
        var credential = new MsalBrowserCredential(
            AuthenticationConstants.WellKnownClientAppId,
            ValidTenantId,
            MsalBrowserCredential.InteractiveAuthenticationMode.Wam,
            acquirer);

        var result = await credential.GetTokenAsync(
            new TokenRequestContext(["https://graph.microsoft.com/User.Read"]),
            CancellationToken.None);

        result.Token.Should().Be(expected.Token,
            because: "Windows should continue using the broker-backed WAM flow for the FPA");
        acquirer.WamCalls.Should().Be(1,
            because: "Windows must retain the broker-backed FPA authentication path");
        acquirer.DeviceCodeCalls.Should().Be(0,
            because: "device code is only the FPA fallback when WAM is unavailable");
        acquirer.SystemBrowserCalls.Should().Be(0,
            because: "native Windows must retain WAM rather than switching the FPA to system-browser authentication");
    }

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

    private static TestMsalTokenAcquirer CreateEmptyAcquirer() => new();

    private static AccessToken CreateAccessToken() =>
        new("test-token", DateTimeOffset.UtcNow.AddHours(1));

    private sealed class TestMsalTokenAcquirer : MsalBrowserCredential.IMsalTokenAcquirer
    {
        public AccessToken DeviceCodeResult { get; set; }
        public AccessToken SystemBrowserResult { get; set; }
        public AccessToken WamResult { get; set; }
        public Exception? OperatingSystemAccountFailure { get; set; }
        public int DeviceCodeCalls { get; private set; }
        public int SystemBrowserCalls { get; private set; }
        public int WamCalls { get; private set; }

        public Task<IReadOnlyList<IAccount>> GetAccountsAsync() =>
            Task.FromResult<IReadOnlyList<IAccount>>([]);

        public Task<AccessToken> AcquireTokenSilentAsync(
            string[] scopes,
            IAccount account,
            bool forceRefresh,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No cached account should be used in these tests.");

        public Task<AccessToken> AcquireOperatingSystemAccountSilentAsync(
            string[] scopes,
            bool forceRefresh,
            CancellationToken cancellationToken) =>
            OperatingSystemAccountFailure is not null
                ? Task.FromException<AccessToken>(OperatingSystemAccountFailure)
                : throw new InvalidOperationException("An operating-system account result was not configured.");

        public Task<AccessToken> AcquireWamAsync(
            string[] scopes,
            IAccount? account,
            string? loginHint,
            CancellationToken cancellationToken)
        {
            WamCalls++;
            return Task.FromResult(WamResult);
        }

        public Task<AccessToken> AcquireSystemBrowserAsync(
            string[] scopes,
            string? loginHint,
            CancellationToken cancellationToken)
        {
            SystemBrowserCalls++;
            return Task.FromResult(SystemBrowserResult);
        }

        public Task<AccessToken> AcquireDeviceCodeAsync(
            string[] scopes,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            DeviceCodeCalls++;
            return Task.FromResult(DeviceCodeResult);
        }
    }

    #region Persistent Cache Tests

    [Fact]
    public void Constructor_ShouldRegisterPersistentCache()
    {
        // Arrange
        var logger = Substitute.For<ILogger>();

        // Act - Creating a credential should initialize and register the persistent cache
        var credential = new MsalBrowserCredential(
            ValidClientId,
            ValidTenantId,
            ValidRedirectUri,
            logger);

        // Assert
        Assert.NotNull(credential);
        // Cache registration happens during construction and should not throw
    }

    [Fact]
    public void Constructor_MultipleInstances_ShouldShareSameCache()
    {
        // Arrange & Act - Create two separate credential instances
        var credential1 = new MsalBrowserCredential(ValidClientId, ValidTenantId, ValidRedirectUri);
        var credential2 = new MsalBrowserCredential(ValidClientId, ValidTenantId, ValidRedirectUri);

        // Assert
        Assert.NotNull(credential1);
        Assert.NotNull(credential2);
        // Both instances should share the same static cache helper internally
        // (We can't directly test the static field, but construction should succeed)
    }

    [Fact]
    public void Constructor_CacheRegistrationFailure_ShouldNotThrow()
    {
        // This test verifies that even if cache registration encounters issues,
        // the credential is still created successfully (non-fatal error handling).

        // Arrange & Act - Create credential (cache registration happens internally)
        var credential = new MsalBrowserCredential(ValidClientId, ValidTenantId, ValidRedirectUri);

        // Assert - Should not throw, authentication will still work without cache
        Assert.NotNull(credential);
    }

    [Fact]
    public void Constructor_ShouldUsePlatformAppropriateCacheEncryption()
    {
        // This test documents the platform-specific cache behavior:
        // - Windows: DPAPI encryption (persistent cache)
        // - macOS: Keychain (persistent cache)
        // - Linux: Persistent caching disabled (tokens remain in-memory only)

        // Arrange
        var logger = Substitute.For<ILogger>();

        // Act
        var credential = new MsalBrowserCredential(
            ValidClientId,
            ValidTenantId,
            ValidRedirectUri,
            logger);

        // Assert
        Assert.NotNull(credential);

        // On Windows, logger should indicate DPAPI usage
        // On macOS, logger should indicate Keychain usage
        // On Linux, logger should indicate persistent caching was skipped
        // The specific platform detection happens at runtime
    }

    [Fact]
    public void Constructor_WithLogger_ShouldLogCacheInitialization()
    {
        // Arrange
        var logger = Substitute.For<ILogger>();

        // Act
        var credential = new MsalBrowserCredential(
            ValidClientId,
            ValidTenantId,
            ValidRedirectUri,
            logger);

        // Assert
        Assert.NotNull(credential);
        // Logger should receive debug messages about cache initialization
        // Specific log calls depend on platform and would be verified through logger mock
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

    #region WAM Declined-Scopes Detection Tests

    // The real WAM error message captured from live MSAL output when requesting Exchange-specific
    // Graph scopes (MailboxSettings.ReadWrite, ExchangeMessageTrace.Read.All) through the broker.
    // IsWamDeclinedScopesError must match ONLY when BOTH signatures are present:
    // the "ApiContractViolation" classification AND the "declined scopes are present" text.
    private const string RealWamDeclinedScopesMessage =
        "WAM Error  \n Error Code: 0 \n Error Message: ApiContractViolation \n" +
        " WAM Error Message: Token response failed because declined scopes are present:'(pii)' \n" +
        " Internal Error Code: 593794722 \n See troubleshooting: https://aka.ms/msal-net-wam";

    [Fact]
    public void IsWamDeclinedScopesError_WithRealWamMessage_ReturnsTrue()
    {
        var ex = new MsalServiceException("WAM_provider_error_0", RealWamDeclinedScopesMessage);

        Assert.True(MsalBrowserCredential.IsWamDeclinedScopesError(ex));
    }

    [Fact]
    public void IsWamDeclinedScopesError_WithApiContractViolationOnly_ReturnsFalse()
    {
        // ApiContractViolation can occur for reasons unrelated to declined scopes; without the
        // "declined scopes are present" text we must NOT treat it as fallback-eligible.
        var ex = new MsalServiceException("WAM_provider_error_0",
            "WAM Error \n Error Message: ApiContractViolation \n Internal Error Code: 12345");

        Assert.False(MsalBrowserCredential.IsWamDeclinedScopesError(ex));
    }

    [Fact]
    public void IsWamDeclinedScopesError_WithDeclinedScopesTextOnly_ReturnsFalse()
    {
        var ex = new MsalServiceException("some_error",
            "Token response failed because declined scopes are present");

        Assert.False(MsalBrowserCredential.IsWamDeclinedScopesError(ex));
    }

    [Fact]
    public void IsWamDeclinedScopesError_WithConsentRequiredError_ReturnsFalse()
    {
        // 0xcaa90019 is the admin-consent-required path and must never be misclassified as
        // declined-scopes (it is handled by LogConsentRequiredAndThrow, not device-code fallback).
        var ex = new MsalServiceException("WAM_provider_error",
            $"WAM Error {AuthenticationConstants.WamConsentRequiredError} Need admin approval");

        Assert.False(MsalBrowserCredential.IsWamDeclinedScopesError(ex));
    }

    [Fact]
    public void IsWamDeclinedScopesError_IsCaseInsensitive()
    {
        var ex = new MsalServiceException("err",
            "error message: apicontractviolation ... token response failed because declined scopes are present");

        Assert.True(MsalBrowserCredential.IsWamDeclinedScopesError(ex));
    }

    #endregion
}
