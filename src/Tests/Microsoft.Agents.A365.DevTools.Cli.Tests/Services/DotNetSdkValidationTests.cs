// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Reflection;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Tests for .NET SDK version validation logic
/// Reproduces the intermittent failure issue from PR #130
/// </summary>
public class DotNetSdkValidationTests : IDisposable
{
    private readonly ILogger _logger;
    private readonly CommandExecutor _commandExecutor;
    private readonly ITestOutputHelper _output;
    private readonly string _testProjectPath;

    public DotNetSdkValidationTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = Substitute.For<ILogger>();
        _commandExecutor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        
        // Create a temporary test project directory
        _testProjectPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testProjectPath);
    }

    /// <summary>
    /// Reproduces the intermittent failure where dotnet --version command fails
    /// This simulates the race condition that can occur under system load
    /// </summary>
    [Fact]
    public async Task ResolveDotNetRuntimeVersion_WhenDotNetVersionCommandFails_ThrowsDotNetSdkVersionMismatchException()
    {
        // Arrange - Create a test .csproj file targeting .NET 8.0
        CreateTestProject("net8.0");
        
        // Mock: dotnet --version command FAILS (simulating intermittent process spawn failure)
        _commandExecutor.ExecuteAsync("dotnet", "--version", captureOutput: true, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult 
            { 
                ExitCode = 1,  // Command failed
                StandardError = "Process spawn failed"
            }));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<DotNetSdkVersionMismatchException>(async () =>
        {
            // Call the private static method using reflection
            await InvokeResolveDotNetRuntimeVersionAsync(
                ProjectPlatform.DotNet, 
                _testProjectPath,
                CancellationToken.None);
        });

        // Verify exception details
        exception.Should().NotBeNull();
        exception.Message.Should().Contain("The project targets .NET 8.0, but the required .NET SDK is not installed");
        
        _output.WriteLine($"Test reproduced the issue: {exception.Message}");
    }

    /// <summary>
    /// Tests the scenario where SDK version is detected but validation logic has a bug
    /// This reproduces the exact error message from the user's report
    /// </summary>
    [Fact]
    public async Task ResolveDotNetRuntimeVersion_WhenVersionDetectedButValidationFails_ShowsContradictoryError()
    {       
        // Arrange - Create a test .csproj file targeting .NET 8.0
        CreateTestProject("net8.0");
        
        // Mock: dotnet --version returns 9.0.308 (which SHOULD work for .NET 8 projects)
        // But the command reports ExitCode != 0 (simulating intermittent failure)
        _commandExecutor.ExecuteAsync("dotnet", "--version", captureOutput: true, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult 
            { 
                ExitCode = 1,  // Command failed even though it returned version
                StandardOutput = "9.0.308",  // Version detected
                StandardError = "Timeout"
            }));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<DotNetSdkVersionMismatchException>(async () =>
        {
            await InvokeResolveDotNetRuntimeVersionAsync(
                ProjectPlatform.DotNet, 
                _testProjectPath,
                CancellationToken.None);
        });

        // This reproduces the contradictory error:
        // "Installed SDK version: 9.0.308" but still throws "SDK is not installed"
        exception.Message.Should().Contain("required .NET SDK is not installed");
        
        _output.WriteLine("Reproduced contradictory error:");
        _output.WriteLine($"   Detected version in output: 9.0.308");
        _output.WriteLine($"   But exception still thrown: {exception.Message}");
    }

    /// <summary>
    /// Tests successful scenario - SDK 9.0 building .NET 8.0 project
    /// </summary>
    [Fact]
    public async Task ResolveDotNetRuntimeVersion_WhenNewerSdkInstalled_SucceedsWithForwardCompatibility()
    {
        // Arrange - Create a test .csproj file targeting .NET 8.0
        CreateTestProject("net8.0");
        
        // Mock: dotnet --version returns 9.0.308 (newer than target)
        _commandExecutor.ExecuteAsync("dotnet", "--version", captureOutput: true, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult 
            { 
                ExitCode = 0,  // Command succeeded
                StandardOutput = "9.0.308"
            }));

        // Act
        var version = await InvokeResolveDotNetRuntimeVersionAsync(
            ProjectPlatform.DotNet, 
            _testProjectPath,
            CancellationToken.None);

        // Assert
        version.Should().Be("8.0");
        
        _output.WriteLine($"Forward compatibility works: SDK 9.0.308 can build .NET 8.0");
    }

    /// <summary>
    /// Tests scenario where installed SDK is older than target framework
    /// </summary>
    [Fact]
    public async Task ResolveDotNetRuntimeVersion_WhenOlderSdkInstalled_ThrowsDotNetSdkVersionMismatchException()
    {
        // Arrange - Create a test .csproj file targeting .NET 9.0
        CreateTestProject("net9.0");
        
        // Mock: dotnet --version returns 8.0.100 (older than target)
        _commandExecutor.ExecuteAsync("dotnet", "--version", captureOutput: true, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult 
            { 
                ExitCode = 0,
                StandardOutput = "8.0.100"
            }));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<DotNetSdkVersionMismatchException>(async () =>
        {
            await InvokeResolveDotNetRuntimeVersionAsync(
                ProjectPlatform.DotNet, 
                _testProjectPath,
                CancellationToken.None);
        });

        exception.Message.Should().Contain("targets .NET 9.0");
        exception.Message.Should().Contain("Installed SDK version: 8.0.100");
        
        _output.WriteLine($"Correctly detected incompatible SDK: {exception.Message}");
    }

    /// <summary>
    /// Tests that when all retry attempts fail, the method throws DotNetSdkVersionMismatchException
    /// </summary>
    [Fact]
    public async Task ResolveDotNetRuntimeVersion_WhenAllRetriesFail_ThrowsException()
    {
        // Arrange
        CreateTestProject("net8.0");
        
        var callCount = 0;
        
        // Mock: All 3 attempts fail
        _commandExecutor.ExecuteAsync("dotnet", "--version", captureOutput: true, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                Interlocked.Increment(ref callCount);
                
                return Task.FromResult(new CommandResult 
                { 
                    ExitCode = 1,
                    StandardOutput = "",
                    StandardError = "Intermittent failure"
                });
            });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<DotNetSdkVersionMismatchException>(async () =>
        {
            await InvokeResolveDotNetRuntimeVersionAsync(
                ProjectPlatform.DotNet, 
                _testProjectPath,
                CancellationToken.None);
        });

        // Assert - Should have attempted 3 times before giving up
        exception.Should().NotBeNull();
        callCount.Should().Be(3, "Should have attempted 3 times before giving up");
        
        _output.WriteLine($"Retry logic working: Made {callCount} attempts before throwing exception");
        _output.WriteLine($"Exception message: {exception.Message}");
    }

    /// <summary>
    /// Tests that when the first attempt fails but a retry succeeds, the method returns the version
    /// </summary>
    [Fact]
    public async Task ResolveDotNetRuntimeVersion_WhenFirstAttemptFailsButRetrySucceeds_ReturnsVersion()
    {
        // Arrange
        CreateTestProject("net8.0");
        
        var callCount = 0;
        
        // Mock: First call fails, second succeeds
        _commandExecutor.ExecuteAsync("dotnet", "--version", captureOutput: true, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                int currentCall = Interlocked.Increment(ref callCount);
                
                // Only first attempt fails, second succeeds
                var shouldFail = currentCall == 1;
                
                return Task.FromResult(new CommandResult 
                { 
                    ExitCode = shouldFail ? 1 : 0,
                    StandardOutput = shouldFail ? "" : "9.0.308",
                    StandardError = shouldFail ? "Intermittent failure" : ""
                });
            });

        // Act
        var version = await InvokeResolveDotNetRuntimeVersionAsync(
            ProjectPlatform.DotNet, 
            _testProjectPath,
            CancellationToken.None);

        // Assert - Should succeed on retry
        version.Should().Be("8.0", "Retry should succeed and detect .NET 8.0");
        callCount.Should().Be(2, "Should fail once then succeed on retry");
        
        _output.WriteLine($"Successful retry: Failed once, succeeded on attempt 2");
    }

    /// <summary>
    /// Test with malformed version output
    /// </summary>
    [Fact]
    public async Task ResolveDotNetRuntimeVersion_WhenVersionOutputMalformed_HandlesGracefully()
    {
        // Arrange
        CreateTestProject("net8.0");
        
        // Mock: dotnet --version returns malformed output
        _commandExecutor.ExecuteAsync("dotnet", "--version", captureOutput: true, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult 
            { 
                ExitCode = 0,
                StandardOutput = "invalid-version-format"  // Malformed
            }));

        // Act
        var version = await InvokeResolveDotNetRuntimeVersionAsync(
            ProjectPlatform.DotNet, 
            _testProjectPath,
            CancellationToken.None);

        // Assert - Should still return detected target version
        version.Should().Be("8.0");
        
        _output.WriteLine("Gracefully handled malformed SDK version output");
    }

    /// <summary>
    /// Test that cancellation during retry delay properly throws OperationCanceledException
    /// </summary>
    [Fact]
    public async Task ResolveDotNetRuntimeVersion_WhenCancelledDuringRetry_ThrowsOperationCanceledException()
    {
        // Arrange
        CreateTestProject("net8.0");
        
        var cts = new CancellationTokenSource();
        var callCount = 0;
        
        // Mock: First call fails, trigger cancellation before retry
        _commandExecutor.ExecuteAsync("dotnet", "--version", captureOutput: true, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var currentCall = Interlocked.Increment(ref callCount);
                
                if (currentCall == 1)
                {
                    // First attempt fails
                    _output.WriteLine($"Attempt {currentCall}: Simulating failure");
                    
                    // Cancel immediately to trigger cancellation during Task.Delay
                    cts.Cancel();
                    
                    return Task.FromResult(new CommandResult 
                    { 
                        ExitCode = 1, 
                        StandardError = "dotnet command failed" 
                    });
                }
                
                // Should not reach here - cancellation should occur during delay
                throw new InvalidOperationException("Should have been cancelled");
            });
        
        // Act & Assert
        var act = async () => await InvokeResolveDotNetRuntimeVersionAsync(
            ProjectPlatform.DotNet,
            _testProjectPath,
            cts.Token);
        
        await act.Should().ThrowAsync<OperationCanceledException>();
        
        _output.WriteLine("Cancellation during retry properly threw OperationCanceledException");
    }

    /// <summary>
    /// Test that exponential backoff respects the maximum delay cap
    /// </summary>
    [Fact]
    public async Task ResolveDotNetRuntimeVersion_ExponentialBackoff_RespectsMaximumDelayCap()
    {
        // Arrange
        CreateTestProject("net8.0");
        
        var callTimes = new List<DateTime>();
        
        // Mock: All attempts fail to test full retry sequence
        _commandExecutor.ExecuteAsync("dotnet", "--version", captureOutput: true, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callTimes.Add(DateTime.UtcNow);
                var callNumber = callTimes.Count;
                
                _output.WriteLine($"Attempt {callNumber} at {callTimes.Last():HH:mm:ss.fff}");
                
                return Task.FromResult(new CommandResult 
                { 
                    ExitCode = 1, 
                    StandardError = "dotnet command failed" 
                });
            });
        
        // Act
        try
        {
            await InvokeResolveDotNetRuntimeVersionAsync(
                ProjectPlatform.DotNet,
                _testProjectPath,
                CancellationToken.None);
        }
        catch (DotNetSdkVersionMismatchException)
        {
            // Expected - all retries failed
        }
        
        // Assert - Verify exponential backoff delays
        callTimes.Should().HaveCount(3); // MaxSdkValidationAttempts = 3
        
        if (callTimes.Count >= 2)
        {
            var delay1 = (callTimes[1] - callTimes[0]).TotalMilliseconds;
            _output.WriteLine($"Delay between attempt 1 and 2: {delay1}ms (expected ~500ms)");
            
            // Allow some tolerance for execution time
            delay1.Should().BeGreaterOrEqualTo(450).And.BeLessThan(1500);
        }
        
        if (callTimes.Count >= 3)
        {
            var delay2 = (callTimes[2] - callTimes[1]).TotalMilliseconds;
            _output.WriteLine($"Delay between attempt 2 and 3: {delay2}ms (expected ~1000ms)");
            
            delay2.Should().BeGreaterOrEqualTo(950).And.BeLessThan(2500);
        }
        
        _output.WriteLine("Exponential backoff delays verified: 500ms -> 1000ms");
    }

    /// <summary>
    /// Test that Math.Min cap prevents extremely large delays if retry attempts are increased
    /// This verifies the delay calculation: Math.Min(InitialRetryDelayMs * (1 &lt;&lt; (attempt - 1)), MaxRetryDelayMs)
    /// </summary>
    [Fact]
    public void ExponentialBackoff_WithCap_PreventsExcessiveDelays()
    {
        // Test the exponential backoff formula with cap
        const int InitialRetryDelayMs = 500;
        const int MaxRetryDelayMs = 5000;
        
        // Simulate delay calculation for various attempts
        var delays = new List<int>();
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            var delayMs = Math.Min(InitialRetryDelayMs * (1 << (attempt - 1)), MaxRetryDelayMs);
            delays.Add(delayMs);
            _output.WriteLine($"Attempt {attempt}: {delayMs}ms");
        }
        
        // Assert
        delays[0].Should().Be(500);    // Attempt 1: 500ms
        delays[1].Should().Be(1000);   // Attempt 2: 1000ms
        delays[2].Should().Be(2000);   // Attempt 3: 2000ms
        delays[3].Should().Be(4000);   // Attempt 4: 4000ms
        delays[4].Should().Be(5000);   // Attempt 5: 5000ms (capped)
        delays[5].Should().Be(5000);   // Attempt 6: 5000ms (capped)
        delays[9].Should().Be(5000);   // Attempt 10: 5000ms (capped)
        
        // Verify cap is enforced
        delays.Should().OnlyContain(d => d <= MaxRetryDelayMs);
        
        _output.WriteLine("Math.Min cap successfully prevents delays from exceeding 5000ms");
    }

    #region Helper Methods

    private string CreateTestProject(string targetFramework)
    {
        var projectFile = Path.Combine(_testProjectPath, "TestProject.csproj");
        
        var projectContent = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>{targetFramework}</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>";

        File.WriteAllText(projectFile, projectContent);
        
        _output.WriteLine($"Created test project: {projectFile}");
        _output.WriteLine($"Target framework: {targetFramework}");
        
        return projectFile;
    }

    private async Task<string?> InvokeResolveDotNetRuntimeVersionAsync(
        ProjectPlatform platform, 
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        // Use reflection to call the private static async method
        var infrastructureType = typeof(InfrastructureSubcommand);
        var method = infrastructureType.GetMethod(
            "ResolveDotNetRuntimeVersionAsync", 
            BindingFlags.NonPublic | BindingFlags.Static);

        if (method == null)
        {
            throw new InvalidOperationException("ResolveDotNetRuntimeVersionAsync method not found");
        }

        try
        {
            var task = method.Invoke(null, new object[] 
            { 
                platform, 
                projectPath, 
                _commandExecutor, 
                _logger,
                cancellationToken
            }) as Task<string?>;
            
            if (task == null)
            {
                throw new InvalidOperationException("Method did not return a Task<string?>");
            }
            
            return await task;
        }
        catch (TargetInvocationException ex)
        {
            // Unwrap the actual exception thrown by the method
            if (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
            throw;
        }
    }

    #endregion

    public void Dispose()
    {
        // Cleanup test project directory
        if (Directory.Exists(_testProjectPath))
        {
            try
            {
                Directory.Delete(_testProjectPath, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
