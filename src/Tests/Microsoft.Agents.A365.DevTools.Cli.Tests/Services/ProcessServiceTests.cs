// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class ProcessServiceTests : IDisposable
{
    private readonly TestLogger _testLogger;
    private readonly ProcessService _processService;

    public ProcessServiceTests()
    {
        _testLogger = new TestLogger();
        _processService = new ProcessService();
    }

    public void Dispose()
    {
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

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("simple", "simple")]
    [InlineData("hello world", "hello world")]
    public void EscapeAppleScriptString_WithSimpleStrings_ReturnsUnchanged(string? input, string? expected)
    {
        // Act
        var result = InvokeEscapeAppleScriptString(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("quote\"test", "quote\\\"test")]
    [InlineData("\"quotes everywhere\"", "\\\"quotes everywhere\\\"")]
    [InlineData("say \"hello\"", "say \\\"hello\\\"")]
    public void EscapeAppleScriptString_WithQuotes_EscapesCorrectly(string input, string expected)
    {
        // Act
        var result = InvokeEscapeAppleScriptString(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("path\\to\\file", "path\\\\to\\\\file")]
    [InlineData("C:\\Program Files\\", "C:\\\\Program Files\\\\")]
    [InlineData("\\\\server\\share", "\\\\\\\\server\\\\share")]
    public void EscapeAppleScriptString_WithBackslashes_EscapesCorrectly(string input, string expected)
    {
        // Act
        var result = InvokeEscapeAppleScriptString(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("line1\nline2", "line1\\nline2")]
    [InlineData("line1\rline2", "line1\\rline2")]
    [InlineData("tab\there", "tab\\there")]
    [InlineData("mixed\n\r\t", "mixed\\n\\r\\t")]
    public void EscapeAppleScriptString_WithSpecialCharacters_EscapesCorrectly(string input, string expected)
    {
        // Act
        var result = InvokeEscapeAppleScriptString(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\"\nwith\ttabs", "\\\"C:\\\\Program Files\\\\App\\\"\\nwith\\ttabs")]
    [InlineData("say \"hello\\world\"\n\ttest", "say \\\"hello\\\\world\\\"\\n\\ttest")]
    public void EscapeAppleScriptString_WithComplexStrings_EscapesAllCharacters(string input, string expected)
    {
        // Act
        var result = InvokeEscapeAppleScriptString(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void StartInNewTerminal_WithNullCommand_ThrowsArgumentException()
    {
        // Act & Assert - Should throw ArgumentException
        Assert.Throws<ArgumentException>(() =>
            _processService.StartInNewTerminal(null!, ["args"], "C:\\", _testLogger));
    }

    [Fact]
    public void StartInNewTerminal_WithNullArguments_ThrowsArgumentException()
    {
        // Act & Assert - Should throw ArgumentException
        Assert.Throws<ArgumentException>(() =>
            _processService.StartInNewTerminal("cmd", null!, "C:\\", _testLogger));
    }

    private string InvokeEscapeAppleScriptString(string? input)
    {
        var method = typeof(ProcessService).GetMethod("EscapeAppleScriptString", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object?[] { input });
        return (string)result!;
    }
}
