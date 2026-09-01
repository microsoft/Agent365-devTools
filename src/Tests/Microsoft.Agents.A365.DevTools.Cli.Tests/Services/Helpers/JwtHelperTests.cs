// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Helpers;

/// <summary>
/// Unit tests for JwtHelper, focused on TryDecodeSpaceDelimitedClaim (used to validate a
/// first-party client app's granted scopes from the token's 'scp' claim).
/// </summary>
public class JwtHelperTests
{
    private static string BuildJwt(object payload)
    {
        var header = Base64UrlEncode("""{"alg":"none","typ":"JWT"}""");
        var body = Base64UrlEncode(JsonSerializer.Serialize(payload));
        // Signature segment is irrelevant to claim decoding — only the payload is parsed.
        return $"{header}.{body}.sig";
    }

    private static string Base64UrlEncode(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    [Fact]
    public void TryDecodeSpaceDelimitedClaim_WithMultipleScopes_ReturnsAllValues()
    {
        var jwt = BuildJwt(new { scp = "AgentIdentityBlueprint.ReadWrite.All User.Read Application.Read.All" });

        var succeeded = JwtHelper.TryDecodeSpaceDelimitedClaim(
            jwt, "scp", out var scopes, out var failureReason);

        succeeded.Should().BeTrue(
            because: "a valid string scp claim must be accepted for delegated authorization validation");
        failureReason.Should().BeEmpty(
            because: "a successfully decoded scp claim must not report an authorization parsing failure");
        scopes.Should().BeEquivalentTo(
            new[] { "AgentIdentityBlueprint.ReadWrite.All", "User.Read", "Application.Read.All" },
            because: "each space-separated token in the 'scp' claim must be decoded as a distinct granted scope");
    }

    [Fact]
    public void TryDecodeSpaceDelimitedClaim_IsCaseInsensitive()
    {
        var jwt = BuildJwt(new { scp = "User.Read" });

        var succeeded = JwtHelper.TryDecodeSpaceDelimitedClaim(
            jwt, "scp", out var scopes, out _);

        succeeded.Should().BeTrue(
            because: "a valid string scp claim must be decoded before case-insensitive scope comparison");
        scopes.Contains("user.read").Should().BeTrue(
            because: "scope comparison must be case-insensitive so validation isn't brittle to casing differences from Graph");
    }

    [Fact]
    public void TryDecodeSpaceDelimitedClaim_WhenClaimAbsent_ReturnsExplicitFailure()
    {
        var jwt = BuildJwt(new { aud = "00000003-0000-0000-c000-000000000000" });

        var succeeded = JwtHelper.TryDecodeSpaceDelimitedClaim(
            jwt, "scp", out var scopes, out var failureReason);

        succeeded.Should().BeFalse(
            because: "an absent scp claim cannot prove delegated authorization");
        scopes.Should().BeEmpty(
            because: "no delegated scopes can be proven when the token omits scp");
        failureReason.Should().Contain("does not contain a 'scp' claim",
            because: "operators must be told that delegated authorization could not be inspected");
    }

    [Fact]
    public void TryDecodeSpaceDelimitedClaim_WhenTokenMalformed_ReturnsExplicitFailure()
    {
        var succeeded = JwtHelper.TryDecodeSpaceDelimitedClaim(
            "not-a-jwt", "scp", out var scopes, out var failureReason);

        succeeded.Should().BeFalse(
            because: "a malformed token must be distinguishable from a valid token that omits a required scope");
        scopes.Should().BeEmpty(
            because: "no delegated scopes can be trusted from a malformed compact token");
        failureReason.Should().Contain("not a valid compact JWT",
            because: "malformed token structure must be diagnosed separately from missing permissions");
    }

    [Fact]
    public void TryDecodeSpaceDelimitedClaim_WhenTokenNull_ReturnsExplicitFailure()
    {
        var succeeded = JwtHelper.TryDecodeSpaceDelimitedClaim(
            null, "scp", out var scopes, out var failureReason);

        succeeded.Should().BeFalse(
            because: "an absent access token cannot prove first-party delegated authorization");
        scopes.Should().BeEmpty(
            because: "no scopes exist to validate when token acquisition returns null");
        failureReason.Should().Contain("empty",
            because: "token acquisition failure must be reported explicitly");
    }

    [Fact]
    public void TryDecodeSpaceDelimitedClaim_WhenClaimEmptyString_SucceedsWithEmptySet()
    {
        var jwt = BuildJwt(new { scp = "" });

        var succeeded = JwtHelper.TryDecodeSpaceDelimitedClaim(
            jwt, "scp", out var scopes, out var failureReason);

        succeeded.Should().BeTrue(
            because: "an empty string is a readable scp claim whose authorization set is empty");
        scopes.Should().BeEmpty(because: "an empty 'scp' claim means no scopes were granted");
        failureReason.Should().BeEmpty();
    }

    [Fact]
    public void TryDecodeSpaceDelimitedClaim_WhenPayloadIsInvalidBase64Url_ReturnsExplicitFailure()
    {
        var header = Base64UrlEncode("""{"alg":"none","typ":"JWT"}""");

        var succeeded = JwtHelper.TryDecodeSpaceDelimitedClaim(
            $"{header}.%%%.sig", "scp", out var scopes, out var failureReason);

        succeeded.Should().BeFalse(
            because: "an unreadable payload must not be reported as a valid token with missing scopes");
        scopes.Should().BeEmpty(
            because: "no scopes can be trusted when the JWT payload cannot be decoded");
        failureReason.Should().Contain("invalid Base64Url",
            because: "invalid token encoding must be distinguishable from a valid token missing scopes");
    }

    [Fact]
    public void TryDecodeSpaceDelimitedClaim_WhenPayloadIsInvalidJson_ReturnsExplicitFailure()
    {
        var header = Base64UrlEncode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncode("not-json");

        var succeeded = JwtHelper.TryDecodeSpaceDelimitedClaim(
            $"{header}.{payload}.sig", "scp", out var scopes, out var failureReason);

        succeeded.Should().BeFalse(
            because: "an unreadable payload must not be reported as a valid token with missing scopes");
        scopes.Should().BeEmpty(
            because: "no scopes can be trusted when the decoded token payload is not JSON");
        failureReason.Should().Contain("not valid JSON",
            because: "invalid token JSON must be distinguishable from missing delegated scopes");
    }

    [Fact]
    public void TryDecodeSpaceDelimitedClaim_WhenClaimIsNotAString_ReturnsExplicitFailure()
    {
        var jwt = BuildJwt(new { scp = new[] { "User.Read" } });

        var succeeded = JwtHelper.TryDecodeSpaceDelimitedClaim(
            jwt, "scp", out var scopes, out var failureReason);

        succeeded.Should().BeFalse(
            because: "the OAuth scp claim must be a space-delimited JSON string");
        scopes.Should().BeEmpty(
            because: "OAuth delegated scopes cannot be interpreted from a non-string scp value");
        failureReason.Should().Contain("'scp' claim is not a string",
            because: "the OAuth scp contract requires a space-delimited string");
    }
}
