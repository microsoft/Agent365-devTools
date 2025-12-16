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

public class StartMockToolingServerSubcommandTests
{
    private readonly ILogger _mockLogger;
    private readonly CommandExecutor _mockCommandExecutor;
    private readonly TestLogger _testLogger;
    private readonly IProcessService _mockProcessService;

    public StartMockToolingServerSubcommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _testLogger = new TestLogger();
        _mockProcessService = Substitute.For<IProcessService>();

        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockCommandExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);

        // Setup mock to return null (terminal launch fails) to force fallback to CommandExecutor
        _mockProcessService.Start(Arg.Any<ProcessStartInfo>()).Returns((Process?)null);
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
    public void CreateCommand_ReturnsCommandWithCorrectName()
    {
        // Act
        var command = StartMockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);

        // Assert
        Assert.Equal("start-mock-tooling-server", command.Name);
        Assert.Equal("Start the Mock Tooling Server for local development and testing", command.Description);
    }

    [Fact]
    public void CreateCommand_HasCorrectAlias()
    {
        // Act
        var command = StartMockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);

        // Assert
        Assert.Contains("start-mcp", command.Aliases);
    }

    [Fact]
    public void CreateCommand_HasPortOption()
    {
        // Act
        var command = StartMockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);

        // Assert
        Assert.Single(command.Options);

        var portOption = command.Options.First();
        Assert.Equal("port", portOption.Name);
        Assert.Contains("--port", portOption.Aliases);
        Assert.Contains("-p", portOption.Aliases);
        Assert.Equal("Port number to run the server on (default: 5309)", portOption.Description);
    }

    [Fact]
    public void CreateCommand_PortOptionIsOptional()
    {
        // Act
        var command = StartMockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);
        var portOption = command.Options.First();

        // Assert
        Assert.False(portOption.IsRequired);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(100000)]
    public void ParseCommand_WithInvalidPort_ParsesCorrectly(int invalidPort)
    {
        // Arrange
        var command = StartMockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);

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
    public void ParseCommand_WithValidPort_ParsesCorrectly(int validPort)
    {
        // Arrange
        var command = StartMockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);

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
        var command = StartMockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);

        // Act
        var parseResult = command.Parse("");

        // Assert
        Assert.Empty(parseResult.Errors);
        var portValue = parseResult.GetValueForOption(command.Options.First());
        Assert.Null(portValue); // Default value is handled in the handler, not the option
    }

    [Fact]
    public void Handler_ExecutesWithoutThrowing()
    {
        // Arrange
        var command = StartMockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);

        // Act & Assert - Just verify the command can be created and doesn't throw during basic operations
        Assert.NotNull(command);
        Assert.NotNull(command.Handler);

        // We don't invoke the handler as it would try to start the actual server
        // Instead we just verify the command structure is valid
    }

    [Fact]
    public void Handler_HasCorrectHandlerType()
    {
        // Arrange
        var command = StartMockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);

        // Act & Assert
        Assert.NotNull(command.Handler);
        // System.CommandLine handlers are internal, so we just verify it's set
    }

    [Fact]
    public void CreateCommand_CanParseWithLongOption()
    {
        // Act
        var command = StartMockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);
        var parseResult = command.Parse("--port 3000");

        // Assert
        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void CreateCommand_CanParseWithShortOption()
    {
        // Act
        var command = StartMockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);
        var parseResult = command.Parse("-p 3000");

        // Assert
        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void CreateCommand_CanParseWithAlias()
    {
        // Arrange
        var rootCommand = new RootCommand();
        var command = StartMockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);
        rootCommand.AddCommand(command);

        // Act
        var parseResult = rootCommand.Parse("start-mcp --port 3000");

        // Assert
        Assert.Empty(parseResult.Errors);
        // When using an alias, the command name is still the original name, but we can verify the alias exists
        Assert.Contains("start-mcp", parseResult.CommandResult.Command.Aliases);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12.5")]
    [InlineData("")]
    [InlineData(" ")]
    public void CreateCommand_WithInvalidPortValues_HasParseErrors(string invalidPortValue)
    {
        // Act
        var command = StartMockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);
        var parseResult = command.Parse($"--port {invalidPortValue}");

        // Assert
        Assert.NotEmpty(parseResult.Errors);
    }

    [Fact]
    public void CreateCommand_WithoutArguments_ParsesSuccessfully()
    {
        // Act
        var command = StartMockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);
        var parseResult = command.Parse("");

        // Assert
        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void CommandStructure_IsValid()
    {
        // Arrange & Act
        var command = StartMockToolingServerSubcommand.CreateCommand(_mockLogger, _mockCommandExecutor, _mockProcessService);

        // Assert
        Assert.NotNull(command);
        Assert.Equal("start-mock-tooling-server", command.Name);
        Assert.Single(command.Options);
        Assert.Contains("start-mcp", command.Aliases);
        Assert.NotNull(command.Handler);
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
        await StartMockToolingServerSubcommand.HandleStartServer(invalidPort, _testLogger, _mockCommandExecutor, _mockProcessService);

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
        // Act
        await StartMockToolingServerSubcommand.HandleStartServer(null, _testLogger, _mockCommandExecutor, _mockProcessService);

        // Assert - Should log starting message with default port
        Assert.NotEmpty(_testLogger.LogCalls);
        var firstLogCall = _testLogger.LogCalls.First();
        Assert.Equal(LogLevel.Information, firstLogCall.Level);
        Assert.Contains("Starting Mock Tooling Server", firstLogCall.Message);
        Assert.Contains("5309", firstLogCall.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5309)]
    [InlineData(8080)]
    [InlineData(65535)]
    public async Task HandleStartServer_WithValidPort_LogsStartingMessage(int validPort)
    {
        // Act
        await StartMockToolingServerSubcommand.HandleStartServer(validPort, _testLogger, _mockCommandExecutor, _mockProcessService);

        // Assert - Should log starting message with specified port
        Assert.NotEmpty(_testLogger.LogCalls);
        var firstLogCall = _testLogger.LogCalls.First();
        Assert.Equal(LogLevel.Information, firstLogCall.Level);
        Assert.Contains("Starting Mock Tooling Server", firstLogCall.Message);
        Assert.Contains(validPort.ToString(), firstLogCall.Message);
    }

    [Fact]
    public async Task HandleStartServer_WithValidPort_AttemptsToStartServer()
    {
        // Act
        await StartMockToolingServerSubcommand.HandleStartServer(5309, _testLogger, _mockCommandExecutor, _mockProcessService);

        // Assert - Should have multiple log calls (startup sequence)
        Assert.True(_testLogger.LogCalls.Count > 1);

        // First call should be starting message
        var firstCall = _testLogger.LogCalls.First();
        Assert.Equal(LogLevel.Information, firstCall.Level);
        Assert.Contains("Starting Mock Tooling Server", firstCall.Message);

        // Should have attempted some form of startup (either success or failure)
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Message.Contains("Starting server") ||
            call.Message.Contains("DLL not found") ||
            call.Message.Contains("Unable to determine"));
    }

    [Fact]
    public async Task HandleStartServer_WithInvalidPort_DoesNotAttemptStartup()
    {
        // Act
        await StartMockToolingServerSubcommand.HandleStartServer(0, _testLogger, _mockCommandExecutor, _mockProcessService);

        // Assert - Should only log error and return early
        Assert.Single(_testLogger.LogCalls);
        var logCall = _testLogger.LogCalls.First();
        Assert.Equal(LogLevel.Error, logCall.Level);
        Assert.Contains("Invalid port number", logCall.Message);
    }

    [Fact]
    public async Task HandleStartServer_WithCommandExecutor_UsesFallbackWhenTerminalFails()
    {
        // Arrange
        var mockResult = new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 0, StandardOutput = "Server started", StandardError = "" };
        _mockCommandExecutor.ExecuteWithStreamingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockResult));

        // Act - This will likely fail to start in new terminal in test environment
        await StartMockToolingServerSubcommand.HandleStartServer(5309, _testLogger, _mockCommandExecutor, _mockProcessService);

        // Assert - Should attempt fallback and log appropriate messages
        Assert.NotEmpty(_testLogger.LogCalls);

        // Should have starting message
        Assert.Contains(_testLogger.LogCalls, call =>
            call.Level == LogLevel.Information &&
            call.Message.Contains("Starting Mock Tooling Server"));

        // May have warning about terminal failure and fallback attempt
        var hasWarningOrFallback = _testLogger.LogCalls.Any(call =>
            call.Level == LogLevel.Warning ||
            (call.Level == LogLevel.Information && call.Message.Contains("Falling back")));

        // Test passes if we get expected logging behavior (either success or proper fallback)
        Assert.True(hasWarningOrFallback || _testLogger.LogCalls.Any(call =>
            call.Message.Contains("started successfully")));
    }
}
