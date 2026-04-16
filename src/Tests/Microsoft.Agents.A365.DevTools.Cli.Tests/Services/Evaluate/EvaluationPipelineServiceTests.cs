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

        result.Should().Be("my-server-com");
    }

    [Fact]
    public void DeriveServerName_UrlWithNonStandardPort_IncludesPort()
    {
        var result = EvaluationPipelineService.DeriveServerName("http://localhost:3000/mcp");

        result.Should().Be("localhost-3000");
    }

    [Fact]
    public void DeriveServerName_UrlWithDefaultPort_ExcludesPort()
    {
        var result = EvaluationPipelineService.DeriveServerName("http://example.com/mcp");

        result.Should().Be("example-com");
    }

    [Fact]
    public void DeriveServerName_InvalidUri_ReturnsSanitizedFallback()
    {
        var result = EvaluationPipelineService.DeriveServerName("not a valid uri");

        result.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DeriveServerName_InvalidUriWithSpecialChars_ReplacesSpecialChars()
    {
        var result = EvaluationPipelineService.DeriveServerName("fake://host.name:1234/path");

        result.Should().NotContain("://");
        result.Should().NotContain("/");
    }

    [Fact]
    public void DeriveServerName_EmptyString_ReturnsUnknownServer()
    {
        var result = EvaluationPipelineService.DeriveServerName("");

        result.Should().Be("unknown-server");
    }
}
