// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using System.Text.Json;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Models;

/// <summary>
/// Unit tests for Agent365Config class.
/// Tests init-only properties (immutability), get/set properties (mutability), and JSON serialization.
/// </summary>
public class Agent365ConfigTests
{
    #region Static Properties (init-only) Tests

    [Fact]
    public void StaticProperties_CanBeInitialized()
    {
        // Arrange & Act
        var config = new Agent365Config
        {
            TenantId = "12345678-1234-1234-1234-123456789012",
            SubscriptionId = "87654321-4321-4321-4321-210987654321",
            ResourceGroup = "rg-test",
            Location = "eastus",
            AppServicePlanName = "asp-test",
            AppServicePlanSku = "B1",
            WebAppName = "webapp-test",
            AgentIdentityDisplayName = "Test Agent",
            // AgentIdentityScopes are now hardcoded defaults
            DeploymentProjectPath = "./test/path",
            AgentDescription = "Test description"
        };

        // Assert
        Assert.Equal("12345678-1234-1234-1234-123456789012", config.TenantId);
        Assert.Equal("87654321-4321-4321-4321-210987654321", config.SubscriptionId);
        Assert.Equal("rg-test", config.ResourceGroup);
        Assert.Equal("eastus", config.Location);
        Assert.Equal("asp-test", config.AppServicePlanName);
        Assert.Equal("B1", config.AppServicePlanSku);
        Assert.Equal("webapp-test", config.WebAppName);
        Assert.Equal("Test Agent", config.AgentIdentityDisplayName);
        Assert.NotNull(config.AgentIdentityScopes);
        Assert.NotEmpty(config.AgentIdentityScopes); // Should have hardcoded defaults
        Assert.Equal("./test/path", config.DeploymentProjectPath);
        Assert.Equal("Test description", config.AgentDescription);
    }

    [Fact]
    public void StaticProperties_HaveDefaultValues()
    {
        // Arrange & Act
        var config = new Agent365Config
        {
            TenantId = "test-tenant"
        };

        // Assert - check default values
        Assert.NotNull(config.AgentIdentityScopes); // Hardcoded defaults
        Assert.NotEmpty(config.AgentIdentityScopes); // Should contain default scopes
    }

    [Fact]
    public void StaticProperties_AreImmutableAfterConstruction()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "original-tenant",
            SubscriptionId = "original-subscription"
        };

        // Assert - cannot reassign (compile-time check)
        // The following would NOT compile:
        // config.TenantId = "new-tenant";  // CS8852: Init-only property can only be assigned in object initializer
        // config.SubscriptionId = "new-subscription";

        Assert.Equal("original-tenant", config.TenantId);
        Assert.Equal("original-subscription", config.SubscriptionId);
    }

    #endregion

    #region Dynamic Properties (get/set) Tests

    [Fact]
    public void DynamicProperties_AreMutable()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant"
        };

        // Act - modify dynamic properties
        config.ManagedIdentityPrincipalId = "principal-123";
        config.AgentBlueprintId = "blueprint-456";
        config.AgenticAppId = "identity-789";
        config.AgenticUserId = "user-abc";
        config.BotId = "bot-def";
        config.BotMsaAppId = "msa-ghi";
        config.BotMessagingEndpoint = "https://bot.example.com/messages";
        config.ResourceConsents.Add(new ResourceConsent
        {
            ResourceName = "Microsoft Graph",
            ResourceAppId = "00000003-0000-0000-c000-000000000000",
            ConsentGranted = true,
            ConsentTimestamp = DateTime.Parse("2025-10-14T12:00:00Z")
        });
        config.DeploymentLastTimestamp = DateTime.Parse("2025-10-14T13:00:00Z");
        config.DeploymentLastStatus = "success";
        config.DeploymentLastCommitHash = "abc123";
        config.DeploymentLastBuildId = "build-123";
        config.LastUpdated = DateTime.Parse("2025-10-14T14:00:00Z");
        config.CliVersion = "1.0.0";

        // Assert
        Assert.Equal("principal-123", config.ManagedIdentityPrincipalId);
        Assert.Equal("blueprint-456", config.AgentBlueprintId);
        Assert.Equal("identity-789", config.AgenticAppId);
        Assert.Equal("user-abc", config.AgenticUserId);
        Assert.Equal("bot-def", config.BotId);
        Assert.Equal("msa-ghi", config.BotMsaAppId);
        Assert.Equal("https://bot.example.com/messages", config.BotMessagingEndpoint);
        Assert.NotEmpty(config.ResourceConsents);
        Assert.Equal("Microsoft Graph", config.ResourceConsents[0].ResourceName);
        Assert.True(config.ResourceConsents[0].ConsentGranted);
        Assert.Equal(DateTime.Parse("2025-10-14T13:00:00Z"), config.DeploymentLastTimestamp);
        Assert.Equal("success", config.DeploymentLastStatus);
        Assert.Equal("abc123", config.DeploymentLastCommitHash);
        Assert.Equal("build-123", config.DeploymentLastBuildId);
        Assert.Equal(DateTime.Parse("2025-10-14T14:00:00Z"), config.LastUpdated);
        Assert.Equal("1.0.0", config.CliVersion);
    }

    [Fact]
    public void DynamicProperties_CanBeSetToNull()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant"
        };

        // Act - set to non-null first, then null
        config.AgentBlueprintId = "blueprint-123";
        Assert.Equal("blueprint-123", config.AgentBlueprintId);

        config.AgentBlueprintId = null;

        // Assert
        Assert.Null(config.AgentBlueprintId);
    }

    [Fact]
    public void DynamicProperties_DefaultToNull()
    {
        // Arrange & Act
        var config = new Agent365Config
        {
            TenantId = "test-tenant"
        };

        // Assert - all dynamic properties should default to null
        Assert.Null(config.ManagedIdentityPrincipalId);
        Assert.Null(config.AgentBlueprintId);
        Assert.Null(config.AgenticAppId);
        Assert.Null(config.AgenticUserId);
        Assert.Null(config.BotId);
        Assert.Null(config.BotMsaAppId);
        Assert.Null(config.BotMessagingEndpoint);
        Assert.Empty(config.ResourceConsents);
        Assert.Null(config.DeploymentLastTimestamp);
        Assert.Null(config.DeploymentLastStatus);
        Assert.Null(config.DeploymentLastCommitHash);
        Assert.Null(config.DeploymentLastBuildId);
        Assert.Null(config.LastUpdated);
        Assert.Null(config.CliVersion);
    }

    #endregion

    #region JSON Serialization Tests

    [Fact]
    public void SerializeToJson_IncludesAllProperties()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "tenant-123",
            SubscriptionId = "sub-456",
            ResourceGroup = "rg-test",
            Location = "eastus",
            AppServicePlanName = "asp-test",
            WebAppName = "webapp-test",
            AgentIdentityDisplayName = "Test Agent",
            // AgentIdentityScopes are now hardcoded
            DeploymentProjectPath = "./test",
            AgentDescription = "Test description"
        };
        config.AgentBlueprintId = "blueprint-789";
        config.BotId = "bot-abc";

        // Act
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        // Assert
        Assert.Contains("\"tenantId\"", json);
        Assert.Contains("tenant-123", json);
        Assert.Contains("\"subscriptionId\"", json);
        Assert.Contains("sub-456", json);
        Assert.Contains("\"agentBlueprintId\"", json);
        Assert.Contains("blueprint-789", json);
        Assert.Contains("\"botId\"", json);
        Assert.Contains("bot-abc", json);
    }

    [Fact]
    public void DeserializeFromJson_RestoresAllProperties()
    {
        // Arrange
        var json = @"{
            ""tenantId"": ""tenant-123"",
            ""subscriptionId"": ""sub-456"",
            ""resourceGroup"": ""rg-test"",
            ""location"": ""eastus"",
            ""appServicePlanName"": ""asp-test"",
            ""webAppName"": ""webapp-test"",
            ""agentIdentityDisplayName"": ""Test Agent"",
            ""deploymentProjectPath"": ""./test"",
            ""agentDescription"": ""Test description"",
            ""Agent365ToolsEndpoint"": ""https://test.com"",
            ""agentBlueprintId"": ""blueprint-789"",
            ""botId"": ""bot-abc""
        }";

        // Act
        var config = JsonSerializer.Deserialize<Agent365Config>(json);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("tenant-123", config.TenantId);
        Assert.Equal("sub-456", config.SubscriptionId);
        Assert.Equal("rg-test", config.ResourceGroup);
        Assert.Equal("eastus", config.Location);
        Assert.Equal("asp-test", config.AppServicePlanName);
        Assert.Equal("webapp-test", config.WebAppName);
        Assert.Equal("Test Agent", config.AgentIdentityDisplayName);
        Assert.NotNull(config.AgentIdentityScopes);
        Assert.NotEmpty(config.AgentIdentityScopes); // Should have hardcoded defaults
        Assert.Equal("./test", config.DeploymentProjectPath);
        Assert.Equal("Test description", config.AgentDescription);
        Assert.Equal("blueprint-789", config.AgentBlueprintId);
        Assert.Equal("bot-abc", config.BotId);
    }

    [Fact]
    public void DeserializeFromJson_HandlesNullValues()
    {
        // Arrange
        var json = @"{
            ""tenantId"": ""tenant-123"",
            ""subscriptionId"": ""sub-456"",
            ""resourceGroup"": ""rg-test"",
            ""location"": ""eastus"",
            ""agentBlueprintId"": null,
            ""botId"": null
        }";

        // Act
        var config = JsonSerializer.Deserialize<Agent365Config>(json);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("tenant-123", config.TenantId);
        Assert.Null(config.AgentBlueprintId);
        Assert.Null(config.BotId);
    }

    [Fact]
    public void DeserializeFromJson_HandlesDateTimeValues()
    {
        // Arrange
        var json = @"{
            ""tenantId"": ""tenant-123"",
            ""deploymentLastTimestamp"": ""2025-10-14T13:45:30Z"",
            ""lastUpdated"": ""2025-10-14T14:56:40Z"",
            ""resourceConsents"": [
                {
                    ""resourceName"": ""Microsoft Graph"",
                    ""resourceAppId"": ""00000003-0000-0000-c000-000000000000"",
                    ""consentGranted"": true,
                    ""consentTimestamp"": ""2025-10-14T12:34:56Z""
                }
            ]
        }";

        // Act
        var config = JsonSerializer.Deserialize<Agent365Config>(json);

        // Assert
        Assert.NotNull(config);
        Assert.NotEmpty(config.ResourceConsents);
        Assert.NotNull(config.ResourceConsents[0].ConsentTimestamp);
        var timestamp = config.ResourceConsents[0].ConsentTimestamp!.Value;
        Assert.Equal(2025, timestamp.Year);
        Assert.Equal(10, timestamp.Month);
        Assert.Equal(14, timestamp.Day);
    }

    #endregion

    #region Nested Type Tests

    [Fact]
    public void McpServerConfig_CanBeCreatedAndSerialized()
    {
        // Arrange
        var mcpServer = new McpServerConfig
        {
            McpServerName = "Test Server",
            McpServerUniqueName = "test-server",
            Url = "https://test-server.example.com"
        };

        // Act
        var json = JsonSerializer.Serialize(mcpServer);

        // Assert
        Assert.Contains("\"mcpServerName\"", json);
        Assert.Contains("Test Server", json);
        Assert.Contains("\"url\"", json);
        Assert.Contains("https://test-server.example.com", json);
        Assert.Contains("\"mcpServerUniqueName\"", json);
        Assert.Contains("test-server", json);
    }

    [Fact]
    public void Agent365Config_CanContainMcpServerConfigs()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "tenant-123",
            McpDefaultServers = new List<McpServerConfig>
            {
                new() { McpServerName = "Server 1", McpServerUniqueName = "server1", Url = "https://s1.com" },
                new() { McpServerName = "Server 2", McpServerUniqueName = "server2", Url = "https://s2.com" }
            }
        };

        // Act & Assert
        Assert.NotNull(config.McpDefaultServers);
        Assert.Equal(2, config.McpDefaultServers.Count);
        Assert.Equal("Server 1", config.McpDefaultServers[0].McpServerName);
        Assert.True(config.McpDefaultServers[0].IsValid());
        Assert.Equal("Server 2", config.McpDefaultServers[1].McpServerName);
        Assert.True(config.McpDefaultServers[1].IsValid());
    }

    #endregion

    #region MessagingEndpoint Tests

    [Fact]
    public void Validate_WithMessagingEndpoint_DoesNotRequireAppServiceFields()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            SubscriptionId = "11111111-1111-1111-1111-111111111111",
            ResourceGroup = "test-rg",
            Location = "eastus",
            MessagingEndpoint = "https://external-agent.example.com/api/messages",
            AgentIdentityDisplayName = "Test Agent Identity",
            DeploymentProjectPath = ".",
            NeedDeployment = false
            // AppServicePlanName and WebAppName not provided
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().BeEmpty("messaging endpoint makes App Service fields optional");
    }

    [Fact]
    public void Validate_WithoutMessagingEndpoint_RequiresAppServiceFields()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            SubscriptionId = "11111111-1111-1111-1111-111111111111",
            ResourceGroup = "test-rg",
            Location = "eastus",
            AgentIdentityDisplayName = "Test Agent Identity",
            DeploymentProjectPath = "."
            // AppServicePlanName, WebAppName, and MessagingEndpoint not provided
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().Contain("appServicePlanName is required.");
        errors.Should().Contain("webAppName is required.");
    }

    [Fact]
    public void Validate_WithEmptyMessagingEndpoint_RequiresAppServiceFields()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            SubscriptionId = "11111111-1111-1111-1111-111111111111",
            ResourceGroup = "test-rg",
            Location = "eastus",
            MessagingEndpoint = "", // Empty string should be treated as not provided
            AgentIdentityDisplayName = "Test Agent Identity",
            DeploymentProjectPath = "."
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().Contain("appServicePlanName is required.");
        errors.Should().Contain("webAppName is required.");
    }

    [Fact]
    public void Validate_WithMessagingEndpoint_StillRequiresBaseFields()
    {
        // Arrange
        var config = new Agent365Config
        {
            MessagingEndpoint = "https://external-agent.example.com/api/messages"
            // Missing all required base fields
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().Contain("tenantId is required.");
        errors.Should().Contain("subscriptionId is required.");
        errors.Should().Contain("resourceGroup is required.");
        errors.Should().Contain("location is required.");
        errors.Should().Contain("agentIdentityDisplayName is required.");
        errors.Should().Contain("deploymentProjectPath is required.");
    }

    #endregion
}
