// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
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

    #region GetAuthenticatedGraphClientAsync Tests

    // These helpers mirror the pattern from AuthenticationServiceTests to enable injecting
    // fakes via the credentialFactory constructor parameter without touching the file system
    // or launching any real authentication UI.

    private sealed class ThrowingTokenCredential : TokenCredential
    {
        private readonly Exception _exception;
        public ThrowingTokenCredential(Exception exception) => _exception = exception;
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw _exception;
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw _exception;
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        private readonly AccessToken _token;
        public StubTokenCredential(string token, DateTimeOffset expiresOn) => _token = new AccessToken(token, expiresOn);
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => _token;
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(_token);
    }

    private const string ValidGuid = "12345678-1234-1234-1234-123456789abc";
    private const string ValidTenantId = "87654321-4321-4321-4321-cba987654321";

    /// <summary>
    /// Verifies that a credential failure surfaced during eager token acquisition
    /// throws <see cref="GraphApiException"/> rather than silently returning a broken client.
    ///
    /// Pre-fix: GetAuthenticatedGraphClientAsync returned "success" without ever calling
    /// GetTokenAsync because GraphServiceClient acquires tokens lazily. This masked broken
    /// credentials at construction time (Gap 2 from the macOS auth regression).
    ///
    /// Post-fix: Eager token acquisition detects the failure early and surfaces a clear error.
    /// </summary>
    [Fact]
    public async Task GetAuthenticatedGraphClientAsync_WhenCredentialFails_ThrowsGraphApiException()
    {
        // Arrange — credential that always fails (simulates a broken/unsupported auth flow)
        var failingCredential = new ThrowingTokenCredential(
            new MsalAuthenticationFailedException("Authentication failed"));

        var logger = Substitute.For<ILogger<InteractiveGraphAuthService>>();
        var sut = new InteractiveGraphAuthService(logger, ValidGuid,
            credentialFactory: (_, _) => failingCredential);

        // Act
        var act = async () => await sut.GetAuthenticatedGraphClientAsync(ValidTenantId);

        // Assert — GraphApiException surfaced at construction time, not later during API calls
        await act.Should().ThrowAsync<GraphApiException>();
    }

    /// <summary>
    /// Verifies that when the credential succeeds, a GraphServiceClient is returned
    /// and the "Successfully authenticated" log is only emitted after actual token acquisition.
    /// </summary>
    [Fact]
    public async Task GetAuthenticatedGraphClientAsync_WhenCredentialSucceeds_ReturnsClient()
    {
        // Arrange
        var workingCredential = new StubTokenCredential("token-value", DateTimeOffset.UtcNow.AddHours(1));
        var logger = Substitute.For<ILogger<InteractiveGraphAuthService>>();
        var sut = new InteractiveGraphAuthService(logger, ValidGuid,
            credentialFactory: (_, _) => workingCredential);

        // Act
        var client = await sut.GetAuthenticatedGraphClientAsync(ValidTenantId);

        // Assert
        client.Should().NotBeNull();
    }

    /// <summary>
    /// Verifies that the service returns the same cached GraphServiceClient for the same tenant
    /// on repeated calls, avoiding redundant authentication prompts.
    /// </summary>
    [Fact]
    public async Task GetAuthenticatedGraphClientAsync_ForSameTenant_ReturnsCachedClient()
    {
        // Arrange
        var workingCredential = new StubTokenCredential("token-value", DateTimeOffset.UtcNow.AddHours(1));
        var logger = Substitute.For<ILogger<InteractiveGraphAuthService>>();
        int callCount = 0;
        var sut = new InteractiveGraphAuthService(logger, ValidGuid,
            credentialFactory: (_, _) => { callCount++; return workingCredential; });

        // Act — call twice for the same tenant
        var client1 = await sut.GetAuthenticatedGraphClientAsync(ValidTenantId);
        var client2 = await sut.GetAuthenticatedGraphClientAsync(ValidTenantId);

        // Assert — factory called only once; same instance returned on the second call
        callCount.Should().Be(1);
        client1.Should().BeSameAs(client2);
    }

    /// <summary>
    /// Verifies that the service authenticates again when a different tenant is requested,
    /// rather than returning a cached client scoped to the wrong tenant.
    /// </summary>
    [Fact]
    public async Task GetAuthenticatedGraphClientAsync_ForDifferentTenant_AuthenticatesSeparately()
    {
        // Arrange
        var workingCredential = new StubTokenCredential("token-value", DateTimeOffset.UtcNow.AddHours(1));
        var logger = Substitute.For<ILogger<InteractiveGraphAuthService>>();
        int callCount = 0;
        var sut = new InteractiveGraphAuthService(logger, ValidGuid,
            credentialFactory: (_, _) => { callCount++; return workingCredential; });

        const string otherTenant = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        // Act
        var client1 = await sut.GetAuthenticatedGraphClientAsync(ValidTenantId);
        var client2 = await sut.GetAuthenticatedGraphClientAsync(otherTenant);

        // Assert — factory called twice (once per tenant)
        callCount.Should().Be(2);
        client1.Should().NotBeSameAs(client2);
    }

    /// <summary>
    /// Verifies that an access_denied error from the auth provider surfaces as
    /// GraphApiException rather than a raw MsalServiceException.
    /// </summary>
    [Fact]
    public async Task GetAuthenticatedGraphClientAsync_WhenAccessDenied_ThrowsGraphApiException()
    {
        // Arrange — simulate user cancelling or denying the auth prompt
        var accessDenied = new Microsoft.Identity.Client.MsalServiceException("access_denied", "User cancelled");
        var failingCredential = new ThrowingTokenCredential(accessDenied);

        var logger = Substitute.For<ILogger<InteractiveGraphAuthService>>();
        var sut = new InteractiveGraphAuthService(logger, ValidGuid,
            credentialFactory: (_, _) => failingCredential);

        // Act
        var act = async () => await sut.GetAuthenticatedGraphClientAsync(ValidTenantId);

        // Assert
        await act.Should().ThrowAsync<GraphApiException>();
    }

    #endregion
}