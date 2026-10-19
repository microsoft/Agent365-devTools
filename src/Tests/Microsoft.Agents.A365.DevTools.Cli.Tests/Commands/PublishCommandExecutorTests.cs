// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Invocation tests for the publish subcommand's --secret-lifetime-months pre-flight range
/// validation. Exercises the [1, 24] guard in <see cref="PublishCommandExecutor"/> via the full
/// System.CommandLine pipeline so the resulting exit code is asserted as the user would observe it.
/// </summary>
public class PublishCommandExecutorTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("25")]
    [InlineData("-1")]
    [InlineData("48")]
    public async Task Publish_WithOutOfRangeSecretLifetimeMonths_ReturnsExitCode1AndDoesNotCallTooling(string lifetimeArg)
    {
        // Arrange
        var logger = Substitute.For<ILogger>();
        var toolingService = Substitute.For<IAgent365ToolingService>();
        var command = DevelopMcpCommand.CreateCommand(logger, toolingService, graphApiService: null);

        var args = new[]
        {
            "publish",
            "--environment-id", "env-123",
            "--server-name", "Test_Server",
            "--alias", "testalias",
            "--display-name", "Test Display",
            "--yes",
            "--secret-lifetime-months", lifetimeArg,
        };

        // Act
        var exitCode = await command.InvokeAsync(args);

        // Assert — exit code surfaces failure as the user would observe it
        exitCode.Should().Be(1, because: "an out-of-range secret lifetime must fail publish before any Entra app is created or the platform is called");

        // Assert — error log names the valid range and the rejected value so the user can recover
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null
                && state.ToString()!.Contains("--secret-lifetime-months")
                && state.ToString()!.Contains("between 1 and 24")
                && state.ToString()!.Contains($"Got: {lifetimeArg}")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Assert — validation short-circuits before any downstream publish call
        await toolingService.DidNotReceive().PublishServerAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Microsoft.Agents.A365.DevTools.Cli.Models.PublishMcpServerRequest>(),
            Arg.Any<CancellationToken>());
    }
}
