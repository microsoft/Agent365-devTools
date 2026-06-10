// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Evaluate;

/// <summary>
/// Verifies the evaluation pipeline fires the user-telemetry marker at the start of RunAsync,
/// so any future surface that drives evaluations (not just the CLI command) is also attributed.
/// </summary>
public class EvaluationPipelineServiceTelemetryTests
{
    [Fact]
    public async Task RunAsync_FiresEvaluateTelemetry_BeforeAnyPipelineWork()
    {
        var tooling = Substitute.For<IAgent365ToolingService>();
        var discovery = Substitute.For<ISchemaDiscoveryService>();
        var checklistGen = Substitute.For<IChecklistGenerator>();
        var evaluator = Substitute.For<IChecklistEvaluator>();
        var analyzer = Substitute.For<IEvaluationAnalyzer>();
        var report = Substitute.For<IReportGenerator>();

        // Short-circuit RunAsync immediately after the telemetry call so we don't need
        // to stub the full pipeline. Discovery is the first dep called after telemetry;
        // throwing here forces the catch-block to wrap and re-throw as EvaluationException.
        discovery
            .DiscoverToolsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<List<ToolSchema>>>(_ => throw new InvalidOperationException("short-circuit"));

        var sut = new EvaluationPipelineService(
            NullLogger<EvaluationPipelineService>.Instance,
            discovery,
            checklistGen,
            evaluator,
            analyzer,
            report,
            tooling);

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var act = async () => await sut.RunAsync(
            serverUrl: "http://localhost:9999/mcp",
            outputDir: tempDir,
            evalEngine: "none",
            authToken: null,
            cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<EvaluationException>(
            because: "discovery throws after the telemetry call has fired; RunAsync wraps it");

        // Privacy boundary: telemetry call carries no customer content — only that an
        // evaluation was kicked off. The serverUrl above is the customer's MCP endpoint
        // and intentionally does NOT appear in the LogEvaluateUsageAsync call signature.
        await tooling.Received(1).LogEvaluateUsageAsync(Arg.Any<CancellationToken>());
    }
}
