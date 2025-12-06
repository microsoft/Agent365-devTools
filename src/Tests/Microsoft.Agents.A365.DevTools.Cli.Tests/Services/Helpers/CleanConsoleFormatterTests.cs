// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using NSubstitute;
using Xunit;
using System.IO;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Helpers;

/// <summary>
/// Unit tests for CleanConsoleFormatter
/// </summary>
[Collection("ConsoleOutput")]
public class CleanConsoleFormatterTests : IDisposable
{
    private readonly TextWriter _originalOut;
    private readonly StringWriter _consoleWriter;
    private readonly CleanConsoleFormatter _formatter;

    public CleanConsoleFormatterTests()
    {
        _originalOut = Console.Out;
        _consoleWriter = new StringWriter();
        Console.SetOut(_consoleWriter);

        _formatter = new CleanConsoleFormatter();
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _consoleWriter.Dispose();
    }

    [Fact]
    public void Write_WithInformationLevel_OutputsMessageWithoutPrefix()
    {
        // Arrange
        var message = "This is an info message";
        var logEntry = CreateLogEntry(LogLevel.Information, message);

        // Act
        _formatter.Write(logEntry, null, _consoleWriter);

        // Assert
        var output = _consoleWriter.ToString();
        output.Should().Contain(message);
        output.Should().NotContain("ERROR:");
        output.Should().NotContain("WARNING:");
    }

    [Fact]
    public void Write_WithErrorLevel_OutputsMessageWithErrorPrefix()
    {
        // Arrange
        var message = "This is an error message";
        var logEntry = CreateLogEntry(LogLevel.Error, message);

        // Act
        _formatter.Write(logEntry, null, _consoleWriter);

        // Assert
        var output = _consoleWriter.ToString();
        output.Should().Contain("ERROR:");
        output.Should().Contain(message);
    }

    [Fact]
    public void Write_WithCriticalLevel_OutputsMessageWithErrorPrefix()
    {
        // Arrange
        var message = "This is a critical message";
        var logEntry = CreateLogEntry(LogLevel.Critical, message);

        // Act
        _formatter.Write(logEntry, null, _consoleWriter);

        // Assert
        var output = _consoleWriter.ToString();
        output.Should().Contain("ERROR:");
        output.Should().Contain(message);
    }

    [Fact]
    public void Write_WithWarningLevel_OutputsMessageWithWarningPrefix()
    {
        // Arrange
        var message = "This is a warning message";
        var logEntry = CreateLogEntry(LogLevel.Warning, message);

        // Act
        _formatter.Write(logEntry, null, _consoleWriter);

        // Assert
        var output = _consoleWriter.ToString();
        output.Should().Contain("WARNING:");
        output.Should().Contain(message);
    }

    [Fact]
    public void Write_WithException_IncludesExceptionDetails()
    {
        // Arrange
        var message = "Error occurred";
        var exception = new InvalidOperationException("Test exception");
        var logEntry = CreateLogEntry(LogLevel.Error, message, exception);

        // Act
        _formatter.Write(logEntry, null, _consoleWriter);

        // Assert
        var output = _consoleWriter.ToString();
        output.Should().Contain("ERROR:");
        output.Should().Contain(message);
        output.Should().Contain("Test exception");
        output.Should().Contain("InvalidOperationException");
    }

    [Fact]
    public void Write_WithExceptionAndWarning_IncludesExceptionDetails()
    {
        // Arrange
        var message = "Warning with exception";
        var exception = new ArgumentException("Test warning exception");
        var logEntry = CreateLogEntry(LogLevel.Warning, message, exception);

        // Act
        _formatter.Write(logEntry, null, _consoleWriter);

        // Assert
        var output = _consoleWriter.ToString();
        output.Should().Contain("WARNING:");
        output.Should().Contain(message);
        output.Should().Contain("Test warning exception");
        output.Should().Contain("ArgumentException");
    }

    [Fact]
    public void Write_WithNullMessage_DoesNotWriteAnything()
    {
        // Arrange
        var logEntry = CreateLogEntry(LogLevel.Information, null!);

        // Act
        _formatter.Write(logEntry, null, _consoleWriter);

        // Assert
        _consoleWriter.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Write_WithEmptyMessage_DoesNotWriteAnything()
    {
        // Arrange
        var logEntry = CreateLogEntry(LogLevel.Information, string.Empty);

        // Act
        _formatter.Write(logEntry, null, _consoleWriter);

        // Assert
        _consoleWriter.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Write_WithWhitespaceMessage_OutputsWhitespace()
    {
        // Arrange
        var message = "   ";
        var logEntry = CreateLogEntry(LogLevel.Information, message);

        // Act
        _formatter.Write(logEntry, null, _consoleWriter);

        // Assert
        var output = _consoleWriter.ToString();
        output.Should().NotBeEmpty();
        output.Should().Contain(message);
    }

    [Fact]
    public void Write_WithMultilineMessage_PreservesLineBreaks()
    {
        // Arrange
        var message = "Line 1\nLine 2\nLine 3";
        var logEntry = CreateLogEntry(LogLevel.Information, message);

        // Act
        _formatter.Write(logEntry, null, _consoleWriter);

        // Assert
        var output = _consoleWriter.ToString();
        output.Should().Contain("Line 1");
        output.Should().Contain("Line 2");
        output.Should().Contain("Line 3");
    }

    [Fact]
    public void Write_WithLongMessage_DoesNotTruncate()
    {
        // Arrange
        var message = new string('A', 1000);
        var logEntry = CreateLogEntry(LogLevel.Information, message);

        // Act
        _formatter.Write(logEntry, null, _consoleWriter);

        // Assert
        var output = _consoleWriter.ToString();
        output.Should().Contain(message);
        output.Length.Should().BeGreaterThanOrEqualTo(message.Length);
    }

    [Fact]
    public void Constructor_CreatesFormatterWithCleanName()
    {
        // Act
        var formatter = new CleanConsoleFormatter();

        // Assert
        formatter.Should().NotBeNull();
        formatter.Name.Should().Be("clean");
    }

    [Theory]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Trace)]
    public void Write_WithNonWarningOrErrorLevel_DoesNotIncludePrefix(LogLevel logLevel)
    {
        // Arrange
        var message = "Test message";
        var logEntry = CreateLogEntry(logLevel, message);

        // Act
        _formatter.Write(logEntry, null, _consoleWriter);

        // Assert
        var output = _consoleWriter.ToString();
        output.Should().Contain(message);
        output.Should().NotContain("ERROR:");
        output.Should().NotContain("WARNING:");
    }

    [Theory]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    public void Write_WithErrorOrCriticalLevel_IncludesErrorPrefix(LogLevel logLevel)
    {
        // Arrange
        var message = "Test error message";
        var logEntry = CreateLogEntry(logLevel, message);

        // Act
        _formatter.Write(logEntry, null, _consoleWriter);

        // Assert
        var output = _consoleWriter.ToString();
        output.Should().Contain("ERROR:");
        output.Should().Contain(message);
    }

    private static LogEntry<string> CreateLogEntry(
        LogLevel logLevel,
        string message,
        Exception? exception = null,
        string category = "TestCategory")
    {
        return new LogEntry<string>(
            logLevel,
            category,
            new EventId(0),
            message,
            exception,
            (state, ex) => state);
    }
}
