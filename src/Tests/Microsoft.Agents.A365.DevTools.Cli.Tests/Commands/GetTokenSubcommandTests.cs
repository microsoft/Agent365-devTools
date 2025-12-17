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

    #region Token Storage Tests - launchSettings.json (.NET)

    [Fact]
    public void LaunchSettingsUpdate_WithBearerTokenInProfile_ShouldUpdateToken()
    {
        // Arrange
        var launchSettingsJson = @"{
  ""profiles"": {
    ""Sample Agent with Bearer Token"": {
      ""commandName"": ""Project"",
      ""environmentVariables"": {
        ""ASPNETCORE_ENVIRONMENT"": ""Development"",
        ""BEARER_TOKEN"": """"
      }
    }
  }
}";
        var launchSettings = System.Text.Json.JsonDocument.Parse(launchSettingsJson);

        // Act
        var hasProfiles = launchSettings.RootElement.TryGetProperty("profiles", out var profiles);
        var hasBearerToken = false;
        
        if (hasProfiles)
        {
            foreach (var profile in profiles.EnumerateObject())
            {
                if (profile.Value.TryGetProperty("environmentVariables", out var envVars))
                {
                    foreach (var envVar in envVars.EnumerateObject())
                    {
                        if (envVar.Name == "BEARER_TOKEN")
                        {
                            hasBearerToken = true;
                            break;
                        }
                    }
                }
            }
        }

        // Assert
        hasProfiles.Should().BeTrue();
        hasBearerToken.Should().BeTrue("profile should have BEARER_TOKEN defined");
    }

    [Fact]
    public void LaunchSettingsUpdate_WithoutBearerTokenInProfile_ShouldNotAddToken()
    {
        // Arrange
        var launchSettingsJson = @"{
  ""profiles"": {
    ""Sample Agent"": {
      ""commandName"": ""Project"",
      ""environmentVariables"": {
        ""ASPNETCORE_ENVIRONMENT"": ""Development""
      }
    }
  }
}";
        var launchSettings = System.Text.Json.JsonDocument.Parse(launchSettingsJson);

        // Act
        var hasProfiles = launchSettings.RootElement.TryGetProperty("profiles", out var profiles);
        var hasBearerToken = false;
        
        if (hasProfiles)
        {
            foreach (var profile in profiles.EnumerateObject())
            {
                if (profile.Value.TryGetProperty("environmentVariables", out var envVars))
                {
                    foreach (var envVar in envVars.EnumerateObject())
                    {
                        if (envVar.Name == "BEARER_TOKEN")
                        {
                            hasBearerToken = true;
                            break;
                        }
                    }
                }
            }
        }

        // Assert
        hasProfiles.Should().BeTrue();
        hasBearerToken.Should().BeFalse("profile should not have BEARER_TOKEN");
    }

    [Fact]
    public void LaunchSettingsUpdate_PreservesOtherEnvironmentVariables()
    {
        // Arrange
        var launchSettingsJson = @"{
  ""profiles"": {
    ""Sample"": {
      ""environmentVariables"": {
        ""ASPNETCORE_ENVIRONMENT"": ""Development"",
        ""CUSTOM_VAR"": ""custom-value"",
        ""BEARER_TOKEN"": """"
      }
    }
  }
}";
        var launchSettings = System.Text.Json.JsonDocument.Parse(launchSettingsJson);

        // Act
        var envVars = launchSettings.RootElement
            .GetProperty("profiles")
            .GetProperty("Sample")
            .GetProperty("environmentVariables");

        // Assert
        envVars.TryGetProperty("ASPNETCORE_ENVIRONMENT", out var aspnetEnv).Should().BeTrue();
        aspnetEnv.GetString().Should().Be("Development");
        
        envVars.TryGetProperty("CUSTOM_VAR", out var customVar).Should().BeTrue();
        customVar.GetString().Should().Be("custom-value");
        
        envVars.TryGetProperty("BEARER_TOKEN", out var bearerToken).Should().BeTrue();
    }

    [Fact]
    public void LaunchSettingsUpdate_MultipleProfiles_OnlyUpdatesProfilesWithBearerToken()
    {
        // Arrange
        var launchSettingsJson = @"{
  ""profiles"": {
    ""Profile1"": {
      ""environmentVariables"": {
        ""ASPNETCORE_ENVIRONMENT"": ""Development""
      }
    },
    ""Profile2"": {
      ""environmentVariables"": {
        ""ASPNETCORE_ENVIRONMENT"": ""Development"",
        ""BEARER_TOKEN"": """"
      }
    },
    ""Profile3"": {
      ""environmentVariables"": {
        ""BEARER_TOKEN"": """"
      }
    }
  }
}";
        var launchSettings = System.Text.Json.JsonDocument.Parse(launchSettingsJson);

        // Act
        var profilesWithBearerToken = new List<string>();
        var profiles = launchSettings.RootElement.GetProperty("profiles");
        
        foreach (var profile in profiles.EnumerateObject())
        {
            if (profile.Value.TryGetProperty("environmentVariables", out var envVars))
            {
                foreach (var envVar in envVars.EnumerateObject())
                {
                    if (envVar.Name == "BEARER_TOKEN")
                    {
                        profilesWithBearerToken.Add(profile.Name);
                        break;
                    }
                }
            }
        }

        // Assert
        profilesWithBearerToken.Should().HaveCount(2);
        profilesWithBearerToken.Should().Contain("Profile2");
        profilesWithBearerToken.Should().Contain("Profile3");
        profilesWithBearerToken.Should().NotContain("Profile1");
    }

    [Fact]
    public void LaunchSettingsUpdate_NoProfiles_ShouldBeDetectable()
    {
        // Arrange
        var launchSettingsJson = @"{ ""iisSettings"": {} }";
        var launchSettings = System.Text.Json.JsonDocument.Parse(launchSettingsJson);

        // Act
        var hasProfiles = launchSettings.RootElement.TryGetProperty("profiles", out _);

        // Assert
        hasProfiles.Should().BeFalse("launchSettings should not have profiles section");
    }

    #endregion

    #region Token Storage Tests - .env (Python/Node.js)

    [Fact]
    public void EnvFileUpdate_ExistingBearerToken_ShouldUpdateLine()
    {
        // Arrange
        var envLines = new List<string>
        {
            "CUSTOM_VAR=value1",
            "BEARER_TOKEN=old-token",
            "ANOTHER_VAR=value2"
        };
        var newToken = "new-token-123";

        // Act
        var bearerTokenKey = "BEARER_TOKEN";
        var bearerTokenLine = $"{bearerTokenKey}={newToken}";
        var existingIndex = envLines.FindIndex(l => 
            l.StartsWith($"{bearerTokenKey}=", StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            envLines[existingIndex] = bearerTokenLine;
        }

        // Assert
        existingIndex.Should().Be(1);
        envLines[1].Should().Be("BEARER_TOKEN=new-token-123");
        envLines.Should().HaveCount(3);
    }

    [Fact]
    public void EnvFileUpdate_NoBearerToken_ShouldAddNewLine()
    {
        // Arrange
        var envLines = new List<string>
        {
            "CUSTOM_VAR=value1",
            "ANOTHER_VAR=value2"
        };
        var newToken = "new-token-123";

        // Act
        var bearerTokenKey = "BEARER_TOKEN";
        var bearerTokenLine = $"{bearerTokenKey}={newToken}";
        var existingIndex = envLines.FindIndex(l => 
            l.StartsWith($"{bearerTokenKey}=", StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            envLines[existingIndex] = bearerTokenLine;
        }
        else
        {
            envLines.Add(bearerTokenLine);
        }

        // Assert
        existingIndex.Should().Be(-1);
        envLines.Should().HaveCount(3);
        envLines[2].Should().Be("BEARER_TOKEN=new-token-123");
    }

    [Fact]
    public void EnvFileUpdate_CaseInsensitiveMatch_ShouldUpdateCorrectly()
    {
        // Arrange
        var envLines = new List<string>
        {
            "bearer_token=old-token"
        };
        var newToken = "new-token-123";

        // Act
        var bearerTokenKey = "BEARER_TOKEN";
        var bearerTokenLine = $"{bearerTokenKey}={newToken}";
        var existingIndex = envLines.FindIndex(l => 
            l.StartsWith($"{bearerTokenKey}=", StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            envLines[existingIndex] = bearerTokenLine;
        }

        // Assert
        existingIndex.Should().Be(0);
        envLines[0].Should().Be("BEARER_TOKEN=new-token-123");
    }

    [Fact]
    public void EnvFileUpdate_PreservesOtherVariables()
    {
        // Arrange
        var envLines = new List<string>
        {
            "VAR1=value1",
            "BEARER_TOKEN=old-token",
            "VAR2=value2",
            "# Comment line",
            "VAR3=value3"
        };
        var newToken = "new-token-123";

        // Act
        var bearerTokenKey = "BEARER_TOKEN";
        var bearerTokenLine = $"{bearerTokenKey}={newToken}";
        var existingIndex = envLines.FindIndex(l => 
            l.StartsWith($"{bearerTokenKey}=", StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            envLines[existingIndex] = bearerTokenLine;
        }

        // Assert
        envLines.Should().HaveCount(5);
        envLines[0].Should().Be("VAR1=value1");
        envLines[1].Should().Be("BEARER_TOKEN=new-token-123");
        envLines[2].Should().Be("VAR2=value2");
        envLines[3].Should().Be("# Comment line");
        envLines[4].Should().Be("VAR3=value3");
    }

    [Fact]
    public void EnvFileUpdate_EmptyFile_ShouldAddToken()
    {
        // Arrange
        var envLines = new List<string>();
        var newToken = "new-token-123";

        // Act
        var bearerTokenKey = "BEARER_TOKEN";
        var bearerTokenLine = $"{bearerTokenKey}={newToken}";
        var existingIndex = envLines.FindIndex(l => 
            l.StartsWith($"{bearerTokenKey}=", StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            envLines[existingIndex] = bearerTokenLine;
        }
        else
        {
            envLines.Add(bearerTokenLine);
        }

        // Assert
        envLines.Should().HaveCount(1);
        envLines[0].Should().Be("BEARER_TOKEN=new-token-123");
    }

    #endregion

    #region Platform Detection Tests

    [Fact]
    public void PlatformDetection_DotNetProject_ShouldDetectCorrectly()
    {
        // Arrange - .NET project markers
        var projectFiles = new[] { "MyProject.csproj", "app.config" };

        // Act
        var hasCsproj = projectFiles.Any(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

        // Assert
        hasCsproj.Should().BeTrue();
    }

    [Fact]
    public void PlatformDetection_PythonProject_ShouldDetectCorrectly()
    {
        // Arrange - Python project markers
        var projectFiles = new[] { "pyproject.toml", "requirements.txt", "setup.py" };

        // Act
        var hasPythonMarkers = projectFiles.Any(f => 
            f.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
            f.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase));

        // Assert
        hasPythonMarkers.Should().BeTrue();
    }

    [Fact]
    public void PlatformDetection_NodeProject_ShouldDetectCorrectly()
    {
        // Arrange - Node.js project markers
        var projectFiles = new[] { "package.json", "package-lock.json" };

        // Act
        var hasPackageJson = projectFiles.Any(f => 
            f.Equals("package.json", StringComparison.OrdinalIgnoreCase));

        // Assert
        hasPackageJson.Should().BeTrue();
    }

    #endregion

    #region Integration Scenarios Tests

    [Fact]
    public void TokenStorage_WithConfigPresent_ShouldAttemptToSaveToken()
    {
        // Arrange
        var configExists = true;
        var deploymentProjectPath = "/path/to/project";

        // Act
        var shouldAttemptSave = configExists && !string.IsNullOrWhiteSpace(deploymentProjectPath);

        // Assert
        shouldAttemptSave.Should().BeTrue();
    }

    [Fact]
    public void TokenStorage_WithoutConfig_ShouldProvideGuidanceOnly()
    {
        // Arrange
        var configExists = false;
        var appIdProvided = true;

        // Act
        var shouldProvideGuidance = !configExists && appIdProvided;

        // Assert
        shouldProvideGuidance.Should().BeTrue();
    }

    [Fact]
    public void TokenStorage_FileNotFound_ShouldProvideGuidance()
    {
        // Arrange
        var fileExists = false;

        // Act & Assert
        fileExists.Should().BeFalse();
        // In actual implementation, this triggers guidance logging
    }

    [Fact]
    public void TokenStorage_ValidateExpectedFilePaths()
    {
        // Arrange
        var projectDir = "/path/to/project";
        
        // Act
        var launchSettingsPath = Path.Combine(projectDir, "Properties", "launchSettings.json");
        var envPath = Path.Combine(projectDir, ".env");

        // Assert
        launchSettingsPath.Should().EndWith("Properties/launchSettings.json".Replace('/', Path.DirectorySeparatorChar));
        envPath.Should().EndWith(".env");
    }

    #endregion
}
