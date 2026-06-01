// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Evaluate;

/// <summary>
/// Verifies RunAsync maps the evaluator's <see cref="EvaluationOutcome"/> to the
/// right process exit code: a genuine "could not evaluate" (no agent installed, or
/// an agent that left checks unscored) is a failure — exit 1 — so CI detects it
/// instead of seeing a silent success with no report; an intentional
/// bring-your-own-LLM stop (--eval-engine none) exits 0.
/// </summary>
public class EvaluationPipelineServiceExitCodeTests
{
    private static EvaluationPipelineService BuildSut(IChecklistEvaluator evaluator)
    {
        var tooling = Substitute.For<IAgent365ToolingService>();
        var discovery = Substitute.For<ISchemaDiscoveryService>();
        var checklistGen = Substitute.For<IChecklistGenerator>();
        var analyzer = Substitute.For<IEvaluationAnalyzer>();
        var report = Substitute.For<IReportGenerator>();

        // Fresh-run path: discovery + generation are stubbed so control reaches the
        // (mocked) evaluator, whose Outcome is the thing under test.
        discovery
            .DiscoverToolsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ToolSchema>());
        checklistGen
            .Generate(Arg.Any<List<ToolSchema>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new EvaluationChecklist());

        return new EvaluationPipelineService(
            NullLogger<EvaluationPipelineService>.Instance,
            discovery,
            checklistGen,
            evaluator,
            analyzer,
            report,
            tooling);
    }

    private static string FreshOutputDir()
        => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RunAsync_WhenEvaluationCouldNotComplete_ReturnsExitCode1()
    {
        var evaluator = Substitute.For<IChecklistEvaluator>();
        evaluator
            .EvaluateAsync(Arg.Any<EvaluationChecklist>(), Arg.Any<string>(), Arg.Any<EvalEngine>(), Arg.Any<CancellationToken>())
            .Returns(new ChecklistEvaluationResult { Outcome = EvaluationOutcome.CouldNotEvaluate });

        var sut = BuildSut(evaluator);

        var exitCode = await sut.RunAsync(
            serverUrl: "http://localhost:9999/mcp",
            outputDir: FreshOutputDir(),
            evalEngine: "claude-code",
            authToken: null,
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(1, because: "a requested evaluation that produced no report is a failure CI must be able to detect");
    }

    [Fact]
    public async Task RunAsync_WhenUserOptedOutWithEngineNone_ReturnsExitCode0()
    {
        var evaluator = Substitute.For<IChecklistEvaluator>();
        evaluator
            .EvaluateAsync(Arg.Any<EvaluationChecklist>(), Arg.Any<string>(), Arg.Any<EvalEngine>(), Arg.Any<CancellationToken>())
            .Returns(new ChecklistEvaluationResult { Outcome = EvaluationOutcome.OptedOut });

        var sut = BuildSut(evaluator);

        var exitCode = await sut.RunAsync(
            serverUrl: "http://localhost:9999/mcp",
            outputDir: FreshOutputDir(),
            evalEngine: "none",
            authToken: null,
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(0, because: "--eval-engine none is a deliberate bring-your-own-LLM stop, not a failure");
    }
}
