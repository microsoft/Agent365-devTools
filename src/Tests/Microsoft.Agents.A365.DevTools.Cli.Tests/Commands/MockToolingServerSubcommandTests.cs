// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Commands.DevelopSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class MockToolingServerSubcommandTests : IDisposable
{
    private readonly ILogger _mockLogger;
    private readonly CommandExecutor _mockCommandExecutor;
    private readonly TestLogger _testLogger;
    private readonly IProcessService _mockProcessService;

    public MockToolingServerSubcommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _testLogger = new TestLogger();
        _mockProcessService = Substitute.For<IProcessService>();

        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockCommandExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);

        // ALWAYS configure CommandExecutor to return mock result to prevent accidental server startup
        var defaultMockResult = new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult
        {
            ExitCode = 0,
            StandardOutput = "Mock server output",
            StandardError = ""
        };
        _mockCommandExecutor.ExecuteWithStreamingAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(defaultMockResult));

        // Clear any previous state - this runs before each test
        _testLogger.LogCalls.Clear();
        _mockProcessService.ClearReceivedCalls();
        _mockCommandExecutor.ClearReceivedCalls();
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
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);

        // Assert
        Assert.Equal("start-mock-tooling-server", command.Name);
        Assert.Equal("Start the Mock Tooling Server for local development and testing", command.Description);
        Assert.Contains("mts", command.Aliases);
    }

    [Fact]
    public void CreateCommand_HasPortOptionConfigured()
    {
        // Act
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);

        // Assert
        Assert.Single(command.Options);

        var portOption = command.Options.First();
        Assert.Equal("port", portOption.Name);
        Assert.Contains("--port", portOption.Aliases);
        Assert.Contains("-p", portOption.Aliases);
        Assert.False(portOption.IsRequired);
        Assert.Equal("Port number to run the server on (default: 5309)", portOption.Description);
    }

    [Fact]
    public void CreateCommand_HasHandler()
    {
        // Arrange
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);

        // Act & Assert - Just verify the command can be created and doesn't throw during basic operations
        Assert.NotNull(command);
        Assert.NotNull(command.Handler);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(100000)]
    public void ParseCommand_WithInvalidPort_ParsesWithoutError(int invalidPort)
    {
        // Arrange
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);

        // Act
        var parseResult = command.Parse($"--port {invalidPort}");

        // Assert
        Assert.Empty(parseResult.Errors);
        var portValue = parseResult.GetValueForOption(command.Options.First());
        Assert.Equal(invalidPort, portValue);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5309)]
    [InlineData(8080)]
    [InlineData(65535)]
    public void ParseCommand_WithValidPort_ParsesWithoutError(int validPort)
    {
        // Arrange
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);

        // Act
        var parseResult = command.Parse($"--port {validPort}");

        // Assert
        Assert.Empty(parseResult.Errors);
        var portValue = parseResult.GetValueForOption(command.Options.First());
        Assert.Equal(validPort, portValue);
    }

    [Fact]
    public void ParseCommand_WithoutPort_UsesDefaultValue()
    {
        // Arrange
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);

        // Act
        var parseResult = command.Parse("");

        // Assert
        Assert.Empty(parseResult.Errors);
        var portValue = parseResult.GetValueForOption(command.Options.First());
        Assert.Null(portValue); // Default value is handled in the handler, not the option
    }

    [Fact]
    public void ParseCommand_CanParseWithLongOption()
    {
        // Act
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);
        var parseResult = command.Parse("--port 3000");

        // Assert
        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void ParseCommand_CanParseWithShortOption()
    {
        // Act
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);
        var parseResult = command.Parse("-p 3000");

        // Assert
        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void ParseCommand_CanParseWithAlias()
    {
        // Arrange
        var rootCommand = new RootCommand();
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);
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
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);
        var parseResult = command.Parse($"--port {invalidPortValue}");

        // Assert
        Assert.NotEmpty(parseResult.Errors);
    }

    [Fact]
    public void ParseCommand_WithoutArguments_ParsesSuccessfully()
    {
        // Act
        var command = MockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);
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
        await MockToolingServerSubcommand.HandleStartServer(invalidPort, _testLogger, _mockCommandExecutor, _mockProcessService);

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
        // Arrange - Configure StartInNewTerminal to succeed
        _mockProcessService.StartInNewTerminal(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ILogger>()).Returns(true);

        // Act
        await MockToolingServerSubcommand.HandleStartServer(null, _testLogger, _mockCommandExecutor, _mockProcessService);

        // Assert - Should log starting message with default port
        Assert.NotEmpty(_testLogger.LogCalls);
        var firstLogCall = _testLogger.LogCalls.First();
        Assert.Equal(LogLevel.Information, firstLogCall.Level);
        Assert.Contains("Starting Mock Tooling Server", firstLogCall.Message);
        Assert.Contains("5309", firstLogCall.Message);

        // Verify terminal launch was successful
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("Mock Tooling Server started successfully in a new terminal window"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5309)]
    [InlineData(8080)]
    [InlineData(65535)]
    public async Task HandleStartServer_WithValidPort_LogsStartingMessage(int validPort)
    {
        // Arrange - Configure StartInNewTerminal to succeed
        _mockProcessService.StartInNewTerminal(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ILogger>()).Returns(true);

        // Act
        await MockToolingServerSubcommand.HandleStartServer(validPort, _testLogger, _mockCommandExecutor, _mockProcessService);

        // Assert - Should log starting message with specified port
        Assert.NotEmpty(_testLogger.LogCalls);
        var firstLogCall = _testLogger.LogCalls.First();
        Assert.Equal(LogLevel.Information, firstLogCall.Level);
        Assert.Contains("Starting Mock Tooling Server", firstLogCall.Message);
        Assert.Contains(validPort.ToString(), firstLogCall.Message);

        // Verify terminal launch was successful
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("Mock Tooling Server started successfully in a new terminal window"));
    }

    [Fact]
    public async Task HandleStartServer_WithInvalidPort_DoesNotAttemptStartup()
    {
        // Act
        await MockToolingServerSubcommand.HandleStartServer(0, _testLogger, _mockCommandExecutor, _mockProcessService);

        // Assert - Should only log error and return early
        Assert.Single(_testLogger.LogCalls);
        var logCall = _testLogger.LogCalls.First();
        Assert.Equal(LogLevel.Error, logCall.Level);
        Assert.Contains("Invalid port number", logCall.Message);
    }

    [Fact]
    public async Task HandleStartServer_WhenTerminalLaunchFails_LogsWarningAndAttemptsFallback()
    {
        // Arrange - Configure StartInNewTerminal to fail
        // CommandExecutor is already configured in constructor to return mock result
        _mockProcessService.StartInNewTerminal(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ILogger>()).Returns(false);

        // Act
        await MockToolingServerSubcommand.HandleStartServer(5309, _testLogger, _mockCommandExecutor, _mockProcessService);

        // Assert - Verify specific sequence of expected log messages
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("Starting Mock Tooling Server on port 5309"));

        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Warning &&
            call.Message.Contains("Failed to start Mock Tooling Server in a new terminal window"));

        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("Falling back to running server in current terminal"));

        // Verify CommandExecutor was called for fallback
        await _mockCommandExecutor.Received(1).ExecuteWithStreamingAsync(
            Arg.Is<string>(cmd => cmd == "dotnet"),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<string>(prefix => prefix == "MockServer: "),
            Arg.Is<bool>(interactive => interactive),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleStartServer_WhenTerminalLaunchSucceeds_DoesNotUseFallback()
    {
        // Arrange - Configure StartInNewTerminal to succeed
        _mockProcessService.StartInNewTerminal(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ILogger>()).Returns(true);

        // Act
        await MockToolingServerSubcommand.HandleStartServer(5309, _testLogger, _mockCommandExecutor, _mockProcessService);

        // Assert - Verify server starting message is logged
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("Starting Mock Tooling Server on port 5309"));

        // Verify successful terminal launch
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("Mock Tooling Server started successfully in a new terminal window"));

        // Verify CommandExecutor was NOT called (no fallback)
        await _mockCommandExecutor.DidNotReceive().ExecuteWithStreamingAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleStartServer_WhenFallbackCommandFails_LogsError()
    {
        // Arrange - Configure StartInNewTerminal to fail, and override CommandExecutor to also fail
        var failedResult = new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 1, StandardOutput = "", StandardError = "Server failed to start" };
        _mockProcessService.StartInNewTerminal(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ILogger>()).Returns(false);
        _mockCommandExecutor.ExecuteWithStreamingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(failedResult));

        // Act
        await MockToolingServerSubcommand.HandleStartServer(5309, _testLogger, _mockCommandExecutor, _mockProcessService);

        // Assert - Verify error logging sequence
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Warning &&
            call.Message.Contains("Failed to start Mock Tooling Server in a new terminal window"));

        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("Falling back to running server in current terminal"));

        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Error &&
            call.Message.Contains("Failed to start Mock Tooling Server"));
    }
}
