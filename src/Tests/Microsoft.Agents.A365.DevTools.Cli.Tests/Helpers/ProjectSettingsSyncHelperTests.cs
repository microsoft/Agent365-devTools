// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
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
}