// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Microsoft.Extensions.Logging;
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
}
