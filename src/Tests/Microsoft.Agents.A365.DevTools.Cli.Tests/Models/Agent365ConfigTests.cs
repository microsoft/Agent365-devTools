// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
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
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6",
            AgentIdentityDisplayName = "Test Agent",
            // AgentIdentityScopes are now hardcoded defaults
            DeploymentProjectPath = "./test/path",
            AgentDescription = "Test description"
        };

        // Assert
        Assert.Equal("12345678-1234-1234-1234-123456789012", config.TenantId);
        Assert.Equal("a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6", config.ClientAppId);
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
        };

        // Assert - cannot reassign (compile-time check)
        // The following would NOT compile:
        // config.TenantId = "new-tenant";  // CS8852: Init-only property can only be assigned in object initializer

        Assert.Equal("original-tenant", config.TenantId);
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
            ResourceAppId = AuthenticationConstants.MicrosoftGraphResourceAppId,
            ConsentGranted = true,
            ConsentTimestamp = DateTime.Parse("2025-10-14T12:00:00Z")
        });
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
            ""lastUpdated"": ""2025-10-14T14:56:40Z"",
            ""resourceConsents"": [
                {
                    ""resourceName"": ""Microsoft Graph"",
                    ""resourceAppId"": ""{AuthenticationConstants.MicrosoftGraphResourceAppId}"",
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
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6", // Added required clientAppId
            MessagingEndpoint = "https://external-agent.example.com/api/messages",
            AgentIdentityDisplayName = "Test Agent Identity",
            DeploymentProjectPath = ".",
            // AppServicePlanName and WebAppName not provided
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().BeEmpty("messaging endpoint makes App Service fields optional");
    }

    [Fact]
    public void Validate_WithNoMessagingEndpoint_ReturnsNoError()
    {
        // Arrange — bootstrap config: externally hosted agent with no endpoint yet
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6",
            AgentIdentityDisplayName = "Test Agent Identity",
            DeploymentProjectPath = ".",
            // MessagingEndpoint intentionally absent — filled in after the agent is hosted externally
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().NotContain(e => e.Contains("messagingEndpoint"),
            because: "messagingEndpoint is optional at config-validation time; SetupHelpers enforces it at registration time");
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

        // Assert — only the remaining required fields: tenantId, clientAppId, agentIdentityDisplayName
        errors.Should().Contain("tenantId is required.");
        errors.Should().Contain(e => e.Contains("clientAppId is required."),
            because: "clientAppId is required for all agent configurations");
        errors.Should().Contain("agentIdentityDisplayName is required.");
        errors.Should().NotContain("subscriptionId is required.",
            because: "subscriptionId was removed when a365 deploy was removed");
        errors.Should().NotContain("resourceGroup is required.",
            because: "resourceGroup was removed when a365 deploy was removed");
        errors.Should().NotContain("location is required.",
            because: "location was removed when bot endpoint registration was disabled");
    }

    #endregion

    #region ClientAppId Validation Tests

    [Fact]
    public void Validate_WithMissingClientAppId_ReturnsError()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            // ClientAppId is missing
            AgentIdentityDisplayName = "Test Agent",
            DeploymentProjectPath = ".",
            MessagingEndpoint = "https://test.com/api/messages"
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().Contain(e => e.Contains("clientAppId is required"));
        errors.Should().Contain(e => e.Contains("learn.microsoft.com"));
    }

    [Fact]
    public void Validate_WithEmptyClientAppId_ReturnsError()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            ClientAppId = "", // Empty string
            AgentIdentityDisplayName = "Test Agent",
            DeploymentProjectPath = ".",
            MessagingEndpoint = "https://test.com/api/messages"
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().Contain(e => e.Contains("clientAppId is required"));
    }

    [Fact]
    public void Validate_WithWhitespaceClientAppId_ReturnsError()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            ClientAppId = "   ", // Whitespace only
            AgentIdentityDisplayName = "Test Agent",
            DeploymentProjectPath = ".",
            MessagingEndpoint = "https://test.com/api/messages"
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().Contain(e => e.Contains("clientAppId is required"));
    }

    [Fact]
    public void Validate_WithInvalidClientAppIdFormat_ReturnsError()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            ClientAppId = "not-a-valid-guid", // Invalid GUID format
            AgentIdentityDisplayName = "Test Agent",
            DeploymentProjectPath = ".",
            MessagingEndpoint = "https://test.com/api/messages"
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().Contain(e => e.Contains("ClientAppId") && e.Contains("valid GUID"));
    }

    [Fact]
    public void Validate_WithValidClientAppId_NoClientAppIdErrors()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6", // Valid GUID
            AgentIdentityDisplayName = "Test Agent",
            DeploymentProjectPath = ".",
            MessagingEndpoint = "https://test.com/api/messages"
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().NotContain(e => e.Contains("clientAppId"));
    }

    [Theory]
    [InlineData("A1B2C3D4-E5F6-A7B8-C9D0-E1F2A3B4C5D6")] // Uppercase
    [InlineData("a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6")] // Lowercase
    [InlineData("A1b2C3d4-e5F6-a7B8-C9d0-E1f2A3b4C5d6")] // Mixed case
    public void Validate_WithValidClientAppIdFormats_NoErrors(string clientAppId)
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            ClientAppId = clientAppId,
            AgentIdentityDisplayName = "Test Agent",
            DeploymentProjectPath = ".",
            MessagingEndpoint = "https://test.com/api/messages"
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().NotContain(e => e.Contains("clientAppId"));
    }

    #endregion

    #region BotName derived property tests

    [Fact]
    public void BotName_WithNoEndpoint_ReturnsEmpty()
    {
        var config = new Agent365Config();
        config.BotName.Should().BeEmpty();
    }

    [Fact]
    public void BotName_WithMessagingEndpointAndBlueprintId_UsesHostPlusBlueprintSuffix()
    {
        var config = new Agent365Config
        {
            MessagingEndpoint = "https://microsoftcape.app.n8n.cloud/webhook/abc123/webhook"
        };
        config.AgentBlueprintId = "9ab0b58c-c49e-4adb-b164-1ed10cbe3956";

        config.BotName.Should().Be("microsoftcape-app-n8n-cloud-9ab0b58c");
    }

    [Fact]
    public void BotName_AgreesWithGetEndpointNameFromHost_ForNonAzureConfig()
    {
        // This is the contract test: BotName must always return exactly what
        // GetEndpointNameFromHost returns for the same inputs, because cleanup
        // derives the delete target from BotName while setup registers via GetEndpointNameFromHost.
        var config = new Agent365Config
        {
            MessagingEndpoint = "https://microsoftcape.app.n8n.cloud/webhook/abc123/webhook"
        };
        config.AgentBlueprintId = "9ab0b58c-c49e-4adb-b164-1ed10cbe3956";

        var expected = EndpointHelper.GetEndpointNameFromHost(
            new Uri(config.MessagingEndpoint).Host,
            config.AgentBlueprintId);

        config.BotName.Should().Be(expected,
            "BotName and GetEndpointNameFromHost must agree or cleanup will target the wrong endpoint");
    }

    [Fact]
    public void BotName_WithMessagingEndpointAndNullBlueprintId_UsesLegacyHostEndpointSuffix()
    {
        var config = new Agent365Config
        {
            MessagingEndpoint = "https://myapp.example.com/api/messages"
        };
        // AgentBlueprintId not set

        config.BotName.Should().Be("myapp-example-com-endpoint");
    }

    [Fact]
    public void BotName_WithNoWebAppOrMessagingEndpoint_ReturnsEmpty()
    {
        var config = new Agent365Config();
        config.BotName.Should().BeEmpty();
    }

    [Fact]
    public void BotName_WithMessagingEndpoint_DerivedFromEndpointHost()
    {
        // BotName is derived from MessagingEndpoint host + blueprint ID suffix.
        // This must agree with what SetupHelpers registers so cleanup targets the right endpoint.
        var config = new Agent365Config
        {
            MessagingEndpoint = "https://microsoftcape.app.n8n.cloud/webhook/abc123/webhook"
        };
        config.AgentBlueprintId = "9ab0b58c-c49e-4adb-b164-1ed10cbe3956";

        config.BotName.Should().Be("microsoftcape-app-n8n-cloud-9ab0b58c",
            "BotName is derived from MessagingEndpoint host so cleanup targets the same endpoint that setup registered");
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    [InlineData("")]
    public void BotName_WithInvalidOrRelativeMessagingEndpoint_ReturnsEmpty(string endpoint)
    {
        // Uri.TryCreate fails for non-absolute URIs — BotName falls through to empty
        var config = new Agent365Config
        {
            MessagingEndpoint = endpoint
        };
        config.AgentBlueprintId = "9ab0b58c-c49e-4adb-b164-1ed10cbe3956";

        config.BotName.Should().BeEmpty(
            "invalid or relative URI falls through to empty — caller must handle this case");
    }

    #endregion

    #region Custom Blueprint Permissions Validation Tests

    [Fact]
    public void Validate_WithValidCustomBlueprintPermissions_NoErrors()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6",
            AgentIdentityDisplayName = "Test Agent",
            DeploymentProjectPath = ".",
            MessagingEndpoint = "https://test.com/api/messages",
            CustomBlueprintPermissions = new List<CustomResourcePermission>
            {
                new()
                {
                    ResourceAppId = "00000003-0000-0000-c000-000000000000",
                    ResourceName = "Microsoft Graph",
                    Scopes = new List<string> { "User.Read", "Mail.Send" }
                }
            }
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WithInvalidCustomBlueprintPermission_ReturnsError()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6",
            AgentIdentityDisplayName = "Test Agent",
            DeploymentProjectPath = ".",
            MessagingEndpoint = "https://test.com/api/messages",
            CustomBlueprintPermissions = new List<CustomResourcePermission>
            {
                new()
                {
                    ResourceAppId = "invalid-guid",
                    ResourceName = null,  // ResourceName is optional and will be auto-resolved
                    Scopes = new List<string>(),
                },
            },
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("customBlueprintPermissions[0]");
        errors[0].Should().Contain("resourceAppId must be a valid GUID");
        errors[0].Should().Contain("At least one scope is required");
    }

    [Fact]
    public void Validate_WithDuplicateResourceAppIds_ReturnsError()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6",
            AgentIdentityDisplayName = "Test Agent",
            DeploymentProjectPath = ".",
            MessagingEndpoint = "https://test.com/api/messages",
            CustomBlueprintPermissions = new List<CustomResourcePermission>
            {
                new()
                {
                    ResourceAppId = "00000003-0000-0000-c000-000000000000",
                    ResourceName = "Microsoft Graph 1",
                    Scopes = new List<string> { "User.Read" }
                },
                new()
                {
                    ResourceAppId = "00000003-0000-0000-c000-000000000000",
                    ResourceName = "Microsoft Graph 2",
                    Scopes = new List<string> { "Mail.Send" }
                }
            }
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().Contain(e => e.Contains("Duplicate resourceAppId found in customBlueprintPermissions"));
        errors.Should().Contain(e => e.Contains("00000003-0000-0000-c000-000000000000"));
    }

    [Fact]
    public void Validate_WithDuplicateResourceAppIdsCaseInsensitive_ReturnsError()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6",
            AgentIdentityDisplayName = "Test Agent",
            DeploymentProjectPath = ".",
            MessagingEndpoint = "https://test.com/api/messages",
            CustomBlueprintPermissions = new List<CustomResourcePermission>
            {
                new()
                {
                    ResourceAppId = "00000003-0000-0000-c000-000000000000",
                    ResourceName = "Microsoft Graph 1",
                    Scopes = new List<string> { "User.Read" }
                },
                new()
                {
                    ResourceAppId = "00000003-0000-0000-C000-000000000000", // Different case
                    ResourceName = "Microsoft Graph 2",
                    Scopes = new List<string> { "Mail.Send" }
                }
            }
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().Contain(e => e.Contains("Duplicate resourceAppId"));
    }

    [Fact]
    public void Validate_WithMultipleValidCustomBlueprintPermissions_NoErrors()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6",
            AgentIdentityDisplayName = "Test Agent",
            DeploymentProjectPath = ".",
            MessagingEndpoint = "https://test.com/api/messages",
            CustomBlueprintPermissions = new List<CustomResourcePermission>
            {
                new()
                {
                    ResourceAppId = "00000003-0000-0000-c000-000000000000",
                    ResourceName = "Microsoft Graph",
                    Scopes = new List<string> { "User.Read", "Mail.Send" }
                },
                new()
                {
                    ResourceAppId = "12345678-1234-1234-1234-123456789012",
                    ResourceName = "Custom API",
                    Scopes = new List<string> { "custom.read" }
                }
            }
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WithNullCustomBlueprintPermissions_NoErrors()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6",
            AgentIdentityDisplayName = "Test Agent",
            DeploymentProjectPath = ".",
            MessagingEndpoint = "https://test.com/api/messages",
            CustomBlueprintPermissions = null
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WithEmptyCustomBlueprintPermissionsList_NoErrors()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6",
            AgentIdentityDisplayName = "Test Agent",
            DeploymentProjectPath = ".",
            MessagingEndpoint = "https://test.com/api/messages",
            CustomBlueprintPermissions = new List<CustomResourcePermission>()
        };

        // Act
        var errors = config.Validate();

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void SerializeToJson_WithCustomBlueprintPermissions_IncludesPermissions()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "tenant-123",
            CustomBlueprintPermissions = new List<CustomResourcePermission>
            {
                new()
                {
                    ResourceAppId = "00000003-0000-0000-c000-000000000000",
                    ResourceName = "Microsoft Graph",
                    Scopes = new List<string> { "User.Read", "Mail.Send" }
                }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        // Assert
        json.Should().Contain("\"customBlueprintPermissions\"");
        json.Should().Contain("\"resourceAppId\"");
        json.Should().Contain("00000003-0000-0000-c000-000000000000");
        json.Should().Contain("\"resourceName\"");
        json.Should().Contain("Microsoft Graph");
        json.Should().Contain("\"scopes\"");
        json.Should().Contain("User.Read");
        json.Should().Contain("Mail.Send");
    }

    [Fact]
    public void DeserializeFromJson_WithCustomBlueprintPermissions_RestoresPermissions()
    {
        // Arrange
        var json = @"{
            ""tenantId"": ""tenant-123"",
            ""customBlueprintPermissions"": [
                {
                    ""resourceAppId"": ""00000003-0000-0000-c000-000000000000"",
                    ""resourceName"": ""Microsoft Graph"",
                    ""scopes"": [""User.Read"", ""Mail.Send""]
                }
            ]
        }";

        // Act
        var config = JsonSerializer.Deserialize<Agent365Config>(json);

        // Assert
        config.Should().NotBeNull();
        config!.CustomBlueprintPermissions.Should().NotBeNull();
        config.CustomBlueprintPermissions.Should().HaveCount(1);
        config.CustomBlueprintPermissions![0].ResourceAppId.Should().Be("00000003-0000-0000-c000-000000000000");
        config.CustomBlueprintPermissions[0].ResourceName.Should().Be("Microsoft Graph");
        config.CustomBlueprintPermissions[0].Scopes.Should().BeEquivalentTo(new[] { "User.Read", "Mail.Send" });
    }

    #endregion

    #region OwnAccess and IsBlueprintAgent Tests

    [Theory]
    [InlineData(false, true)]   // ownAccess=false → blueprint agent
    [InlineData(true, false)]   // ownAccess=true  → own-identity agent
    [InlineData(null, false)]   // not set → own-identity agent (default)
    public void IsBlueprintAgent_ReturnsCorrectValue(bool? ownAccess, bool expected)
    {
        var config = new Agent365Config { OwnAccess = ownAccess };

        config.IsBlueprintAgent.Should().Be(expected);
    }

    [Fact]
    public void OwnAccess_IsSerializedToJson_WithCorrectPropertyName()
    {
        var config = new Agent365Config { OwnAccess = false };

        var json = JsonSerializer.Serialize(config);

        json.Should().Contain("\"ownAccess\"");
        json.Should().Contain("false");
    }

    [Fact]
    public void OwnAccess_IsDeserializedFromJson()
    {
        const string json = "{\"ownAccess\": false}";

        var config = JsonSerializer.Deserialize<Agent365Config>(json);

        config.Should().NotBeNull();
        config!.OwnAccess.Should().BeFalse();
        config.IsBlueprintAgent.Should().BeTrue();
    }

    [Fact]
    public void OwnAccess_IsNullByDefault_WhenNotSpecified()
    {
        var config = new Agent365Config();

        config.OwnAccess.Should().BeNull();
        config.IsBlueprintAgent.Should().BeFalse();
    }

    [Fact]
    public void AzureOpenAIProperties_AreSerializedCorrectly()
    {
        var config = new Agent365Config
        {
            AzureOpenAIName = "aoai-test",
            AzureOpenAILocation = "swedencentral",
            AzureOpenAIModelDeploymentName = "gpt-4.1",
            NeedAzureOpenAI = true
        };

        var json = JsonSerializer.Serialize(config);

        json.Should().Contain("\"azureOpenAIName\"");
        json.Should().Contain("aoai-test");
        json.Should().Contain("\"azureOpenAILocation\"");
        json.Should().Contain("swedencentral");
        json.Should().Contain("\"azureOpenAIModelDeploymentName\"");
        json.Should().Contain("gpt-4.1");
        json.Should().Contain("\"needAzureOpenAI\"");
    }

    [Theory]
    [InlineData(false, true, true)]    // ownAccess=false + useBlueprint=true → blueprint non-DW
    [InlineData(false, false, false)]  // ownAccess=false + useBlueprint=false → app-based non-DW
    [InlineData(false, null, false)]   // ownAccess=false + useBlueprint not set → app-based non-DW
    [InlineData(true, true, false)]    // ownAccess=true (DW) → never blueprint non-DW
    [InlineData(null, true, false)]    // not set (DW default) → never blueprint non-DW
    public void IsNonDwBlueprint_ReturnsCorrectValue(bool? ownAccess, bool? useBlueprint, bool expected)
    {
        var config = new Agent365Config { OwnAccess = ownAccess, UseBlueprint = useBlueprint };

        config.IsNonDwBlueprint.Should().Be(expected);
    }

    [Fact]
    public void UseBlueprint_IsSerializedToJson_WithCorrectPropertyName()
    {
        var config = new Agent365Config { UseBlueprint = true };

        var json = JsonSerializer.Serialize(config);

        json.Should().Contain("\"useBlueprint\"");
        json.Should().Contain("true");
    }

    [Fact]
    public void UseBlueprint_IsDeserializedFromJson()
    {
        const string json = "{\"ownAccess\": false, \"useBlueprint\": true}";

        var config = JsonSerializer.Deserialize<Agent365Config>(json);

        config.Should().NotBeNull();
        config!.UseBlueprint.Should().BeTrue();
        config.IsNonDwBlueprint.Should().BeTrue();
    }

    [Fact]
    public void WithCustomBlueprintPermissions_PreservesOwnAccess()
    {
        var config = new Agent365Config
        {
            OwnAccess = false,
            UseBlueprint = true,
            AzureOpenAIName = "aoai-test",
            NeedAzureOpenAI = true
        };

        var cloned = config.WithCustomBlueprintPermissions(null);

        cloned.OwnAccess.Should().BeFalse();
        cloned.UseBlueprint.Should().BeTrue();
        cloned.AzureOpenAIName.Should().Be("aoai-test");
        cloned.NeedAzureOpenAI.Should().BeTrue();
    }

    #endregion

    #region ValidateNonDwMinimal Tests

    [Fact]
    public void ValidateNonDwMinimal_ValidMinimalConfig_ReturnsNoErrors()
    {
        var config = new Agent365Config
        {
            TenantId = "tenant-id",
            ClientAppId = "f2d098d5-09d2-40e1-a7b0-d9fff1ace230",
            AgentIdentityDisplayName = "My Agent"
        };

        var errors = config.ValidateNonDwMinimal();

        errors.Should().BeEmpty(
            because: "a config with valid tenantId, clientAppId (GUID), and agentIdentityDisplayName meets the minimal bootstrap requirements");
    }

    [Fact]
    public void ValidateNonDwMinimal_MissingTenantId_ReturnsError()
    {
        var config = new Agent365Config
        {
            TenantId = "",
            ClientAppId = "f2d098d5-09d2-40e1-a7b0-d9fff1ace230",
            AgentIdentityDisplayName = "My Agent"
        };

        var errors = config.ValidateNonDwMinimal();

        errors.Should().ContainMatch("*tenantId*",
            because: "tenantId is required for the bootstrap path to acquire tokens");
    }

    [Fact]
    public void ValidateNonDwMinimal_MissingClientAppId_ReturnsError()
    {
        var config = new Agent365Config
        {
            TenantId = "tenant-id",
            ClientAppId = "",
            AgentIdentityDisplayName = "My Agent"
        };

        var errors = config.ValidateNonDwMinimal();

        errors.Should().ContainMatch("*clientAppId*",
            because: "clientAppId is required to authenticate against Graph and ARM; an empty value means the well-known app lookup failed");
    }

    [Fact]
    public void ValidateNonDwMinimal_NonGuidClientAppId_ReturnsError()
    {
        var config = new Agent365Config
        {
            TenantId = "tenant-id",
            ClientAppId = "not-a-guid",
            AgentIdentityDisplayName = "My Agent"
        };

        var errors = config.ValidateNonDwMinimal();

        errors.Should().NotBeEmpty(
            because: "clientAppId must be a valid GUID for MSAL to accept it as an application ID");
    }

    [Fact]
    public void ValidateNonDwMinimal_MissingAgentIdentityDisplayName_ReturnsError()
    {
        var config = new Agent365Config
        {
            TenantId = "tenant-id",
            ClientAppId = "f2d098d5-09d2-40e1-a7b0-d9fff1ace230",
            AgentIdentityDisplayName = ""
        };

        var errors = config.ValidateNonDwMinimal();

        errors.Should().ContainMatch("*agentIdentityDisplayName*",
            because: "agentIdentityDisplayName is required to name the Entra app registration created for the agent identity");
    }

    [Fact]
    public void ValidateNonDwMinimal_DoesNotRequireSubscriptionId()
    {
        var config = new Agent365Config
        {
            TenantId = "tenant-id",
            ClientAppId = "f2d098d5-09d2-40e1-a7b0-d9fff1ace230",
            AgentIdentityDisplayName = "My Agent"
            // SubscriptionId, ResourceGroup, DeploymentProjectPath intentionally omitted
        };

        var errors = config.ValidateNonDwMinimal();

        errors.Should().BeEmpty(
            because: "bootstrap (--agent-name) path uses external hosting — no Azure subscription or deployment path is required");
    }

    #endregion
}
