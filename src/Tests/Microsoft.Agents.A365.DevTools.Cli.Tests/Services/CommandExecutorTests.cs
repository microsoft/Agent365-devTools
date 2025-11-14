// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class CommandExecutorTests
{
    private readonly ILogger<CommandExecutor> _logger;
    private readonly CommandExecutor _executor;

    public CommandExecutorTests()
    {
        _logger = Substitute.For<ILogger<CommandExecutor>>();
        _executor = new CommandExecutor(_logger);
    }

    [Fact]
    public async Task ExecuteAsync_ValidCommand_ReturnsSuccess()
    {
        // Arrange - Use a simple command that works on all platforms
        var command = OperatingSystem.IsWindows() ? "cmd.exe" : "echo";
        var args = OperatingSystem.IsWindows() ? "/c echo test" : "test";

        // Act
        var result = await _executor.ExecuteAsync(command, args, captureOutput: true);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Contain("test");
    }

    [Fact]
    public async Task ExecuteAsync_InvalidCommand_ThrowsException()
    {
        // Arrange
        var command = "nonexistent-command-12345";
        var args = "";

        // Act & Assert
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
            _executor.ExecuteAsync(command, args, captureOutput: true));
    }

    [Fact]
    public async Task ExecuteAsync_CommandWithError_CapturesStandardError()
    {
        // Arrange - Command that writes to stderr
        var command = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";
        var args = OperatingSystem.IsWindows() 
            ? "/c echo error message 1>&2 && exit 1" 
            : "-c \"echo error message >&2; exit 1\"";

        // Act
        var result = await _executor.ExecuteAsync(command, args, captureOutput: true);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(1);
        result.StandardError.Should().Contain("error message");
    }

    [Fact]
    public async Task ExecuteAsync_DotNetVersion_WorksCorrectly()
    {
        // Arrange - Test with a real dotnet command
        var command = "dotnet";
        var args = "--version";

        // Act
        var result = await _executor.ExecuteAsync(command, args, captureOutput: true);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().MatchRegex(@"\d+\.\d+\.\d+"); // Version pattern
    }

    [Fact]
    public async Task ExecuteAsync_CaptureOutputFalse_DoesNotCaptureOutput()
    {
        // Arrange
        var command = OperatingSystem.IsWindows() ? "cmd.exe" : "echo";
        var args = OperatingSystem.IsWindows() ? "/c echo test" : "test";

        // Act
        var result = await _executor.ExecuteAsync(command, args, captureOutput: false);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.StandardOutput.Should().BeEmpty();
    }

    [Theory]
    [InlineData("az", true)]
    [InlineData("az.cmd", true)]
    [InlineData("AZ", true)]
    [InlineData("Az.CMD", true)]
    [InlineData("dotnet", false)]
    [InlineData("pwsh", false)]
    [InlineData("cmd", false)]
    public void IsAzureCliCommand_IdentifiesAzureCliCorrectly(string command, bool expectedResult)
    {
        // Use reflection to test the private method
        var method = typeof(CommandExecutor).GetMethod("IsAzureCliCommand", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        var result = (bool)method!.Invoke(_executor, new object[] { command })!;
        
        result.Should().Be(expectedResult);
    }

    [Theory]
    [InlineData("WARNING: This is a warning message", "This is a warning message")]
    [InlineData("WARNING:No space after colon", "No space after colon")]
    [InlineData("  WARNING: Leading spaces stripped", "Leading spaces stripped")]
    [InlineData("warning: Case insensitive", "Case insensitive")]
    [InlineData("Regular message without warning", "Regular message without warning")]
    [InlineData("This WARNING: is not at start", "This WARNING: is not at start")]
    public void StripAzureWarningPrefix_RemovesWarningPrefixCorrectly(string input, string expected)
    {
        // Use reflection to test the private method
        var method = typeof(CommandExecutor).GetMethod("StripAzureWarningPrefix", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        var result = (string)method!.Invoke(_executor, new object[] { input })!;
        
        result.Should().Be(expected);
    }

    [Fact]
    public async Task ExecuteWithStreamingAsync_CapturesOutputInRealTime()
    {
        // Arrange
        var command = OperatingSystem.IsWindows() ? "cmd.exe" : "echo";
        var args = OperatingSystem.IsWindows() ? "/c echo streaming test" : "streaming test";

        // Act
        var result = await _executor.ExecuteWithStreamingAsync(command, args);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.StandardOutput.Should().Contain("streaming test");
    }
}
