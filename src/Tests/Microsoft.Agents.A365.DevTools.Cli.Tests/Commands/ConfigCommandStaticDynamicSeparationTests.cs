// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using FluentAssertions;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Critical tests to ensure static/dynamic property separation is enforced.
/// These tests verify that a365.config.json NEVER contains dynamic properties.
/// 
/// Regression test for bug where config init was serializing all properties
/// (including dynamic ones with null values) to a365.config.json.
/// </summary>
[Collection("ConfigTests")]
public class ConfigCommandStaticDynamicSeparationTests
{
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    private string GetTestConfigDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "a365_cli_separation_tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// CRITICAL TEST: Verifies that config init wizard only saves static properties to a365.config.json.
    /// This test would have caught the bug where all properties (including null dynamic ones) were saved.
    /// </summary>
    [Fact]
    public async Task ConfigInit_WithWizard_OnlySavesStaticPropertiesToConfigFile()
    {
        // Arrange
        var logger = _loggerFactory.CreateLogger("Test");
        var configDir = GetTestConfigDir();
        var localConfigPath = Path.Combine(configDir, "a365.config.json");

        // Create a mock wizard that returns a complete config object
        var mockWizard = Substitute.For<IConfigurationWizardService>();
        var wizardResult = new Agent365Config
        {
            // Static properties (should be saved)
            TenantId = "12345678-1234-1234-1234-123456789012",
            AgentIdentityDisplayName = "Test Agent",
            AgentBlueprintDisplayName = "Test Blueprint",
            AgentUserPrincipalName = "agent.test@contoso.com",
            AgentUserDisplayName = "Test Agent User",
            ManagerEmail = "manager@contoso.com",
            AgentUserUsageLocation = "US",
            DeploymentProjectPath = configDir,
            AgentDescription = "Test Agent Description"
        };

        // Set some dynamic properties (these should NOT be saved)
        wizardResult.ManagedIdentityPrincipalId = "dynamic-principal-123";
        wizardResult.AgentBlueprintId = "dynamic-blueprint-456";
        wizardResult.AgenticAppId = "dynamic-identity-789";
        wizardResult.AgenticUserId = "dynamic-user-abc";
        wizardResult.BotId = "dynamic-bot-def";
        wizardResult.ResourceConsents.Add(new ResourceConsent
        {
            ResourceName = "Microsoft Graph",
            ResourceAppId = AuthenticationConstants.MicrosoftGraphResourceAppId,
            ConsentGranted = true
        });
        wizardResult.Completed = true;
        wizardResult.CliVersion = "1.0.0";

        mockWizard.RunWizardAsync(Arg.Any<Agent365Config?>()).Returns(wizardResult);

        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = configDir;

            // Act - Run config init
            var root = new RootCommand();
            root.AddCommand(ConfigCommand.CreateCommand(logger, configDir, mockWizard));
            var result = await root.InvokeAsync("config init");

            // Assert
            result.Should().Be(0, "command should succeed");
            File.Exists(localConfigPath).Should().BeTrue("config file should be created");

            // Read the saved config file
            var savedJson = await File.ReadAllTextAsync(localConfigPath);
            var savedDoc = JsonDocument.Parse(savedJson);
            var rootElement = savedDoc.RootElement;

            // Verify STATIC properties ARE present
            rootElement.TryGetProperty("tenantId", out _).Should().BeTrue("static property tenantId should be saved");
            rootElement.TryGetProperty("agentIdentityDisplayName", out _).Should().BeTrue("static property agentIdentityDisplayName should be saved");
            rootElement.TryGetProperty("deploymentProjectPath", out _).Should().BeTrue("static property deploymentProjectPath should be saved");

            // Verify DYNAMIC properties are NOT present (THIS IS THE CRITICAL ASSERTION)
            rootElement.TryGetProperty("managedIdentityPrincipalId", out _).Should().BeFalse(
                "REGRESSION: dynamic property managedIdentityPrincipalId should NOT be in a365.config.json");
            rootElement.TryGetProperty("agentBlueprintId", out _).Should().BeFalse(
                "REGRESSION: dynamic property agentBlueprintId should NOT be in a365.config.json");
            rootElement.TryGetProperty("AgenticAppId", out _).Should().BeFalse(
                "REGRESSION: dynamic property AgenticAppId should NOT be in a365.config.json");
            rootElement.TryGetProperty("AgenticUserId", out _).Should().BeFalse(
                "REGRESSION: dynamic property AgenticUserId should NOT be in a365.config.json");
            rootElement.TryGetProperty("botId", out _).Should().BeFalse(
                "REGRESSION: dynamic property botId should NOT be in a365.config.json");
            rootElement.TryGetProperty("botMsaAppId", out _).Should().BeFalse(
                "REGRESSION: dynamic property botMsaAppId should NOT be in a365.config.json");
            rootElement.TryGetProperty("messagingEndpoint", out _).Should().BeFalse(
                "REGRESSION: dynamic property messagingEndpoint should NOT be in a365.config.json");
            rootElement.TryGetProperty("resourceConsents", out _).Should().BeFalse(
                "REGRESSION: dynamic property resourceConsents should NOT be in a365.config.json");
            rootElement.TryGetProperty("inheritanceConfigured", out _).Should().BeFalse(
                "REGRESSION: dynamic property inheritanceConfigured should NOT be in a365.config.json");
            rootElement.TryGetProperty("inheritanceConfigError", out _).Should().BeFalse(
                "REGRESSION: dynamic property inheritanceConfigError should NOT be in a365.config.json");
            rootElement.TryGetProperty("deploymentLastTimestamp", out _).Should().BeFalse(
                "REGRESSION: dynamic property deploymentLastTimestamp should NOT be in a365.config.json");
            rootElement.TryGetProperty("deploymentLastStatus", out _).Should().BeFalse(
                "REGRESSION: dynamic property deploymentLastStatus should NOT be in a365.config.json");
            rootElement.TryGetProperty("lastUpdated", out _).Should().BeFalse(
                "REGRESSION: dynamic property lastUpdated should NOT be in a365.config.json");
            rootElement.TryGetProperty("cliVersion", out _).Should().BeFalse(
                "REGRESSION: dynamic property cliVersion should NOT be in a365.config.json");
            rootElement.TryGetProperty("completed", out _).Should().BeFalse(
                "REGRESSION: dynamic property completed should NOT be in a365.config.json");
            rootElement.TryGetProperty("completedAt", out _).Should().BeFalse(
                "REGRESSION: dynamic property completedAt should NOT be in a365.config.json");

            // Additional assertion: Count properties to ensure no extras snuck in
            var propertyCount = 0;
            foreach (var _ in rootElement.EnumerateObject())
            {
                propertyCount++;
            }

            // Static config should have ~14 properties (varies based on optional fields)
            // But definitely should NOT have 30+ properties (which would include dynamic ones)
            propertyCount.Should().BeLessThan(20,
                "a365.config.json should only contain static properties (~14), not all properties including dynamic ones");
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            if (Directory.Exists(configDir))
            {
                await CleanupTestDirectoryAsync(configDir);
            }
        }
    }

    /// <summary>
    /// CRITICAL TEST: Verifies that config init with import only saves static properties.
    /// This test ensures imported configs are properly filtered before saving.
    /// </summary>
    [Fact]
    public async Task ConfigInit_WithImport_OnlySavesStaticPropertiesToConfigFile()
    {
        // Arrange
        var logger = _loggerFactory.CreateLogger("Test");
        var configDir = GetTestConfigDir();
        var importPath = Path.Combine(configDir, "import.json");
        var localConfigPath = Path.Combine(configDir, "a365.config.json");

        // Create an import file with BOTH static and dynamic properties
        var importConfig = new Agent365Config
        {
            // Static properties (ALL required fields for validation to pass)
            TenantId = "import-tenant-123",
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6", // Required clientAppId
            AgentIdentityDisplayName = "Import Agent",
            AgentBlueprintDisplayName = "Import Blueprint",
            AgentUserPrincipalName = "import@test.com",
            AgentUserDisplayName = "Import User",
            ManagerEmail = "manager@test.com",
            AgentUserUsageLocation = "US",
            DeploymentProjectPath = configDir,
            AgentDescription = "Import Agent Description"
        };

        // Add dynamic properties that should NOT be saved
        importConfig.AgentBlueprintId = "should-not-be-saved-123";
        importConfig.BotId = "should-not-be-saved-456";
        importConfig.ResourceConsents.Add(new ResourceConsent
        {
            ResourceName = "Should Not Be Saved",
            ResourceAppId = "00000000-0000-0000-0000-000000000000",
            ConsentGranted = true
        });
        importConfig.Completed = true;

        // Write the full config (including dynamic properties) to import file
        var importJson = JsonSerializer.Serialize(importConfig, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(importPath, importJson);

        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = configDir;

            // Act - Import config
            var root = new RootCommand();
            root.AddCommand(ConfigCommand.CreateCommand(logger, configDir, wizardService: null));
            var result = await root.InvokeAsync($"config init -c \"{importPath}\"");

            // Assert
            result.Should().Be(0, "import should succeed");
            File.Exists(localConfigPath).Should().BeTrue("config file should be created");

            // Read the saved config
            var savedJson = await File.ReadAllTextAsync(localConfigPath);
            var savedDoc = JsonDocument.Parse(savedJson);
            var rootElement = savedDoc.RootElement;

            // Verify static properties ARE present
            rootElement.GetProperty("tenantId").GetString().Should().Be("import-tenant-123");

            // Verify dynamic properties are NOT present
            rootElement.TryGetProperty("agentBlueprintId", out _).Should().BeFalse(
                "REGRESSION: imported dynamic property agentBlueprintId should NOT be saved to a365.config.json");
            rootElement.TryGetProperty("botId", out _).Should().BeFalse(
                "REGRESSION: imported dynamic property botId should NOT be saved to a365.config.json");
            rootElement.TryGetProperty("resourceConsents", out _).Should().BeFalse(
                "REGRESSION: imported dynamic property resourceConsents should NOT be saved to a365.config.json");
            rootElement.TryGetProperty("completed", out _).Should().BeFalse(
                "REGRESSION: imported dynamic property completed should NOT be saved to a365.config.json");
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            if (Directory.Exists(configDir))
            {
                await CleanupTestDirectoryAsync(configDir);
            }
        }
    }

    /// <summary>
    /// Test that GetStaticConfig() helper method correctly filters properties.
    /// This is a unit test for the method used by ConfigCommand to ensure separation.
    /// </summary>
    [Fact]
    public void GetStaticConfig_OnlyReturnsInitOnlyProperties()
    {
        // Arrange
        var config = new Agent365Config
        {
            // Static properties (init-only)
            TenantId = "tenant-123",
            AgentIdentityDisplayName = "Test Agent",
            DeploymentProjectPath = "/test"
        };

        // Set dynamic properties (get/set)
        config.AgentBlueprintId = "blueprint-789";
        config.BotId = "bot-abc";
        config.ResourceConsents.Add(new ResourceConsent
        {
            ResourceName = "Microsoft Graph",
            ResourceAppId = AuthenticationConstants.MicrosoftGraphResourceAppId,
            ConsentGranted = true
        });
        config.Completed = true;

        // Act
        var staticConfig = config.GetStaticConfig();
        var staticJson = JsonSerializer.Serialize(staticConfig);
        var staticDoc = JsonDocument.Parse(staticJson);
        var root = staticDoc.RootElement;

        // Assert - Static properties present
        root.TryGetProperty("tenantId", out _).Should().BeTrue("static property should be included");
        root.TryGetProperty("agentIdentityDisplayName", out _).Should().BeTrue("static property should be included");

        // Assert - Dynamic properties NOT present
        root.TryGetProperty("agentBlueprintId", out _).Should().BeFalse(
            "dynamic property should NOT be included in GetStaticConfig()");
        root.TryGetProperty("botId", out _).Should().BeFalse(
            "dynamic property should NOT be included in GetStaticConfig()");
        root.TryGetProperty("resourceConsents", out _).Should().BeFalse(
            "dynamic property should NOT be included in GetStaticConfig()");
        root.TryGetProperty("completed", out _).Should().BeFalse(
            "dynamic property should NOT be included in GetStaticConfig()");
    }

    /// <summary>
    /// Test that GetGeneratedConfig() helper method correctly filters properties.
    /// Ensures generated config only contains dynamic properties.
    /// </summary>
    [Fact]
    public void GetGeneratedConfig_OnlyReturnsMutableProperties()
    {
        // Arrange
        var config = new Agent365Config
        {
            // Static properties (init-only)
            TenantId = "tenant-123",
        };

        // Set dynamic properties (get/set)
        config.AgentBlueprintId = "blueprint-789";
        config.BotId = "bot-abc";
        config.ResourceConsents.Add(new ResourceConsent
        {
            ResourceName = "Microsoft Graph",
            ResourceAppId = AuthenticationConstants.MicrosoftGraphResourceAppId,
            ConsentGranted = true
        });
        config.Completed = true;

        // Act
        var generatedConfig = config.GetGeneratedConfig();
        var generatedJson = JsonSerializer.Serialize(generatedConfig);
        var generatedDoc = JsonDocument.Parse(generatedJson);
        var root = generatedDoc.RootElement;

        // Assert - Dynamic properties present
        root.TryGetProperty("agentBlueprintId", out _).Should().BeTrue("dynamic property should be included");
        root.TryGetProperty("botId", out _).Should().BeTrue("dynamic property should be included");
        root.TryGetProperty("resourceConsents", out _).Should().BeTrue("dynamic property should be included");
        root.TryGetProperty("completed", out _).Should().BeTrue("dynamic property should be included");

        // Assert - Static properties NOT present
        root.TryGetProperty("tenantId", out _).Should().BeFalse(
            "static property should NOT be included in GetGeneratedConfig()");
        root.TryGetProperty("agentIdentityDisplayName", out _).Should().BeFalse(
            "static property should NOT be included in GetGeneratedConfig()");
    }

    [Fact]
    public async Task ConfigInit_WithWizard_MessagingEndpoint()
    {
        // Arrange
        var logger = _loggerFactory.CreateLogger("Test");
        var configDir = GetTestConfigDir();
        var localConfigPath = Path.Combine(configDir, "a365.config.json");

        // Create a mock wizard that returns a complete config object
        var mockWizard = Substitute.For<IConfigurationWizardService>();
        var wizardResult = new Agent365Config
        {
            // Static properties (should be saved)
            TenantId = "12345678-1234-1234-1234-123456789012",
            MessagingEndpoint = "https://custom-endpoint.contoso.com/api/messages",
            AgentIdentityDisplayName = "Test Agent",
            AgentBlueprintDisplayName = "Test Blueprint",
            AgentUserPrincipalName = "agent.test@contoso.com",
            AgentUserDisplayName = "Test Agent User",
            ManagerEmail = "manager@contoso.com",
            AgentUserUsageLocation = "US",
            DeploymentProjectPath = configDir,
            AgentDescription = "Test Agent Description"
        };

        mockWizard.RunWizardAsync(Arg.Any<Agent365Config?>()).Returns(wizardResult);

        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = configDir;

            // Act - Run config init
            var root = new RootCommand();
            root.AddCommand(ConfigCommand.CreateCommand(logger, configDir, mockWizard));
            var result = await root.InvokeAsync("config init");

            // Assert
            result.Should().Be(0, "command should succeed");
            File.Exists(localConfigPath).Should().BeTrue("config file should be created");

            // Read the saved config file
            var savedJson = await File.ReadAllTextAsync(localConfigPath);
            var savedDoc = JsonDocument.Parse(savedJson);
            var rootElement = savedDoc.RootElement;

            // Verify STATIC properties ARE present
            rootElement.TryGetProperty("tenantId", out _).Should().BeTrue("static property tenantId should be saved");
            rootElement.TryGetProperty("messagingEndpoint", out _).Should().BeTrue("static property messagingEndpoint should be saved");
            rootElement.TryGetProperty("agentIdentityDisplayName", out _).Should().BeTrue("static property agentIdentityDisplayName should be saved");
            rootElement.TryGetProperty("deploymentProjectPath", out _).Should().BeTrue("static property deploymentProjectPath should be saved");
            // Verify deploy-era properties are no longer present
            rootElement.TryGetProperty("subscriptionId", out _).Should().BeFalse("subscriptionId was removed when a365 deploy was removed");
            rootElement.TryGetProperty("resourceGroup", out _).Should().BeFalse("resourceGroup was removed when a365 deploy was removed");
            rootElement.TryGetProperty("location", out _).Should().BeFalse("location was removed when bot endpoint registration was disabled");
            rootElement.TryGetProperty("appServicePlanName", out _).Should().BeFalse("appServicePlanName was removed when a365 deploy was removed");
            rootElement.TryGetProperty("webAppName", out _).Should().BeFalse("webAppName was removed when a365 deploy was removed");
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            if (Directory.Exists(configDir))
            {
                await CleanupTestDirectoryAsync(configDir);
            }
        }
    }

    /// <summary>
    /// TryGetConfigField returns the string value when the field exists in the generated config.
    /// </summary>
    [Fact]
    public void TryGetConfigField_FieldInGeneratedConfig_ReturnsValue()
    {
        var logger = NullLogger.Instance;
        var config = new Agent365Config
        {
            TenantId = "tenant-123",
        };
        config.BotMessagingEndpoint = "https://myapp.azurewebsites.net/api/messages";

        var result = ConfigCommand.TryGetConfigField(config, "messagingEndpoint", checkGenerated: true, checkStatic: false, logger);

        result.Should().Be("https://myapp.azurewebsites.net/api/messages",
            because: "TryGetConfigField must return BotMessagingEndpoint when searching generated config");
    }

    /// <summary>
    /// TryGetConfigField returns the string value when the field exists only in the static config.
    /// </summary>
    [Fact]
    public void TryGetConfigField_FieldInStaticConfig_ReturnsValue()
    {
        var logger = NullLogger.Instance;
        var config = new Agent365Config
        {
            TenantId = "tenant-abc",
        };

        var result = ConfigCommand.TryGetConfigField(config, "tenantId", checkGenerated: false, checkStatic: true, logger);

        result.Should().Be("tenant-abc",
            because: "TryGetConfigField must return static config value when checkStatic is true");
    }

    /// <summary>
    /// TryGetConfigField returns null when the field is not found in either config.
    /// </summary>
    [Fact]
    public void TryGetConfigField_FieldNotFound_ReturnsNull()
    {
        var logger = NullLogger.Instance;
        var config = new Agent365Config
        {
            TenantId = "tenant-123"
        };

        var result = ConfigCommand.TryGetConfigField(config, "nonExistentField", checkGenerated: true, checkStatic: true, logger);

        result.Should().BeNull(
            because: "TryGetConfigField must return null when the field does not exist in any config");
    }

    /// <summary>
    /// TryGetConfigField returns raw JSON text for non-string fields (e.g. booleans).
    /// </summary>
    [Fact]
    public void TryGetConfigField_NonStringField_ReturnsRawJson()
    {
        var logger = NullLogger.Instance;
        var config = new Agent365Config
        {
            TenantId = "tenant-123"
        };
        config.AgentBlueprintClientSecretProtected = true;

        var result = ConfigCommand.TryGetConfigField(config, "agentBlueprintClientSecretProtected", checkGenerated: true, checkStatic: false, logger);

        result.Should().Be("true",
            because: "TryGetConfigField must return the raw JSON representation for boolean fields");
    }

    /// <summary>
    /// TryGetConfigField searches generated config before static config (generated wins when field exists in both).
    /// </summary>
    [Fact]
    public void TryGetConfigField_FieldInBothConfigs_ReturnsGeneratedValue()
    {
        var logger = NullLogger.Instance;
        // MessagingEndpoint (init-only, static) and BotMessagingEndpoint (settable, generated)
        // both serialize as "messagingEndpoint" in their respective config dictionaries.
        var config = new Agent365Config
        {
            TenantId = "tenant-123",
            MessagingEndpoint = "https://static-endpoint.contoso.com/api/messages"
        };
        config.BotMessagingEndpoint = "https://derived-endpoint.azurewebsites.net/api/messages";

        var result = ConfigCommand.TryGetConfigField(config, "messagingEndpoint", checkGenerated: true, checkStatic: true, logger);

        result.Should().Be("https://derived-endpoint.azurewebsites.net/api/messages",
            because: "generated config must take precedence over static config when both contain the same field");
    }

    /// <summary>
    /// TryGetConfigField falls back to static config when the field is absent from generated config.
    /// </summary>
    [Fact]
    public void TryGetConfigField_FieldAbsentFromGenerated_FallsBackToStatic()
    {
        var logger = NullLogger.Instance;
        var config = new Agent365Config
        {
            TenantId = "tenant-fallback",
            MessagingEndpoint = "https://static-endpoint.contoso.com/api/messages"
        };
        // BotMessagingEndpoint is null — not present in generated config

        var result = ConfigCommand.TryGetConfigField(config, "messagingEndpoint", checkGenerated: true, checkStatic: true, logger);

        result.Should().Be("https://static-endpoint.contoso.com/api/messages",
            because: "TryGetConfigField must fall back to static config when the field is missing from generated config");
    }

    /// <summary>
    /// Helper method to clean up test directories with retry logic
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
                if (i > 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    await Task.Delay(delayMs);
                }

                Directory.Delete(directory, true);
                return;
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                continue;
            }
            catch (UnauthorizedAccessException) when (i < maxRetries - 1)
            {
                continue;
            }
        }
    }
}
