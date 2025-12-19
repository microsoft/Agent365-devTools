// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Commands.DevelopSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.MockToolingServer;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class MockToolingServerSubcommandTests : IDisposable
{
    private readonly ILogger _mockLogger;
    private readonly TestLogger _testLogger;
    private readonly IProcessService _mockProcessService;

    public MockToolingServerSubcommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _testLogger = new TestLogger();
        _mockProcessService = Substitute.For<IProcessService>();

        // Clear any previous state - this runs before each test
        _testLogger.LogCalls.Clear();
        _mockProcessService.ClearReceivedCalls();
    }

    public void Dispose()
    {
        // Cleanup after each test if needed
        _testLogger.LogCalls.Clear();
    }

    // Test logger that captures calls for verification
    private class TestLogger : ILogger
    {
        public List<(LogLevel Level, string Message, object[] Args)> LogCalls { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var args = state is IReadOnlyList<KeyValuePair<string, object?>> kvps
                ? kvps.Where(kvp => kvp.Key != "{OriginalFormat}").Select(kvp => kvp.Value ?? "").ToArray()
                : Array.Empty<object>();
            LogCalls.Add((logLevel, message, args));
        }
    }

    [Fact]
    public void CreateCommand_ReturnsCommandWithCorrectNames()
    {
        // Act
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockProcessService);

        // Assert
        Assert.Equal("start-mock-tooling-server", command.Name);
        Assert.Equal("Start the Mock Tooling Server for local development and testing", command.Description);
        Assert.Contains("mts", command.Aliases);
    }

    [Fact]
    public void CreateCommand_HasAllOptionsConfigured()
    {
        // Act
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockProcessService);

        // Assert
        Assert.Equal(4, command.Options.Count);

        // Port option
        var portOption = command.Options.First(o => o.Name == "port");
        Assert.Equal("port", portOption.Name);
        Assert.Contains("--port", portOption.Aliases);
        Assert.Contains("-p", portOption.Aliases);
        Assert.False(portOption.IsRequired);
        Assert.Equal("Port number to run the server on (default: 5309)", portOption.Description);

        // Verbose option
        var verboseOption = command.Options.First(o => o.Name == "verbose");
        Assert.Equal("verbose", verboseOption.Name);
        Assert.Contains("--verbose", verboseOption.Aliases);
        Assert.Contains("-v", verboseOption.Aliases);
        Assert.Equal("Enable verbose logging", verboseOption.Description);

        // Dry-run option
        var dryRunOption = command.Options.First(o => o.Name == "dry-run");
        Assert.Equal("dry-run", dryRunOption.Name);
        Assert.Contains("--dry-run", dryRunOption.Aliases);
        Assert.Equal("Show what would be done without executing", dryRunOption.Description);

        // Background option
        var backgroundOption = command.Options.First(o => o.Name == "background");
        Assert.Equal("background", backgroundOption.Name);
        Assert.Contains("--background", backgroundOption.Aliases);
        Assert.Contains("-bg", backgroundOption.Aliases);
        Assert.Equal("Run the server in the background (opens new terminal to run server)", backgroundOption.Description);
    }

    [Fact]
    public void CreateCommand_HasHandler()
    {
        // Arrange
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockProcessService);

        // Act & Assert
        Assert.NotNull(command);
        Assert.NotNull(command.Handler);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(100000)]
    public void ParseCommand_WithOutOfRangePort_AllowsParsingValidationOccursLater(int outOfRangePort)
    {
        // Arrange
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockProcessService);

        // Act
        var parseResult = command.Parse($"--port {outOfRangePort}");

        // Assert - Parsing should succeed; port validation happens in HandleStartServer method
        Assert.Empty(parseResult.Errors);
        var portValue = parseResult.GetValueForOption(command.Options.First(o => o.Name == "port"));
        Assert.Equal(outOfRangePort, portValue);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5309)]
    [InlineData(8080)]
    [InlineData(65535)]
    public void ParseCommand_WithValidPort_ParsesWithoutError(int validPort)
    {
        // Arrange
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockProcessService);

        // Act
        var parseResult = command.Parse($"--port {validPort}");

        // Assert
        Assert.Empty(parseResult.Errors);
        var portValue = parseResult.GetValueForOption(command.Options.First(o => o.Name == "port"));
        Assert.Equal(validPort, portValue);
    }

    [Fact]
    public void ParseCommand_WithoutPort_UsesDefaultValue()
    {
        // Arrange
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockProcessService);

        // Act
        var parseResult = command.Parse("");

        // Assert
        Assert.Empty(parseResult.Errors);
        var portValue = parseResult.GetValueForOption(command.Options.First(o => o.Name == "port"));
        Assert.Null(portValue); // Default value is handled in the handler, not the option
    }

    [Fact]
    public void ParseCommand_CanParseWithLongOption()
    {
        // Act
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockProcessService);
        var parseResult = command.Parse("--port 3000");

        // Assert
        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void ParseCommand_CanParseWithShortOption()
    {
        // Act
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockProcessService);
        var parseResult = command.Parse("-p 3000");

        // Assert
        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void ParseCommand_CanParseWithAlias()
    {
        // Arrange
        var rootCommand = new RootCommand();
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockProcessService);
        rootCommand.AddCommand(command);

        // Act
        var parseResult = rootCommand.Parse("mts --port 3000");

        // Assert
        Assert.Empty(parseResult.Errors);
        // When using an alias, the command name is still the original name, but we can verify the alias exists
        Assert.Contains("mts", parseResult.CommandResult.Command.Aliases);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12.5")]
    [InlineData("")]
    [InlineData(" ")]
    public void ParseCommand_WithInvalidPortValues_HasParseErrors(string invalidPortValue)
    {
        // Act
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockProcessService);
        var parseResult = command.Parse($"--port {invalidPortValue}");

        // Assert
        Assert.NotEmpty(parseResult.Errors);
    }

    [Fact]
    public void ParseCommand_WithoutArguments_ParsesSuccessfully()
    {
        // Act
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockProcessService);
        var parseResult = command.Parse("");

        // Assert
        Assert.Empty(parseResult.Errors);
    }

    // Handler Method Tests

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(100000)]
    public async Task HandleStartServer_WithInvalidPort_LogsError(int invalidPort)
    {
        // Act
        await MockToolingServerSubcommand.HandleStartServer(invalidPort, false, false, false, _testLogger, _mockProcessService);

        // Assert
        Assert.Single(_testLogger.LogCalls);
        var logCall = _testLogger.LogCalls.First();
        Assert.Equal(LogLevel.Error, logCall.Level);
        Assert.Contains("Invalid port number", logCall.Message);
        Assert.Contains(invalidPort.ToString(), logCall.Message);
    }

    [Fact]
    public async Task HandleStartServer_WithNullPort_UsesDefaultPort()
    {
        // Act - Start task but don't await (will be cancelled by timeout)
        // We're testing the initial log messages before Server.Start() blocks
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var task = Task.Run(async () =>
        {
            await MockToolingServerSubcommand.HandleStartServer(null, false, false, false, _testLogger, _mockProcessService);
        }, cts.Token);

        // Wait briefly for initial logging to occur
        await Task.Delay(100);

        // Assert - Should log foreground startup messages with default port
        Assert.NotEmpty(_testLogger.LogCalls);
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("Starting Up MockToolingServer."));

        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("Press Ctrl+C to stop the server."));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5309)]
    [InlineData(8080)]
    [InlineData(65535)]
    public async Task HandleStartServer_WithValidPort_LogsStartingMessage(int validPort)
    {
        // Act - Start task but don't await (will be cancelled by timeout)
        // We're testing the initial log messages before Server.Start() blocks
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var task = Task.Run(async () =>
        {
            await MockToolingServerSubcommand.HandleStartServer(validPort, false, false, false, _testLogger, _mockProcessService);
        }, cts.Token);

        // Wait briefly for initial logging to occur
        await Task.Delay(100);

        // Assert - Should log foreground startup messages
        Assert.NotEmpty(_testLogger.LogCalls);
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("Starting Up MockToolingServer."));

        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("Press Ctrl+C to stop the server."));
    }

    [Fact]
    public async Task HandleStartServer_WithInvalidPort_DoesNotAttemptStartup()
    {
        // Act
        await MockToolingServerSubcommand.HandleStartServer(0, false, false, false, _testLogger, _mockProcessService);

        // Assert - Should only log error and return early
        Assert.Single(_testLogger.LogCalls);
        var logCall = _testLogger.LogCalls.First();
        Assert.Equal(LogLevel.Error, logCall.Level);
        Assert.Contains("Invalid port number", logCall.Message);
    }



    [Fact]
    public async Task HandleStartServer_WhenTerminalLaunchSucceeds_LogsSuccessMessage()
    {
        // Arrange - Configure StartInNewTerminal to succeed
        _mockProcessService.StartInNewTerminal(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string>(), Arg.Any<ILogger>()).Returns(true);

        // Act - Use background=true to test new terminal behavior
        await MockToolingServerSubcommand.HandleStartServer(5309, false, false, true, _testLogger, _mockProcessService);

        // Assert - Verify server running message is logged
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("The server is running on http://localhost:5309 in a new terminal"));

        // Verify close terminal message
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("Close the new terminal window to stop the server."));
    }

    [Fact]
    public async Task HandleStartServer_WhenTerminalLaunchFails_LogsError()
    {
        // Arrange - Configure StartInNewTerminal to fail
        _mockProcessService.StartInNewTerminal(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string>(), Arg.Any<ILogger>()).Returns(false);

        // Act - Use background=true to test new terminal behavior
        await MockToolingServerSubcommand.HandleStartServer(5309, false, false, true, _testLogger, _mockProcessService);

        // Assert - Verify error is logged
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Error &&
            call.Message.Contains("Failed to start Mock Tooling Server in new terminal"));
    }

    // Verbose Mode Tests

    [Fact]
    public async Task HandleStartServer_WithVerboseTrue_LogsVerboseMessage()
    {
        // Arrange - Configure StartInNewTerminal to succeed
        _mockProcessService.StartInNewTerminal(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string>(), Arg.Any<ILogger>()).Returns(true);

        // Act - Use background=true to test new terminal behavior with verbose
        await MockToolingServerSubcommand.HandleStartServer(5309, true, false, true, _testLogger, _mockProcessService);

        // Assert - Should log verbose enabled message
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("Verbose logging enabled"));
    }

    // Dry Run Tests

    [Fact]
    public async Task HandleStartServer_WithDryRunTrue_LogsDryRunMessagesOnly()
    {
        // Act - Default foreground behavior (background=false)
        await MockToolingServerSubcommand.HandleStartServer(7000, false, true, false, _testLogger, _mockProcessService);

        // Assert - Should log dry run messages for foreground mode
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("[DRY RUN] Would start Mock Tooling Server on port 7000"));

        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("[DRY RUN] Would use verbose logging: False"));

        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("[DRY RUN] Background mode: False"));

        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("[DRY RUN] Would run MockToolingServer in foreground (blocking current terminal)"));

        // Should NOT attempt to start terminal
        _mockProcessService.DidNotReceive().StartInNewTerminal(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string>(), Arg.Any<ILogger>());
    }

    [Fact]
    public async Task HandleStartServer_WithDryRunTrueAndVerboseTrue_LogsBothFlags()
    {
        // Act - Default foreground behavior (background=false) with verbose
        await MockToolingServerSubcommand.HandleStartServer(6000, true, true, false, _testLogger, _mockProcessService);

        // Assert - Should log dry run message with verbose flag shown as True
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("[DRY RUN] Would use verbose logging: True"));

        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("[DRY RUN] Would start Mock Tooling Server on port 6000"));

        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("[DRY RUN] Background mode: False"));

        // Should NOT attempt actual execution
        _mockProcessService.DidNotReceive().StartInNewTerminal(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string>(), Arg.Any<ILogger>());
    }

    [Fact]
    public async Task HandleStartServer_WithDryRunTrueAndBackgroundTrue_LogsBackgroundDryRun()
    {
        // Act - Background mode (background=true) with dry run
        await MockToolingServerSubcommand.HandleStartServer(8000, false, true, true, _testLogger, _mockProcessService);

        // Assert - Should log dry run messages for background mode
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("[DRY RUN] Would start Mock Tooling Server on port 8000"));

        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("[DRY RUN] Would use verbose logging: False"));

        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("[DRY RUN] Background mode: True"));

        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("[DRY RUN] Would start in new terminal: a365"));

        // Should NOT attempt to start terminal
        _mockProcessService.DidNotReceive().StartInNewTerminal(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string>(), Arg.Any<ILogger>());
    }

    [Fact]
    public async Task HandleStartServer_WithBackgroundMode_StartsInNewTerminal()
    {
        // Arrange - Configure StartInNewTerminal to succeed
        _mockProcessService.StartInNewTerminal(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string>(), Arg.Any<ILogger>()).Returns(true);

        // Act - Background mode (background=true)
        await MockToolingServerSubcommand.HandleStartServer(9000, false, false, true, _testLogger, _mockProcessService);

        // Assert - Should start in new terminal and log success
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("The server is running on http://localhost:9000 in a new terminal"));

        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("Close the new terminal window to stop the server."));

        // Should attempt to start new terminal
        _mockProcessService.Received(1).StartInNewTerminal(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string>(), Arg.Any<ILogger>());
    }
}
