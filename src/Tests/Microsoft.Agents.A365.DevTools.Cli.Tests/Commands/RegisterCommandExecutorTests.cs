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
/// Invocation tests for the register-external-mcp-server command's pre-flight validation of
/// --secret-lifetime-months and --connectivity. Exercises the guards in
/// <see cref="RegisterCommandExecutor"/> via the full System.CommandLine pipeline so the
/// resulting exit code is asserted as the user would observe it.
/// </summary>
public class RegisterCommandExecutorTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("25")]
    [InlineData("-1")]
    [InlineData("48")]
    public async Task RegisterExternalMcpServer_WithOutOfRangeSecretLifetimeMonths_ReturnsExitCode1AndDoesNotCallTooling(string lifetimeArg)
    {
        // Arrange
        var logger = Substitute.For<ILogger>();
        var toolingService = Substitute.For<IAgent365ToolingService>();
        var command = DevelopMcpCommand.CreateCommand(logger, toolingService, graphApiService: null);

        var args = new[]
        {
            "register-external-mcp-server",
            "--server-name", "ext_Test",
            "--server-url", "https://example.com/mcp",
            "--secret-lifetime-months", lifetimeArg,
        };

        // Act
        var exitCode = await command.InvokeAsync(args);

        // Assert — exit code surfaces failure as the user would observe it
        exitCode.Should().Be(1);

        // Assert — error log mentions the valid range so the user knows how to recover
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null
                && state.ToString()!.Contains("--secret-lifetime-months")
                && state.ToString()!.Contains("between 1 and 24")
                && state.ToString()!.Contains($"Got: {lifetimeArg}")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Assert — validation short-circuits before any downstream tooling call
        await toolingService.DidNotReceive().AddMcpServerAsync(
            Arg.Any<Microsoft.Agents.A365.DevTools.Cli.Models.AddMcpServerRequest>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await toolingService.DidNotReceive().LogRegisterUsageAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("publik")]
    [InlineData("vnet")]
    [InlineData("Public Internet")]
    public async Task RegisterExternalMcpServer_WithInvalidConnectivity_ReturnsExitCode1AndDoesNotCallTooling(string connectivityArg)
    {
        // Arrange
        var logger = Substitute.For<ILogger>();
        var toolingService = Substitute.For<IAgent365ToolingService>();
        var command = DevelopMcpCommand.CreateCommand(logger, toolingService, graphApiService: null);

        var args = new[]
        {
            "register-external-mcp-server",
            "--server-name", "ext_Test",
            "--server-url", "https://example.com/mcp",
            "--connectivity", connectivityArg,
        };

        // Act
        var exitCode = await command.InvokeAsync(args);

        // Assert — exit code surfaces failure as the user would observe it
        exitCode.Should().Be(1);

        // Assert — error log names the option and both accepted values
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null
                && state.ToString()!.Contains("--connectivity")
                && state.ToString()!.Contains("public")
                && state.ToString()!.Contains("private")
                && state.ToString()!.Contains($"Got: {connectivityArg.Trim()}")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Assert — validation short-circuits before any downstream tooling call
        await toolingService.DidNotReceive().AddMcpServerAsync(
            Arg.Any<Microsoft.Agents.A365.DevTools.Cli.Models.AddMcpServerRequest>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await toolingService.DidNotReceive().LogRegisterUsageAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("public")]
    [InlineData("PUBLIC")]
    [InlineData("Public ")]
    [InlineData(" private")]
    public async Task RegisterExternalMcpServer_WithValidConnectivity_PassesConnectivityGuardAndReachesAuthTypeValidation(string connectivityArg)
    {
        // Arrange — a deliberately invalid --auth-type stops the run at the guard immediately
        // after the connectivity guard, so the run terminates without prompting and reaching
        // that guard proves connectivity was accepted.
        var logger = Substitute.For<ILogger>();
        var toolingService = Substitute.For<IAgent365ToolingService>();
        var command = DevelopMcpCommand.CreateCommand(logger, toolingService, graphApiService: null);

        var args = new[]
        {
            "register-external-mcp-server",
            "--server-name", "ext_Test",
            "--server-url", "https://example.com/mcp",
            "--connectivity", connectivityArg,
            "--auth-type", "NotAnAuthType",
        };

        // Act
        var exitCode = await command.InvokeAsync(args);

        // Assert
        exitCode.Should().Be(1);

        // Assert — surrounding whitespace and casing are tolerated, so the connectivity guard
        // never fires
        logger.DidNotReceive().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null
                && state.ToString()!.Contains("--connectivity")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Assert — execution reached the next guard, which is what stopped the run
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null
                && state.ToString()!.Contains("Invalid auth type")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
