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

    // GetEndpointNameFromHost tests

    [Fact]
    public void GetEndpointNameFromHost_WithBlueprintId_UsesBlueprintSuffixInsteadOfEndpointLiteral()
    {
        // Arrange - simulates the n8n.cloud scenario from issue report
        var host = "microsoftcape.app.n8n.cloud";
        var blueprintId = "9ab0b58c-c49e-4adb-b164-1ed10cbe3956";

        // Act
        var result = EndpointHelper.GetEndpointNameFromHost(host, blueprintId);

        // Assert - host (dots → dashes) + first 8 non-hyphen chars of blueprint ID
        result.Should().Be("microsoftcape-app-n8n-cloud-9ab0b58c");
        result.Should().NotEndWith("-endpoint", "legacy literal suffix should not be used when blueprint ID is available");
    }

    [Fact]
    public void GetEndpointNameFromHost_TwoDifferentBlueprintsOnSameHost_ProduceDifferentNames()
    {
        // Arrange - two webhooks on same n8n tenant but different workflows
        var host = "microsoftcape.app.n8n.cloud";
        var blueprintId1 = "9ab0b58c-c49e-4adb-b164-1ed10cbe3956";
        var blueprintId2 = "ffffffff-aaaa-bbbb-cccc-dddddddddddd";

        // Act
        var name1 = EndpointHelper.GetEndpointNameFromHost(host, blueprintId1);
        var name2 = EndpointHelper.GetEndpointNameFromHost(host, blueprintId2);

        // Assert
        name1.Should().NotBe(name2, "same host with different blueprints must produce unique endpoint names");
    }

    [Fact]
    public void GetEndpointNameFromHost_WithNullBlueprintId_FallsBackToLegacyEndpointSuffix()
    {
        // Arrange
        var host = "myapp.example.com";

        // Act
        var result = EndpointHelper.GetEndpointNameFromHost(host, null);

        // Assert
        result.Should().Be("myapp-example-com-endpoint");
    }

    [Fact]
    public void GetEndpointNameFromHost_WithEmptyBlueprintId_FallsBackToLegacyEndpointSuffix()
    {
        // Arrange
        var host = "myapp.example.com";

        // Act
        var result = EndpointHelper.GetEndpointNameFromHost(host, "");

        // Assert
        result.Should().Be("myapp-example-com-endpoint");
    }

    [Fact]
    public void GetEndpointNameFromHost_WithLongHost_TruncatesHostToFitWithinLimit()
    {
        // Arrange - host that would exceed 42 chars even before the suffix
        var host = "this-is-a-very-long-hostname-that-exceeds-limits.example.com";
        var blueprintId = "aabbccdd-0000-1111-2222-333344445555";

        // Act
        var result = EndpointHelper.GetEndpointNameFromHost(host, blueprintId);

        // Assert
        result.Length.Should().BeLessOrEqualTo(42);
        result.Should().EndWith("-aabbccdd");
        result.Should().NotEndWith("-", "must not end with hyphen");
    }

    [Fact]
    public void GetEndpointNameFromHost_WithNullHost_ThrowsSetupValidationException()
    {
        // Act
        Action act = () => EndpointHelper.GetEndpointNameFromHost(null!, "any-blueprint-id");

        // Assert
        act.Should().Throw<SetupValidationException>()
            .WithMessage("*Hostname cannot be null or whitespace*");
    }

    [Fact]
    public void GetEndpointNameFromHost_ResultIsAlwaysWithin42CharLimit()
    {
        // Arrange - very long host, valid blueprint ID
        var host = "extremely-long-subdomain.another-long-part.and-another.example.com";
        var blueprintId = "12345678-1234-1234-1234-123456789012";

        // Act
        var result = EndpointHelper.GetEndpointNameFromHost(host, blueprintId);

        // Assert
        result.Length.Should().BeLessOrEqualTo(42);
    }
}
