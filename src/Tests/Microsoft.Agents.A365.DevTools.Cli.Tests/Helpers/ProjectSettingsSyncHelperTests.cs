// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

public class ProjectSettingsSyncHelperTests : IDisposable
{
    private readonly string _tempRoot;

    public ProjectSettingsSyncHelperTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "A365_ProjectSettingsSyncTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); } catch { /* ignore */ }
    }

    private static ILogger CreateLogger() =>
        LoggerFactory.Create(b => b.AddConsole()).CreateLogger("tests");

    private static PlatformDetector CreatePlatformDetector()
    {
        var cleanLoggerFactory = LoggerFactoryHelper.CreateCleanLoggerFactory();
        return new PlatformDetector(cleanLoggerFactory.CreateLogger<PlatformDetector>());
    }

    private static Mock<IConfigService> MockConfigService(Agent365Config cfg)
    {
        var mock = new Mock<IConfigService>(MockBehavior.Strict);
        mock.Setup(m => m.LoadAsync(
            It.IsAny<string>(),
            It.IsAny<string>()))
            .ReturnsAsync(cfg);
        return mock;
    }

    private static string WriteFile(string dir, string name, string contents = "")
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static JsonObject ReadJson(string path)
    {
        var text = File.ReadAllText(path);
        return (JsonNode.Parse(text) as JsonObject) ?? new JsonObject();
    }

    [Fact]
    public async Task ExecuteAsync_DotNet_WritesExpectedAppsettings()
    {
        // Arrange
        var projectDir = Path.Combine(_tempRoot, "dotnet_proj");
        Directory.CreateDirectory(projectDir);

        // Real detection: ensure .NET by placing a .csproj
        WriteFile(projectDir, "MyAgent.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var appsettingsPath = WriteFile(projectDir, "appsettings.json", "{}");

        // Required by ExecuteAsync (existence only)
        var genPath = WriteFile(_tempRoot, "a365.generated.config.json", "{}");
        var cfgPath = WriteFile(_tempRoot, "a365.config.json", "{}");

        var cfg = new Agent365Config
        {
            DeploymentProjectPath = projectDir,

            TenantId = "5369a35c-46a5-4677-8ff9-2e65587654e7",
            AgenticAppId = "2321586e-2611-4048-be95-962d0445f8ab",
            AgentBlueprintId = "73cfe0a9-87bb-4cfd-bfe1-4309c487d56c",
            AgentBlueprintClientSecret = "blueprintSecret!"
        };

        var configService = MockConfigService(cfg).Object;
        var platformDetector = CreatePlatformDetector();
        var logger = CreateLogger();

        // Act
        await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath, configService, platformDetector, logger);

        // Assert
        var j = ReadJson(appsettingsPath);

        // TokenValidation
        var tokenValidation = j["TokenValidation"]!.AsObject();
        Assert.False(tokenValidation["Enabled"]!.GetValue<bool>());
        var audiences = tokenValidation["Audiences"]!.AsArray();
        Assert.Contains(cfg.AgentBlueprintId, audiences.Select(x => x!.GetValue<string>()));
        Assert.Equal(cfg.TenantId, tokenValidation["TenantId"]!.GetValue<string>());

        // AgentApplication.UserAuthorization.agentic.Settings
        var agentApp = j["AgentApplication"]!.AsObject();
        Assert.False(agentApp["StartTypingTimer"]!.GetValue<bool>());
        Assert.False(agentApp["RemoveRecipientMention"]!.GetValue<bool>());
        Assert.False(agentApp["NormalizeMentions"]!.GetValue<bool>());

        var userAuth = agentApp["UserAuthorization"]!.AsObject();
        Assert.False(userAuth["AutoSignin"]!.GetValue<bool>());
        var agentic = userAuth["Handlers"]!.AsObject()["agentic"]!.AsObject();
        Assert.Equal("AgenticUserAuthorization", agentic["Type"]!.GetValue<string>());
        var uaScopes = agentic["Settings"]!.AsObject()["Scopes"]!.AsArray();
        Assert.Single(uaScopes);
        Assert.Equal("https://graph.microsoft.com/.default", uaScopes[0]!.GetValue<string>());

        // Connections.ServiceConnection.Settings
        var svcSettings = j["Connections"]!.AsObject()["ServiceConnection"]!.AsObject()["Settings"]!.AsObject();
        Assert.Equal("ClientSecret", svcSettings["AuthType"]!.GetValue<string>());
        Assert.Equal($"https://login.microsoftonline.com/{cfg.TenantId}", svcSettings["AuthorityEndpoint"]!.GetValue<string>());
        Assert.Equal(cfg.AgentBlueprintId, svcSettings["ClientId"]!.GetValue<string>());
        Assert.Equal(cfg.AgentBlueprintClientSecret, svcSettings["ClientSecret"]!.GetValue<string>());
        var svcScopes = svcSettings["Scopes"]!.AsArray();
        Assert.Single(svcScopes);
        Assert.Equal("5a807f24-c9de-44ee-a3a7-329e88a00ffc/.default", svcScopes[0]!.GetValue<string>());

        // ConnectionsMap
        var connectionsMap = j["ConnectionsMap"]!.AsArray();
        Assert.Single(connectionsMap);
        var map0 = connectionsMap[0]!.AsObject();
        Assert.Equal("*", map0["ServiceUrl"]!.GetValue<string>());
        Assert.Equal("ServiceConnection", map0["Connection"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_Python_WritesExpectedEnv()
    {
        // Arrange
        var projectDir = Path.Combine(_tempRoot, "py_proj");
        Directory.CreateDirectory(projectDir);

        // Real detection: Python markers
        WriteFile(projectDir, "pyproject.toml", "[tool.poetry]");
        var envPath = WriteFile(projectDir, ".env", "");

        var genPath = WriteFile(_tempRoot, "a365.generated.config.json", "{}");
        var cfgPath = WriteFile(_tempRoot, "a365.config.json", "{}");

        var cfg = new Agent365Config
        {
            DeploymentProjectPath = projectDir,
            TenantId = "5369a35c-46a5-4677-8ff9-2e65587654e7",
            AgenticAppId = "2321586e-2611-4048-be95-962d0445f8ab",
            AgentBlueprintId = "73cfe0a9-87bb-4cfd-bfe1-4309c487d56c",
            AgentBlueprintClientSecret = "blueprintSecret!"
        };

        var configService = MockConfigService(cfg).Object;
        var platformDetector = CreatePlatformDetector();
        var logger = CreateLogger();

        // Act
        await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath, configService, platformDetector, logger);

        // Assert
        var lines = File.ReadAllLines(envPath);

        void AssertHas(string key, string value)
        {
            Assert.Contains(lines, l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
                                     && l.Split('=', 2)[1] == value);
        }

        AssertHas("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID", cfg.AgentBlueprintId);
        AssertHas("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTSECRET", "blueprintSecret!");
        AssertHas("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID", cfg.TenantId);

        AssertHas("AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__AGENTIC__SETTINGS__TYPE", "AgenticUserAuthorization");
        AssertHas("AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__AGENTIC__SETTINGS__ALT_BLUEPRINT_NAME", "SERVICE_CONNECTION");
        AssertHas("AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__AGENTIC__SETTINGS__SCOPES", "https://graph.microsoft.com/.default");

        AssertHas("CONNECTIONSMAP__0__SERVICEURL", "*");
        AssertHas("CONNECTIONSMAP__0__CONNECTION", "SERVICE_CONNECTION");

        // Observability
        AssertHas("ENABLE_A365_OBSERVABILITY_EXPORTER", "false");
    }

    [Fact]
    public async Task ExecuteAsync_Node_WritesExpectedEnv()
    {
        // Arrange
        var projectDir = Path.Combine(_tempRoot, "node_proj");
        Directory.CreateDirectory(projectDir);

        // Real detection: Node markers
        WriteFile(projectDir, "package.json", "{ \"name\": \"sample\" }");
        var envPath = WriteFile(projectDir, ".env", "");

        var genPath = WriteFile(_tempRoot, "a365.generated.config.json", "{}");
        var cfgPath = WriteFile(_tempRoot, "a365.config.json", "{}");

        var cfg = new Agent365Config
        {
            DeploymentProjectPath = projectDir,
            TenantId = "5369a35c-46a5-4677-8ff9-2e65587654e7",
            AgenticAppId = "2321586e-2611-4048-be95-962d0445f8ab",
            AgentBlueprintId = "73cfe0a9-87bb-4cfd-bfe1-4309c487d56c",
            AgentBlueprintClientSecret = "blueprintSecret!"
        };

        var configService = MockConfigService(cfg).Object;
        var platformDetector = CreatePlatformDetector();
        var logger = CreateLogger();

        // Act
        await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath, configService, platformDetector, logger);

        // Assert
        var lines = File.ReadAllLines(envPath);

        void AssertHas(string key, string value)
        {
            Assert.Contains(lines, l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
                                     && l.Split('=', 2)[1] == value);
        }

        // Service Connection
        AssertHas("connections__service_connection__settings__clientId", cfg.AgentBlueprintId);
        AssertHas("connections__service_connection__settings__clientSecret", "blueprintSecret!");
        AssertHas("connections__service_connection__settings__tenantId", cfg.TenantId);

        // Default connection mapping
        AssertHas("connectionsMap__0__serviceUrl", "*");
        AssertHas("connectionsMap__0__connection", "service_connection");

        // AgenticAuthentication
        AssertHas("agentic_altBlueprintConnectionName", "service_connection");
        AssertHas("agentic_scopes", "https://graph.microsoft.com/.default");
        AssertHas("agentic_connectionName", "AgenticAuthConnection");

        // Observability
        AssertHas("ENABLE_A365_OBSERVABILITY_EXPORTER", "false");
    }

    [Fact]
    public async Task ExecuteAsync_MissingProjectPath_LogsWarningAndDoesNothing()
    {
        // Arrange: project path does not exist
        var projectDir = Path.Combine(_tempRoot, "missing_dir");
        var genPath = WriteFile(_tempRoot, "a365.generated.config.json", "{}");
        var cfgPath = WriteFile(_tempRoot, "a365.config.json", "{}");

        var cfg = new Agent365Config
        {
            DeploymentProjectPath = projectDir,
            TenantId = "tenant"
        };

        var configService = MockConfigService(cfg).Object;
        var platformDetector = CreatePlatformDetector();
        var logger = CreateLogger();

        // Act (should not throw)
        await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath, configService, platformDetector, logger);

        // Assert: no files created
        Assert.False(File.Exists(Path.Combine(projectDir, "appsettings.json")));
        Assert.False(File.Exists(Path.Combine(projectDir, ".env")));
    }

    [Fact]
    public async Task ExecuteAsync_MissingGenerated_ThrowsFileNotFound()
    {
        // Arrange
        var projectDir = Path.Combine(_tempRoot, "dotnet_proj2");
        Directory.CreateDirectory(projectDir);
        WriteFile(projectDir, "MyAgent.csproj", "<Project />");
        var cfgPath = WriteFile(_tempRoot, "a365.config.json", "{}");

        var cfg = new Agent365Config
        {
            DeploymentProjectPath = projectDir
        };

        var configService = MockConfigService(cfg).Object;
        var platformDetector = CreatePlatformDetector();
        var logger = CreateLogger();

        // Act + Assert
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, Path.Combine(_tempRoot, "nope.json"),
                configService, platformDetector, logger));
    }

    [Fact]
    public async Task ExecuteAsync_Python_DecryptsProtectedSecret()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            return;
        }

        // Arrange
        var projectDir = Path.Combine(_tempRoot, "py_proj_encrypted");
        Directory.CreateDirectory(projectDir);

        WriteFile(projectDir, "pyproject.toml", "[tool.poetry]");
        var envPath = WriteFile(projectDir, ".env", "");

        var genPath = WriteFile(_tempRoot, "a365.generated.config.json", "{}");
        var cfgPath = WriteFile(_tempRoot, "a365.config.json", "{}");

        var plaintextSecret = "MyPlaintextSecret123!";
        var logger = CreateLogger();
        var protectedSecret = SecretProtectionHelper.ProtectSecret(plaintextSecret, logger);

        var cfg = new Agent365Config
        {
            DeploymentProjectPath = projectDir,
            TenantId = "5369a35c-46a5-4677-8ff9-2e65587654e7",
            AgenticAppId = "2321586e-2611-4048-be95-962d0445f8ab",
            AgentBlueprintId = "73cfe0a9-87bb-4cfd-bfe1-4309c487d56c",
            AgentBlueprintClientSecret = protectedSecret,
            AgentBlueprintClientSecretProtected = true
        };

        var configService = MockConfigService(cfg).Object;
        var platformDetector = CreatePlatformDetector();

        // Act
        await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath, configService, platformDetector, logger);

        // Assert
        var lines = File.ReadAllLines(envPath);
        var secretLine = lines.FirstOrDefault(l => l.StartsWith("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTSECRET="));

        Assert.NotNull(secretLine);
        var secretValue = secretLine.Split('=', 2)[1];
        Assert.Equal(plaintextSecret, secretValue);
        Assert.NotEqual(protectedSecret, secretValue);
    }

    [Fact]
    public async Task ExecuteAsync_Node_DecryptsProtectedSecret()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            return;
        }

        // Arrange
        var projectDir = Path.Combine(_tempRoot, "node_proj_encrypted");
        Directory.CreateDirectory(projectDir);

        WriteFile(projectDir, "package.json", "{ \"name\": \"sample\" }");
        var envPath = WriteFile(projectDir, ".env", "");

        var genPath = WriteFile(_tempRoot, "a365.generated.config.json", "{}");
        var cfgPath = WriteFile(_tempRoot, "a365.config.json", "{}");

        var plaintextSecret = "MyPlaintextSecret123!";
        var logger = CreateLogger();
        var protectedSecret = SecretProtectionHelper.ProtectSecret(plaintextSecret, logger);

        var cfg = new Agent365Config
        {
            DeploymentProjectPath = projectDir,
            TenantId = "5369a35c-46a5-4677-8ff9-2e65587654e7",
            AgenticAppId = "2321586e-2611-4048-be95-962d0445f8ab",
            AgentBlueprintId = "73cfe0a9-87bb-4cfd-bfe1-4309c487d56c",
            AgentBlueprintClientSecret = protectedSecret,
            AgentBlueprintClientSecretProtected = true
        };

        var configService = MockConfigService(cfg).Object;
        var platformDetector = CreatePlatformDetector();

        // Act
        await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath, configService, platformDetector, logger);

        // Assert
        var lines = File.ReadAllLines(envPath);
        var secretLine = lines.FirstOrDefault(l => l.StartsWith("connections__service_connection__settings__clientSecret=", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(secretLine);
        var secretValue = secretLine.Split('=', 2)[1];
        Assert.Equal(plaintextSecret, secretValue);
        Assert.NotEqual(protectedSecret, secretValue);
    }

    [Fact]
    public async Task ExecuteAsync_DotNet_DecryptsProtectedSecret()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            return;
        }

        // Arrange
        var projectDir = Path.Combine(_tempRoot, "dotnet_proj_encrypted");
        Directory.CreateDirectory(projectDir);

        WriteFile(projectDir, "MyAgent.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var appsettingsPath = WriteFile(projectDir, "appsettings.json", "{}");

        var genPath = WriteFile(_tempRoot, "a365.generated.config.json", "{}");
        var cfgPath = WriteFile(_tempRoot, "a365.config.json", "{}");

        var plaintextSecret = "MyPlaintextSecret123!";
        var logger = CreateLogger();
        var protectedSecret = SecretProtectionHelper.ProtectSecret(plaintextSecret, logger);

        var cfg = new Agent365Config
        {
            DeploymentProjectPath = projectDir,
            TenantId = "5369a35c-46a5-4677-8ff9-2e65587654e7",
            AgenticAppId = "2321586e-2611-4048-be95-962d0445f8ab",
            AgentBlueprintId = "73cfe0a9-87bb-4cfd-bfe1-4309c487d56c",
            AgentBlueprintClientSecret = protectedSecret,
            AgentBlueprintClientSecretProtected = true
        };

        var configService = MockConfigService(cfg).Object;
        var platformDetector = CreatePlatformDetector();

        // Act
        await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath, configService, platformDetector, logger);

        // Assert
        var j = ReadJson(appsettingsPath);
        var svcSettings = j["Connections"]!.AsObject()["ServiceConnection"]!.AsObject()["Settings"]!.AsObject();
        var clientSecret = svcSettings["ClientSecret"]!.GetValue<string>();

        Assert.Equal(plaintextSecret, clientSecret);
        Assert.NotEqual(protectedSecret, clientSecret);
    }

    [Fact]
    public async Task ExecuteAsync_Python_UnprotectedSecret_WritesAsIs()
    {
        // Arrange
        var projectDir = Path.Combine(_tempRoot, "py_proj_unprotected");
        Directory.CreateDirectory(projectDir);

        WriteFile(projectDir, "pyproject.toml", "[tool.poetry]");
        var envPath = WriteFile(projectDir, ".env", "");

        var genPath = WriteFile(_tempRoot, "a365.generated.config.json", "{}");
        var cfgPath = WriteFile(_tempRoot, "a365.config.json", "{}");

        var plaintextSecret = "UnprotectedSecret123!";

        var cfg = new Agent365Config
        {
            DeploymentProjectPath = projectDir,
            TenantId = "5369a35c-46a5-4677-8ff9-2e65587654e7",
            AgenticAppId = "2321586e-2611-4048-be95-962d0445f8ab",
            AgentBlueprintId = "73cfe0a9-87bb-4cfd-bfe1-4309c487d56c",
            AgentBlueprintClientSecret = plaintextSecret,
            AgentBlueprintClientSecretProtected = false
        };

        var configService = MockConfigService(cfg).Object;
        var platformDetector = CreatePlatformDetector();
        var logger = CreateLogger();

        // Act
        await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath, configService, platformDetector, logger);

        // Assert
        var lines = File.ReadAllLines(envPath);
        var secretLine = lines.FirstOrDefault(l => l.StartsWith("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTSECRET="));

        Assert.NotNull(secretLine);
        var secretValue = secretLine.Split('=', 2)[1];
        Assert.Equal(plaintextSecret, secretValue);
    }

    [Fact]
    public async Task ExecuteAsync_Node_UnprotectedSecret_WritesAsIs()
    {
        // Arrange
        var projectDir = Path.Combine(_tempRoot, "node_proj_unprotected");
        Directory.CreateDirectory(projectDir);

        WriteFile(projectDir, "package.json", "{ \"name\": \"sample\" }");
        var envPath = WriteFile(projectDir, ".env", "");

        var genPath = WriteFile(_tempRoot, "a365.generated.config.json", "{}");
        var cfgPath = WriteFile(_tempRoot, "a365.config.json", "{}");

        var plaintextSecret = "UnprotectedSecret123!";

        var cfg = new Agent365Config
        {
            DeploymentProjectPath = projectDir,
            TenantId = "5369a35c-46a5-4677-8ff9-2e65587654e7",
            AgenticAppId = "2321586e-2611-4048-be95-962d0445f8ab",
            AgentBlueprintId = "73cfe0a9-87bb-4cfd-bfe1-4309c487d56c",
            AgentBlueprintClientSecret = plaintextSecret,
            AgentBlueprintClientSecretProtected = false
        };

        var configService = MockConfigService(cfg).Object;
        var platformDetector = CreatePlatformDetector();
        var logger = CreateLogger();

        // Act
        await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath, configService, platformDetector, logger);

        // Assert
        var lines = File.ReadAllLines(envPath);
        var secretLine = lines.FirstOrDefault(l => l.StartsWith("connections__service_connection__settings__clientSecret=", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(secretLine);
        var secretValue = secretLine.Split('=', 2)[1];
        Assert.Equal(plaintextSecret, secretValue);
    }

    [Fact]
    public async Task ExecuteAsync_DotNet_UnprotectedSecret_WritesAsIs()
    {
        // Arrange
        var projectDir = Path.Combine(_tempRoot, "dotnet_proj_unprotected");
        Directory.CreateDirectory(projectDir);

        WriteFile(projectDir, "MyAgent.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var appsettingsPath = WriteFile(projectDir, "appsettings.json", "{}");

        var genPath = WriteFile(_tempRoot, "a365.generated.config.json", "{}");
        var cfgPath = WriteFile(_tempRoot, "a365.config.json", "{}");

        var plaintextSecret = "UnprotectedSecret123!";

        var cfg = new Agent365Config
        {
            DeploymentProjectPath = projectDir,
            TenantId = "5369a35c-46a5-4677-8ff9-2e65587654e7",
            AgenticAppId = "2321586e-2611-4048-be95-962d0445f8ab",
            AgentBlueprintId = "73cfe0a9-87bb-4cfd-bfe1-4309c487d56c",
            AgentBlueprintClientSecret = plaintextSecret,
            AgentBlueprintClientSecretProtected = false
        };

        var configService = MockConfigService(cfg).Object;
        var platformDetector = CreatePlatformDetector();
        var logger = CreateLogger();

        // Act
        await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath, configService, platformDetector, logger);

        // Assert
        var j = ReadJson(appsettingsPath);
        var svcSettings = j["Connections"]!.AsObject()["ServiceConnection"]!.AsObject()["Settings"]!.AsObject();
        var clientSecret = svcSettings["ClientSecret"]!.GetValue<string>();

        Assert.Equal(plaintextSecret, clientSecret);
    }

    // -------------------------------------------------------------------------
    // Agent365Observability section tests
    // -------------------------------------------------------------------------

    /// <summary>
    /// Non-DW flow (AgenticAppId set): AgentId must use the Agent Identity, not the Blueprint.
    /// AgentName, AgentDescription, TenantId, ClientId (blueprint), and ClientSecret are written.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DotNet_WritesAgent365Observability_NonDw()
    {
        var projectDir = Path.Combine(_tempRoot, "dotnet_obs_nondw");
        Directory.CreateDirectory(projectDir);
        WriteFile(projectDir, "MyAgent.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var appsettingsPath = WriteFile(projectDir, "appsettings.json", "{}");

        var genPath = WriteFile(_tempRoot, "a365.generated.obs_nondw.json", "{}");
        var cfgPath = WriteFile(_tempRoot, "a365.obs_nondw.config.json", "{}");


        var cfg = new Agent365Config
        {
            DeploymentProjectPath = projectDir,
            TenantId = "tenant-obs-id",
            AgenticAppId = "agent-identity-app-id",
            AgentBlueprintId = "blueprint-app-id",
            AgentIdentityDisplayName = "My Agent Identity",
            AgentDescription = "An agent for testing",
            AgentBlueprintClientSecret = "obs-secret",
            AgentBlueprintClientSecretProtected = false
        };

        await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath,
            MockConfigService(cfg).Object, CreatePlatformDetector(), CreateLogger());

        var obs = ReadJson(appsettingsPath)["Agent365Observability"]!.AsObject();
        // non-DW must use Agent Identity app ID, not Blueprint
        Assert.Equal("agent-identity-app-id", obs["AgentId"]!.GetValue<string>());
        Assert.Equal("My Agent Identity", obs["AgentName"]!.GetValue<string>());
        Assert.Equal("An agent for testing", obs["AgentDescription"]!.GetValue<string>());
        Assert.Equal("tenant-obs-id", obs["TenantId"]!.GetValue<string>());
        Assert.Equal("blueprint-app-id", obs["AgentBlueprintId"]!.GetValue<string>());
        Assert.Equal("blueprint-app-id", obs["ClientId"]!.GetValue<string>());
        Assert.Equal("obs-secret", obs["ClientSecret"]!.GetValue<string>());
    }

    /// <summary>
    /// DW flow (AgenticAppId null/empty): AgentId falls back to the Blueprint app ID.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DotNet_WritesAgent365Observability_Dw()
    {
        var projectDir = Path.Combine(_tempRoot, "dotnet_obs_dw");
        Directory.CreateDirectory(projectDir);
        WriteFile(projectDir, "MyAgent.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var appsettingsPath = WriteFile(projectDir, "appsettings.json", "{}");

        var genPath = WriteFile(_tempRoot, "a365.generated.obs_dw.json", "{}");
        var cfgPath = WriteFile(_tempRoot, "a365.obs_dw.config.json", "{}");

        var cfg = new Agent365Config
        {
            DeploymentProjectPath = projectDir,
            TenantId = "tenant-dw-id",
            AgenticAppId = null,                   // DW: no agent identity
            AgentBlueprintId = "blueprint-dw-id",
            AgentBlueprintClientSecret = "dw-secret",
            AgentBlueprintClientSecretProtected = false
        };

        await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath,
            MockConfigService(cfg).Object, CreatePlatformDetector(), CreateLogger());

        var j = ReadJson(appsettingsPath);
        var obs = j["Agent365Observability"]!.AsObject();
        // DW must fall back to Blueprint app ID when AgenticAppId is absent
        Assert.Equal("blueprint-dw-id", obs["AgentId"]!.GetValue<string>());
        Assert.Equal("tenant-dw-id", obs["TenantId"]!.GetValue<string>());
        Assert.Equal("blueprint-dw-id", obs["AgentBlueprintId"]!.GetValue<string>());
        Assert.Equal("blueprint-dw-id", obs["ClientId"]!.GetValue<string>());
        // EnableAgent365Exporter is written to false by default
        Assert.False(j["EnableAgent365Exporter"]!.GetValue<bool>());
    }

    /// <summary>
    /// Python .env: Agent365Observability keys use UPPER_SNAKE_CASE with double-underscores.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Python_WritesAgent365Observability()
    {
        var projectDir = Path.Combine(_tempRoot, "py_obs");
        Directory.CreateDirectory(projectDir);
        WriteFile(projectDir, "pyproject.toml", "[tool.poetry]");
        var envPath = WriteFile(projectDir, ".env", "");

        var genPath = WriteFile(_tempRoot, "a365.generated.py_obs.json", "{}");
        var cfgPath = WriteFile(_tempRoot, "a365.py_obs.config.json", "{}");

        var cfg = new Agent365Config
        {
            DeploymentProjectPath = projectDir,
            TenantId = "tenant-py-id",
            AgenticAppId = "agent-py-id",
            AgentBlueprintId = "blueprint-py-id",
            AgentIdentityDisplayName = "Py-Agent",
            AgentDescription = "Python-test-agent",
            AgentBlueprintClientSecret = "py-secret",
            AgentBlueprintClientSecretProtected = false
        };

        await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath,
            MockConfigService(cfg).Object, CreatePlatformDetector(), CreateLogger());

        var lines = File.ReadAllLines(envPath);
        string Val(string key) => lines
            .First(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            .Split('=', 2)[1];

        // non-DW uses Agent Identity app ID
        Assert.Equal("agent-py-id", Val("AGENT365OBSERVABILITY__AGENTID"));
        Assert.Equal("Py-Agent", Val("AGENT365OBSERVABILITY__AGENTNAME"));
        Assert.Equal("Python-test-agent", Val("AGENT365OBSERVABILITY__AGENTDESCRIPTION"));
        Assert.Equal("tenant-py-id", Val("AGENT365OBSERVABILITY__TENANTID"));
        Assert.Equal("blueprint-py-id", Val("AGENT365OBSERVABILITY__AGENTBLUEPRINTID"));
        Assert.Equal("blueprint-py-id", Val("AGENT365OBSERVABILITY__CLIENTID"));
        Assert.Equal("py-secret", Val("AGENT365OBSERVABILITY__CLIENTSECRET"));
        Assert.Contains(lines, l => l.StartsWith("ENABLE_A365_OBSERVABILITY_EXPORTER=", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Node .env: Agent365Observability keys use camelCase with double-underscores.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Node_WritesAgent365Observability()
    {
        var projectDir = Path.Combine(_tempRoot, "node_obs");
        Directory.CreateDirectory(projectDir);
        WriteFile(projectDir, "package.json", "{ \"name\": \"sample\" }");
        var envPath = WriteFile(projectDir, ".env", "");

        var genPath = WriteFile(_tempRoot, "a365.generated.node_obs.json", "{}");
        var cfgPath = WriteFile(_tempRoot, "a365.node_obs.config.json", "{}");

        var cfg = new Agent365Config
        {
            DeploymentProjectPath = projectDir,
            TenantId = "tenant-node-id",
            AgenticAppId = "agent-node-id",
            AgentBlueprintId = "blueprint-node-id",
            AgentIdentityDisplayName = "Node Agent",
            AgentDescription = "Node test agent",
            AgentBlueprintClientSecret = "node-secret",
            AgentBlueprintClientSecretProtected = false
        };

        await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath,
            MockConfigService(cfg).Object, CreatePlatformDetector(), CreateLogger());

        var lines = File.ReadAllLines(envPath);
        string Val(string key) => lines
            .First(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            .Split('=', 2)[1];

        // non-DW uses Agent Identity app ID
        Assert.Equal("agent-node-id", Val("agent365Observability__agentId"));
        Assert.Equal("Node Agent", Val("agent365Observability__agentName"));
        Assert.Equal("Node test agent", Val("agent365Observability__agentDescription"));
        Assert.Equal("tenant-node-id", Val("agent365Observability__tenantId"));
        Assert.Equal("blueprint-node-id", Val("agent365Observability__agentBlueprintId"));
        Assert.Equal("blueprint-node-id", Val("agent365Observability__clientId"));
        Assert.Equal("node-secret", Val("agent365Observability__clientSecret"));
        Assert.Contains(lines, l => l.StartsWith("ENABLE_A365_OBSERVABILITY_EXPORTER=", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------------------------
    // Secret destinations: a365.config.json is often committed, so deploymentProjectPath must not be
    // able to send a bearer token or the blueprint client secret outside the config's directory.
    // ---------------------------------------------------------------------------------------------

    private const string BearerToken = "bearer-token-value";
    private const string BlueprintSecret = "blueprint-secret-value";

    private static Agent365Config SecretConfig(string deploymentProjectPath) => new()
    {
        DeploymentProjectPath = deploymentProjectPath,
        TenantId = "5369a35c-46a5-4677-8ff9-2e65587654e7",
        AgentBlueprintId = "73cfe0a9-87bb-4cfd-bfe1-4309c487d56c",
        AgentBlueprintClientSecret = BlueprintSecret,
        AgentBlueprintClientSecretProtected = false
    };

    [Fact]
    public async Task SaveBearerToken_NodeProjectInsideConfigDirectory_WritesToken()
    {
        var configDir = Path.Combine(_tempRoot, "repo");
        var projectDir = Path.Combine(configDir, "agent");
        WriteFile(projectDir, "package.json", "{}");
        var envPath = WriteFile(projectDir, ".env", "BEARER_TOKEN=\n");

        var allowed = await ProjectSettingsSyncHelper.SaveBearerTokenToPlatformConfigAsync(
            BearerToken, new Agent365Config { DeploymentProjectPath = "agent" }, configDir, CreateLogger());

        allowed.Should().BeTrue(because: "a project inside the a365.config.json directory is an accepted destination");
        File.ReadAllLines(envPath).Should().Contain($"BEARER_TOKEN={BearerToken}",
            because: "get-token still saves the token into a project inside the a365.config.json directory");
    }

    [Fact]
    public async Task SaveBearerToken_DotNetProjectInsideConfigDirectory_WritesToken()
    {
        var configDir = Path.Combine(_tempRoot, "repo");
        WriteFile(configDir, "Agent.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var launchSettingsPath = WriteFile(Path.Combine(configDir, "Properties"), "launchSettings.json",
            "{ \"profiles\": { \"dev\": { \"environmentVariables\": { \"BEARER_TOKEN\": \"\" } } } }");

        await ProjectSettingsSyncHelper.SaveBearerTokenToPlatformConfigAsync(
            BearerToken, new Agent365Config(), configDir, CreateLogger());

        ReadJson(launchSettingsPath)["profiles"]!["dev"]!["environmentVariables"]!["BEARER_TOKEN"]!
            .GetValue<string>().Should().Be(BearerToken,
                because: "an unset deploymentProjectPath means the a365.config.json directory, which is trusted");
    }

    [Fact]
    public async Task SaveBearerToken_ProjectOutsideConfigDirectory_DoesNotWriteToken()
    {
        var configDir = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(configDir);
        var outsideProject = Path.Combine(_tempRoot, "outside");
        WriteFile(outsideProject, "package.json", "{}");
        var envPath = WriteFile(outsideProject, ".env", "BEARER_TOKEN=\n");

        var allowed = await ProjectSettingsSyncHelper.SaveBearerTokenToPlatformConfigAsync(
            BearerToken, new Agent365Config { DeploymentProjectPath = outsideProject }, configDir, CreateLogger());

        allowed.Should().BeFalse(because: "get-token stops saving further tokens once the project destination is rejected");
        File.ReadAllText(envPath).Should().NotContain(BearerToken,
            because: "a committed deploymentProjectPath must not direct the bearer token outside the a365.config.json directory");
    }

    [Fact]
    public async Task SaveBearerToken_RelativeTraversalOutsideConfigDirectory_DoesNotWriteToken()
    {
        var configDir = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(configDir);
        var outsideProject = Path.Combine(_tempRoot, "outside");
        WriteFile(outsideProject, "Agent.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var launchSettingsPath = WriteFile(Path.Combine(outsideProject, "Properties"), "launchSettings.json",
            "{ \"profiles\": { \"dev\": { \"environmentVariables\": { \"BEARER_TOKEN\": \"\" } } } }");

        await ProjectSettingsSyncHelper.SaveBearerTokenToPlatformConfigAsync(
            BearerToken, new Agent365Config { DeploymentProjectPath = Path.Combine("..", "outside") }, configDir, CreateLogger());

        File.ReadAllText(launchSettingsPath).Should().NotContain(BearerToken,
            because: "'..' in deploymentProjectPath must not escape the a365.config.json directory");
    }

    [Fact]
    public async Task SaveBearerToken_SymlinkedEnvFile_DoesNotWriteThroughLink()
    {
        var configDir = Path.Combine(_tempRoot, "repo");
        WriteFile(configDir, "package.json", "{}");
        var target = WriteFile(Path.Combine(_tempRoot, "outside"), "captured.env", "BEARER_TOKEN=\n");
        if (!FileSystemLinks.TryCreateSymlink(() => File.CreateSymbolicLink(Path.Combine(configDir, ".env"), target)))
            return;

        await ProjectSettingsSyncHelper.SaveBearerTokenToPlatformConfigAsync(
            BearerToken, new Agent365Config(), configDir, CreateLogger());

        File.ReadAllText(target).Should().NotContain(BearerToken,
            because: "a committed .env symlink must not redirect the bearer token to the link target");
    }

    [Fact]
    public async Task ExecuteAsync_ProjectOutsideConfigDirectory_DoesNotWriteSecrets()
    {
        var configDir = Path.Combine(_tempRoot, "repo");
        var cfgPath = WriteFile(configDir, "a365.config.json", "{}");
        var genPath = WriteFile(configDir, "a365.generated.config.json", "{}");
        var outsideProject = Path.Combine(_tempRoot, "outside");
        WriteFile(outsideProject, "package.json", "{}");

        var result = await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath,
            MockConfigService(SecretConfig(outsideProject)).Object, CreatePlatformDetector(), CreateLogger());

        result.Should().Be(ProjectSettingsSyncResult.Blocked,
            because: "settings sync is skipped, and reported as blocked, when deploymentProjectPath leaves the config directory");
        File.Exists(Path.Combine(outsideProject, ".env")).Should().BeFalse(
            because: "the blueprint client secret must not be written outside the a365.config.json directory");
        File.Exists(Path.Combine(configDir, ".env")).Should().BeFalse(
            because: "an untrusted deploymentProjectPath must not fall back to stamping secrets elsewhere");
    }

    [Fact]
    public async Task ExecuteAsync_SymlinkedEnvFile_DoesNotWriteThroughLink()
    {
        var configDir = Path.Combine(_tempRoot, "repo");
        var cfgPath = WriteFile(configDir, "a365.config.json", "{}");
        var genPath = WriteFile(configDir, "a365.generated.config.json", "{}");
        WriteFile(configDir, "package.json", "{}");
        var target = Path.Combine(_tempRoot, "outside", "captured.env");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!FileSystemLinks.TryCreateSymlink(() => File.CreateSymbolicLink(Path.Combine(configDir, ".env"), target)))
            return;

        var result = await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath,
            MockConfigService(SecretConfig(string.Empty)).Object, CreatePlatformDetector(), CreateLogger());

        result.Should().Be(ProjectSettingsSyncResult.Blocked,
            because: "settings sync refuses to write through a symbolic link and reports it as blocked");
        File.Exists(target).Should().BeFalse(
            because: "writing through a dangling .env symlink would create the secret file at the link target");
    }

    [Fact]
    public async Task ExecuteAsync_RelativeProjectPath_ResolvesAgainstConfigDirectory()
    {
        var configDir = Path.Combine(_tempRoot, "repo");
        var cfgPath = WriteFile(configDir, "a365.config.json", "{}");
        var genPath = WriteFile(configDir, "a365.generated.config.json", "{}");
        var projectDir = Path.Combine(configDir, "src", "agent");
        WriteFile(projectDir, "package.json", "{}");

        var result = await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath,
            MockConfigService(SecretConfig(Path.Combine("src", "agent"))).Object, CreatePlatformDetector(), CreateLogger());

        result.Should().Be(ProjectSettingsSyncResult.Written,
            because: "a project below the a365.config.json directory is a trusted destination");
        File.ReadAllText(Path.Combine(projectDir, ".env")).Should().Contain(BlueprintSecret,
            because: "relative deploymentProjectPath values resolve against the a365.config.json directory");
    }

    [Fact]
    public async Task ExecuteAsync_JunctionedProjectDirectory_DoesNotWriteSecrets()
    {
        if (!OperatingSystem.IsWindows())
            return; // Junctions exist only on Windows; Linux and macOS are covered by the symlink tests.

        var configDir = Path.Combine(_tempRoot, "repo");
        var cfgPath = WriteFile(configDir, "a365.config.json", "{}");
        var genPath = WriteFile(configDir, "a365.generated.config.json", "{}");
        var outsideProject = Path.Combine(_tempRoot, "outside");
        WriteFile(outsideProject, "package.json", "{}");
        FileSystemLinks.CreateJunction(Path.Combine(configDir, "agent"), outsideProject);

        var result = await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath,
            MockConfigService(SecretConfig("agent")).Object, CreatePlatformDetector(), CreateLogger());

        result.Should().Be(ProjectSettingsSyncResult.Blocked,
            because: "a junction below the a365.config.json directory is rejected like a symbolic link");
        File.Exists(Path.Combine(outsideProject, ".env")).Should().BeFalse(
            because: "a committed junction must not redirect the blueprint client secret outside the project");
    }

    /// <summary>
    /// Runs an action the first time a log message contains a trigger. The helpers log between the up-front
    /// destination check and the write, so tests use this to swap in a link at exactly that point.
    /// </summary>
    private sealed class HookLogger : ILogger
    {
        private readonly string _trigger;
        private readonly Action _onTrigger;
        private bool _fired;

        public HookLogger(string trigger, Action onTrigger)
        {
            _trigger = trigger;
            _onTrigger = onTrigger;
        }

        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Add(message);
            if (!_fired && message.Contains(_trigger, StringComparison.Ordinal))
            {
                _fired = true;
                _onTrigger();
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_LinkSwappedInBeforeWrite_ReturnsBlocked()
    {
        var configDir = Path.Combine(_tempRoot, "repo");
        var cfgPath = WriteFile(configDir, "a365.config.json", "{}");
        var genPath = WriteFile(configDir, "a365.generated.config.json", "{}");
        var projectDir = Path.Combine(configDir, "agent");
        WriteFile(projectDir, "package.json", "{}");
        var outsideProject = Path.Combine(_tempRoot, "outside");
        Directory.CreateDirectory(outsideProject);

        // The empty .env is created first; the project directory becomes a link before the secret is written.
        var logger = new HookLogger("Created:", () => FileSystemLinks.ReplaceWithDirectoryLink(projectDir, outsideProject));

        var result = await ProjectSettingsSyncHelper.ExecuteAsync(cfgPath, genPath,
            MockConfigService(SecretConfig("agent")).Object, CreatePlatformDetector(), logger);

        result.Should().Be(ProjectSettingsSyncResult.Blocked,
            because: "a link that appears before the write is a rejected destination, so setup completes with a warning instead of failing");
        File.Exists(Path.Combine(outsideProject, ".env")).Should().BeFalse(
            because: "the blueprint client secret must not be written through a link that appeared after the first check");
        logger.Messages.Should().Contain(ErrorMessages.SecretDestinationRemediation,
            because: "a write-time rejection must give the same remediation as an up-front rejection");
    }

    [Fact]
    public async Task SaveBearerToken_LinkSwappedInBeforeWrite_ReturnsFalse()
    {
        var configDir = Path.Combine(_tempRoot, "repo");
        var projectDir = Path.Combine(configDir, "agent");
        WriteFile(projectDir, "package.json", "{}");
        WriteFile(projectDir, ".env", "BEARER_TOKEN=\n");
        var outsideProject = Path.Combine(_tempRoot, "outside");
        var outsideEnv = WriteFile(outsideProject, ".env", "BEARER_TOKEN=\n");

        // The existing .env is read first; the project directory becomes a link before the token is written.
        var logger = new HookLogger("Updated BEARER_TOKEN", () => FileSystemLinks.ReplaceWithDirectoryLink(projectDir, outsideProject));

        var allowed = await ProjectSettingsSyncHelper.SaveBearerTokenToPlatformConfigAsync(
            BearerToken, new Agent365Config { DeploymentProjectPath = "agent" }, configDir, logger);

        allowed.Should().BeFalse(
            because: "a link that appears before the write is a rejected destination, so get-token stops saving the remaining tokens");
        File.ReadAllText(outsideEnv).Should().NotContain(BearerToken,
            because: "the bearer token must not be written through a link that appeared after the first check");
        logger.Messages.Should().Contain(ErrorMessages.SecretDestinationRemediation,
            because: "a write-time rejection must give the same remediation as an up-front rejection");
    }
}
