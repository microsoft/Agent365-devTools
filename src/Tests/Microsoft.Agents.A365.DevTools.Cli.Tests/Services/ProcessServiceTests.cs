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
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("arg1", new[] { "arg1" })]
    [InlineData("arg1 arg2", new[] { "arg1", "arg2" })]
    [InlineData("arg1   arg2", new[] { "arg1", "arg2" })]
    [InlineData("  arg1   arg2  ", new[] { "arg1", "arg2" })]
    public void SplitArguments_WithBasicArguments_SplitsCorrectly(string input, string[] expected)
    {
        // Act
        var result = InvokeSplitArguments(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\"quoted string\"", new[] { "quoted string" })]
    [InlineData("\"quoted string\" normal", new[] { "quoted string", "normal" })]
    [InlineData("normal \"quoted string\"", new[] { "normal", "quoted string" })]
    [InlineData("\"first quoted\" \"second quoted\"", new[] { "first quoted", "second quoted" })]
    [InlineData("\"quotes with spaces\" arg", new[] { "quotes with spaces", "arg" })]
    public void SplitArguments_WithQuotedStrings_RemovesQuotesAndPreservesContent(string input, string[] expected)
    {
        // Act
        var result = InvokeSplitArguments(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\\\"escaped\\\"", new[] { "\\\"escaped\\\"" })]
    [InlineData("\"string with \\\"inner quotes\\\"\"", new[] { "string with \\\"inner quotes\\\"" })]
    [InlineData("\\\"start normal \\\"end", new[] { "\\\"start", "normal", "\\\"end" })]
    [InlineData("\"quoted \\\"escaped\\\" content\"", new[] { "quoted \\\"escaped\\\" content" })]
    public void SplitArguments_WithEscapedQuotes_PreservesEscapeSequences(string input, string[] expected)
    {
        // Act
        var result = InvokeSplitArguments(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\\\\path", new[] { "\\\\path" })]
    [InlineData("\"C:\\\\Program Files\\\\Test\"", new[] { "C:\\\\Program Files\\\\Test" })]
    [InlineData("\\n\\t\\r", new[] { "\\n\\t\\r" })]
    public void SplitArguments_WithBackslashes_PreservesBackslashes(string input, string[] expected)
    {
        // Act
        var result = InvokeSplitArguments(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("--port 5309", new[] { "--port", "5309" })]
    [InlineData("--message \"hello world\"", new[] { "--message", "hello world" })]
    [InlineData("--path \"C:\\Program Files\\Test\"", new[] { "--path", "C:\\Program Files\\Test" })]
    [InlineData("--config \"{\\\"key\\\": \\\"value\\\"}\"", new[] { "--config", "{\\\"key\\\": \\\"value\\\"}" })]
    public void SplitArguments_WithRealWorldExamples_HandlesCorrectly(string input, string[] expected)
    {
        // Act
        var result = InvokeSplitArguments(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, null)]
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
    public void StartInNewTerminal_WithNullCommand_HandlesGracefully()
    {
        // Act & Assert - Should not throw
        var result = _processService.StartInNewTerminal(null!, "args", "C:\\", _testLogger);
        Assert.False(result);
    }

    [Fact]
    public void StartInNewTerminal_WithNullArguments_HandlesGracefully()
    {
        // Act & Assert - Should not throw
        var result = _processService.StartInNewTerminal("cmd", null!, "C:\\", _testLogger);
        Assert.False(result);
    }

    private string[] InvokeSplitArguments(string arguments)
    {
        var method = typeof(ProcessService).GetMethod("SplitArguments", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object[] { arguments });
        return (string[])result!;
    }

    private string InvokeEscapeAppleScriptString(string? input)
    {
        var method = typeof(ProcessService).GetMethod("EscapeAppleScriptString", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object?[] { input });
        return (string)result!;
    }
}
