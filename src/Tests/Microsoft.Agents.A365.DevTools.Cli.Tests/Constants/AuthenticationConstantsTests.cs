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
    public void MsalCacheFileName_ShouldBeCorrect()
    {
        AuthenticationConstants.MsalCacheFileName.Should().Be("msal-token-cache");
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

    [Fact]
    public void WamDeclinedScopesError_ShouldMatchKnownWamErrorSubstring()
    {
        // This constant is matched against the WAM error message that reads:
        // "Token response failed because declined scopes are present:'(pii)'"
        // Verified against live WAM output when requesting Exchange-specific Graph scopes
        // (MailboxSettings.ReadWrite, ExchangeMessageTrace.Read.All) through the a365 CLI.
        AuthenticationConstants.WamDeclinedScopesError.Should().Be("declined scopes are present");
    }

    [Fact]
    public void WamApiContractViolation_ShouldMatchKnownWamErrorClassification()
    {
        // WAM surfaces this as "Error Message: ApiContractViolation" in the MSAL exception message
        // when the broker rejects the request reporting declined scopes (known broker behavior;
        // the precise root cause is not publicly documented).
        // Used alongside WamDeclinedScopesError to trigger device code fallback.
        AuthenticationConstants.WamApiContractViolation.Should().Be("ApiContractViolation");
    }

    [Fact]
    public void WamDeclinedScopesError_ShouldBeDifferentFromWamConsentRequiredError()
    {
        // These are two distinct failure modes:
        // - WamConsentRequiredError (0xcaa90019): admin consent NOT granted — do not fall back to device code
        // - WamDeclinedScopesError (ApiContractViolation + declined scopes): scopes are valid and
        //   consent is in place, but the broker still refuses — fall back to device code
        AuthenticationConstants.WamDeclinedScopesError.Should()
            .NotBe(AuthenticationConstants.WamConsentRequiredError);
        AuthenticationConstants.WamDeclinedScopesError.Should()
            .NotContain("0xcaa");
    }
}
