// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Tests for ConfigCommand. 
/// These tests are run sequentially (not in parallel) because they interact with shared global state
/// in %LocalAppData% (Windows) or ~/.config (Linux/Mac).
/// </summary>
[Collection("ConfigTests")]
public class ConfigCommandTests
{
    // Use NullLoggerFactory instead of console logger to avoid I/O bottleneck during test runs
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private readonly IConfigurationWizardService _mockWizardService;

    public ConfigCommandTests()
    {
        // Create a mock wizard service that never actually runs (for import-only tests)
        _mockWizardService = Substitute.For<IConfigurationWizardService>();
    }

    private string GetTestConfigDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "a365_cli_tests", Guid.NewGuid().ToString());
        return dir;
    }



    [Fact(Skip = "Disabled due to System.CommandLine invocation overhead when running full test suite")]
    public async Task Init_ValidConfigFile_IsAcceptedAndSaved()
    {
        var logger = _loggerFactory.CreateLogger("Test");
        var configDir = GetTestConfigDir();
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "a365.config.json");

        var validConfig = new Agent365Config
        {
            TenantId = "12345678-1234-1234-1234-123456789012",
            SubscriptionId = "87654321-4321-4321-4321-210987654321",
            ResourceGroup = "rg-test",
            Location = "eastus",
            AppServicePlanName = "asp-test",
            WebAppName = "webapp-test",
            AgentIdentityDisplayName = "Test Agent"
            // AgentIdentityScopes and AgentApplicationScopes are now hardcoded
        };
        var importPath = Path.Combine(configDir, "import.json");
        await File.WriteAllTextAsync(importPath, JsonSerializer.Serialize(validConfig));

        var originalOut = Console.Out;
        using var outputWriter = new StringWriter();
        try
        {
            Console.SetOut(outputWriter);
            var root = new RootCommand();
            root.AddCommand(ConfigCommand.CreateCommand(logger, configDir, _mockWizardService));
            var result = await root.InvokeAsync($"config init -c \"{importPath}\"");
            Assert.Equal(0, result);
            Assert.True(File.Exists(configPath));
            var json = File.ReadAllText(configPath);
            Assert.Contains("12345678-1234-1234-1234-123456789012", json);
        }
        finally
        {
            Console.SetOut(originalOut);
            if (Directory.Exists(configDir)) Directory.Delete(configDir, true);
        }
    }

    [Fact]
    public async Task Init_InvalidConfigFile_IsRejectedAndShowsError()
    {
        // Create a logger that captures output to a string
        var logMessages = new List<string>();
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new TestLoggerProvider(logMessages));
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        var logger = loggerFactory.CreateLogger("Test");
        
        var configDir = GetTestConfigDir();
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "a365.config.json");

        // Missing required fields
        var invalidConfig = new Agent365Config();
        var importPath = Path.Combine(configDir, "import_invalid.json");
        await File.WriteAllTextAsync(importPath, JsonSerializer.Serialize(invalidConfig));

        try
        {
            var root = new RootCommand();
            root.AddCommand(ConfigCommand.CreateCommand(logger, configDir, _mockWizardService));
            var result = await root.InvokeAsync($"config init -c \"{importPath}\"");
            Assert.Equal(0, result);
            Assert.False(File.Exists(configPath));
            
            // Check log messages instead of console output
            var allLogs = string.Join("\n", logMessages);
            Assert.Contains("Imported configuration is invalid", allLogs);
            Assert.Contains("tenantId is required", allLogs, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(configDir)) Directory.Delete(configDir, true);
        }
    }

    [Fact]
    public void GetDefaultConfigDirectory_Windows_ReturnsAppData()
    {
        // This test validates the Windows path is correct
        // Actual path will vary by machine, so we just check structure
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var result = Microsoft.Agents.A365.DevTools.Cli.Services.ConfigService.GetGlobalConfigDirectory();

            // Should contain LocalAppData path or fall back to current directory
            Assert.True(result.Contains("Microsoft.Agents.A365.DevTools.Cli") ||
                       result == Environment.CurrentDirectory);
        }
    }

    [Fact]
    public void GetDefaultConfigDirectory_Linux_ReturnsXdgPath()
    {
        // This test validates XDG compliance on Linux
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var result = Microsoft.Agents.A365.DevTools.Cli.Services.ConfigService.GetGlobalConfigDirectory();

            // Should be XDG_CONFIG_HOME/a365 or ~/.config/a365 or current directory
            Assert.True(result.EndsWith("a365") || result == Environment.CurrentDirectory);
        }
    }

    [Fact]
    public async Task Display_WithGeneratedFlag_ShowsGeneratedConfig()
    {
        // Arrange
        var logger = _loggerFactory.CreateLogger("Test");
        var configDir = GetTestConfigDir();
        Directory.CreateDirectory(configDir);

        // Create minimal static config (required by LoadAsync)
        var staticConfigPath = Path.Combine(configDir, "a365.config.json");
        var minimalStaticConfig = new
        {
            tenantId = "12345678-1234-1234-1234-123456789012",
            subscriptionId = "87654321-4321-4321-4321-210987654321",
            resourceGroup = "test-rg",
            location = "eastus",
            appServicePlanName = "test-plan",
            webAppName = "test-app",
            agentIdentityDisplayName = "Test Agent",
            deploymentProjectPath = configDir
        };
        await File.WriteAllTextAsync(staticConfigPath, JsonSerializer.Serialize(minimalStaticConfig));

        // Create generated config
        var generatedConfigPath = Path.Combine(configDir, "a365.generated.config.json");
        var generatedContent = "{\"agentBlueprintId\":\"generated-123\",\"AgenticUserId\":\"user-456\",\"completed\":true}";
        await File.WriteAllTextAsync(generatedConfigPath, generatedContent);

        var originalOut = Console.Out;
        var originalDir = Environment.CurrentDirectory;
        using var outputWriter = new StringWriter();
        try
        {
            Console.SetOut(outputWriter);
            Environment.CurrentDirectory = configDir; // Set working directory to test dir

            // Act
            var root = new RootCommand();
            root.AddCommand(ConfigCommand.CreateCommand(logger, configDir, _mockWizardService));
            var result = await root.InvokeAsync("config display --generated");

            // Assert
            Assert.Equal(0, result);
            var output = outputWriter.ToString();
            Assert.Contains("generated-123", output);
            Assert.Contains("user-456", output);
            Assert.Contains("true", output);
        }
        finally
        {
            Console.SetOut(originalOut);
            Environment.CurrentDirectory = originalDir;

            // Cleanup with retry to avoid file locking issues
            await CleanupTestDirectoryAsync(configDir);
        }
    }

    [Fact]
    public async Task Display_PrefersLocalConfigOverGlobal()
    {
        // Arrange
        var logger = _loggerFactory.CreateLogger("Test");
        var configDir = GetTestConfigDir(); // Global config dir
        var localDir = GetTestConfigDir(); // Local config dir
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(localDir);

        // Create global config
        var globalConfigPath = Path.Combine(configDir, "a365.config.json");
        var globalConfig = new
        {
            tenantId = "11111111-1111-1111-1111-111111111111",
            subscriptionId = "22222222-2222-2222-2222-222222222222",
            resourceGroup = "global-rg",
            location = "eastus",
            appServicePlanName = "global-plan",
            webAppName = "global-app",
            agentIdentityDisplayName = "Global Agent"
        };
        await File.WriteAllTextAsync(globalConfigPath, JsonSerializer.Serialize(globalConfig));

        // Create local config (should take precedence)
        var localConfigPath = Path.Combine(localDir, "a365.config.json");
        var localConfig = new
        {
            tenantId = "33333333-3333-3333-3333-333333333333",
            subscriptionId = "44444444-4444-4444-4444-444444444444",
            resourceGroup = "local-rg",
            location = "eastus",
            appServicePlanName = "local-plan",
            webAppName = "local-app",
            agentIdentityDisplayName = "Local Agent"
        };
        await File.WriteAllTextAsync(localConfigPath, JsonSerializer.Serialize(localConfig));

        var originalOut = Console.Out;
        var originalDir = Environment.CurrentDirectory;
        using var outputWriter = new StringWriter();
        try
        {
            Environment.CurrentDirectory = localDir;
            Console.SetOut(outputWriter);

            // Act
            var root = new RootCommand();
            root.AddCommand(ConfigCommand.CreateCommand(logger, configDir, _mockWizardService));
            var result = await root.InvokeAsync("config display");

            // Assert
            Assert.Equal(0, result);
            var output = outputWriter.ToString();
            Assert.Contains("33333333-3333-3333-3333-333333333333", output);
            Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", output);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            Console.SetOut(originalOut);

            // Cleanup with retry
            await CleanupTestDirectoryAsync(configDir);
            await CleanupTestDirectoryAsync(localDir);
        }
    }

    [Fact]
    public async Task Display_WithGeneratedFlag_ShowsOnlyGeneratedConfig()
    {
        // Arrange
        var logger = _loggerFactory.CreateLogger("Test");
        var configDir = GetTestConfigDir();
        Directory.CreateDirectory(configDir);

        // Create static config (required by LoadAsync)
        var configPath = Path.Combine(configDir, "a365.config.json");
        var minimalStaticConfig = new
        {
            tenantId = "12345678-1234-1234-1234-123456789012",
            subscriptionId = "87654321-4321-4321-4321-210987654321",
            resourceGroup = "test-rg",
            location = "eastus",
            appServicePlanName = "test-plan",
            webAppName = "test-app",
            agentIdentityDisplayName = "Test Agent",
            deploymentProjectPath = configDir
        };
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(minimalStaticConfig));

        // Create generated config
        var generatedPath = Path.Combine(configDir, "a365.generated.config.json");
        await File.WriteAllTextAsync(generatedPath, "{\"agentBlueprintId\":\"generated-id-123\"}");

        var originalOut = Console.Out;
        var originalDir = Environment.CurrentDirectory;
        using var outputWriter = new StringWriter();
        try
        {
            Environment.CurrentDirectory = configDir;
            Console.SetOut(outputWriter);

            // Act
            var root = new RootCommand();
            root.AddCommand(ConfigCommand.CreateCommand(logger, configDir, _mockWizardService));
            var result = await root.InvokeAsync("config display --generated");

            // Assert
            Assert.Equal(0, result);
            var output = outputWriter.ToString();
            Assert.Contains("generated-id-123", output);
            Assert.DoesNotContain("12345678-1234-1234-1234-123456789012", output);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            Console.SetOut(originalOut);

            // Cleanup with retry
            await CleanupTestDirectoryAsync(configDir);
        }
    }

    [Fact]
    public async Task Display_WithAllFlag_ShowsBothConfigs()
    {
        // Arrange
        var logger = _loggerFactory.CreateLogger("Test");
        var configDir = GetTestConfigDir();
        Directory.CreateDirectory(configDir);

        // Create static config with required fields
        var configPath = Path.Combine(configDir, "a365.config.json");
        var minimalStaticConfig = new
        {
            tenantId = "12345678-1234-1234-1234-123456789012",
            subscriptionId = "87654321-4321-4321-4321-210987654321",
            resourceGroup = "test-rg",
            location = "eastus",
            appServicePlanName = "test-plan",
            webAppName = "test-app",
            agentIdentityDisplayName = "Test Agent",
            deploymentProjectPath = configDir
        };
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(minimalStaticConfig));

        // Create generated config
        var generatedPath = Path.Combine(configDir, "a365.generated.config.json");
        await File.WriteAllTextAsync(generatedPath, "{\"agentBlueprintId\":\"generated-id-456\"}");

        var originalOut = Console.Out;
        var originalDir = Environment.CurrentDirectory;
        using var outputWriter = new StringWriter();
        try
        {
            Environment.CurrentDirectory = configDir;
            Console.SetOut(outputWriter);

            // Act
            var root = new RootCommand();
            root.AddCommand(ConfigCommand.CreateCommand(logger, configDir, _mockWizardService));
            var result = await root.InvokeAsync("config display --all");

            // Assert
            Assert.Equal(0, result);
            var output = outputWriter.ToString();
            Assert.Contains("Static Configuration", output);
            Assert.Contains("Generated Configuration", output);
            Assert.Contains("12345678-1234-1234-1234-123456789012", output);
            Assert.Contains("generated-id-456", output);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            Console.SetOut(originalOut);

            // Cleanup with retry
            await CleanupTestDirectoryAsync(configDir);
        }
    }

    /// <summary>
    /// Helper method to clean up test directories with retry logic to handle file locking.
    /// Prevents flaky test failures in CI pipelines.
    /// </summary>
    private static async Task CleanupTestDirectoryAsync(string directory)
    {
        if (!Directory.Exists(directory))
            return;
            
        const int maxRetries = 5;
        const int delayMs = 200;
        
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                // Force garbage collection and finalization to release file handles
                if (i > 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    await Task.Delay(delayMs);
                }
                    
                Directory.Delete(directory, true);
                return; // Success
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                // Retry on IOException (file locked)
                continue;
            }
            catch (UnauthorizedAccessException) when (i < maxRetries - 1)
            {
                // Retry on access denied (file in use)
                continue;
            }
        }
        
        // If still failing after retries, log but don't fail the test
        // The temp directory will be cleaned up by the OS eventually
        Console.WriteLine($"Warning: Could not delete test directory {directory} after {maxRetries} attempts. Directory may be cleaned up later.");
    }

    [Fact]
    public void GetDefaultConfigDirectory_Windows_ReturnsLocalAppData()
    {
        // Arrange - only run on Windows
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // Skip on non-Windows
        }

        // Act
        var configDir = Microsoft.Agents.A365.DevTools.Cli.Services.ConfigService.GetGlobalConfigDirectory();

        // Assert
        Assert.NotNull(configDir);
        Assert.Contains("Microsoft.Agents.A365.DevTools.Cli", configDir);
    }

    [Fact]
    public void GetDefaultConfigDirectory_Linux_UsesXdgPath()
    {
        // Arrange - only run on Linux/Mac
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // Skip on Windows
        }

        // Save original environment
        var originalXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        
        try
        {
            // Test 1: XDG_CONFIG_HOME is set
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "/custom/config");
            var configDir1 = Microsoft.Agents.A365.DevTools.Cli.Services.ConfigService.GetGlobalConfigDirectory();
            Assert.Equal("/custom/config/a365", configDir1);

            // Test 2: XDG_CONFIG_HOME not set, HOME is set (default ~/.config/a365)
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
            Environment.SetEnvironmentVariable("HOME", "/home/testuser");
            var configDir2 = Microsoft.Agents.A365.DevTools.Cli.Services.ConfigService.GetGlobalConfigDirectory();
            Assert.Equal("/home/testuser/.config/a365", configDir2);
        }
        finally
        {
            // Restore original environment
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalXdg);
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }
}

/// <summary>
/// Test collection definition that disables parallel execution for config tests.
/// Config tests must run sequentially because they sync files to a shared global directory
/// (%LocalAppData%\Microsoft.Agents.A365.DevTools.Cli on Windows or ~/.config/a365 on Linux/Mac).
/// Running in parallel would cause race conditions and file locking issues.
/// </summary>
[CollectionDefinition("ConfigTests", DisableParallelization = true)]
public class ConfigTestCollection
{
    // This class is never instantiated. It exists only to define the collection.
}

/// <summary>
/// Test logger provider that captures log messages to a list
/// </summary>
internal class TestLoggerProvider : ILoggerProvider
{
    private readonly List<string> _logMessages;

    public TestLoggerProvider(List<string> logMessages)
    {
        _logMessages = logMessages;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new TestLogger(_logMessages);
    }

    public void Dispose() { }
}

/// <summary>
/// Test logger that captures messages to a list
/// </summary>
internal class TestLogger : ILogger
{
    private readonly List<string> _logMessages;

    public TestLogger(List<string> logMessages)
    {
        _logMessages = logMessages;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        _logMessages.Add($"[{logLevel}] {message}");
    }
}
