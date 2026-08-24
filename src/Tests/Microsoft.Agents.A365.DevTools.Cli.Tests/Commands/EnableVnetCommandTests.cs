// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class EnableVnetCommandTests
{
    private readonly CapturingLogger _logger = new();
    private readonly IAgent365ToolingService _toolingService =
        Substitute.For<IAgent365ToolingService>();

    [Fact]
    public async Task EnableVnet_ServiceSucceeds_ExitsZeroAndWritesSuccessOutput()
    {
        _toolingService.EnableVnetAsync(Arg.Any<CancellationToken>()).Returns(true);
        var command = DevelopMcpCommand.CreateCommand(_logger, _toolingService);

        var exitCode = await command.InvokeAsync("enable-vnet");

        exitCode.Should().Be(
            0,
            because: "a successful enable-vnet operation must exit successfully");
        _logger.Messages.Should().Contain(
            "Virtual network support for external MCP servers enabled successfully.",
            because: "users must receive confirmation after the platform enables VNet support");
        await _toolingService.Received(1).EnableVnetAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnableVnet_ServiceFails_ExitsOneAndWritesErrorOutput()
    {
        _toolingService.EnableVnetAsync(Arg.Any<CancellationToken>()).Returns(false);
        var command = DevelopMcpCommand.CreateCommand(_logger, _toolingService);

        var exitCode = await command.InvokeAsync("enable-vnet");

        exitCode.Should().Be(
            1,
            because: "a failed platform operation must produce a nonzero exit code");
        _logger.Messages.Should().Contain(
            "Failed to enable virtual network support for external MCP servers.",
            because: "users must receive an actionable failure message");
        await _toolingService.Received(1).EnableVnetAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnableVnet_DryRun_ExitsZeroWithoutCallingService()
    {
        var command = DevelopMcpCommand.CreateCommand(_logger, _toolingService);

        var exitCode = await command.InvokeAsync("enable-vnet --dry-run");

        exitCode.Should().Be(
            0,
            because: "dry-run validation should complete successfully");
        _logger.Messages.Should().Contain(
            "[DRY RUN] Would enable virtual network support for external MCP servers",
            because: "dry-run output must describe the operation without executing it");
        await _toolingService.DidNotReceive().EnableVnetAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("enable-vnet unexpected-argument")]
    [InlineData("enable-vnet --unsupported-option")]
    public async Task EnableVnet_UnsupportedInput_ExitsWithErrorWithoutCallingService(string input)
    {
        var command = DevelopMcpCommand.CreateCommand(_logger, _toolingService);

        var exitCode = await command.InvokeAsync(input);

        exitCode.Should().Be(
            1,
            because: "unsupported input must return the standard CLI failure exit code");
        await _toolingService.DidNotReceive().EnableVnetAsync(Arg.Any<CancellationToken>());
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
