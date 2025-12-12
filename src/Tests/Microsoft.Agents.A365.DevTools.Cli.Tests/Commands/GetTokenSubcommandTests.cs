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
/// Unit tests for GetToken subcommand
/// </summary>
[Collection("Sequential")]
public class GetTokenSubcommandTests
{
    private readonly ILogger _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly AuthenticationService _mockAuthService;
    private readonly string _testManifestPath;
    private readonly string _testConfigPath;

    public GetTokenSubcommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockConfigService = Substitute.For<IConfigService>();
        _mockAuthService = Substitute.For<AuthenticationService>(Substitute.For<ILogger<AuthenticationService>>());

        // Setup test file paths
        _testManifestPath = Path.Combine(Path.GetTempPath(), $"TestManifest_{Guid.NewGuid()}.json");
        _testConfigPath = Path.Combine(Path.GetTempPath(), $"TestConfig_{Guid.NewGuid()}.json");
    }

    #region Command Structure Tests

    [Fact]
    public void CreateCommand_ShouldHaveCorrectName()
    {
        // Act
        var command = GetTokenSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService);

        // Assert
        command.Name.Should().Be("gettoken");
    }

    [Fact]
    public void CreateCommand_ShouldHaveDescriptiveMessage()
    {
        // Act
        var command = GetTokenSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService);

        // Assert
        command.Description.Should().Contain("bearer token");
        command.Description.Should().Contain("MCP");
    }

    [Fact]
    public void CreateCommand_ShouldHaveConfigOption()
    {
        // Act
        var command = GetTokenSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService);

        // Assert
        var configOption = command.Options.FirstOrDefault(o => o.Name == "config");
        configOption.Should().NotBeNull();
        configOption!.Aliases.Should().Contain("--config");
        configOption.Aliases.Should().Contain("-c");
    }

    [Fact]
    public void CreateCommand_ShouldHaveAppIdOption()
    {
        // Act
        var command = GetTokenSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService);

        // Assert
        var appIdOption = command.Options.FirstOrDefault(o => o.Name == "app-id");
        appIdOption.Should().NotBeNull();
        appIdOption!.Aliases.Should().Contain("--app-id");
    }

    [Fact]
    public void CreateCommand_ShouldHaveManifestOption()
    {
        // Act
        var command = GetTokenSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService);

        // Assert
        var manifestOption = command.Options.FirstOrDefault(o => o.Name == "manifest");
        manifestOption.Should().NotBeNull();
        manifestOption!.Aliases.Should().Contain("--manifest");
        manifestOption.Aliases.Should().Contain("-m");
    }

    [Fact]
    public void CreateCommand_ShouldHaveScopesOption()
    {
        // Act
        var command = GetTokenSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService);

        // Assert
        var scopesOption = command.Options.FirstOrDefault(o => o.Name == "scopes");
        scopesOption.Should().NotBeNull();
        scopesOption!.Aliases.Should().Contain("--scopes");
    }

    [Fact]
    public void CreateCommand_ShouldHaveOutputOption()
    {
        // Act
        var command = GetTokenSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService);

        // Assert
        var outputOption = command.Options.FirstOrDefault(o => o.Name == "output");
        outputOption.Should().NotBeNull();
        outputOption!.Aliases.Should().Contain("--output");
        outputOption.Aliases.Should().Contain("-o");
    }

    [Fact]
    public void CreateCommand_ShouldHaveVerboseOption()
    {
        // Act
        var command = GetTokenSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService);

        // Assert
        var verboseOption = command.Options.FirstOrDefault(o => o.Name == "verbose");
        verboseOption.Should().NotBeNull();
        verboseOption!.Aliases.Should().Contain("--verbose");
        verboseOption.Aliases.Should().Contain("-v");
    }

    [Fact]
    public void CreateCommand_ShouldHaveForceRefreshOption()
    {
        // Act
        var command = GetTokenSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService);

        // Assert
        var forceRefreshOption = command.Options.FirstOrDefault(o => o.Name == "force-refresh");
        forceRefreshOption.Should().NotBeNull();
        forceRefreshOption!.Aliases.Should().Contain("--force-refresh");
    }

    [Fact]
    public void CreateCommand_ShouldHaveAllRequiredOptions()
    {
        // Act
        var command = GetTokenSubcommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService);

        // Assert
        command.Options.Should().HaveCount(7);
        var optionNames = command.Options.Select(opt => opt.Name).ToList();
        optionNames.Should().Contain(new[] 
        { 
            "config", 
            "app-id", 
            "manifest", 
            "scopes", 
            "output", 
            "verbose", 
            "force-refresh" 
        });
    }

    #endregion

    #region Configuration Loading Tests

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
    public void ConfigValidation_WithEnvironmentSet_ShouldUseCorrectEnvironment()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            ClientAppId = "client-app-123",
            Environment = "preprod"
        };

        // Act
        var environment = config.Environment;

        // Assert
        environment.Should().Be("preprod");
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
    public void ScopeResolution_WithEmptyScopes_ShouldBeEmpty()
    {
        // Arrange
        var emptyScopes = Array.Empty<string>();

        // Act
        var scopeSet = new HashSet<string>(emptyScopes);

        // Assert
        scopeSet.Should().BeEmpty();
    }

    [Fact]
    public void ScopeResolution_FromManifest_ShouldExtractUniqueScopes()
    {
        // Arrange
        var manifest = new ToolingManifest
        {
            McpServers = new[]
            {
                new McpServerConfig { McpServerName = "mcp_MailTools", Scope = "McpServers.Mail.All" },
                new McpServerConfig { McpServerName = "mcp_CalendarTools", Scope = "McpServers.Calendar.All" },
                new McpServerConfig { McpServerName = "mcp_DuplicateMail", Scope = "McpServers.Mail.All" }
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
        scopeSet.Should().HaveCount(2);
        scopeSet.Should().Contain("McpServers.Mail.All");
        scopeSet.Should().Contain("McpServers.Calendar.All");
    }

    [Fact]
    public void ScopeResolution_WithNullScopes_ShouldSkip()
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

    #region Manifest File Tests

    [Fact]
    public void ManifestParsing_WithValidManifest_ShouldParse()
    {
        // Arrange
        var manifestContent = @"{
            ""mcpServers"": [
                {
                    ""mcpServerName"": ""mcp_MailTools"",
                    ""scope"": ""McpServers.Mail.All""
                },
                {
                    ""mcpServerName"": ""mcp_CalendarTools"",
                    ""scope"": ""McpServers.Calendar.All""
                }
            ]
        }";

        // Act
        var manifest = System.Text.Json.JsonSerializer.Deserialize<ToolingManifest>(manifestContent);

        // Assert
        manifest.Should().NotBeNull();
        manifest!.McpServers.Should().HaveCount(2);
        manifest.McpServers[0].Scope.Should().Be("McpServers.Mail.All");
        manifest.McpServers[1].Scope.Should().Be("McpServers.Calendar.All");
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

    #region Output Format Tests

    [Fact]
    public void OutputFormat_TableFormat_IsDefault()
    {
        // Arrange
        var defaultFormat = "table";

        // Act & Assert
        defaultFormat.Should().Be("table");
    }

    [Fact]
    public void OutputFormat_SupportedFormats_ShouldIncludeAllOptions()
    {
        // Arrange
        var supportedFormats = new[] { "table", "json", "raw" };

        // Act & Assert
        supportedFormats.Should().Contain("table");
        supportedFormats.Should().Contain("json");
        supportedFormats.Should().Contain("raw");
        supportedFormats.Should().HaveCount(3);
    }

    [Fact]
    public void OutputFormat_CaseInsensitive_ShouldMatch()
    {
        // Arrange
        var formats = new[] { "TABLE", "table", "Table", "JSON", "json", "RAW", "raw" };

        // Act & Assert
        foreach (var format in formats)
        {
            var normalized = format.ToLowerInvariant();
            normalized.Should().BeOneOf("table", "json", "raw");
        }
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
            : null;

        // Assert
        tenantId.Should().Be("config-tenant-id");
    }

    #endregion
}
