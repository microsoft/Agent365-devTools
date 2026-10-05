// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Issue #412 follow-up — the develop-command flow leaked the local <c>a365.config.json</c>
/// into the global config directory on every <c>LoadAsync</c>, and the bootstrap resolver
/// fell back to reading <c>a365.generated.config.json</c> from the same global directory
/// when no local copy existed. Both behaviors are leftover from before PR #385 made commands
/// config-file-optional. These tests pin the new policy: the global config directory is
/// never read from nor written to by the config layer.
///
/// Tests manipulate the <c>LocalAppData</c> / <c>XDG_CONFIG_HOME</c> / <c>HOME</c> env vars
/// and <see cref="Environment.CurrentDirectory"/>, so they must not run in parallel.
/// </summary>
[CollectionDefinition("GlobalConfigDirectoryCleanupTests", DisableParallelization = true)]
public class GlobalConfigDirectoryCleanupTestsCollection { }

[Collection("GlobalConfigDirectoryCleanupTests")]
public class GlobalConfigDirectoryCleanupTests : IDisposable
{
    private readonly string _localCwd;
    private readonly string _fakeGlobalRoot;
    private readonly string _originalCwd;
    private readonly string? _originalLocalAppData;
    private readonly string? _originalXdgConfigHome;
    private readonly string? _originalHome;

    public GlobalConfigDirectoryCleanupTests()
    {
        _originalCwd = Directory.GetCurrentDirectory();
        _originalLocalAppData = Environment.GetEnvironmentVariable("LocalAppData");
        _originalXdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        _originalHome = Environment.GetEnvironmentVariable("HOME");

        var root = Path.Combine(Path.GetTempPath(), "a365-global-cleanup-" + Guid.NewGuid().ToString("N"));
        _localCwd = Path.Combine(root, "project");
        _fakeGlobalRoot = Path.Combine(root, "fake-global");
        Directory.CreateDirectory(_localCwd);
        Directory.CreateDirectory(_fakeGlobalRoot);

        // Route GetGlobalConfigDirectory() to our isolated fake-global root on every platform.
        Environment.SetEnvironmentVariable("LocalAppData", _fakeGlobalRoot);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _fakeGlobalRoot);
        Environment.SetEnvironmentVariable("HOME", _fakeGlobalRoot);

        Directory.SetCurrentDirectory(_localCwd);
    }

    public void Dispose()
    {
        // Restore each piece of process-wide state independently so a failure in one
        // does not leak the rest into other test classes in this assembly.
        try { Directory.SetCurrentDirectory(_originalCwd); } catch { /* best-effort */ }
        try { Environment.SetEnvironmentVariable("LocalAppData", _originalLocalAppData); } catch { /* best-effort */ }
        try { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalXdgConfigHome); } catch { /* best-effort */ }
        try { Environment.SetEnvironmentVariable("HOME", _originalHome); } catch { /* best-effort */ }

        try
        {
            var root = Path.GetDirectoryName(_localCwd);
            if (root != null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// Returns the directory <see cref="ConfigService.GetGlobalConfigDirectory"/> resolves to
    /// under the env-var overrides applied in the constructor. Computed identically to the
    /// production helper so the test pins the same path the code under test would use.
    /// </summary>
    private string ResolvedGlobalConfigDir() => ConfigService.GetGlobalConfigDirectory();

    [Fact]
    public async Task LoadAsync_HasNoSideEffectsOutsideCwd()
    {
        // Issue #412 follow-up: LoadAsync previously mirrored both a365.config.json and
        // a365.generated.config.json into the machine-global folder. The new policy is that
        // config files live only in the project directory. The global folder is reserved for
        // caches (MSAL tokens, version-check cache) and logs.
        var localConfigPath = Path.Combine(_localCwd, "a365.config.json");
        await File.WriteAllTextAsync(localConfigPath, """
        {
          "tenantId": "11111111-1111-1111-1111-111111111111",
          "clientAppId": "22222222-2222-2222-2222-222222222222",
          "agentIdentityDisplayName": "TestAgent"
        }
        """);

        var configService = new ConfigService(NullLogger<ConfigService>.Instance);

        var loaded = await configService.LoadAsync("a365.config.json");

        loaded.Should().NotBeNull();
        loaded.TenantId.Should().Be("11111111-1111-1111-1111-111111111111");

        var globalDir = ResolvedGlobalConfigDir();
        File.Exists(Path.Combine(globalDir, "a365.config.json")).Should().BeFalse(
            because: "LoadAsync must not mirror the static config into the global directory — the global folder is for caches and logs only");
        File.Exists(Path.Combine(globalDir, "a365.generated.config.json")).Should().BeFalse(
            because: "LoadAsync must not mirror the generated config into the global directory either — neither file may ever appear outside the project");
    }

    [Fact]
    public async Task SaveStateAsync_HasNoSideEffectsOutsideCwd()
    {
        // CR-003: pin the production invariant at ConfigService.cs:249-250
        // ("Global directory fallback has been removed — config is always project-local").
        // Without this test a future contributor could re-introduce a global write while
        // reading that comment as historical context.
        var configService = new ConfigService(NullLogger<ConfigService>.Instance);

        await configService.SaveStateAsync(
            new Agent365Config { TenantId = "11111111-1111-1111-1111-111111111111" });

        var globalGenerated = Path.Combine(ResolvedGlobalConfigDir(), "a365.generated.config.json");
        File.Exists(globalGenerated).Should().BeFalse(
            because: "SaveStateAsync must write only to CWD — the global directory is for caches, not config state");

        var localGenerated = Path.Combine(_localCwd, "a365.generated.config.json");
        File.Exists(localGenerated).Should().BeTrue(
            because: "the project-local generated config must still be written");
    }

    [Fact]
    public async Task ResolveAsync_InCleanupMode_DoesNotInheritResourceIdsFromOutsideCwd()
    {
        // Issue #412 follow-up: BuildBootstrapConfigForCleanupAsync used to consult
        // a365.generated.config.json in the global folder when no local copy existed. Under
        // the new policy, only the project's own state participates in cleanup decisions —
        // an `a365 cleanup --agent-name X` in a fresh directory must not pull resource IDs
        // from a leftover global file written by a previous CLI version or another project.
        var poisonedBlueprintId = Guid.NewGuid().ToString();
        const string poisonedRegistrationId = "REG_FROM_GLOBAL_SHOULD_NOT_LEAK";
        var globalGeneratedDir = ResolvedGlobalConfigDir();
        Directory.CreateDirectory(globalGeneratedDir);
        var globalGeneratedPath = Path.Combine(globalGeneratedDir, "a365.generated.config.json");
        // Casing here mirrors exactly what BootstrapConfigResolver.BuildBootstrapConfigForCleanupAsync
        // reads via SetupHelpers.GetJsonString (which is case-sensitive). Most fields are camelCase,
        // but "AgenticAppId" is PascalCase because the production reader at BootstrapConfigResolver.cs
        // line ~336 reads that exact key — legacy CLI versions wrote it that way and the reader
        // preserves compatibility. If a future Copilot/reviewer flags this as inconsistent, the
        // answer is: the casing is deliberate and must match the reader, not the typical config schema.
        await File.WriteAllTextAsync(globalGeneratedPath, $$"""
        {
          "agentBlueprintId": "{{poisonedBlueprintId}}",
          "agentRegistrationId": "{{poisonedRegistrationId}}",
          "AgenticAppId": "AGENTIC_FROM_GLOBAL_SHOULD_NOT_LEAK",
          "agentBlueprintServicePrincipalObjectId": "SP_FROM_GLOBAL_SHOULD_NOT_LEAK"
        }
        """);

        // Sanity: CWD must have no local generated config so the only file the
        // pre-fix global fallback would have found is the poisoned global one —
        // if the fallback regresses, this is the file that would leak through.
        File.Exists(Path.Combine(_localCwd, "a365.generated.config.json")).Should().BeFalse();

        // Mock the resolver's dependencies so Mode 1 (agent-name + cleanup) reaches Step 4.
        var configService = Substitute.For<IConfigService>();
        var executorLogger = Substitute.For<ILogger<CommandExecutor>>();
        var executor = Substitute.For<CommandExecutor>(executorLogger);
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult
            {
                ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty
            }));

        var graphApiService = Substitute.ForPartsOf<GraphApiService>(
            Substitute.For<ILogger<GraphApiService>>(),
            executor,
            (Func<Task<string?>>)(() => Task.FromResult<string?>(null)));
        graphApiService.LookupServicePrincipalByAppIdWithResponseAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                StatusCode = 200
            });

        // Graph resolves the blueprint to the SAME ID as the poisoned global file would
        // carry — so if the pre-fix merge logic at BuildBootstrapConfigForCleanupAsync
        // were still active, it would accept the global file and pull its resource IDs
        // into the returned config. The post-fix code never consults the global file at
        // all; this assertion proves the regression cannot reappear silently.
        graphApiService.FindApplicationByDisplayNameAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(poisonedBlueprintId));

        var resolver = new BootstrapConfigResolver(
            configService, executor, graphApiService, NullLoggerFactory.Instance);

        var configFile = new FileInfo(Path.Combine(_localCwd, "a365.config.json"));
        var result = await resolver.ResolveAsync(
            agentName: "CleanupAgent",
            tenantIdFlag: "explicit-tenant",
            configFile,
            isCleanupMode: true);

        result.Should().NotBeNull(because: "cleanup mode must still resolve from Entra");
        result!.AgentBlueprintId.Should().Be(poisonedBlueprintId,
            because: "the blueprint ID is the authoritative Entra value, independent of any on-disk file");
        result.AgentRegistrationId.Should().BeNull(
            because: "AgentRegistrationId must not leak in from outside CWD — only the project-local generated config is consulted");
        result.AgenticAppId.Should().BeNull(
            because: "AgenticAppId must not leak in from outside CWD");
        result.AgentBlueprintServicePrincipalObjectId.Should().BeNull(
            because: "AgentBlueprintServicePrincipalObjectId must not leak in from outside CWD");
    }

    [Fact]
    public async Task CleanupCommand_BuildBootstrapConfigForCleanupAsync_DoesNotInheritResourceIdsFromOutsideCwd()
    {
        // The global-fallback pattern the PR removed from
        // BootstrapConfigResolver.BuildBootstrapConfigForCleanupAsync still exists as
        // a near-identical duplicate in CleanupCommand.BuildBootstrapConfigForCleanupAsync
        // (the fallback path used when resolver == null). The original PR fixed one of
        // two copies; this test pins the same invariant on the duplicate so the fix
        // can land symmetrically (or the duplicate can be deleted, in which case this
        // test will fail with a clear "method not found" and can be removed too).
        //
        // Uses reflection because the method is `private static`. If the production code
        // is refactored to delete or rename the method, the BindingFlags lookup will
        // surface the change loudly rather than silently passing.
        var poisonedBlueprintId = Guid.NewGuid().ToString();
        const string poisonedRegistrationId = "REG_FROM_GLOBAL_SHOULD_NOT_LEAK_VIA_CLEANUP";
        var globalGeneratedDir = ResolvedGlobalConfigDir();
        Directory.CreateDirectory(globalGeneratedDir);
        var globalGeneratedPath = Path.Combine(globalGeneratedDir, "a365.generated.config.json");
        // Casing intentional: matches the duplicate reader's case-sensitive
        // SetupHelpers.GetJsonString lookups. "AgenticAppId" is PascalCase by design
        // — see the ResolveAsync_InCleanupMode test above for the full rationale.
        await File.WriteAllTextAsync(globalGeneratedPath, $$"""
        {
          "agentBlueprintId": "{{poisonedBlueprintId}}",
          "agentRegistrationId": "{{poisonedRegistrationId}}",
          "AgenticAppId": "AGENTIC_FROM_GLOBAL_SHOULD_NOT_LEAK_VIA_CLEANUP",
          "agentBlueprintServicePrincipalObjectId": "SP_FROM_GLOBAL_SHOULD_NOT_LEAK_VIA_CLEANUP"
        }
        """);
        File.Exists(Path.Combine(_localCwd, "a365.generated.config.json")).Should().BeFalse(
            because: "the CWD must be clean so the only available source of poison is the global file");

        var executorLogger = Substitute.For<ILogger<CommandExecutor>>();
        var executor = Substitute.For<CommandExecutor>(executorLogger);
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult
            {
                ExitCode = 0, StandardOutput = "explicit-tenant", StandardError = string.Empty
            }));

        var graphApiService = Substitute.ForPartsOf<GraphApiService>(
            Substitute.For<ILogger<GraphApiService>>(),
            executor,
            (Func<Task<string?>>)(() => Task.FromResult<string?>(null)));
        graphApiService.LookupServicePrincipalByAppIdWithResponseAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                StatusCode = 200
            });
        // The cleanup duplicate calls FindApplicationByDisplayNameAsync WITHOUT a
        // CancellationToken argument (see CleanupCommand.cs ~line 1356-1357), so the
        // 2-arg overload must be stubbed in addition to the 3-arg one for safety.
        graphApiService.FindApplicationByDisplayNameAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(poisonedBlueprintId));
        graphApiService.FindApplicationByDisplayNameAsync(
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult<string?>(poisonedBlueprintId));

        var cleanupLogger = Substitute.For<ILogger<CleanupCommand>>();

        var method = typeof(CleanupCommand).GetMethod(
            "BuildBootstrapConfigForCleanupAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull(
            because: "the private static fallback duplicate must exist for this regression test to verify; if a later PR collapses or deletes the duplicate, delete this test too");

        var task = (Task<Agent365Config?>)method!.Invoke(null, new object?[]
        {
            "CleanupAgent",
            "explicit-tenant",
            executor,
            graphApiService,
            cleanupLogger
        })!;
        var result = await task;

        result.Should().NotBeNull(
            because: "the fallback must still build a valid config from Entra even when no local generated file exists");
        result!.AgentBlueprintId.Should().Be(poisonedBlueprintId,
            because: "the blueprint ID is the authoritative Entra value, independent of any on-disk file");
        result.AgentRegistrationId.Should().BeNull(
            because: "AgentRegistrationId must not leak in from outside CWD via the cleanup duplicate — only the project-local generated config may be consulted");
        result.AgenticAppId.Should().BeNull(
            because: "AgenticAppId must not leak in from outside CWD via the cleanup duplicate");
        result.AgentBlueprintServicePrincipalObjectId.Should().BeNull(
            because: "AgentBlueprintServicePrincipalObjectId must not leak in from outside CWD via the cleanup duplicate");
    }
}
