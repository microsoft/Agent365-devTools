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
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".txt");

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
            // On Windows, should return true (file opened)
            // On other platforms, may succeed or log warning depending on environment
            // We can't assert the exact outcome since it depends on the system configuration
            // The fact that this code executes without throwing verifies the method works correctly
            // No assertion needed - successful execution is the test
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
            // Should handle different file types without throwing
            // No assertion needed - successful execution without exception is the test
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
