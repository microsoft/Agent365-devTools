// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class DevelopMcpCommandPureFunctionTests
{
    [Theory]
    [InlineData("https://example.com/redirect/callback", "https://example.com/redirect/tc-callback")]
    [InlineData("https://global-test.consent.azure-apim.net/redirect/ext-2dserver-5f123", "https://global-test.consent.azure-apim.net/redirect/tc-ext-2dserver-5f123")]
    public void AddTcPrefix_AddsPrefix_WhenLastSegmentLacksTc(string input, string expected)
    {
        DevelopMcpCommand.AddTcPrefix(input).Should().Be(expected);
    }

    [Fact]
    public void AddTcPrefix_ReturnsNull_WhenLastSegmentAlreadyHasTcPrefix()
    {
        DevelopMcpCommand.AddTcPrefix("https://example.com/redirect/tc-callback").Should().BeNull();
    }

    [Fact]
    public void AddTcPrefix_ReturnsNull_WhenNoSlashInUri()
    {
        DevelopMcpCommand.AddTcPrefix("noslash").Should().BeNull();
    }

    [Theory]
    [InlineData("https://example.com/redirect/tc-callback", "https://example.com/redirect/callback")]
    [InlineData("https://global-test.consent.azure-apim.net/redirect/tc-ext-2dserver", "https://global-test.consent.azure-apim.net/redirect/ext-2dserver")]
    public void RemoveTcPrefix_RemovesPrefix_WhenLastSegmentHasTc(string input, string expected)
    {
        DevelopMcpCommand.RemoveTcPrefix(input).Should().Be(expected);
    }

    [Fact]
    public void RemoveTcPrefix_ReturnsNull_WhenLastSegmentLacksTcPrefix()
    {
        DevelopMcpCommand.RemoveTcPrefix("https://example.com/redirect/callback").Should().BeNull();
    }

    [Fact]
    public void RemoveTcPrefix_ReturnsNull_WhenNoSlashInUri()
    {
        DevelopMcpCommand.RemoveTcPrefix("noslash").Should().BeNull();
    }

    [Fact]
    public void BuildRedirectUriList_ReturnsOriginal_WhenVariantsAreNull()
    {
        var result = DevelopMcpCommand.BuildRedirectUriList("https://example.com/cb", null, null);
        result.Should().ContainSingle().Which.Should().Be("https://example.com/cb");
    }

    [Fact]
    public void BuildRedirectUriList_IncludesAllVariants()
    {
        var result = DevelopMcpCommand.BuildRedirectUriList(
            "https://example.com/redirect/cb",
            "https://example.com/redirect/tc-cb",
            "https://example.com/redirect/cb-notc");

        result.Should().HaveCount(3);
        result.Should().Contain("https://example.com/redirect/cb");
        result.Should().Contain("https://example.com/redirect/tc-cb");
        result.Should().Contain("https://example.com/redirect/cb-notc");
    }

    [Fact]
    public void BuildRedirectUriList_DeduplicatesIdenticalUris()
    {
        var result = DevelopMcpCommand.BuildRedirectUriList(
            "https://example.com/redirect/cb",
            "https://example.com/redirect/cb",
            null);

        result.Should().ContainSingle();
    }

    [Fact]
    public void AddTcPrefix_And_RemoveTcPrefix_AreInverses()
    {
        var original = "https://example.com/redirect/callback";
        var withTc = DevelopMcpCommand.AddTcPrefix(original);
        withTc.Should().NotBeNull();
        var roundTripped = DevelopMcpCommand.RemoveTcPrefix(withTc!);
        roundTripped.Should().Be(original);
    }
}
