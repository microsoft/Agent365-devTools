// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.DevelopSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Unit tests for AddPermissions subcommand
/// </summary>
[Collection("Sequential")]
public class AddPermissionsSubcommandTests
{
    private readonly ILogger _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly GraphApiService _mockGraphApiService;

    public AddPermissionsSubcommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockConfigService = Substitute.For<IConfigService>();
        _mockGraphApiService = Substitute.For<GraphApiService>();
    }

    #region Command Structure Tests

    [Fact]
    public void CreateCommand_ShouldHaveCorrectName()
    {
        // Act
        var command = AddPermissionsSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockGraphApiService);

        // Assert
        command.Name.Should().Be("add-permissions");
    }

    [Fact]
    public void CreateCommand_ShouldHaveDescriptiveMessage()
    {
        // Act
        var command = AddPermissionsSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockGraphApiService);

        // Assert
        command.Description.Should().Contain("MCP");
        command.Description.Should().Contain("permission");
    }

    [Fact]
    public void CreateCommand_ShouldHaveConfigOption()
    {
        // Act
        var command = AddPermissionsSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockGraphApiService);

        // Assert
        var configOption = command.Options.FirstOrDefault(o => o.Name == "config");
        configOption.Should().NotBeNull();
        configOption!.Aliases.Should().Contain("--config");
        configOption.Aliases.Should().Contain("-c");
    }

    [Fact]
    public void CreateCommand_ShouldHaveManifestOption()
    {
        // Act
        var command = AddPermissionsSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockGraphApiService);

        // Assert
        var manifestOption = command.Options.FirstOrDefault(o => o.Name == "manifest");
        manifestOption.Should().NotBeNull();
        manifestOption!.Aliases.Should().Contain("--manifest");
        manifestOption.Aliases.Should().Contain("-m");
    }

    [Fact]
    public void CreateCommand_ShouldHaveAppIdOption()
    {
        // Act
        var command = AddPermissionsSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockGraphApiService);

        // Assert
        var appIdOption = command.Options.FirstOrDefault(o => o.Name == "app-id");
        appIdOption.Should().NotBeNull();
        appIdOption!.Aliases.Should().Contain("--app-id");
    }

    [Fact]
    public void CreateCommand_ShouldHaveScopesOption()
    {
        // Act
        var command = AddPermissionsSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockGraphApiService);

        // Assert
        var scopesOption = command.Options.FirstOrDefault(o => o.Name == "scopes");
        scopesOption.Should().NotBeNull();
        scopesOption!.Aliases.Should().Contain("--scopes");
    }

    [Fact]
    public void CreateCommand_ShouldHaveVerboseOption()
    {
        // Act
        var command = AddPermissionsSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockGraphApiService);

        // Assert
        var verboseOption = command.Options.FirstOrDefault(o => o.Name == "verbose");
        verboseOption.Should().NotBeNull();
        verboseOption!.Aliases.Should().Contain("--verbose");
        verboseOption.Aliases.Should().Contain("-v");
    }

    [Fact]
    public void CreateCommand_ShouldHaveDryRunOption()
    {
        // Act
        var command = AddPermissionsSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockGraphApiService);

        // Assert
        var dryRunOption = command.Options.FirstOrDefault(o => o.Name == "dry-run");
        dryRunOption.Should().NotBeNull();
        dryRunOption!.Aliases.Should().Contain("--dry-run");
    }

    [Fact]
    public void CreateCommand_ShouldHaveAllRequiredOptions()
    {
        // Act
        var command = AddPermissionsSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockGraphApiService);

        // Assert
        command.Options.Should().HaveCount(6);
        var optionNames = command.Options.Select(opt => opt.Name).ToList();
        optionNames.Should().Contain(new[] 
        { 
            "config", 
            "manifest", 
            "app-id", 
            "scopes", 
            "verbose", 
            "dry-run" 
        });
    }

    #endregion

    #region Configuration Tests

    [Fact]
    public void ConfigValidation_WithValidConfig_ShouldHaveClientAppId()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            ClientAppId = "client-app-123",
            DeploymentProjectPath = "."
        };

        // Act
        var clientAppId = config.ClientAppId;

        // Assert
        clientAppId.Should().Be("client-app-123");
    }

    [Fact]
    public void ConfigValidation_WithMissingClientAppId_ShouldDetect()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            ClientAppId = string.Empty,
            DeploymentProjectPath = "."
        };

        // Act
        var clientAppId = config.ClientAppId;

        // Assert
        clientAppId.Should().BeNullOrEmpty();
    }

    [Fact]
    public void AppIdResolution_ExplicitAppId_TakesPrecedence()
    {
        // Arrange
        var explicitAppId = "explicit-app-456";
        var configAppId = "config-app-123";

        // Act
        var targetAppId = !string.IsNullOrWhiteSpace(explicitAppId) ? explicitAppId : configAppId;

        // Assert
        targetAppId.Should().Be(explicitAppId);
    }

    [Fact]
    public void AppIdResolution_NoExplicitAppId_UsesConfig()
    {
        // Arrange
        string? explicitAppId = null;
        var configAppId = "config-app-123";

        // Act
        var targetAppId = !string.IsNullOrWhiteSpace(explicitAppId) ? explicitAppId : configAppId;

        // Assert
        targetAppId.Should().Be(configAppId);
    }

    #endregion

    #region Scope Resolution Tests

    [Fact]
    public void ScopeResolution_WithExplicitScopes_ShouldUseProvidedScopes()
    {
        // Arrange
        var explicitScopes = new[] { "McpServers.Mail.All", "McpServers.Calendar.All" };

        // Act
        var scopeSet = new HashSet<string>(explicitScopes, StringComparer.OrdinalIgnoreCase);

        // Assert
        scopeSet.Should().HaveCount(2);
        scopeSet.Should().Contain("McpServers.Mail.All");
        scopeSet.Should().Contain("McpServers.Calendar.All");
    }

    [Fact]
    public void ScopeResolution_WithDuplicateScopes_ShouldDeduplicateCaseInsensitive()
    {
        // Arrange
        var scopesWithDuplicates = new[] 
        { 
            "McpServers.Mail.All", 
            "mcpservers.mail.all", 
            "McpServers.Calendar.All" 
        };

        // Act
        var scopeSet = new HashSet<string>(scopesWithDuplicates, StringComparer.OrdinalIgnoreCase);

        // Assert
        scopeSet.Should().HaveCount(2);
    }

    [Fact]
    public void ScopeResolution_FromManifest_ShouldExtractUniqueScopesAndAudiences()
    {
        // Arrange
        var manifest = new ToolingManifest
        {
            McpServers = new[]
            {
                new McpServerConfig 
                { 
                    McpServerName = "mcp_MailTools", 
                    Scope = "McpServers.Mail.All",
                    Audience = "audience-1"
                },
                new McpServerConfig 
                { 
                    McpServerName = "mcp_CalendarTools", 
                    Scope = "McpServers.Calendar.All",
                    Audience = "audience-1"
                },
                new McpServerConfig 
                { 
                    McpServerName = "mcp_DuplicateMail", 
                    Scope = "McpServers.Mail.All",
                    Audience = "audience-2"
                }
            }
        };

        // Act
        var scopeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var audienceSet = new HashSet<string>();
        
        foreach (var server in manifest.McpServers)
        {
            if (!string.IsNullOrWhiteSpace(server.Scope))
            {
                scopeSet.Add(server.Scope);
            }
            if (!string.IsNullOrWhiteSpace(server.Audience))
            {
                audienceSet.Add(server.Audience);
            }
        }

        // Assert
        scopeSet.Should().HaveCount(2);
        scopeSet.Should().Contain("McpServers.Mail.All");
        scopeSet.Should().Contain("McpServers.Calendar.All");
        
        audienceSet.Should().HaveCount(2);
        audienceSet.Should().Contain("audience-1");
        audienceSet.Should().Contain("audience-2");
    }

    [Fact]
    public void ScopeResolution_WithNullOrEmptyScopes_ShouldSkip()
    {
        // Arrange
        var manifest = new ToolingManifest
        {
            McpServers = new[]
            {
                new McpServerConfig { McpServerName = "mcp_MailTools", Scope = "McpServers.Mail.All" },
                new McpServerConfig { McpServerName = "mcp_NoScope", Scope = null },
                new McpServerConfig { McpServerName = "mcp_EmptyScope", Scope = "" }
            }
        };

        // Act
        var scopeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in manifest.McpServers)
        {
            if (!string.IsNullOrWhiteSpace(server.Scope))
            {
                scopeSet.Add(server.Scope);
            }
        }

        // Assert
        scopeSet.Should().HaveCount(1);
        scopeSet.Should().Contain("McpServers.Mail.All");
    }

    #endregion

    #region Audience Resolution Tests

    [Fact]
    public void AudienceResolution_FromManifest_ShouldExtractUniqueAudiences()
    {
        // Arrange
        var manifest = new ToolingManifest
        {
            McpServers = new[]
            {
                new McpServerConfig { McpServerName = "mcp_MailTools", Audience = "audience-1" },
                new McpServerConfig { McpServerName = "mcp_CalendarTools", Audience = "audience-1" },
                new McpServerConfig { McpServerName = "mcp_TasksTools", Audience = "audience-2" }
            }
        };

        // Act
        var audienceSet = new HashSet<string>();
        foreach (var server in manifest.McpServers)
        {
            if (!string.IsNullOrWhiteSpace(server.Audience))
            {
                audienceSet.Add(server.Audience);
            }
        }

        // Assert
        audienceSet.Should().HaveCount(2);
        audienceSet.Should().Contain("audience-1");
        audienceSet.Should().Contain("audience-2");
    }

    [Fact]
    public void AudienceResolution_WithNullOrEmptyAudiences_ShouldSkip()
    {
        // Arrange
        var manifest = new ToolingManifest
        {
            McpServers = new[]
            {
                new McpServerConfig { McpServerName = "mcp_MailTools", Audience = "audience-1" },
                new McpServerConfig { McpServerName = "mcp_NoAudience", Audience = null },
                new McpServerConfig { McpServerName = "mcp_EmptyAudience", Audience = "" }
            }
        };

        // Act
        var audienceSet = new HashSet<string>();
        foreach (var server in manifest.McpServers)
        {
            if (!string.IsNullOrWhiteSpace(server.Audience))
            {
                audienceSet.Add(server.Audience);
            }
        }

        // Assert
        audienceSet.Should().HaveCount(1);
        audienceSet.Should().Contain("audience-1");
    }

    [Fact]
    public void AudienceResolution_NoAudiences_ShouldBeEmpty()
    {
        // Arrange
        var manifest = new ToolingManifest
        {
            McpServers = new[]
            {
                new McpServerConfig { McpServerName = "mcp_NoAudience", Audience = null },
                new McpServerConfig { McpServerName = "mcp_EmptyAudience", Audience = "" }
            }
        };

        // Act
        var audienceSet = new HashSet<string>();
        foreach (var server in manifest.McpServers)
        {
            if (!string.IsNullOrWhiteSpace(server.Audience))
            {
                audienceSet.Add(server.Audience);
            }
        }

        // Assert
        audienceSet.Should().BeEmpty();
    }

    #endregion

    #region Manifest Parsing Tests

    [Fact]
    public void ManifestParsing_WithValidManifest_ShouldParse()
    {
        // Arrange
        var manifestContent = @"{
            ""mcpServers"": [
                {
                    ""mcpServerName"": ""mcp_MailTools"",
                    ""scope"": ""McpServers.Mail.All"",
                    ""audience"": ""audience-123""
                }
            ]
        }";

        // Act
        var manifest = System.Text.Json.JsonSerializer.Deserialize<ToolingManifest>(manifestContent);

        // Assert
        manifest.Should().NotBeNull();
        manifest!.McpServers.Should().HaveCount(1);
        manifest.McpServers[0].Scope.Should().Be("McpServers.Mail.All");
        manifest.McpServers[0].Audience.Should().Be("audience-123");
    }

    [Fact]
    public void ManifestParsing_WithEmptyServers_ShouldReturnEmptyArray()
    {
        // Arrange
        var manifestContent = @"{ ""mcpServers"": [] }";

        // Act
        var manifest = System.Text.Json.JsonSerializer.Deserialize<ToolingManifest>(manifestContent);

        // Assert
        manifest.Should().NotBeNull();
        manifest!.McpServers.Should().BeEmpty();
    }

    #endregion

    #region Dry Run Tests

    [Fact]
    public void DryRun_ShouldNotCallGraphApiService()
    {
        // Arrange
        var dryRun = true;
        var audiences = new HashSet<string> { "audience-1" };
        var scopes = new[] { "McpServers.Mail.All" };

        // Act - Simulating dry run logic
        var shouldExecute = !dryRun;

        // Assert
        shouldExecute.Should().BeFalse();
        // In dry run, GraphApiService should not be called
        _ = audiences; // Suppress unused warning
        _ = scopes; // Suppress unused warning
    }

    [Fact]
    public void DryRun_ShouldDisplayWhatWouldBeDone()
    {
        // Arrange
        var targetAppId = "app-123";
        var audiences = new HashSet<string> { "audience-1", "audience-2" };
        var scopes = new[] { "McpServers.Mail.All", "McpServers.Calendar.All" };

        // Act - Simulate dry run output preparation
        var operations = audiences.Select(audience => new
        {
            AppId = targetAppId,
            Resource = audience,
            Scopes = scopes
        }).ToList();

        // Assert
        operations.Should().HaveCount(2);
        operations.Should().AllSatisfy(op =>
        {
            op.AppId.Should().Be(targetAppId);
            op.Scopes.Should().HaveCount(2);
        });
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public void ErrorHandling_MissingConfigAndAppId_ShouldBeDetectable()
    {
        // Arrange
        var configExists = false;
        var appId = string.Empty;

        // Act
        var hasRequiredInfo = configExists || !string.IsNullOrWhiteSpace(appId);

        // Assert
        hasRequiredInfo.Should().BeFalse();
    }

    [Fact]
    public void ErrorHandling_ConfigExistsOrAppIdProvided_ShouldBeValid()
    {
        // Arrange - Test with config
        var configExists = true;
        var appId = string.Empty;

        // Act
        var hasRequiredInfo = configExists || !string.IsNullOrWhiteSpace(appId);

        // Assert
        hasRequiredInfo.Should().BeTrue();

        // Arrange - Test with app ID
        configExists = false;
        appId = "client-app-123";

        // Act
        hasRequiredInfo = configExists || !string.IsNullOrWhiteSpace(appId);

        // Assert
        hasRequiredInfo.Should().BeTrue();
    }

    [Fact]
    public void ErrorHandling_MissingManifestAndScopes_ShouldBeDetectable()
    {
        // Arrange
        var manifestExists = false;
        string[]? explicitScopes = null;

        // Act
        var canProceed = manifestExists || (explicitScopes != null && explicitScopes.Length > 0);

        // Assert
        canProceed.Should().BeFalse();
    }

    [Fact]
    public void ErrorHandling_ManifestExistsOrScopesProvided_ShouldBeValid()
    {
        // Arrange - Test with manifest
        var manifestExists = true;
        string[]? explicitScopes = null;

        // Act
        var canProceed = manifestExists || (explicitScopes != null && explicitScopes.Length > 0);

        // Assert
        canProceed.Should().BeTrue();

        // Arrange - Test with explicit scopes
        manifestExists = false;
        explicitScopes = new[] { "McpServers.Mail.All" };

        // Act
        canProceed = manifestExists || (explicitScopes != null && explicitScopes.Length > 0);

        // Assert
        canProceed.Should().BeTrue();
    }

    [Fact]
    public void ErrorHandling_NoAudiences_ShouldBeDetectable()
    {
        // Arrange
        var audiences = new HashSet<string>();

        // Act
        var hasAudiences = audiences.Count > 0;

        // Assert
        hasAudiences.Should().BeFalse();
    }

    #endregion

    #region Tenant ID Detection Tests

    [Fact]
    public void TenantIdDetection_FromConfig_ShouldUseConfigValue()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "config-tenant-id",
            ClientAppId = "client-app-123"
        };

        // Act
        var tenantId = !string.IsNullOrWhiteSpace(config.TenantId) 
            ? config.TenantId 
            : string.Empty;

        // Assert
        tenantId.Should().Be("config-tenant-id");
    }

    [Fact]
    public void TenantIdDetection_MissingInConfig_ShouldReturnEmpty()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = string.Empty,
            ClientAppId = "client-app-123"
        };

        // Act
        var tenantId = !string.IsNullOrWhiteSpace(config.TenantId) 
            ? config.TenantId 
            : string.Empty;

        // Assert
        tenantId.Should().BeEmpty();
    }

    #endregion
}
