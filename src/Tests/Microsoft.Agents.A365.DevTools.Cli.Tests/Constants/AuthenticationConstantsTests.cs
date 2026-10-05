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
    public void RequiredPermissionGrantScopes_IncludeApplicationAndBlueprintScopes()
    {
        AuthenticationConstants.RequiredPermissionGrantScopes.Should().Contain(
            AuthenticationConstants.ApplicationReadAllScope,
            because: "permission setup reads applications and service principals before applying grants");
        AuthenticationConstants.RequiredPermissionGrantScopes.Should().Contain(
            "AgentIdentityBlueprint.ReadWrite.All",
            because: "permission setup reads and writes inheritable blueprint permissions");
    }

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
        AuthenticationConstants.WamDeclinedScopesError.Should().Be("declined scopes are present",
            because: "this exact substring is matched against the live WAM error message to gate the " +
                     "device-code fallback; changing it silently disables the fallback");
    }

    [Fact]
    public void WamApiContractViolation_ShouldMatchKnownWamErrorClassification()
    {
        // WAM surfaces this as "Error Message: ApiContractViolation" in the MSAL exception message
        // when the broker rejects the request reporting declined scopes (known broker behavior;
        // the precise root cause is not publicly documented).
        // Used alongside WamDeclinedScopesError to trigger device code fallback.
        AuthenticationConstants.WamApiContractViolation.Should().Be("ApiContractViolation",
            because: "this classification string is matched together with WamDeclinedScopesError to " +
                     "distinguish the fallback-eligible failure from other WAM errors; it must remain stable");
    }

    [Fact]
    public void WamDeclinedScopesError_ShouldBeDifferentFromWamConsentRequiredError()
    {
        // These are two distinct failure modes:
        // - WamConsentRequiredError (0xcaa90019): admin consent NOT granted — do not fall back to device code
        // - WamDeclinedScopesError (ApiContractViolation + declined scopes): scopes are valid and
        //   consent is in place, but the broker still refuses — fall back to device code
        AuthenticationConstants.WamDeclinedScopesError.Should()
            .NotBe(AuthenticationConstants.WamConsentRequiredError,
                because: "declined-scopes and consent-required are handled differently — the former " +
                         "falls back to device code, the latter must not — so the two signatures must stay distinct");
        AuthenticationConstants.WamDeclinedScopesError.Should()
            .NotContain("0xcaa",
                because: "the consent-required path keys on the 0xcaa prefix; the declined-scopes signature " +
                         "must not overlap with it or the fallback would trigger on consent errors");
    }

    [Fact]
    public void WellKnownClientAppId_ShouldBeExpectedFirstPartyGuid()
    {
        AuthenticationConstants.WellKnownClientAppId.Should().Be("f54280f4-395e-4ea8-9e48-bf2d4952aa14",
            because: "setup/bootstrap flows must resolve this exact well-known first-party application " +
                     "ID as the default client app before falling back to any tenant-owned custom app");
        Guid.TryParse(AuthenticationConstants.WellKnownClientAppId, out _).Should().BeTrue(
            because: "the default client app ID must be a valid GUID accepted by Agent365Config.Validate()");
    }

    [Theory]
    [InlineData("f54280f4-395e-4ea8-9e48-bf2d4952aa14", true)]
    [InlineData("F54280F4-395E-4EA8-9E48-BF2D4952AA14", true)]
    [InlineData("{f54280f4-395e-4ea8-9e48-bf2d4952aa14}", true)]
    [InlineData("a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsWellKnownFirstPartyClientApp_ReturnsExpected(string? clientAppId, bool expected)
    {
        AuthenticationConstants.IsWellKnownFirstPartyClientApp(clientAppId).Should().Be(expected,
            because: "first-party detection must match the well-known ID case-insensitively and reject any other value");
    }
}
