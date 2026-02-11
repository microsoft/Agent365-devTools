// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Unit tests for ConfigService class with the new Agent365Config two-file model.
/// Tests LoadAsync (merge), SaveStateAsync (split), validation, and file operations.
/// </summary>
public class Agent365ConfigServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly ConfigService _service;

    public Agent365ConfigServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"agent365-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _service = new ConfigService();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    #region LoadAsync Tests

    [Fact]
    public async Task LoadAsync_ThrowsFileNotFoundException_WhenConfigFileDoesNotExist()
    {
        // Arrange
        var configPath = Path.Combine(_testDirectory, "nonexistent.json");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _service.LoadAsync(configPath));
    }

    [Fact]
    public async Task LoadAsync_LoadsStaticConfigOnly_WhenStateFileDoesNotExist()
    {
        // Arrange
        var configPath = Path.Combine(_testDirectory, "a365.config.json");
        var staticConfig = new
        {
            tenantId = "12345678-1234-1234-1234-123456789012",
            subscriptionId = "87654321-4321-4321-4321-210987654321",
            resourceGroup = "rg-test",
            location = "eastus",
            appServicePlanName = "asp-test",
            webAppName = "webapp-test",
            agentIdentityDisplayName = "Test Agent",
            // agentIdentityScopes are now hardcoded
            deploymentProjectPath = "./test"
        };
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(staticConfig, new JsonSerializerOptions { WriteIndented = true }));

        // Act
        var config = await _service.LoadAsync(configPath, Path.Combine(_testDirectory, "nonexistent.json"));

        // Assert
        Assert.NotNull(config);
        Assert.Equal("12345678-1234-1234-1234-123456789012", config.TenantId);
        Assert.Equal("87654321-4321-4321-4321-210987654321", config.SubscriptionId);
        Assert.Equal("rg-test", config.ResourceGroup);
        Assert.Equal("Test Agent", config.AgentIdentityDisplayName);
        // Dynamic properties should be null
        Assert.Null(config.AgentBlueprintId);
        Assert.Null(config.BotId);
    }

    [Fact]
    public async Task LoadAsync_MergesStaticAndDynamicConfig_WhenBothFilesExist()
    {
        // Arrange
        var configPath = Path.Combine(_testDirectory, "a365.config.json");
        var statePath = Path.Combine(_testDirectory, "a365.generated.config.json");

        var staticConfig = new
        {
            tenantId = "12345678-1234-1234-1234-123456789012",
            subscriptionId = "87654321-4321-4321-4321-210987654321",
            resourceGroup = "rg-test",
            location = "eastus",
            appServicePlanName = "asp-test",
            webAppName = "webapp-test",
            agentIdentityDisplayName = "Test Agent",
            // agentIdentityScopes are now hardcoded
            deploymentProjectPath = "./test"
        };

        var dynamicState = new
        {
            managedIdentityPrincipalId = "11111111-2222-3333-4444-555555555555",
            agentBlueprintId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            botId = "99999999-8888-7777-6666-555555555555",
            lastUpdated = "2025-10-14T12:00:00Z",
            cliVersion = "1.0.0"
        };

        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(staticConfig, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(dynamicState, new JsonSerializerOptions { WriteIndented = true }));

        // Act
        var config = await _service.LoadAsync(configPath, statePath);

        // Assert - static properties
        Assert.Equal("12345678-1234-1234-1234-123456789012", config.TenantId);
        Assert.Equal("87654321-4321-4321-4321-210987654321", config.SubscriptionId);
        Assert.Equal("rg-test", config.ResourceGroup);
        Assert.Equal("Test Agent", config.AgentIdentityDisplayName);

        // Assert - dynamic properties
        Assert.Equal("11111111-2222-3333-4444-555555555555", config.ManagedIdentityPrincipalId);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", config.AgentBlueprintId);
        Assert.Equal("99999999-8888-7777-6666-555555555555", config.BotId);
        Assert.Equal("1.0.0", config.CliVersion);
    }

    #endregion

    #region SaveStateAsync Tests

    [Fact]
    public async Task SaveStateAsync_SavesOnlyDynamicProperties()
    {
        // Arrange
        var statePath = Path.Combine(_testDirectory, "a365.generated.config.json");
        var config = new Agent365Config
        {
            // Static properties (init)
            TenantId = "12345678-1234-1234-1234-123456789012",
            SubscriptionId = "87654321-4321-4321-4321-210987654321",
            ResourceGroup = "rg-test",
            Location = "eastus",
            AppServicePlanName = "asp-test",
            WebAppName = "webapp-test",
            AgentIdentityDisplayName = "Test Agent",
            // AgentIdentityScopes are now hardcoded
            DeploymentProjectPath = "./test"
        };

        // Set dynamic properties
        config.AgentBlueprintId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        config.BotId = "99999999-8888-7777-6666-555555555555";
        config.ResourceConsents.Add(new ResourceConsent
        {
            ResourceName = "Microsoft Graph",
            ResourceAppId = AuthenticationConstants.MicrosoftGraphResourceAppId,
            ConsentGranted = true
        });

        // Act
        await _service.SaveStateAsync(config, statePath);

        // Assert
        Assert.True(File.Exists(statePath));
        var json = await File.ReadAllTextAsync(statePath);
        var savedData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        Assert.NotNull(savedData);

        // Should have dynamic properties
        Assert.True(savedData.ContainsKey("agentBlueprintId"));
        Assert.True(savedData.ContainsKey("botId"));
        Assert.True(savedData.ContainsKey("resourceConsents"));
        Assert.True(savedData.ContainsKey("lastUpdated")); // Added by SaveStateAsync
        Assert.True(savedData.ContainsKey("cliVersion")); // Added by SaveStateAsync

        // Should NOT have static properties
        Assert.False(savedData.ContainsKey("tenantId"));
        Assert.False(savedData.ContainsKey("subscriptionId"));
        Assert.False(savedData.ContainsKey("resourceGroup"));
        Assert.False(savedData.ContainsKey("appServicePlanName"));
    }

    [Fact]
    public async Task SaveStateAsync_OverwritesExistingFile()
    {
        // Arrange
        var statePath = Path.Combine(_testDirectory, "state.json");
        var config1 = new Agent365Config { TenantId = "12345678-1234-1234-1234-123456789012" };
        config1.AgentBlueprintId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        var config2 = new Agent365Config { TenantId = "12345678-1234-1234-1234-123456789012" };
        config2.AgentBlueprintId = "bbbbbbbb-aaaa-cccc-dddd-eeeeeeeeeeee";

        // Act
        await _service.SaveStateAsync(config1, statePath);
        var firstContent = await File.ReadAllTextAsync(statePath);
        Assert.Contains("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", firstContent);

        await _service.SaveStateAsync(config2, statePath);
        var secondContent = await File.ReadAllTextAsync(statePath);

        // Assert
        Assert.Contains("bbbbbbbb-aaaa-cccc-dddd-eeeeeeeeeeee", secondContent);
        Assert.DoesNotContain("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", secondContent);
    }

    [Fact]
    public async Task SaveStateAsync_SavesLocallyWhenStaticConfigExists()
    {
        // Arrange - Create a project directory with a static config
        var projectDir = Path.Combine(Path.GetTempPath(), $"agent365-project-{Guid.NewGuid()}");
        Directory.CreateDirectory(projectDir);
        
        try
        {
            var originalDir = Environment.CurrentDirectory;
            Environment.CurrentDirectory = projectDir;
            
            try
            {
                // Create a static config file in the project directory
                var staticConfigPath = Path.Combine(projectDir, ConfigConstants.DefaultConfigFileName);
                var staticConfig = new
                {
                    tenantId = "12345678-1234-1234-1234-123456789012",
                    subscriptionId = "87654321-4321-4321-4321-210987654321",
                    resourceGroup = "rg-test",
                    location = "eastus",
                    appServicePlanName = "asp-test",
                    webAppName = "webapp-test",
                    agentIdentityDisplayName = "Test Agent"
                };
                await File.WriteAllTextAsync(staticConfigPath, JsonSerializer.Serialize(staticConfig, new JsonSerializerOptions { WriteIndented = true }));

                // Create a config to save
                var config = new Agent365Config { TenantId = "12345678-1234-1234-1234-123456789012" };
                config.AgentBlueprintId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

                // Get global config path to verify it's NOT written there
                var globalDir = ConfigService.GetGlobalConfigDirectory();
                var globalStatePath = Path.Combine(globalDir, ConfigConstants.DefaultStateFileName);
                
                // Delete global state if it exists to ensure clean test
                if (File.Exists(globalStatePath))
                {
                    File.Delete(globalStatePath);
                }

                // Act - Save state (should go to local directory, NOT global)
                await _service.SaveStateAsync(config, ConfigConstants.DefaultStateFileName);

                // Assert - State should be saved locally
                var localStatePath = Path.Combine(projectDir, ConfigConstants.DefaultStateFileName);
                Assert.True(File.Exists(localStatePath), "Local state file should exist in project directory");
                
                var localContent = await File.ReadAllTextAsync(localStatePath);
                Assert.Contains("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", localContent);

                // Assert - State should NOT be saved to global directory
                Assert.False(File.Exists(globalStatePath), "Global state file should NOT exist when saving in a project directory");
            }
            finally
            {
                Environment.CurrentDirectory = originalDir;
            }
        }
        finally
        {
            if (Directory.Exists(projectDir))
            {
                Directory.Delete(projectDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveStateAsync_SavesGloballyWhenNoStaticConfigExists()
    {
        // Arrange - Use a directory without a static config
        var tempDir = Path.Combine(Path.GetTempPath(), $"agent365-noproj-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            var originalDir = Environment.CurrentDirectory;
            Environment.CurrentDirectory = tempDir;
            
            try
            {
                // Create a config to save
                var config = new Agent365Config { TenantId = "12345678-1234-1234-1234-123456789012" };
                config.AgentBlueprintId = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";

                // Get global config path
                var globalDir = ConfigService.GetGlobalConfigDirectory();
                var globalStatePath = Path.Combine(globalDir, ConfigConstants.DefaultStateFileName);
                
                // Delete global state if it exists to ensure clean test
                if (File.Exists(globalStatePath))
                {
                    File.Delete(globalStatePath);
                }

                // Act - Save state (should go to global directory, NOT local)
                await _service.SaveStateAsync(config, ConfigConstants.DefaultStateFileName);

                // Assert - State should be saved globally
                Assert.True(File.Exists(globalStatePath), "Global state file should exist when no local config present");
                
                var globalContent = await File.ReadAllTextAsync(globalStatePath);
                Assert.Contains("bbbbbbbb-cccc-dddd-eeee-ffffffffffff", globalContent);

                // Assert - State should NOT be saved to current directory
                var localStatePath = Path.Combine(tempDir, ConfigConstants.DefaultStateFileName);
                Assert.False(File.Exists(localStatePath), "Local state file should NOT exist when no static config present");
            }
            finally
            {
                Environment.CurrentDirectory = originalDir;
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    #endregion

    #region ValidateAsync Tests

    [Fact]
    public async Task ValidateAsync_ReturnsSuccess_ForValidConfig()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "12345678-1234-1234-1234-123456789012",
            SubscriptionId = "87654321-4321-4321-4321-210987654321",
            ResourceGroup = "rg-test",
            Location = "eastus",
            AppServicePlanName = "asp-test",
            WebAppName = "webapp-test",
            AgentIdentityDisplayName = "Test Agent",
            // AgentIdentityScopes are now hardcoded
            DeploymentProjectPath = "./test"
        };

        // Act
        var result = await _service.ValidateAsync(config);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsErrors_ForMissingRequiredFields()
    {
        // Arrange
        var config = new Agent365Config
        {
            // Missing required fields
        };

        // Act
        var result = await _service.ValidateAsync(config);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("TenantId"));
        Assert.Contains(result.Errors, e => e.Contains("SubscriptionId"));
        Assert.Contains(result.Errors, e => e.Contains("ResourceGroup"));
        Assert.Contains(result.Errors, e => e.Contains("Location"));
    }

    [Fact]
    public async Task ValidateAsync_ReturnsErrors_ForInvalidGuidFormat()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "not-a-guid",
            SubscriptionId = "also-not-a-guid",
            ResourceGroup = "rg-test",
            Location = "eastus"
        };

        // Act
        var result = await _service.ValidateAsync(config);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("TenantId") && e.Contains("GUID"));
        Assert.Contains(result.Errors, e => e.Contains("SubscriptionId") && e.Contains("GUID"));
    }

    #endregion

    #region Helper Method Tests

    [Fact]
    public async Task ConfigExistsAsync_ReturnsTrue_WhenFileExists()
    {
        // Arrange
        var configPath = Path.Combine(_testDirectory, "existing.json");
        await File.WriteAllTextAsync(configPath, "{}");

        // Act
        var exists = await _service.ConfigExistsAsync(configPath);

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public async Task CreateDefaultConfigAsync_CreatesConfigFile()
    {
    // Arrange
    var configPath = Path.Combine(_testDirectory, "default-config.json");
    // Ensure the file exists to match new logic
    File.WriteAllText(configPath, "{}");

    // Act
    await _service.CreateDefaultConfigAsync(configPath);

    // Assert
    Assert.True(File.Exists(configPath));
    var json = await File.ReadAllTextAsync(configPath);
    var config = JsonSerializer.Deserialize<Agent365Config>(json);
    Assert.NotNull(config);
    Assert.Equal(string.Empty, config.Location);
    Assert.Equal("B1", config.AppServicePlanSku);
    Assert.Equal(string.Empty, config.TenantId);
    Assert.Equal(string.Empty, config.SubscriptionId);
    Assert.Equal(string.Empty, config.ResourceGroup);
    Assert.Equal(string.Empty, config.WebAppName);
    Assert.Equal(string.Empty, config.AgentIdentityDisplayName);
    }

    [Fact]
    public async Task InitializeStateAsync_CreatesEmptyStateFile()
    {
        // Arrange
        var statePath = Path.Combine(_testDirectory, "init-state.json");

        // Act
        await _service.InitializeStateAsync(statePath);

        // Assert
        Assert.True(File.Exists(statePath));
        var json = await File.ReadAllTextAsync(statePath);
        var state = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        Assert.NotNull(state);
        Assert.True(state.ContainsKey("lastUpdated"));
        Assert.True(state.ContainsKey("cliVersion"));
    }

    #endregion
}
