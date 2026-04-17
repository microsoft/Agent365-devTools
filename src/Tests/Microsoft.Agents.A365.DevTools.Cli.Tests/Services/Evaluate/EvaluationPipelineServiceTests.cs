// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Evaluate;

/// <summary>
/// Tests for EvaluationPipelineService helper methods.
/// </summary>
public class EvaluationPipelineServiceTests
{
    // -----------------------------------------------------------------------
    // ParseEvalEngine
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("auto", EvalEngine.Auto)]
    [InlineData("AUTO", EvalEngine.Auto)]
    [InlineData("github-copilot", EvalEngine.GithubCopilot)]
    [InlineData("GITHUB-COPILOT", EvalEngine.GithubCopilot)]
    [InlineData("claude-code", EvalEngine.ClaudeCode)]
    [InlineData("Claude-Code", EvalEngine.ClaudeCode)]
    [InlineData("none", EvalEngine.None)]
    [InlineData("NONE", EvalEngine.None)]
    public void ParseEvalEngine_ValidValues_ReturnsCorrectEnum(string input, EvalEngine expected)
    {
        var result = EvaluationPipelineService.ParseEvalEngine(input);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("openai")]
    [InlineData("")]
    public void ParseEvalEngine_InvalidValues_ThrowsEvaluationException(string input)
    {
        var act = () => EvaluationPipelineService.ParseEvalEngine(input);

        act.Should().Throw<EvaluationException>();
    }

    // -----------------------------------------------------------------------
    // DeriveServerName
    // -----------------------------------------------------------------------

    [Fact]
    public void DeriveServerName_StandardUrl_ReturnsHostWithDotsReplaced()
    {
        var result = EvaluationPipelineService.DeriveServerName("http://my.server.com/mcp");

        result.Should().Be("my-server-com",
            because: "derived names feed into filenames, so dots in the host must be replaced with filesystem-safe hyphens");
    }

    [Fact]
    public void DeriveServerName_UrlWithNonStandardPort_IncludesPort()
    {
        var result = EvaluationPipelineService.DeriveServerName("http://localhost:3000/mcp");

        result.Should().Be("localhost-3000",
            because: "non-default ports must be included so two servers on the same host don't collide to the same filename");
    }

    [Fact]
    public void DeriveServerName_UrlWithDefaultPort_ExcludesPort()
    {
        var result = EvaluationPipelineService.DeriveServerName("http://example.com/mcp");

        result.Should().Be("example-com",
            because: "default ports are implicit in the scheme and would add noise to the filename");
    }

    [Fact]
    public void DeriveServerName_InvalidUri_ReturnsSanitizedFallback()
    {
        var result = EvaluationPipelineService.DeriveServerName("not a valid uri");

        result.Should().NotBeNullOrWhiteSpace(
            because: "a malformed URL should still produce a usable name rather than breaking the pipeline");
    }

    [Fact]
    public void DeriveServerName_InvalidUriWithSpecialChars_ReplacesSpecialChars()
    {
        var result = EvaluationPipelineService.DeriveServerName("fake://host.name:1234/path");

        result.Should().NotContain("://",
            because: "the derived name is used in file paths which cannot contain scheme separators");
        result.Should().NotContain("/",
            because: "the derived name is used as a filename, not a path");
    }

    [Fact]
    public void DeriveServerName_EmptyString_ReturnsUnknownServer()
    {
        var result = EvaluationPipelineService.DeriveServerName("");

        result.Should().Be("unknown-server",
            because: "empty input must fall back to a stable placeholder so report generation still has a filename");
    }
}
