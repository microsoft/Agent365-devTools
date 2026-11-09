// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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
    [InlineData("github-copilot", EvalEngine.GitHubCopilot)]
    [InlineData("GITHUB-COPILOT", EvalEngine.GitHubCopilot)]
    [InlineData("claude-code", EvalEngine.ClaudeCode)]
    [InlineData("Claude-Code", EvalEngine.ClaudeCode)]
    [InlineData("azure-openai", EvalEngine.AzureOpenAI)]
    [InlineData("Azure-OpenAI", EvalEngine.AzureOpenAI)]
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

    // -----------------------------------------------------------------------
    // Trust-boundary warning (RunAsync)
    //
    // The warning fires immediately after ParseEvalEngine and before any server
    // discovery, so a real pipeline with a substitute logger and mocked
    // sub-dependencies surfaces it without contacting any MCP server or Azure
    // OpenAI endpoint. The evaluator is stubbed to report CouldNotEvaluate so the
    // pipeline short-circuits (exit 1) right after the warning, never reaching
    // analysis or report generation.
    // -----------------------------------------------------------------------

    private static EvaluationPipelineService BuildPipelineWithEvaluatorOutcome(
        ILogger<EvaluationPipelineService> logger,
        EvaluationOutcome outcome)
    {
        var evaluator = Substitute.For<IChecklistEvaluator>();
        evaluator
            .EvaluateAsync(Arg.Any<EvaluationChecklist>(), Arg.Any<string>(), Arg.Any<EvalEngine>(), Arg.Any<CancellationToken>())
            .Returns(call => new ChecklistEvaluationResult
            {
                Checklist = call.Arg<EvaluationChecklist>(),
                Outcome = outcome,
            });

        var generator = Substitute.For<IChecklistGenerator>();
        generator
            .Generate(Arg.Any<List<ToolSchema>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new EvaluationChecklist());

        // Return an empty (non-null) tool list so discovery logging does not NRE; the run still
        // short-circuits at the evaluator outcome, which is all this warning test exercises.
        var discovery = Substitute.For<ISchemaDiscoveryService>();
        discovery
            .DiscoverToolsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ToolSchema>());

        return new EvaluationPipelineService(
            logger,
            discovery,
            generator,
            evaluator,
            Substitute.For<IEvaluationAnalyzer>(),
            Substitute.For<IReportGenerator>(),
            Substitute.For<IAgent365ToolingService>());
    }

    private static void ReceivedWarningContaining(ILogger<EvaluationPipelineService> logger, string fragment) =>
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains(fragment)),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

    private static void DidNotReceiveWarningContaining(ILogger<EvaluationPipelineService> logger, string fragment) =>
        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains(fragment)),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

    // A unique, non-existent output directory so the pipeline always takes the fresh-discovery
    // path (a leftover "<host>_checklist.json" on disk would otherwise flip it to the resume
    // path). Nothing is written here: the stubbed evaluator short-circuits the run before report
    // generation, so no cleanup is needed.
    private static string UniqueOutputDir() =>
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RunAsync_AzureOpenAiEngine_WarnsAboutAzureOpenAiEndpointNotLocalAgent()
    {
        var logger = Substitute.For<ILogger<EvaluationPipelineService>>();
        var pipeline = BuildPipelineWithEvaluatorOutcome(logger, EvaluationOutcome.CouldNotEvaluate);

        var exitCode = await pipeline.RunAsync(
            "http://localhost/mcp", UniqueOutputDir(), "azure-openai", authToken: null, CancellationToken.None);

        exitCode.Should().Be(1,
            because: "a CouldNotEvaluate outcome means scoring did not happen as requested, so the run must fail");
        ReceivedWarningContaining(logger, "Azure OpenAI endpoint");
        DidNotReceiveWarningContaining(logger, "locally running coding agent");
    }

    [Fact]
    public async Task RunAsync_LocalAgentEngine_WarnsAboutLocalAgentNotAzureOpenAi()
    {
        var logger = Substitute.For<ILogger<EvaluationPipelineService>>();
        var pipeline = BuildPipelineWithEvaluatorOutcome(logger, EvaluationOutcome.CouldNotEvaluate);

        var exitCode = await pipeline.RunAsync(
            "http://localhost/mcp", UniqueOutputDir(), "claude-code", authToken: null, CancellationToken.None);

        exitCode.Should().Be(1,
            because: "a CouldNotEvaluate outcome means scoring did not happen as requested, so the run must fail");
        ReceivedWarningContaining(logger, "locally running coding agent");
        DidNotReceiveWarningContaining(logger, "Azure OpenAI endpoint");
    }
}
