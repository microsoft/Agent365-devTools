// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

[CollectionDefinition("EvaluateCommandInvocation", DisableParallelization = true)]
public class EvaluateCommandInvocationCollection { }

/// <summary>
/// Invocation-level tests for the develop-mcp evaluate subcommand — exercises the handler
/// via <see cref="System.CommandLine.Command"/>.InvokeAsync to confirm exit-code propagation
/// and --auth-token secret handling. Non-parallel because the env-var fallback test mutates
/// the process-wide A365_MCP_AUTH_TOKEN.
/// </summary>
[Collection("EvaluateCommandInvocation")]
public class EvaluateCommandInvocationTests
{
    private const string AuthTokenEnvVar = "A365_MCP_AUTH_TOKEN";

    private static Command GetEvaluateSubcommand(ILogger logger, IEvaluationPipelineService pipeline)
    {
        var parent = DevelopMcpCommand.CreateCommand(logger, Substitute.For<IAgent365ToolingService>(), pipeline);
        return parent.Subcommands.First(sc => sc.Name == "evaluate");
    }

    private static IEvaluationPipelineService PipelineReturning(int exitCode)
    {
        var pipeline = Substitute.For<IEvaluationPipelineService>();
        pipeline
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(exitCode);
        return pipeline;
    }

    [Fact]
    public async Task InvokeAsync_SuccessfulRun_PropagatesRunAsyncExitCode()
    {
        var command = GetEvaluateSubcommand(Substitute.For<ILogger>(), PipelineReturning(0));

        var exitCode = await command.InvokeAsync("--server-url http://localhost/mcp --eval-engine none");

        exitCode.Should().Be(0, because: "a successful RunAsync (0) must propagate to the process exit code");
    }

    [Fact]
    public async Task InvokeAsync_FailedRun_PropagatesNonZeroExitCode()
    {
        var command = GetEvaluateSubcommand(Substitute.For<ILogger>(), PipelineReturning(1));

        var exitCode = await command.InvokeAsync("--server-url http://localhost/mcp --eval-engine claude-code");

        exitCode.Should().Be(1, because: "RunAsync's failure exit code must propagate so CI detects it");
    }

    [Fact]
    public async Task InvokeAsync_WithAuthTokenFlag_LogsExposureWarning()
    {
        var logger = Substitute.For<ILogger>();
        var command = GetEvaluateSubcommand(logger, PipelineReturning(0));

        await command.InvokeAsync("--server-url http://localhost/mcp --eval-engine none --auth-token SECRET123");

        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("A365_MCP_AUTH_TOKEN")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task InvokeAsync_NoFlag_ReadsTokenFromEnvironmentVariable()
    {
        var original = Environment.GetEnvironmentVariable(AuthTokenEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AuthTokenEnvVar, "env-token-xyz");
            var pipeline = PipelineReturning(0);
            var command = GetEvaluateSubcommand(Substitute.For<ILogger>(), pipeline);

            await command.InvokeAsync("--server-url http://localhost/mcp --eval-engine none");

            await pipeline.Received(1).RunAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                "env-token-xyz",
                Arg.Any<CancellationToken>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuthTokenEnvVar, original);
        }
    }

    // The bare Command.InvokeAsync(string) shortcut used above wraps every thrown exception
    // as exit code 1 via System.CommandLine's default handler, so it cannot observe an
    // EvaluationException's ExitCode (3). The tests below build a parser that mirrors the
    // exception-to-exit-code mapping in Program.cs (Agent365Exception.ExitCode) plus its
    // parse-error short-circuit, and drive a real EvaluationPipelineService (sub-dependencies
    // mocked) so ParseEvalEngine and the output-dir guard actually run. This pins the
    // invocation-layer exit codes against a refactor of the SetHandler wiring that would
    // otherwise break those branches with all tests still green.
    private static Parser BuildFaithfulParser(Command evaluate) =>
        new CommandLineBuilder(evaluate)
            .UseExceptionHandler((exception, context) =>
                context.ExitCode = exception is Agent365Exception agentEx ? agentEx.ExitCode : 1)
            .AddMiddleware(async (context, next) =>
            {
                // Mirror Program.cs: a parse error (e.g. a missing required option) exits
                // non-zero before the command handler runs.
                if (context.ParseResult.Errors.Count > 0)
                {
                    context.ExitCode = 1;
                    return;
                }

                await next(context);
            }, MiddlewareOrder.ErrorReporting)
            .Build();

    // Real pipeline so ParseEvalEngine and the output-dir guard execute; the sub-dependencies
    // are mocked because the failure paths under test throw or return before reaching them.
    private static IEvaluationPipelineService RealPipeline() =>
        new EvaluationPipelineService(
            NullLogger<EvaluationPipelineService>.Instance,
            Substitute.For<ISchemaDiscoveryService>(),
            Substitute.For<IChecklistGenerator>(),
            Substitute.For<IChecklistEvaluator>(),
            Substitute.For<IEvaluationAnalyzer>(),
            Substitute.For<IReportGenerator>(),
            Substitute.For<IAgent365ToolingService>());

    [Fact]
    public async Task InvokeAsync_InvalidEvalEngine_ReturnsEvaluationExceptionExitCode3()
    {
        var evaluate = GetEvaluateSubcommand(Substitute.For<ILogger>(), RealPipeline());
        var parser = BuildFaithfulParser(evaluate);

        var exitCode = await parser.InvokeAsync(
            new[] { "--server-url", "http://localhost/mcp", "--eval-engine", "bogus" });

        exitCode.Should().Be(3, because: "an unknown --eval-engine raises an EvaluationException, whose ExitCode (3) the real exception handler must propagate to the process");
    }

    [Fact]
    public async Task InvokeAsync_MissingServerUrl_ReturnsExitCode1()
    {
        var evaluate = GetEvaluateSubcommand(Substitute.For<ILogger>(), PipelineReturning(0));
        var parser = BuildFaithfulParser(evaluate);

        var exitCode = await parser.InvokeAsync(new[] { "--eval-engine", "none" });

        exitCode.Should().Be(1, because: "--server-url is a required option; the missing-required-option parse error must exit non-zero without invoking the handler");
    }

    [Fact]
    public async Task InvokeAsync_WhitespaceOutputDir_ReturnsExitCode1()
    {
        var evaluate = GetEvaluateSubcommand(Substitute.For<ILogger>(), RealPipeline());
        var parser = BuildFaithfulParser(evaluate);

        var exitCode = await parser.InvokeAsync(
            new[] { "--server-url", "http://localhost/mcp", "--output-dir", "   ", "--eval-engine", "none" });

        exitCode.Should().Be(1, because: "an empty or whitespace --output-dir must fail fast with exit code 1 through the real handler wiring, not a deep exception later in the run");
    }

    [Fact]
    public async Task InvokeAsync_ValidArguments_ForwardsEachOptionToRunAsync()
    {
        var pipeline = PipelineReturning(0);
        var evaluate = GetEvaluateSubcommand(Substitute.For<ILogger>(), pipeline);
        var parser = BuildFaithfulParser(evaluate);

        var exitCode = await parser.InvokeAsync(
            new[] { "--server-url", "http://localhost:5000/mcp", "--output-dir", "out", "--eval-engine", "claude-code" });

        exitCode.Should().Be(0, because: "RunAsync returned 0, which the handler must propagate as the process exit code");
        await pipeline.Received(1).RunAsync(
            "http://localhost:5000/mcp",
            "out",
            "claude-code",
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }
}
