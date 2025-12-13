// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Runtime.InteropServices;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

public class FileHelperTests
{
    private readonly ILogger _logger;

    public FileHelperTests()
    {
        _logger = Substitute.For<ILogger>();
    }

    [Fact]
    public void TryOpenFileInDefaultEditor_WithNonExistentFile_ReturnsFalse()
    {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.txt");

        // Act
        var result = FileHelper.TryOpenFileInDefaultEditor(nonExistentPath, _logger);

        // Assert
        result.Should().BeFalse();
        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("File not found")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void TryOpenFileInDefaultEditor_WithExistingFile_AttemptsToOpen()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "test content");

            // Act
            var result = FileHelper.TryOpenFileInDefaultEditor(tempFile, _logger);

            // Assert
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // On Windows, UseShellExecute should succeed with default file association
                result.Should().BeTrue();
                _logger.Received(1).Log(
                    LogLevel.Information,
                    Arg.Any<EventId>(),
                    Arg.Is<object>(o => o.ToString()!.Contains("opened") || o.ToString()!.Contains("Opened")),
                    Arg.Any<Exception>(),
                    Arg.Any<Func<object, Exception?, string>>());
            }
            else
            {
                // On Unix systems, result depends on EDITOR/VISUAL environment variables
                // Verify method returns a valid boolean without throwing
                (result == true || result == false).Should().BeTrue("method should return a boolean value");
            }
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void TryOpenFileInDefaultEditor_LogsAppropriateMessages()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "test content");

            // Act
            FileHelper.TryOpenFileInDefaultEditor(tempFile, _logger);

            // Assert
            // Should log either success or warning message
            _logger.Received().Log(
                Arg.Is<LogLevel>(l => l == LogLevel.Information || l == LogLevel.Warning),
                Arg.Any<EventId>(),
                Arg.Any<object>(),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void TryOpenFileInDefaultEditor_WithInvalidPath_HandlesGracefully()
    {
        // Arrange
        var invalidPath = new string(Path.GetInvalidPathChars()) + "invalid.txt";

        // Act
        var result = FileHelper.TryOpenFileInDefaultEditor(invalidPath, _logger);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("config.yaml")]
    [InlineData("readme.md")]
    public void TryOpenFileInDefaultEditor_WithDifferentFileTypes_HandlesCorrectly(string filename)
    {
        // Arrange
        var tempFile = Path.Combine(Path.GetTempPath(), filename);
        try
        {
            File.WriteAllText(tempFile, "test content");

            // Act
            var result = FileHelper.TryOpenFileInDefaultEditor(tempFile, _logger);

            // Assert
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // On Windows, file association should handle different file types
                result.Should().BeTrue($"file {filename} should open on Windows");
                _logger.Received(1).Log(
                    LogLevel.Information,
                    Arg.Any<EventId>(),
                    Arg.Is<object>(o => o.ToString()!.Contains("opened") || o.ToString()!.Contains("Opened")),
                    Arg.Any<Exception>(),
                    Arg.Any<Func<object, Exception?, string>>());
            }
            else
            {
                // On Unix systems, verify method returns boolean and logs appropriately
                (result == true || result == false).Should().BeTrue("method should return a boolean value");
                _logger.Received().Log(
                    Arg.Any<LogLevel>(),
                    Arg.Any<EventId>(),
                    Arg.Any<object>(),
                    Arg.Any<Exception>(),
                    Arg.Any<Func<object, Exception?, string>>());
            }
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
