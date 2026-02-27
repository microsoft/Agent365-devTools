// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Tests for ConfigPermissionsSubcommand.
/// Run sequentially because tests manipulate Environment.CurrentDirectory and temp files.
/// </summary>
[Collection("ConfigTests")]
public class ConfigPermissionsSubcommandTests
{
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private readonly IConfigurationWizardService _mockWizardService = Substitute.For<IConfigurationWizardService>();

    private static readonly string ValidGuid = "00000003-0000-0000-c000-000000000000";
    private static readonly string AnotherValidGuid = "11111111-1111-1111-1111-111111111111";

    private static string GetTestConfigDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "a365_perm_tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<string> CreateConfigFileAsync(string dir, object? customConfig = null)
    {
        var configPath = Path.Combine(dir, "a365.config.json");
        var config = customConfig ?? new { tenantId = "test-tenant", subscriptionId = "test-sub" };
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config));
        return configPath;
    }

    private static async Task<RootCommand> BuildRootCommandAsync(ILogger logger, string configDir, IConfigurationWizardService wizardService)
    {
        var root = new RootCommand();
        root.AddCommand(ConfigCommand.CreateCommand(logger, configDir, wizardService));
        return await Task.FromResult(root);
    }

    private static async Task CleanupAsync(string dir)
    {
        if (!Directory.Exists(dir)) return;
        for (int i = 0; i < 5; i++)
        {
            try { Directory.Delete(dir, true); return; }
            catch { await Task.Delay(100); }
        }
    }

    // --- List (no args) ---

    [Fact]
    public async Task List_NoConfigFile_ReturnsError()
    {
        var logMessages = new List<string>();
        var logger = CreateCapturingLogger(logMessages);
        var configDir = GetTestConfigDir(); // empty — no config file

        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = configDir;
            var root = await BuildRootCommandAsync(logger, configDir, _mockWizardService);
            var result = await root.InvokeAsync("config permissions");

            result.Should().Be(1);
            logMessages.Should().Contain(m => m.Contains("Configuration file not found"));
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            await CleanupAsync(configDir);
        }
    }

    [Fact]
    public async Task List_NoPermissionsConfigured_ShowsEmptyMessage()
    {
        var logger = _loggerFactory.CreateLogger("Test");
        var configDir = GetTestConfigDir();
        await CreateConfigFileAsync(configDir);

        var originalDir = Environment.CurrentDirectory;
        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Environment.CurrentDirectory = configDir;
            Console.SetOut(output);
            var root = await BuildRootCommandAsync(logger, configDir, _mockWizardService);
            var result = await root.InvokeAsync("config permissions");

            result.Should().Be(0);
            output.ToString().Should().Contain("No custom blueprint permissions configured");
        }
        finally
        {
            Console.SetOut(originalOut);
            Environment.CurrentDirectory = originalDir;
            await CleanupAsync(configDir);
        }
    }

    [Fact]
    public async Task List_WithPermissionsConfigured_ShowsPermissions()
    {
        var logger = _loggerFactory.CreateLogger("Test");
        var configDir = GetTestConfigDir();
        var config = new
        {
            tenantId = "test-tenant",
            customBlueprintPermissions = new[]
            {
                new { resourceAppId = ValidGuid, resourceName = (string?)null, scopes = new[] { "User.Read", "Mail.Send" } }
            }
        };
        await CreateConfigFileAsync(configDir, config);

        var originalDir = Environment.CurrentDirectory;
        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Environment.CurrentDirectory = configDir;
            Console.SetOut(output);
            var root = await BuildRootCommandAsync(logger, configDir, _mockWizardService);
            var result = await root.InvokeAsync("config permissions");

            result.Should().Be(0);
            var text = output.ToString();
            text.Should().Contain(ValidGuid);
            text.Should().Contain("User.Read");
            text.Should().Contain("Mail.Send");
        }
        finally
        {
            Console.SetOut(originalOut);
            Environment.CurrentDirectory = originalDir;
            await CleanupAsync(configDir);
        }
    }

    // --- Add ---

    [Fact]
    public async Task Add_ValidPermission_SavesAndSucceeds()
    {
        var logger = _loggerFactory.CreateLogger("Test");
        var configDir = GetTestConfigDir();
        var configPath = await CreateConfigFileAsync(configDir);

        var originalDir = Environment.CurrentDirectory;
        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Environment.CurrentDirectory = configDir;
            Console.SetOut(output);
            var root = await BuildRootCommandAsync(logger, configDir, _mockWizardService);
            var result = await root.InvokeAsync($"config permissions --resource-app-id {ValidGuid} --scopes User.Read,Mail.Send");

            result.Should().Be(0);
            output.ToString().Should().Contain("Permission added successfully");

            var savedJson = await File.ReadAllTextAsync(configPath);
            savedJson.Should().Contain(ValidGuid);
            savedJson.Should().Contain("User.Read");
            savedJson.Should().Contain("Mail.Send");
        }
        finally
        {
            Console.SetOut(originalOut);
            Environment.CurrentDirectory = originalDir;
            await CleanupAsync(configDir);
        }
    }

    [Fact]
    public async Task Add_InvalidGuid_ReturnsError()
    {
        var logMessages = new List<string>();
        var logger = CreateCapturingLogger(logMessages);
        var configDir = GetTestConfigDir();
        await CreateConfigFileAsync(configDir);

        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = configDir;
            var root = await BuildRootCommandAsync(logger, configDir, _mockWizardService);
            var result = await root.InvokeAsync("config permissions --resource-app-id not-a-guid --scopes User.Read");

            result.Should().Be(1);
            logMessages.Should().Contain(m => m.Contains("Invalid resource-app-id"));
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            await CleanupAsync(configDir);
        }
    }

    [Fact]
    public async Task Add_EmptyScopes_ReturnsError()
    {
        var logMessages = new List<string>();
        var logger = CreateCapturingLogger(logMessages);
        var configDir = GetTestConfigDir();
        await CreateConfigFileAsync(configDir);

        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = configDir;
            var root = await BuildRootCommandAsync(logger, configDir, _mockWizardService);
            var result = await root.InvokeAsync($"config permissions --resource-app-id {ValidGuid} --scopes \"  ,  ,  \"");

            result.Should().Be(1);
            logMessages.Should().Contain(m => m.Contains("At least one valid scope is required"));
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            await CleanupAsync(configDir);
        }
    }

    [Fact]
    public async Task Add_OnlyResourceAppIdNoScopes_ReturnsError()
    {
        var logMessages = new List<string>();
        var logger = CreateCapturingLogger(logMessages);
        var configDir = GetTestConfigDir();
        await CreateConfigFileAsync(configDir);

        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = configDir;
            var root = await BuildRootCommandAsync(logger, configDir, _mockWizardService);
            var result = await root.InvokeAsync($"config permissions --resource-app-id {ValidGuid}");

            result.Should().Be(1);
            logMessages.Should().Contain(m => m.Contains("Both --resource-app-id and --scopes are required"));
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            await CleanupAsync(configDir);
        }
    }

    // --- Update ---

    [Fact]
    public async Task Update_ExistingPermissionWithForce_UpdatesScopes()
    {
        var logger = _loggerFactory.CreateLogger("Test");
        var configDir = GetTestConfigDir();
        var config = new
        {
            tenantId = "test-tenant",
            customBlueprintPermissions = new[]
            {
                new { resourceAppId = ValidGuid, resourceName = (string?)null, scopes = new[] { "User.Read" } }
            }
        };
        var configPath = await CreateConfigFileAsync(configDir, config);

        var originalDir = Environment.CurrentDirectory;
        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Environment.CurrentDirectory = configDir;
            Console.SetOut(output);
            var root = await BuildRootCommandAsync(logger, configDir, _mockWizardService);
            var result = await root.InvokeAsync($"config permissions --resource-app-id {ValidGuid} --scopes Mail.Send --force");

            result.Should().Be(0);
            output.ToString().Should().Contain("Permission updated successfully");

            var savedJson = await File.ReadAllTextAsync(configPath);
            savedJson.Should().Contain("Mail.Send");
            savedJson.Should().NotContain("User.Read");
        }
        finally
        {
            Console.SetOut(originalOut);
            Environment.CurrentDirectory = originalDir;
            await CleanupAsync(configDir);
        }
    }

    // --- Reset ---

    [Fact]
    public async Task Reset_ClearsAllPermissions()
    {
        var logger = _loggerFactory.CreateLogger("Test");
        var configDir = GetTestConfigDir();
        var config = new
        {
            tenantId = "test-tenant",
            customBlueprintPermissions = new[]
            {
                new { resourceAppId = ValidGuid, resourceName = (string?)null, scopes = new[] { "User.Read" } },
                new { resourceAppId = AnotherValidGuid, resourceName = (string?)null, scopes = new[] { "Mail.Send" } }
            }
        };
        var configPath = await CreateConfigFileAsync(configDir, config);

        var originalDir = Environment.CurrentDirectory;
        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Environment.CurrentDirectory = configDir;
            Console.SetOut(output);
            var root = await BuildRootCommandAsync(logger, configDir, _mockWizardService);
            var result = await root.InvokeAsync("config permissions --reset");

            result.Should().Be(0);
            var text = output.ToString();
            text.Should().Contain("Custom blueprint permissions cleared");

            var savedJson = await File.ReadAllTextAsync(configPath);
            var savedConfig = JsonSerializer.Deserialize<Agent365Config>(savedJson);
            savedConfig!.CustomBlueprintPermissions.Should().BeNullOrEmpty();
        }
        finally
        {
            Console.SetOut(originalOut);
            Environment.CurrentDirectory = originalDir;
            await CleanupAsync(configDir);
        }
    }

    // --- Config discovery ---

    [Fact]
    public async Task Add_UsesLocalConfigWhenBothExist_DoesNotModifyGlobal()
    {
        var logger = _loggerFactory.CreateLogger("Test");
        var globalDir = GetTestConfigDir();
        var localDir = GetTestConfigDir();

        var globalConfigPath = await CreateConfigFileAsync(globalDir, new { tenantId = "global-tenant" });
        var localConfigPath = await CreateConfigFileAsync(localDir, new { tenantId = "local-tenant" });

        var originalDir = Environment.CurrentDirectory;
        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Environment.CurrentDirectory = localDir;
            Console.SetOut(output);
            var root = await BuildRootCommandAsync(logger, globalDir, _mockWizardService);
            var result = await root.InvokeAsync($"config permissions --resource-app-id {ValidGuid} --scopes User.Read");

            result.Should().Be(0);

            // Local config updated
            var localJson = await File.ReadAllTextAsync(localConfigPath);
            localJson.Should().Contain(ValidGuid);

            // Global config NOT modified
            var globalJson = await File.ReadAllTextAsync(globalConfigPath);
            globalJson.Should().NotContain(ValidGuid);
        }
        finally
        {
            Console.SetOut(originalOut);
            Environment.CurrentDirectory = originalDir;
            await CleanupAsync(globalDir);
            await CleanupAsync(localDir);
        }
    }

    [Fact]
    public async Task Add_NoLocalConfig_UsesGlobalConfig()
    {
        var logger = _loggerFactory.CreateLogger("Test");
        var globalDir = GetTestConfigDir();
        var emptyLocalDir = GetTestConfigDir(); // no config file here

        var globalConfigPath = await CreateConfigFileAsync(globalDir, new { tenantId = "global-tenant" });

        var originalDir = Environment.CurrentDirectory;
        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Environment.CurrentDirectory = emptyLocalDir;
            Console.SetOut(output);
            var root = await BuildRootCommandAsync(logger, globalDir, _mockWizardService);
            var result = await root.InvokeAsync($"config permissions --resource-app-id {ValidGuid} --scopes User.Read");

            result.Should().Be(0);
            var globalJson = await File.ReadAllTextAsync(globalConfigPath);
            globalJson.Should().Contain(ValidGuid);
        }
        finally
        {
            Console.SetOut(originalOut);
            Environment.CurrentDirectory = originalDir;
            await CleanupAsync(globalDir);
            await CleanupAsync(emptyLocalDir);
        }
    }

    // --- Helper ---

    private static ILogger CreateCapturingLogger(List<string> logMessages)
    {
        var factory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new TestLoggerProvider(logMessages));
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        return factory.CreateLogger("Test");
    }
}
