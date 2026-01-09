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

    #region Regression Tests for JWT Token Filtering

    [Theory]
    [InlineData("eyJ0ZXN0IjoidGVzdCJ9.eyJ0ZXN0IjoidGVzdCIsInRlc3QiOiJ0ZXN0IiwidGVzdCI6InRlc3QiLCJ0ZXN0IjoidGVzdCIsInRlc3QiOiJ0ZXN0IiwidGVzdCI6InRlc3QifQ.TEST-SIGNATURE-NOT-REAL-JWT-FOR-UNIT-TESTING-ONLY", true)]
    [InlineData("  eyJ0ZXN0IjoidGVzdCJ9.eyJ0ZXN0IjoidGVzdCIsInRlc3QiOiJ0ZXN0IiwidGVzdCI6InRlc3QiLCJ0ZXN0IjoidGVzdCJ9.TEST-SIG-FAKE  ", true)]
    [InlineData("Regular log message", false)]
    [InlineData("", false)]
    [InlineData("eyJ.test", false)] // Too short
    [InlineData("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", false)] // Only one part, not three
    [InlineData("This eyJ0eXAi line contains eyJ but is not a token", false)]
    public void IsJwtToken_IdentifiesTokensCorrectly(string line, bool expectedResult)
    {
        // REGRESSION TEST: Verify JWT token detection
        // Security fix to prevent tokens from being logged to console
        //
        // Bug History:
        // - Microsoft Graph JWT access tokens were printed to console during 'a365 setup all'
        // - Tokens are sensitive credentials that should never be displayed
        // - Example leaked token started with: eyJ0eXAiOiJKV1QiLCJub25jZSI6...
        //
        // Fix: IsJwtToken() method filters JWT tokens from console output
        // - Detects tokens by signature: starts with "eyJ", has 2 dots, length > 100
        // - Token still captured internally for use, just not displayed

        // Use reflection to test the private method
        var method = typeof(CommandExecutor).GetMethod("IsJwtToken",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = (bool)method!.Invoke(null, new object[] { line })!;

        result.Should().Be(expectedResult);
    }

    [Fact]
    public void IsJwtToken_WithRealMicrosoftGraphToken_ShouldDetect()
    {
        // REGRESSION TEST: Verify detection of actual Microsoft Graph JWT token format
        // This is the exact format that was being leaked to console

        // Arrange - Test JWT with realistic structure but clearly fake content
        // Note: This is NOT a real token - it's a test fixture with dummy data
        var realToken = "eyJ0ZXN0IjoidGVzdCIsInRlc3QiOiJ0ZXN0In0.eyJ0ZXN0IjoidGVzdCIsInRlc3QiOiJ0ZXN0IiwidGVzdCI6InRlc3QiLCJ0ZXN0IjoidGVzdCIsInRlc3QiOiJ0ZXN0IiwidGVzdCI6InRlc3QiLCJ0ZXN0IjoidGVzdCIsInRlc3QiOiJ0ZXN0IiwidGVzdCI6InRlc3QifQ.FAKE-SIGNATURE-FOR-UNIT-TEST-NOT-A-REAL-JWT-TOKEN-USED-TO-TEST-DETECTION-LOGIC-ONLY";

        // Act
        var method = typeof(CommandExecutor).GetMethod("IsJwtToken",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = (bool)method!.Invoke(null, new object[] { realToken })!;

        // Assert
        result.Should().BeTrue("real Microsoft Graph JWT tokens should be detected and filtered");
    }

    [Theory]
    [InlineData("Acquiring Microsoft Graph delegated access token via PowerShell", false)]
    [InlineData("Microsoft Graph access token acquired successfully", false)]
    [InlineData("Connect-MgGraph completed successfully", false)]
    public void IsJwtToken_WithNormalLogMessages_ShouldNotDetect(string logMessage, bool expectedResult)
    {
        // REGRESSION TEST: Verify that normal log messages are NOT filtered
        // Only JWT tokens should be filtered, not informational messages

        // Act
        var method = typeof(CommandExecutor).GetMethod("IsJwtToken",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = (bool)method!.Invoke(null, new object[] { logMessage })!;

        // Assert
        result.Should().Be(expectedResult, "normal log messages should not be filtered");
    }

    [Fact]
    public void IsJwtToken_WithNullOrEmpty_ShouldReturnFalse()
    {
        // REGRESSION TEST: Verify null/empty handling

        var method = typeof(CommandExecutor).GetMethod("IsJwtToken",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        // Test null
        var nullResult = (bool)method!.Invoke(null, new object?[] { null })!;
        nullResult.Should().BeFalse("null should not be detected as token");

        // Test empty
        var emptyResult = (bool)method!.Invoke(null, new object[] { "" })!;
        emptyResult.Should().BeFalse("empty string should not be detected as token");

        // Test whitespace
        var whitespaceResult = (bool)method!.Invoke(null, new object[] { "   " })!;
        whitespaceResult.Should().BeFalse("whitespace should not be detected as token");
    }

    [Fact]
    public async Task ExecuteWithStreamingAsync_CapturesTokenButDoesNotPrintIt()
    {
        // REGRESSION TEST: Verify JWT tokens are captured in StandardOutput but not printed to console
        // This is the core security fix - tokens are filtered from console but still available internally

        // Arrange - Command that outputs a JWT-like token (fake test token)
        var jwtToken = "eyJ0ZXN0IjoidGVzdCJ9.eyJ0ZXN0IjoidGVzdCIsInRlc3QiOiJ0ZXN0IiwidGVzdCI6InRlc3QiLCJ0ZXN0IjoidGVzdCJ9.FAKE-TEST-SIGNATURE-FOR-UNIT-TESTING-JWT-DETECTION-LOGIC-NOT-A-REAL-TOKEN";

        var command = OperatingSystem.IsWindows() ? "cmd.exe" : "echo";
        var args = OperatingSystem.IsWindows() ? $"/c echo {jwtToken}" : jwtToken;

        // Act
        var result = await _executor.ExecuteWithStreamingAsync(command, args);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();

        // Token should be captured in StandardOutput (for internal use)
        result.StandardOutput.Should().Contain(jwtToken,
            "token should be captured in StandardOutput for internal processing");

        // Note: We cannot directly test console output from unit tests, but the IsJwtToken tests
        // verify that the filtering logic works correctly
    }

    [Theory]
    [InlineData("Short token eyJ.abc.xyz")] // Too short to be JWT
    [InlineData("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ")] // Only 2 parts
    public void IsJwtToken_WithInvalidJwtFormat_ShouldNotDetect(string invalidToken)
    {
        // REGRESSION TEST: Verify invalid JWT formats are not filtered
        // Prevents false positives in filtering

        var method = typeof(CommandExecutor).GetMethod("IsJwtToken",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = (bool)method!.Invoke(null, new object[] { invalidToken })!;

        result.Should().BeFalse("invalid JWT formats should not be filtered");
    }

    #endregion
}
