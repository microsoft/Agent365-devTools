// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Constants;

/// <summary>
/// Unit tests for AuthenticationConstants to ensure all constants are properly defined
/// </summary>
public class AuthenticationConstantsTests
{
    [Fact]
    public void AzureCliClientId_ShouldBeValidGuid()
    {
        Guid.TryParse(AuthenticationConstants.AzureCliClientId, out _).Should().BeTrue();
    }

    [Fact]
    public void CommonTenantId_ShouldBeCommon()
    {
        AuthenticationConstants.CommonTenantId.Should().Be("common");
    }

    [Fact]
    public void LocalhostRedirectUri_ShouldBeValidUrl()
    {
        Uri.IsWellFormedUriString(AuthenticationConstants.LocalhostRedirectUri, UriKind.Absolute).Should().BeTrue();
        AuthenticationConstants.LocalhostRedirectUri.Should().StartWith("http://localhost");
    }

    [Fact]
    public void ApplicationName_ShouldBeCorrect()
    {
        AuthenticationConstants.ApplicationName.Should().Be("Microsoft.Agents.A365.DevTools.Cli");
    }

    [Fact]
    public void TokenCacheFileName_ShouldBeCorrect()
    {
        AuthenticationConstants.TokenCacheFileName.Should().Be("auth-token.json");
    }

    [Fact]
    public void TokenExpirationBufferMinutes_ShouldBePositive()
    {
        AuthenticationConstants.TokenExpirationBufferMinutes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TokenExpirationBufferMinutes_ShouldBeReasonable()
    {
        // Should be between 1 and 60 minutes
        AuthenticationConstants.TokenExpirationBufferMinutes.Should().BeInRange(1, 60);
    }
}
