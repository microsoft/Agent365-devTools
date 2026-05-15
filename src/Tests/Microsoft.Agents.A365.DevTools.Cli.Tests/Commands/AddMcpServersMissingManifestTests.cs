// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Regression tests for issue #412 — `a365 develop add-mcp-servers` must create a new
/// ToolingManifest.json when one does not already exist (e.g. when using n8n or any flow
/// that has no agent project on disk). Pre-#385 behavior; #385 introduced a hard error.
///
/// Tests modify <see cref="Directory.GetCurrentDirectory"/> and the
/// <c>A365_MCP_CATALOG_PATH</c> env var, so they cannot run in parallel.
/// </summary>
[CollectionDefinition("AddMcpServersMissingManifestTests", DisableParallelization = true)]
public class AddMcpServersMissingManifestTestsCollection { }

[Collection("AddMcpServersMissingManifestTests")]
public class AddMcpServersMissingManifestTests : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConfigService _configService;
    private readonly CommandExecutor _commandExecutor;
    private readonly AuthenticationService _authService;
    private readonly GraphApiService _graphApiService;
    private readonly AgentBlueprintService _blueprintService;
    private readonly IProcessService _processService;

    private readonly string _originalCwd;
    private readonly List<string> _tempDirs = new();
    private readonly string _catalogPath;
    private readonly string? _savedCatalogEnvVar;

    public AddMcpServersMissingManifestTests()
    {
        _logger = Substitute.For<ILogger>();

        var configLogger = Substitute.For<ILogger<ConfigService>>();
        _configService = Substitute.ForPartsOf<ConfigService>(configLogger);

        var executorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _commandExecutor = Substitute.ForPartsOf<CommandExecutor>(executorLogger);

        var authLogger = Substitute.For<ILogger<AuthenticationService>>();
        _authService = Substitute.ForPartsOf<AuthenticationService>(authLogger);

        var graphLogger = Substitute.For<ILogger<GraphApiService>>();
        _graphApiService = Substitute.ForPartsOf<GraphApiService>(graphLogger, _commandExecutor);
        _blueprintService = Substitute.ForPartsOf<AgentBlueprintService>(
            Substitute.For<ILogger<AgentBlueprintService>>(), _graphApiService);

        _processService = Substitute.For<IProcessService>();

        _originalCwd = Directory.GetCurrentDirectory();

        // Route the production catalog path to a per-test-class temp file via the
        // A365_MCP_CATALOG_PATH override. Avoids the machine-global default in Path.GetTempPath()
        // so this class cannot collide with any other test that touches the catalog.
        _savedCatalogEnvVar = Environment.GetEnvironmentVariable(McpServerCatalogWriter.CatalogPathEnvVar);
        _catalogPath = Path.Combine(Path.GetTempPath(),
            "a365-issue412-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        Environment.SetEnvironmentVariable(McpServerCatalogWriter.CatalogPathEnvVar, _catalogPath);
        File.WriteAllText(_catalogPath, BuildCatalogJson("mcp_M365Copilot", "mcp_MailTools"));
    }

    public void Dispose()
    {
        try { Directory.SetCurrentDirectory(_originalCwd); } catch { /* best-effort */ }
        Environment.SetEnvironmentVariable(McpServerCatalogWriter.CatalogPathEnvVar, _savedCatalogEnvVar);

        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        try { if (File.Exists(_catalogPath)) File.Delete(_catalogPath); }
        catch { /* best-effort */ }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "a365-issue412-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private Command BuildDevelopCommand() =>
        DevelopCommand.CreateCommand(_logger, _configService, _commandExecutor,
            _authService, _graphApiService, _blueprintService, _processService);

    private RootCommand BuildRoot()
    {
        var root = new RootCommand();
        root.AddCommand(BuildDevelopCommand());
        return root;
    }

    private static string BuildCatalogJson(params string[] serverNames)
    {
        var servers = serverNames.Select(name => new
        {
            mcpServerName = name,
            url = $"https://example.invalid/{name}",
            scope = "Tools.ListInvoke.All",
            audience = "00000000-0000-0000-0000-000000000001",
            publisher = "Test"
        });
        return JsonSerializer.Serialize(new { mcpServers = servers });
    }

    [Fact]
    public async Task AddMcpServers_NoManifestInProjectPath_CreatesNewManifest()
    {
        // Issue #412: running add-mcp-servers in a fresh directory (no ToolingManifest.json,
        // no a365.config.json) must create the manifest, not error out.
        var projectDir = CreateTempDir();
        var manifestPath = Path.Combine(projectDir, McpConstants.ToolingManifestFileName);
        File.Exists(manifestPath).Should().BeFalse("the project dir is empty by construction");

        var exitCode = await BuildRoot().InvokeAsync(new[]
        {
            "develop", "add-mcp-servers", "mcp_M365Copilot", "--project-path", projectDir
        });

        exitCode.Should().Be(0, because: "issue #412 — a missing manifest must be created, not an error");
        File.Exists(manifestPath).Should().BeTrue(because: "the manifest file should now exist on disk");

        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        manifestJson.Should().Contain("mcp_M365Copilot",
            because: "the requested server must be written into the new manifest");
    }

    [Fact]
    public async Task AddMcpServers_NoManifestInCwd_CreatesNewManifestInCwd()
    {
        // n8n use case from the issue: user runs the command directly in a working directory
        // without --project-path. The manifest should land in CWD.
        var workingDir = CreateTempDir();
        Directory.SetCurrentDirectory(workingDir);

        var exitCode = await BuildRoot().InvokeAsync(
            new[] { "develop", "add-mcp-servers", "mcp_MailTools" });

        exitCode.Should().Be(0, because: "issue #412 — CWD invocation in a fresh dir must succeed");
        var manifestPath = Path.Combine(workingDir, McpConstants.ToolingManifestFileName);
        File.Exists(manifestPath).Should().BeTrue(
            because: "the manifest must be created in the current working directory");
    }

    [Fact]
    public async Task AddMcpServers_ProjectPathDirectoryMissing_ReturnsExitCode1()
    {
        // CR-002: the new typo-guard branch at DevelopCommand.cs:520-530 must fail loudly
        // when --project-path points at a directory that does not exist. The CLI must not
        // silently auto-create the directory.
        var bogusDir = Path.Combine(Path.GetTempPath(),
            "a365-issue412-doesnotexist-" + Guid.NewGuid().ToString("N"));
        Directory.Exists(bogusDir).Should().BeFalse(because: "the test pre-condition is a typo'd path");

        var exitCode = await BuildRoot().InvokeAsync(new[]
        {
            "develop", "add-mcp-servers", "mcp_M365Copilot", "--project-path", bogusDir
        });

        exitCode.Should().Be(1,
            because: "a --project-path that does not exist is almost always a typo — failing loudly is safer than silently creating a directory tree in the wrong place");
        Directory.Exists(bogusDir).Should().BeFalse(
            because: "the CLI must not auto-create directories supplied via --project-path");
    }

    [Fact]
    public async Task AddMcpServers_DryRunWithNoManifest_DoesNotCreateManifest()
    {
        // CR-009 + Grant's PR #418 review fix: --dry-run still resolves and validates
        // the manifest/project path (including the --project-path typo-guard) before
        // returning. The contract for a fresh-but-valid directory is "dry-run must
        // succeed without writing anything" — paths still get validated, but no
        // manifest is created or modified on disk.
        var projectDir = CreateTempDir();

        var exitCode = await BuildRoot().InvokeAsync(new[]
        {
            "develop", "add-mcp-servers", "mcp_M365Copilot",
            "--project-path", projectDir, "--dry-run"
        });

        exitCode.Should().Be(0,
            because: "--dry-run must succeed even when no manifest exists yet");
        File.Exists(Path.Combine(projectDir, McpConstants.ToolingManifestFileName))
            .Should().BeFalse(because: "--dry-run must never write to disk");
    }

    [Fact]
    public async Task AddMcpServers_DryRunWithBogusProjectPath_ReturnsExitCode1()
    {
        // DevelopCommand.cs:504-515 BEFORE the --project-path typo-guard at lines
        // 520-530. A typo'd --project-path X --dry-run silently exits 0 — exactly
        // the failure the guard is supposed to prevent.
        var bogusDir = Path.Combine(Path.GetTempPath(),
            "a365-issue412-doesnotexist-dryrun-" + Guid.NewGuid().ToString("N"));
        Directory.Exists(bogusDir).Should().BeFalse(because: "the test pre-condition is a typo'd path");

        var exitCode = await BuildRoot().InvokeAsync(new[]
        {
            "develop", "add-mcp-servers", "mcp_M365Copilot",
            "--project-path", bogusDir, "--dry-run"
        });

        exitCode.Should().Be(1,
            because: "--dry-run must validate --project-path the same way real-mode does; otherwise dry-run gives users false confidence that their command would succeed when run for real");
        Directory.Exists(bogusDir).Should().BeFalse(
            because: "--dry-run must never create directories, even by accident");
    }

    [Fact]
    public async Task AddMcpServers_ExistingManifest_UpsertsServer()
    {
        // Guardrail: the happy path (manifest already exists) must continue to work.
        var projectDir = CreateTempDir();
        var manifestPath = Path.Combine(projectDir, McpConstants.ToolingManifestFileName);
        await File.WriteAllTextAsync(manifestPath, "{\"mcpServers\":[]}");

        var exitCode = await BuildRoot().InvokeAsync(new[]
        {
            "develop", "add-mcp-servers", "mcp_M365Copilot", "--project-path", projectDir
        });

        exitCode.Should().Be(0,
            because: "the pre-#412 happy path (manifest already exists) must remain green");
        (await File.ReadAllTextAsync(manifestPath)).Should().Contain("mcp_M365Copilot",
            because: "the requested server must be upserted into the existing manifest");
    }
}
