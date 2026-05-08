// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class LogRedactionServiceTests
{
    private readonly LogRedactionService _sut = new();
    private const string Source = "/logs/a365.setup.log";

    [Fact]
    public void Redact_JwtToken_IsReplaced()
    {
        var log = "[DBG] Token scp: openid | token: eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyIn0.abc123-XYZ_";

        var result = _sut.Redact(log, Source);

        result.RedactedContent.Should().Contain("<JWT-TOKEN>");
        result.RedactedContent.Should().NotContain("eyJhbGciOiJSUzI1NiJ9");
        result.TokensRedacted.Should().Be(1);
    }

    [Fact]
    public void Redact_EmailAddress_IsReplacedWithAlias()
    {
        var log = "[INF] Current user: aarthi-dev <aarthi-dev@agent365002.onmicrosoft.com>";

        var result = _sut.Redact(log, Source);

        result.RedactedContent.Should().Contain("<email-1>");
        result.RedactedContent.Should().NotContain("aarthi-dev@agent365002.onmicrosoft.com");
        result.EmailsRedacted.Should().Be(1);
    }

    [Fact]
    public void Redact_SameEmailAppearsMultipleTimes_SameAliasUsed()
    {
        var log = "[INF] User: user@contoso.com\n[INF] Sponsor: user@contoso.com";

        var result = _sut.Redact(log, Source);

        result.RedactedContent.Should().NotContain("user@contoso.com");
        result.EmailsRedacted.Should().Be(1);
        // Both lines replaced with the same alias
        var lines = result.RedactedContent.Split('\n').Where(l => l.StartsWith("[INF]")).ToList();
        var alias1 = lines[0].Split(' ').Last().Trim();
        var alias2 = lines[1].Split(' ').Last().Trim();
        alias1.Should().Be(alias2);
    }

    [Fact]
    public void Redact_TwoDistinctEmails_GetDifferentAliases()
    {
        var log = "[INF] User: alice@contoso.com\n[INF] Owner: bob@contoso.com";

        var result = _sut.Redact(log, Source);

        result.RedactedContent.Should().Contain("<email-1>");
        result.RedactedContent.Should().Contain("<email-2>");
        result.EmailsRedacted.Should().Be(2);
    }

    [Fact]
    public void Redact_Guid_IsReplacedWithAlias()
    {
        var log = "[INF] Blueprint ID: 48e7c63c-15f8-42ff-9df9-7adb43889e34";

        var result = _sut.Redact(log, Source);

        result.RedactedContent.Should().Contain("<id-1>");
        result.RedactedContent.Should().NotContain("48e7c63c-15f8-42ff-9df9-7adb43889e34");
        result.IdsRedacted.Should().Be(1);
    }

    [Fact]
    public void Redact_SameGuidAppearsMultipleTimes_SameAliasUsed()
    {
        var guid = "48e7c63c-15f8-42ff-9df9-7adb43889e34";
        var log = $"[INF] Blueprint ID: {guid}\n[INF] Confirmed: {guid}";

        var result = _sut.Redact(log, Source);

        result.IdsRedacted.Should().Be(1);
        result.RedactedContent.Should().NotContain(guid);
    }

    [Fact]
    public void Redact_TwoDistinctGuids_GetDifferentAliases()
    {
        var log = "[INF] Blueprint: 48e7c63c-15f8-42ff-9df9-7adb43889e34\n[INF] User ID: 2cd3a148-4462-4f3c-8a8e-0c6f051c6a27";

        var result = _sut.Redact(log, Source);

        result.RedactedContent.Should().Contain("<id-1>");
        result.RedactedContent.Should().Contain("<id-2>");
        result.IdsRedacted.Should().Be(2);
    }

    [Fact]
    public void Redact_NonSensitiveLine_IsPreservedVerbatim()
    {
        var log = "[INF] Requirements: 3 passed, 1 warnings, 0 failed";

        var result = _sut.Redact(log, Source);

        result.RedactedContent.Should().Contain("[INF] Requirements: 3 passed, 1 warnings, 0 failed");
        result.EmailsRedacted.Should().Be(0);
        result.IdsRedacted.Should().Be(0);
        result.TokensRedacted.Should().Be(0);
    }

    [Fact]
    public void Redact_SummaryHeader_PrependedWithCounts()
    {
        var log = "[INF] User: dev@contoso.com\n[INF] ID: 48e7c63c-15f8-42ff-9df9-7adb43889e34";

        var result = _sut.Redact(log, Source);

        result.RedactedContent.Should().StartWith("# Redacted by a365 logs export");
        result.RedactedContent.Should().Contain("1 email(s), 1 id(s), 0 JWT token(s)");
        result.RedactedContent.Should().Contain($"# Original: {Source}");
    }

    [Fact]
    public void Redact_MixedRealLogLine_FullyRedacted()
    {
        var log = "[INF] Current user: Aarthi-dev <aarthi-dev@agent365002.onmicrosoft.com>\n" +
                  "[INF] Sponsor and Owner: User ID 2cd3a148-4462-4f3c-8a8e-0c6f051c6a27\n" +
                  "[INF] Blueprint ID: 48e7c63c-15f8-42ff-9df9-7adb43889e34\n" +
                  "[INF] Requirements: 3 passed, 1 warnings, 0 failed";

        var result = _sut.Redact(log, Source);

        result.RedactedContent.Should().NotContain("aarthi-dev@agent365002.onmicrosoft.com");
        result.RedactedContent.Should().NotContain("2cd3a148-4462-4f3c-8a8e-0c6f051c6a27");
        result.RedactedContent.Should().NotContain("48e7c63c-15f8-42ff-9df9-7adb43889e34");
        result.RedactedContent.Should().Contain("[INF] Requirements: 3 passed, 1 warnings, 0 failed");
        result.EmailsRedacted.Should().Be(1);
        result.IdsRedacted.Should().Be(2);
    }

    [Fact]
    public void Redact_GuidAliasIsCaseInsensitive()
    {
        // Same GUID in different cases should map to the same alias
        var lower = "48e7c63c-15f8-42ff-9df9-7adb43889e34";
        var upper = "48E7C63C-15F8-42FF-9DF9-7ADB43889E34";
        var log = $"[INF] A: {lower}\n[INF] B: {upper}";

        var result = _sut.Redact(log, Source);

        result.IdsRedacted.Should().Be(1);
    }

    [Fact]
    public void Redact_EmptyLog_ReturnsHeaderOnly()
    {
        var result = _sut.Redact(string.Empty, Source);

        result.RedactedContent.Should().StartWith("# Redacted by a365 logs export");
        result.EmailsRedacted.Should().Be(0);
        result.IdsRedacted.Should().Be(0);
        result.TokensRedacted.Should().Be(0);
    }

    [Fact]
    public void Redact_NullLogContent_ThrowsArgumentNullException()
    {
        var act = () => _sut.Redact(null!, Source);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logContent");
    }

    [Fact]
    public void Redact_JwtAndEmailOnSameLine_BothRedactedWithoutCrossContamination()
    {
        // The @ in a JWT payload must not be matched by the email pattern after JWT redaction
        var jwt = "eyJhbGciOiJSUzI1NiJ9.eyJ1cG4iOiJ1c2VyQGNvbnRvc28uY29tIn0.sig";
        var log = $"[DBG] Token: {jwt} | contact: admin@contoso.com";

        var result = _sut.Redact(log, Source);

        result.RedactedContent.Should().Contain("<JWT-TOKEN>");
        result.RedactedContent.Should().Contain("<email-1>");
        result.TokensRedacted.Should().Be(1);
        // The email inside the JWT payload must not be counted separately
        result.EmailsRedacted.Should().Be(1);
    }

    [Fact]
    public void Redact_WindowsPathInSourceFilePath_AppearsInHeader()
    {
        var windowsSource = @"C:\Users\me\AppData\Local\Microsoft.Agents.A365.DevTools.Cli\logs\a365.setup.log";
        var log = "[INF] Setup complete";

        var result = _sut.Redact(log, windowsSource);

        result.RedactedContent.Should().Contain($"# Original: {windowsSource}");
    }
}
