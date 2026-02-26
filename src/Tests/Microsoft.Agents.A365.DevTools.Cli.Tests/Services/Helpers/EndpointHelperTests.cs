// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Helpers;

public class EndpointHelperTests
{
    [Fact]
    public void GetEndpointName_WhenNameIsUnder42Chars_ReturnsOriginalName()
    {
        // Arrange
        var shortName = "my-endpoint";

        // Act
        var result = EndpointHelper.GetEndpointName(shortName);

        // Assert
        result.Should().Be("my-endpoint");
    }

    [Fact]
    public void GetEndpointName_WhenNameIsExactly42Chars_ReturnsOriginalName()
    {
        // Arrange
        var exactName = "twelve345678901234567890123456789012345678"; // 42 chars

        // Act
        var result = EndpointHelper.GetEndpointName(exactName);

        // Assert
        result.Should().Be(exactName);
        result.Length.Should().Be(42);
    }

    [Fact]
    public void GetEndpointName_WhenNameOver42Chars_TruncatesTo42Chars()
    {
        // Arrange
        var longName = "this-is-a-very-long-endpoint-name-that-exceeds-the-limit-of-42-characters";

        // Act
        var result = EndpointHelper.GetEndpointName(longName);

        // Assert
        result.Length.Should().Be(42);
    }

    [Fact]
    public void GetEndpointName_WhenTruncationEndsWithHyphen_ShouldTrimTrailingHyphen()
    {
        // Arrange - Simulates ngrok free domain scenario
        // Original: distressingly-gnathonic-alonzo.ngrok-free.app
        // After conversion: distressingly-gnathonic-alonzo-ngrok-free-app-endpoint
        var longNameEndingWithHyphen = "distressingly-gnathonic-alonzo-ngrok-free-app-endpoint"; // 54 chars

        // Act
        var result = EndpointHelper.GetEndpointName(longNameEndingWithHyphen);

        // Assert
        result.Should().Be("distressingly-gnathonic-alonzo-ngrok-free", "should truncate to 42 chars and trim trailing hyphen");
        result.Length.Should().Be(41);
        result.Should().NotEndWith("-", "Azure Bot Service does not allow bot names ending with hyphen");
    }

    [Fact]
    public void GetEndpointName_WhenTruncationEndsWithMultipleHyphens_ShouldTrimAllTrailingHyphens()
    {
        // Arrange - Edge case with multiple trailing hyphens after truncation
        // Name that when truncated to 42 will end with "---e"
        var nameWithMultipleHyphens = "some-very-long-endpoint-name-with-hyphens---extra-content-here"; // 63 chars

        // Act
        var result = EndpointHelper.GetEndpointName(nameWithMultipleHyphens);

        // Assert - truncates to 42: "some-very-long-endpoint-name-with-hyphens-", then trims to "some-very-long-endpoint-name-with-hyphens"
        result.Should().Be("some-very-long-endpoint-name-with-hyphens");
        result.Length.Should().Be(41);
        result.Should().NotEndWith("-", "Should trim all trailing hyphens");
    }

    [Theory]
    [InlineData("my-endpoint-name-", "my-endpoint-name")]
    [InlineData("endpoint--", "endpoint")]
    [InlineData("test-name---", "test-name")]
    public void GetEndpointName_WhenInputEndsWithHyphen_ShouldTrimTrailingHyphens(string input, string expected)
    {
        // Act
        var result = EndpointHelper.GetEndpointName(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetEndpointName_WhenInputIsNullOrWhitespace_ShouldThrowSetupValidationException(string? input)
    {
        // Act
        Action act = () => EndpointHelper.GetEndpointName(input!);

        // Assert
        act.Should().Throw<SetupValidationException>()
            .WithMessage("*Endpoint name cannot be null or whitespace*");
    }

    [Fact]
    public void GetEndpointName_WhenResultBecomesTooShort_ShouldThrowSetupValidationException()
    {
        // Arrange - Name that becomes less than 4 chars after trimming hyphens
        var shortName = "---";

        // Act
        Action act = () => EndpointHelper.GetEndpointName(shortName);

        // Assert
        act.Should().Throw<SetupValidationException>()
            .WithMessage("*becomes too short after processing*");
    }

    [Fact]
    public void GetEndpointNameFromUrl_WithDevTunnelUrl_ReturnsHostBasedName()
    {
        // Arrange - dev tunnel URL (the customer's scenario)
        var url = "https://x23kz7ll-3979-inc1-devtunnels-ms.devtunnels.ms/api/messages";

        // Act
        var result = EndpointHelper.GetEndpointNameFromUrl(url);

        // Assert - full pre-truncation string: "x23kz7ll-3979-inc1-devtunnels-ms-devtunnels-ms-endpoint" (55 chars)
        // Truncated to 42 chars: "x23kz7ll-3979-inc1-devtunnels-ms-devtunnel" (no trailing hyphen to strip)
        result.Should().Be("x23kz7ll-3979-inc1-devtunnels-ms-devtunnel");
        result.Length.Should().Be(42);
    }

    [Fact]
    public void GetEndpointNameFromUrl_WithSimpleUrl_ReturnsExpectedName()
    {
        // Arrange
        var url = "https://myapp.azurewebsites.net/api/messages";

        // Act
        var result = EndpointHelper.GetEndpointNameFromUrl(url);

        // Assert
        result.Should().Be("myapp-azurewebsites-net-endpoint");
    }

    [Fact]
    public void GetEndpointNameFromUrl_WithNgrokUrl_ReturnsExpectedName()
    {
        // Arrange - ngrok free domain
        var url = "https://distressingly-gnathonic-alonzo.ngrok-free.app/api/messages";

        // Act
        var result = EndpointHelper.GetEndpointNameFromUrl(url);

        // Assert - same result as going through GetEndpointName with the host-based name
        var expected = EndpointHelper.GetEndpointName("distressingly-gnathonic-alonzo-ngrok-free-app-endpoint");
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("relative/path")]
    public void GetEndpointNameFromUrl_WithInvalidUrl_ShouldThrowSetupValidationException(string? input)
    {
        // Act
        // input! suppresses the nullable warning intentionally — tests runtime null/invalid handling
        // even though the parameter is declared non-nullable.
        Action act = () => EndpointHelper.GetEndpointNameFromUrl(input!);

        // Assert
        act.Should().Throw<SetupValidationException>();
    }
}
