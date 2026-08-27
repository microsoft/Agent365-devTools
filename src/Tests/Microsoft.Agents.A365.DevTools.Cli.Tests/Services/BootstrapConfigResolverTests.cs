// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Unit tests for <see cref="BootstrapConfigResolver"/> covering all three resolution modes.
/// </summary>
public class BootstrapConfigResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfigService _configService;
    private readonly CommandExecutor _executor;
    private readonly GraphApiService _graphApiService;
    private readonly ILoggerFactory _loggerFactory;

    public BootstrapConfigResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BootstrapResolverTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _configService = Substitute.For<IConfigService>();
        var executorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _executor = Substitute.For<CommandExecutor>(executorLogger);
        _executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty }));

        _graphApiService = Substitute.ForPartsOf<GraphApiService>(
            Substitute.For<ILogger<GraphApiService>>(),
            _executor,
            (Func<Task<string?>>)(() => Task.FromResult<string?>(null)));
        _graphApiService.LookupServicePrincipalByAppIdWithResponseAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                StatusCode = 200
            });

        _loggerFactory = NullLoggerFactory.Instance;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* temp dir cleanup is best-effort */ }
    }

    private IBootstrapConfigResolver CreateResolver() =>
        new BootstrapConfigResolver(_configService, _executor, _graphApiService, _loggerFactory);

    // ── Mode 2: config file present, no agent-name ────────────────────────────

    [Fact]
    public async Task ResolveAsync_WhenConfigFileExists_LoadsFromDisk()
    {
        var configFile = new FileInfo(Path.Combine(_tempDir, "a365.config.json"));
        File.WriteAllText(configFile.FullName, "{}"); // just needs to exist on disk

        var expected = new Agent365Config { TenantId = "loaded-tenant" };
        _configService.LoadAsync(configFile.FullName).Returns(expected);

        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(agentName: null, tenantIdFlag: null, configFile);

        result.Should().BeSameAs(expected,
            because: "when the config file exists and no agent-name is supplied, the file must be loaded");
    }

    [Fact]
    public async Task ResolveAsync_WhenConfigExistsForDifferentTenant_BacksUpAndReturnsNull()
    {
        var configFile = new FileInfo(Path.Combine(_tempDir, "a365.config.json"));
        File.WriteAllText(configFile.FullName, """{"tenantId": "old-tenant"}""");

        // az account show returns a different tenant
        _executor.ExecuteAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("account show")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult
            {
                ExitCode = 0,
                StandardOutput = "new-tenant",
                StandardError = string.Empty
            }));

        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(agentName: null, tenantIdFlag: null, configFile);

        result.Should().BeNull(
            because: "when the config belongs to a different tenant, setup must start fresh — caller receives null to exit cleanly");
        File.Exists(configFile.FullName).Should().BeFalse(
            because: "the stale config must be backed up and removed before the caller proceeds");
        Directory.GetFiles(_tempDir, "a365.config.json.bak.*").Should().HaveCountGreaterThan(0,
            because: "the stale config must be backed up with a timestamp suffix");
    }

    [Fact]
    public async Task ResolveAsync_WhenConfigExistsAndTenantMatches_LoadsNormally()
    {
        var configFile = new FileInfo(Path.Combine(_tempDir, "a365.config.json"));
        File.WriteAllText(configFile.FullName, """{"tenantId": "current-tenant"}""");

        _executor.ExecuteAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("account show")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult
            {
                ExitCode = 0,
                StandardOutput = "current-tenant",
                StandardError = string.Empty
            }));

        var expected = new Agent365Config { TenantId = "current-tenant" };
        _configService.LoadAsync(configFile.FullName).Returns(expected);

        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(agentName: null, tenantIdFlag: null, configFile);

        result.Should().BeSameAs(expected,
            because: "when the tenant matches, the config must be loaded without backup");
        File.Exists(configFile.FullName).Should().BeTrue(
            because: "the config must not be touched when tenants match");
    }

    [Fact]
    public async Task ResolveAsync_WhenAzCliUnavailable_LoadsConfigNormally()
    {
        var configFile = new FileInfo(Path.Combine(_tempDir, "a365.config.json"));
        File.WriteAllText(configFile.FullName, "{}");

        // Default executor stub returns StandardOutput = string.Empty — simulates az CLI not signed in
        // or unavailable. Tenant check is skipped; config must load normally.
        var expected = new Agent365Config { TenantId = "some-tenant" };
        _configService.LoadAsync(configFile.FullName).Returns(expected);

        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(agentName: null, tenantIdFlag: null, configFile);

        result.Should().BeSameAs(expected,
            because: "when az CLI is unavailable, the tenant check is skipped and config is loaded normally");
        File.Exists(configFile.FullName).Should().BeTrue(
            because: "the config must not be backed up when the tenant check could not run");
    }

    // ── Mode 3: neither file nor agent-name ──────────────────────────────────

    [Fact]
    public async Task ResolveAsync_WhenConfigFileMissingAndNoAgentName_ReturnsNull()
    {
        var configFile = new FileInfo(Path.Combine(_tempDir, "a365.config.json"));
        // deliberately not created

        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(agentName: null, tenantIdFlag: null, configFile);

        result.Should().BeNull(
            because: "when neither the config file nor --agent-name is supplied, resolution cannot proceed");
    }

    private static readonly string _validClientAppId = "12345678-1234-1234-1234-123456789abc";

    // ── Mode 1: agent-name with explicit tenant-id ────────────────────────────

    [Fact]
    public async Task ResolveAsync_WithAgentNameAndExplicitTenantId_ReturnsBootstrapConfig()
    {
        var configFile = new FileInfo(Path.Combine(_tempDir, "a365.config.json"));
        // file does not exist — resolver must not try to load it

        _graphApiService.FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(_validClientAppId));

        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(
            agentName: "MyAgent",
            tenantIdFlag: "explicit-tenant",
            configFile);

        result.Should().NotBeNull(because: "bootstrap mode with --tenant-id must produce a config");
        result!.TenantId.Should().Be("explicit-tenant",
            because: "the resolved tenant ID must match the supplied --tenant-id flag");
        result.AgentIdentityDisplayName.Should().Be("MyAgent Identity",
            because: "the identity display name is derived from --agent-name");
        result.AgentBlueprintDisplayName.Should().Be("MyAgent Blueprint",
            because: "the blueprint display name is derived from --agent-name");
    }

    // ── Mode 1: agent-name + az CLI tenant detection ──────────────────────────

    [Fact]
    public async Task ResolveAsync_WithAgentNameAndAzCliTenant_ReturnsBootstrapConfig()
    {
        var configFile = new FileInfo(Path.Combine(_tempDir, "a365.config.json"));

        // Stub executor to return a tenant ID from "az account show --query tenantId -o tsv"
        _executor.ExecuteAsync("az", Arg.Is<string>(s => s.Contains("account show")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult
            {
                ExitCode = 0,
                StandardOutput = "az-tenant-id",
                StandardError = string.Empty
            }));

        _graphApiService.FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(_validClientAppId));

        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(
            agentName: "AzCliAgent",
            tenantIdFlag: null,   // no explicit tenant — must detect from az CLI
            configFile);

        result.Should().NotBeNull(because: "bootstrap mode via az CLI tenant detection must produce a config");
        result!.TenantId.Should().Be("az-tenant-id",
            because: "the tenant must be detected from az account show output");
    }

    // ── CheckAndBackupStaleConfigAsync ───────────────────────────────────────

    [Fact]
    public async Task CheckAndBackupStaleConfigAsync_WhenTenantMismatch_BacksUpAndReturnsTrue()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        File.WriteAllText(configPath, """{"tenantId": "old-tenant"}""");

        _executor.ExecuteAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("account show")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult
            {
                ExitCode = 0,
                StandardOutput = "new-tenant",
                StandardError = string.Empty
            }));

        var resolver = CreateResolver();
        var result = await resolver.CheckAndBackupStaleConfigAsync(configPath);

        result.Should().BeTrue(
            because: "when the current tenant differs from the config tenant, the config is backed up and the caller must start fresh");
        File.Exists(configPath).Should().BeFalse(
            because: "the stale config file must be removed after backup");
        Directory.GetFiles(_tempDir, "a365.config.json.bak.*").Should().HaveCountGreaterThan(0,
            because: "the stale config must be preserved as a timestamped backup");
    }

    [Fact]
    public async Task CheckAndBackupStaleConfigAsync_WhenAzCliUnavailable_ReturnsFalse()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        File.WriteAllText(configPath, """{"tenantId": "some-tenant"}""");

        // Default stub returns StandardOutput = string.Empty — simulates az CLI unavailable
        var resolver = CreateResolver();
        var result = await resolver.CheckAndBackupStaleConfigAsync(configPath);

        result.Should().BeFalse(
            because: "when az CLI is unavailable, the tenant check is skipped and the caller proceeds normally");
        File.Exists(configPath).Should().BeTrue(
            because: "the config must not be backed up when the tenant check could not run");
    }

    // ── Cleanup mode: blueprint ID resolved from Entra ────────────────────────

    [Fact]
    public async Task ResolveAsync_InCleanupMode_PopulatesBlueprintIdFromEntra()
    {
        var configFile = new FileInfo(Path.Combine(_tempDir, "a365.config.json"));
        var blueprintId = Guid.NewGuid().ToString();

        // Graph lookup returns blueprint ID for the cleanup agent
        _graphApiService.FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(blueprintId));

        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(
            agentName: "CleanupAgent",
            tenantIdFlag: "cleanup-tenant",
            configFile,
            isCleanupMode: true);

        result.Should().NotBeNull(because: "cleanup mode must return a config when agent name is provided");
        result!.AgentBlueprintId.Should().Be(blueprintId,
            because: "the blueprint ID must be populated from the Entra lookup in cleanup mode");
    }
}
