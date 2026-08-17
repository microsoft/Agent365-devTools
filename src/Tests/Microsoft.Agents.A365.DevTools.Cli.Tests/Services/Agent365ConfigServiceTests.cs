// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

[CollectionDefinition("ConfigTests", DisableParallelization = true)]
public class ConfigTestCollection { }

/// <summary>
/// Unit tests for ConfigService class with the new Agent365Config two-file model.
/// Tests LoadAsync (merge), SaveStateAsync (split), validation, and file operations.
/// </summary>
[Collection("ConfigTests")]
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
        await Assert.ThrowsAsync<ConfigFileNotFoundException>(
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
            clientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6",
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
            clientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6",
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

    /// <summary>
    /// Regression test: AgentBlueprintDisplayName must remain <c>init</c>-only (classified as a
    /// static, user-configured field written to a365.config.json) — not a dynamic/generated field.
    /// SaveStateAsync/ExtractDynamicProperties classify static vs. dynamic purely by reflecting on
    /// the property's setter kind (see ConfigService.HasPublicSetter), so if this property is ever
    /// changed to a plain mutable setter, it would silently be written to a365.generated.config.json
    /// instead of a365.config.json, and a365.config.json's value would then be discarded/overwritten
    /// on every SaveStateAsync call — corrupting the "a365.config.json is the source of truth for
    /// displayName" invariant relied on by blueprint discovery (see BlueprintSubcommand.CreateAgentBlueprintAsync).
    /// </summary>
    [Fact]
    public async Task SaveStateAsync_DoesNotIncludeAgentBlueprintDisplayName()
    {
        var statePath = Path.Combine(_testDirectory, "a365.generated.config.json");
        var config = new Agent365Config
        {
            TenantId = "12345678-1234-1234-1234-123456789012",
            AgentIdentityDisplayName = "Test Agent",
            AgentBlueprintDisplayName = "Test Agent Blueprint",
        };
        config.AgentBlueprintId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        await _service.SaveStateAsync(config, statePath);

        var json = await File.ReadAllTextAsync(statePath);
        var savedData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        savedData.Should().NotBeNull();
        savedData!.ContainsKey("agentBlueprintDisplayName").Should().BeFalse(
            because: "agentBlueprintDisplayName is a static/user-configured field belonging in a365.config.json, not the generated/dynamic state file");
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
    public async Task SaveStateAsync_SavesLocallyEvenWhenNoStaticConfigExists()
    {
        // Global config directory fallback was removed — SaveStateAsync always writes
        // to the current directory regardless of whether a static config exists there.
        var tempDir = Path.Combine(Path.GetTempPath(), $"agent365-noproj-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var originalDir = Environment.CurrentDirectory;
            Environment.CurrentDirectory = tempDir;

            try
            {
                var config = new Agent365Config { TenantId = "12345678-1234-1234-1234-123456789012" };
                config.AgentBlueprintId = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";

                // Act
                await _service.SaveStateAsync(config, ConfigConstants.DefaultStateFileName);

                // Assert — state is always saved to the current directory
                var localStatePath = Path.Combine(tempDir, ConfigConstants.DefaultStateFileName);
                Assert.True(File.Exists(localStatePath),
                    "State file should always be saved to the current directory");
                var content = await File.ReadAllTextAsync(localStatePath);
                Assert.Contains("bbbbbbbb-cccc-dddd-eeee-ffffffffffff", content);
            }
            finally
            {
                Environment.CurrentDirectory = originalDir;
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveStateAsync_NullStringProperty_IsOmittedFromJson()
    {
        // Arrange — agentBlueprintClientSecret is null (the state before the secret is created).
        // SaveStateAsync must NOT write "agentBlueprintClientSecret": null because:
        //   (a) DefaultIgnoreCondition.WhenWritingNull does not apply to dictionary values
        //       (dotnet/runtime#30690), so the filter must be applied in ExtractDynamicProperties, and
        //   (b) an explicit null in the file causes MergeDynamicProperties to overwrite any previously
        //       written secret with null on the next LoadAsync — the self-reinforcing null cycle.
        var statePath = Path.Combine(_testDirectory, "a365.generated.config.json");
        var config = new Agent365Config { AgentBlueprintId = "aaa-111" };
        // AgentBlueprintClientSecret is intentionally left null

        // Act
        await _service.SaveStateAsync(config, statePath);

        // Assert — key must be absent, not present with a null value.
        // Note: parse the JSON object and check keys directly rather than doing a substring search,
        // because "agentBlueprintClientSecretProtected" also contains "agentBlueprintClientSecret"
        // as a prefix and would produce a false positive on a raw string check.
        var json = await File.ReadAllTextAsync(statePath);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(node);
        Assert.False(node!.ContainsKey("agentBlueprintClientSecret"),
            "agentBlueprintClientSecret must be omitted when null to prevent the self-reinforcing null cycle on re-run");
        Assert.Contains("aaa-111", json); // sanity: non-null values still written
    }

    [Fact]
    public async Task SaveStateAsync_NonNullStringProperty_IsWrittenToJson()
    {
        // Arrange — after CreateBlueprintClientSecretAsync sets the secret, SaveStateAsync must
        // persist it so the file contains the real value (fixes the macOS null-secret bug, issue #408).
        var statePath = Path.Combine(_testDirectory, "a365.generated.config.json");
        var config = new Agent365Config
        {
            AgentBlueprintId = "aaa-111",
            AgentBlueprintClientSecret = "super-secret-value",
            AgentBlueprintClientSecretProtected = false
        };

        // Act
        await _service.SaveStateAsync(config, statePath);

        // Assert — parse JSON to avoid dependence on serializer whitespace/ordering.
        var json = await File.ReadAllTextAsync(statePath);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(node);
        Assert.Equal("super-secret-value", node!["agentBlueprintClientSecret"]?.GetValue<string>());
        Assert.Equal(false, node["agentBlueprintClientSecretProtected"]?.GetValue<bool>());
    }

    #endregion

    #region Static Config Update Tests

    [Fact]
    public async Task UpdateAgentBlueprintDisplayNameAsync_ReplacesOnlyExistingProperty()
    {
        var configPath = Path.Combine(_testDirectory, "a365.config.json");
        const string original = """
            {
              // retained comment
              "tenantId": "11111111-1111-1111-1111-111111111111",
              "agentBlueprintDisplayName": "Old Blueprint",
              "unknownSetting": "keep-me"
            }
            """;
        await File.WriteAllTextAsync(configPath, original);

        await _service.UpdateAgentBlueprintDisplayNameAsync("Selected Blueprint", configPath);

        var updated = await File.ReadAllTextAsync(configPath);
        updated.Should().Contain("\"agentBlueprintDisplayName\": \"Selected Blueprint\"",
            because: "the explicit blueprint selection must become the static source of truth");
        updated.Should().Contain("// retained comment",
            because: "updating one known field must preserve user-managed comments");
        updated.Should().Contain("\"unknownSetting\": \"keep-me\"",
            because: "updating one known field must preserve unknown user-managed settings");
        updated.Should().NotContain("Old Blueprint");
    }

    [Fact]
    public async Task UpdateAgentBlueprintDisplayNameAsync_AddsMissingProperty()
    {
        var configPath = Path.Combine(_testDirectory, "a365.config.json");
        await File.WriteAllTextAsync(configPath, """{"tenantId":"11111111-1111-1111-1111-111111111111","unknownSetting":"keep-me"}""");

        await _service.UpdateAgentBlueprintDisplayNameAsync("Selected Blueprint", configPath);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        document.RootElement.GetProperty("agentBlueprintDisplayName").GetString().Should().Be(
            "Selected Blueprint",
            because: "older config files may not contain the static blueprint display-name field");
        document.RootElement.GetProperty("unknownSetting").GetString().Should().Be(
            "keep-me",
            because: "adding the field must preserve unrelated settings");
    }

    [Theory]
    [InlineData("Finance $1 Bot")]
    [InlineData("Cost $$ Saver")]
    [InlineData("Team $& Blueprint")]
    [InlineData("Weird $` Name")]
    public async Task UpdateAgentBlueprintDisplayNameAsync_ValueContainsRegexReplacementToken_WritesExactValue(
        string displayName)
    {
        var configPath = Path.Combine(_testDirectory, "a365.config.json");
        await File.WriteAllTextAsync(
            configPath,
            """{"agentBlueprintDisplayName":"Old Blueprint","unknownSetting":"keep-me"}""");

        await _service.UpdateAgentBlueprintDisplayNameAsync(displayName, configPath);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        document.RootElement.GetProperty("agentBlueprintDisplayName").GetString().Should().Be(
            displayName,
            because: "tenant-managed blueprint names must not be interpreted as regex replacement syntax");
        document.RootElement.GetProperty("unknownSetting").GetString().Should().Be(
            "keep-me",
            because: "persisting an arbitrary blueprint name must not corrupt adjacent settings");
    }

    [Fact]
    public async Task UpdateAgentBlueprintDisplayNameAsync_ValueIsUnchanged_PreservesComments()
    {
        var configPath = Path.Combine(_testDirectory, "a365.config.json");
        const string original = """
            {
              // retained comment
              "agentBlueprintDisplayName": "Selected Blueprint",
              "unknownSetting": "keep-me"
            }
            """;
        await File.WriteAllTextAsync(configPath, original);

        await _service.UpdateAgentBlueprintDisplayNameAsync("Selected Blueprint", configPath);

        (await File.ReadAllTextAsync(configPath)).Should().Be(
            original,
            because: "an idempotent blueprint selection must not reformat the user-managed config");
    }

    [Fact]
    public async Task UpdateAgentBlueprintDisplayNameAsync_WhenCancelled_PropagatesCancellation()
    {
        var configPath = Path.Combine(_testDirectory, "a365.config.json");
        await File.WriteAllTextAsync(configPath, """{"agentBlueprintDisplayName":"Old Blueprint"}""");
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var act = () => _service.UpdateAgentBlueprintDisplayNameAsync(
            "Selected Blueprint",
            configPath,
            cancellationSource.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            because: "cancellation must stop static configuration mutation");
    }

    #endregion

    #region InvalidateGeneratedConfigAsync Tests

    [Fact]
    public async Task InvalidateGeneratedConfigAsync_BackupContainsOriginal_AndStateFileIsEmpty()
    {
        // Arrange — pre-seed a generated state file that mimics a prior blueprint's output
        // so we can prove the backup captures the pre-reset state and the new file is empty.
        var statePath = Path.Combine(_testDirectory, "a365.generated.config.json");
        var config = new Agent365Config { TenantId = "12345678-1234-1234-1234-123456789012" };
        config.AgentBlueprintId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        config.AgenticAppId = "37fc1c47-5eda-46ca-93b2-76baba49e605";
        config.AgentBlueprintClientSecret = "secret-from-prior-blueprint";
        config.BotId = "99999999-8888-7777-6666-555555555555";
        config.ResourceConsents.Add(new ResourceConsent
        {
            ResourceName = "Microsoft Graph",
            ResourceAppId = AuthenticationConstants.MicrosoftGraphResourceAppId,
            ConsentGranted = true
        });
        await _service.SaveStateAsync(config, statePath);

        // Act
        var backupPath = await _service.InvalidateGeneratedConfigAsync(config, "newblueprint", statePath);

        // Assert — backup file must exist and still hold the prior identifiers, because the
        // invariant the feature must guarantee is: no data loss when invalidating; the user
        // can always recover their prior generated config from disk.
        backupPath.Should().NotBeNull(because: "an existing file must be backed up before invalidation to prevent data loss");
        File.Exists(backupPath!).Should().BeTrue(because: "the backup file path returned by InvalidateGeneratedConfigAsync must exist on disk");
        var backupJson = await File.ReadAllTextAsync(backupPath!);
        backupJson.Should().Contain("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", because: "the backup must preserve the blueprint id from the file that existed before invalidation");
        backupJson.Should().Contain("37fc1c47-5eda-46ca-93b2-76baba49e605", because: "the backup must preserve the agentic app id from the file that existed before invalidation");
        backupJson.Should().Contain("secret-from-prior-blueprint", because: "the backup must preserve the blueprint client secret so the user can recover credentials if needed");

        // Assert — the live state file must be empty of every dynamic identifier so downstream
        // steps (agent identity creation, registration) cannot reuse anything that belongs to
        // the now-orphaned root resource.
        var liveJson = await File.ReadAllTextAsync(statePath);
        var liveNode = System.Text.Json.Nodes.JsonNode.Parse(liveJson)!.AsObject();
        liveNode.ContainsKey("agentBlueprintId").Should().BeFalse(because: "the agent blueprint id from the prior root must not survive invalidation");
        liveNode.ContainsKey("agenticAppId").Should().BeFalse(because: "the agentic app id is owned by the prior blueprint and would be orphaned");
        liveNode.ContainsKey("agentBlueprintClientSecret").Should().BeFalse(because: "the prior blueprint's client secret cannot authenticate the new blueprint and would mislead retries");
        liveNode.ContainsKey("botId").Should().BeFalse(because: "bot state was provisioned against the prior blueprint and must be re-derived");
        // resourceConsents is a non-nullable collection on Agent365Config; invalidation resets it
        // to an empty list (not null) so downstream code can keep its non-null invariant. The
        // serialized form must therefore be an empty array, not absent.
        liveNode["resourceConsents"]!.AsArray().Count.Should().Be(0, because: "consents granted against the prior blueprint's SP are no longer valid and the array must be empty after invalidation");
        // Only the metadata fields that SaveStateAsync always emits should remain.
        liveNode.ContainsKey("lastUpdated").Should().BeTrue(because: "SaveStateAsync writes the lastUpdated timestamp on every write, including the empty post-invalidation state");
        liveNode.ContainsKey("cliVersion").Should().BeTrue(because: "SaveStateAsync writes cliVersion on every write so the empty state still records which CLI produced it");
    }

    [Fact]
    public async Task InvalidateGeneratedConfigAsync_ResetsInMemoryConfigDynamicProperties()
    {
        // Arrange
        var statePath = Path.Combine(_testDirectory, "a365.generated.config.json");
        var config = new Agent365Config
        {
            // Static (init-only) properties — must NOT be reset because they come from a365.config.json,
            // which is the user-managed source of truth and is untouched by invalidation.
            TenantId = "12345678-1234-1234-1234-123456789012",
            AgentIdentityDisplayName = "Test Agent",
        };
        config.AgentBlueprintId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        config.AgenticAppId = "37fc1c47-5eda-46ca-93b2-76baba49e605";
        config.BotId = "99999999-8888-7777-6666-555555555555";
        config.AgentBlueprintClientSecret = "secret";
        config.ResourceConsents.Add(new ResourceConsent
        {
            ResourceName = "Microsoft Graph",
            ResourceAppId = AuthenticationConstants.MicrosoftGraphResourceAppId,
            ConsentGranted = true
        });

        // Act
        await _service.InvalidateGeneratedConfigAsync(config, "newblueprint", statePath);

        // Assert — every dynamic (get/set) property the prior blueprint populated must be cleared
        // on the in-memory instance, because callers continue to use the same instance after
        // invalidation and any leftover value would silently re-seed the new blueprint's state.
        config.AgentBlueprintId.Should().BeNull(because: "the prior blueprint id was the root of the invalidation and must not persist on the in-memory mirror");
        config.AgenticAppId.Should().BeNull(because: "the agent identity was owned by the prior blueprint and the in-memory mirror must reflect that it no longer exists");
        config.BotId.Should().BeNull(because: "bot state belongs to the prior blueprint and must not be reused for the new blueprint");
        config.AgentBlueprintClientSecret.Should().BeNull(because: "the prior client secret cannot authenticate the new blueprint and must not be retained in memory");
        config.ResourceConsents.Should().NotBeNull(because: "ResourceConsents is a non-nullable collection and must remain a valid empty instance to preserve invariants");
        config.ResourceConsents.Should().BeEmpty(because: "consents were granted against the prior blueprint's SP and do not apply to the new one");

        // Assert — static (init-only) properties must survive invalidation, because they are owned
        // by a365.config.json (the user-managed file), not the generated file we are resetting.
        config.TenantId.Should().Be("12345678-1234-1234-1234-123456789012", because: "TenantId is a static property sourced from a365.config.json and is not part of the generated-config invalidation contract");
        config.AgentIdentityDisplayName.Should().Be("Test Agent", because: "AgentIdentityDisplayName is a static property sourced from a365.config.json and is not part of the generated-config invalidation contract");
    }

    [Fact]
    public async Task InvalidateGeneratedConfigAsync_NoExistingFile_ReturnsNullBackupAndCreatesEmptyState()
    {
        // Arrange — no prior generated.config.json on disk.
        var statePath = Path.Combine(_testDirectory, "a365.generated.config.json");
        File.Exists(statePath).Should().BeFalse(because: "this test exercises the first-run path where no generated config exists yet");
        var config = new Agent365Config { TenantId = "12345678-1234-1234-1234-123456789012" };
        config.AgentBlueprintId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        // Act
        var backupPath = await _service.InvalidateGeneratedConfigAsync(config, "newblueprint", statePath);

        // Assert — no backup is created when there is nothing to back up; the in-memory reset still
        // runs and an empty state file is written so the on-disk and in-memory views stay consistent.
        backupPath.Should().BeNull(because: "no backup file should be created when there is no existing generated config to back up");
        File.Exists(statePath).Should().BeTrue(because: "InvalidateGeneratedConfigAsync must always leave a fresh empty state file so subsequent SaveStateAsync calls write into a known location");
        config.AgentBlueprintId.Should().BeNull(because: "the in-memory reset must run even when there was no prior file, to keep the in-memory and on-disk views consistent");
    }

    [Fact]
    public async Task InvalidateGeneratedConfigAsync_BackupFileNameIsCrossPlatformSafe()
    {
        // Arrange — pre-seed and use a reason that includes characters which are illegal in file
        // names on Windows (':') so we can prove the suffix is sanitized.
        var statePath = Path.Combine(_testDirectory, "a365.generated.config.json");
        var config = new Agent365Config { TenantId = "12345678-1234-1234-1234-123456789012" };
        config.AgentBlueprintId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        await _service.SaveStateAsync(config, statePath);

        // Act
        var backupPath = await _service.InvalidateGeneratedConfigAsync(config, "new:blueprint/v2", statePath);

        // Assert — the backup file name must not contain characters that are invalid on any
        // supported platform (Windows is the strictest); we assert against the broad union.
        backupPath.Should().NotBeNull();
        var fileName = Path.GetFileName(backupPath!);
        var invalid = Path.GetInvalidFileNameChars();
        fileName.Should().NotContainAny(invalid.Select(c => c.ToString()), because: "the backup file name is derived from a free-form reason string and must be safe to create on Windows, macOS, and Linux");
        fileName.Should().StartWith("a365.generated.config.before-", because: "the backup file naming convention is the documented contract callers rely on to locate backups");
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
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6",
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

        // Assert — error messages use camelCase field names (from Agent365Config.Validate())
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("tenantId"));
        Assert.Contains(result.Errors, e => e.Contains("clientAppId"));
        Assert.Contains(result.Errors, e => e.Contains("agentIdentityDisplayName"));
    }

    [Fact]
    public async Task ValidateAsync_ReturnsErrors_ForInvalidGuidFormat()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "not-a-guid",
        };

        // Act
        var result = await _service.ValidateAsync(config);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("TenantId") && e.Contains("GUID"));
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
    Assert.Equal(string.Empty, config.TenantId);
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
