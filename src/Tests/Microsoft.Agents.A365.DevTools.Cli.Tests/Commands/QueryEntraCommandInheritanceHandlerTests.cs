// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Handler-level invocation tests for `a365 query-entra inheritance`. These pin the user-facing
/// contract of the new subcommand introduced by issue #417: a blueprint with kind=allAllowed on
/// both inheritableScopes and inheritableRoles is only "effective" if the blueprint SP also has
/// permissions actually granted. The handler must surface that distinction (effective vs broken
/// vs nothing-to-inherit) and exit with code 1 whenever any resource fails effective inheritance,
/// because a passing exit code is consumed by CI/scripted environments as "agent identities will
/// inherit correctly".
/// </summary>
public class QueryEntraCommandInheritanceHandlerTests
{
    private const string ValidBlueprintId = "33333333-3333-3333-3333-333333333333";
    private const string ValidTenantId = "44444444-4444-4444-4444-444444444444";
    private const string GraphAppId = "00000003-0000-0000-c000-000000000000";
    private const string ObservabilityAppId = "55555555-5555-5555-5555-555555555555";

    private readonly ILogger<QueryEntraCommand> _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly CommandExecutor _mockExecutor;
    private readonly GraphApiService _mockGraphApiService;
    private readonly AgentBlueprintService _mockBlueprintService;
    private readonly IBootstrapConfigResolver _mockResolver;

    public QueryEntraCommandInheritanceHandlerTests()
    {
        _mockLogger = Substitute.For<ILogger<QueryEntraCommand>>();
        _mockConfigService = Substitute.For<IConfigService>();
        var executorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = new CommandExecutor(executorLogger);
        _mockGraphApiService = Substitute.For<GraphApiService>(
            Substitute.For<ILogger<GraphApiService>>(), _mockExecutor);
        _mockBlueprintService = Substitute.ForPartsOf<AgentBlueprintService>(
            Substitute.For<ILogger<AgentBlueprintService>>(), _mockGraphApiService);
        _mockResolver = Substitute.For<IBootstrapConfigResolver>();
    }

    private Command BuildRootCommand() =>
        QueryEntraCommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockResolver);

    private void SetupResolver(Agent365Config? config)
    {
        _mockResolver.ResolveAsync(
            Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<FileInfo>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(config));
    }

    private bool LoggerReceivedContaining(LogLevel level, string fragment)
    {
        var calls = _mockLogger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(c => c.GetArguments())
            .Where(args => args.Length >= 3 && args[0] is LogLevel lvl && lvl == level)
            .Select(args => args[2]?.ToString() ?? string.Empty);
        return calls.Any(s => s.Contains(fragment, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InheritanceSubcommand_MissingBlueprintId_LogsRunSetupBlueprint_AndExitsOne()
    {
        // Arrange — resolver returns a config without AgentBlueprintId. The handler must short-circuit
        // before any Graph call, because the inheritance check is meaningless without a blueprint.
        SetupResolver(new Agent365Config { TenantId = ValidTenantId });
        var parser = new CommandLineBuilder(BuildRootCommand()).Build();

        // Act
        var exitCode = await parser.InvokeAsync("inheritance --agent-name test-agent --tenant-id " + ValidTenantId, new TestConsole());

        // Assert
        exitCode.Should().Be(1,
            because: "missing blueprint ID is a configuration error the operator must fix before the command can do useful work");
        LoggerReceivedContaining(LogLevel.Error, "Run 'a365 setup blueprint'").Should().BeTrue(
            because: "the error must name the exact remediation command — operators on a fresh checkout don't know which subcommand creates the blueprint");

        await _mockBlueprintService.DidNotReceive().ListInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InheritanceSubcommand_MissingTenantId_ExitsOne_AndDoesNotCallBlueprintService()
    {
        // Arrange — config has blueprint ID but no tenant ID; this is structurally impossible from
        // bootstrap but possible from a hand-edited config file. Handler must fail fast.
        SetupResolver(new Agent365Config { AgentBlueprintId = ValidBlueprintId });
        var parser = new CommandLineBuilder(BuildRootCommand()).Build();

        var exitCode = await parser.InvokeAsync("inheritance --agent-name test-agent", new TestConsole());

        exitCode.Should().Be(1);
        await _mockBlueprintService.DidNotReceive().ListInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InheritanceSubcommand_WhenResolverReturnsNull_ExitsOne()
    {
        // Arrange — resolver couldn't bootstrap a config (no --agent-name AND no config file on disk).
        SetupResolver(null);
        var parser = new CommandLineBuilder(BuildRootCommand()).Build();

        var exitCode = await parser.InvokeAsync("inheritance", new TestConsole());

        exitCode.Should().Be(1,
            because: "the subcommand cannot proceed without a resolved config and must surface this as a failure");
    }

    [Fact]
    public async Task InheritanceSubcommand_WhenNoEntriesConfigured_LogsWarning_AndExitsOne()
    {
        // Arrange — blueprint exists but has zero inheritablePermissions entries. This is the
        // "user ran setup blueprint but not setup permissions" state. The contract: tell them what
        // to run next and exit non-zero so scripts don't treat this as success.
        SetupResolver(new Agent365Config { TenantId = ValidTenantId, AgentBlueprintId = ValidBlueprintId });
        _mockBlueprintService.ListInheritablePermissionsAsync(
            ValidTenantId, ValidBlueprintId, Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<(string ResourceAppId, bool ScopesAllAllowed, bool RolesAllAllowed)>()));

        var parser = new CommandLineBuilder(BuildRootCommand()).Build();
        var exitCode = await parser.InvokeAsync("inheritance --agent-name test-agent", new TestConsole());

        exitCode.Should().Be(1,
            because: "a blueprint with zero inheritable permissions is broken — agent identities will inherit nothing");
        LoggerReceivedContaining(LogLevel.Warning, "No inheritable permissions configured").Should().BeTrue();
        LoggerReceivedContaining(LogLevel.Information, "a365 setup permissions").Should().BeTrue(
            because: "the warning must point operators at the command that fixes the state");
    }

    [Fact]
    public async Task InheritanceSubcommand_WhenAllAllowedAndGrantsExist_LogsOk_AndExitsZero()
    {
        // Arrange — the happy path. One resource entry, both kind=allAllowed, and the blueprint SP
        // has at least one delegated scope and one app role granted for that resource.
        SetupResolver(new Agent365Config { TenantId = ValidTenantId, AgentBlueprintId = ValidBlueprintId });
        _mockBlueprintService.ListInheritablePermissionsAsync(
            ValidTenantId, ValidBlueprintId, Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<(string, bool, bool)>
            {
                (GraphAppId, true, true)
            }));
        _mockBlueprintService.GetBlueprintSpGrantsAsync(
            ValidTenantId, ValidBlueprintId, Arg.Any<IEnumerable<string>>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<string, (string[], string[])>(StringComparer.OrdinalIgnoreCase)
            {
                [GraphAppId] = (new[] { "User.Read" }, new[] { "Application.Read.All" })
            }));

        var parser = new CommandLineBuilder(BuildRootCommand()).Build();
        var exitCode = await parser.InvokeAsync("inheritance --agent-name test-agent", new TestConsole());

        exitCode.Should().Be(0,
            because: "kind=allAllowed on both sides AND grants on the blueprint SP is the only state in which agent identities truly inherit");
        LoggerReceivedContaining(LogLevel.Information, "Scopes: OK").Should().BeTrue(
            because: "the Scopes: OK token is the documented marker operators grep for in CI logs");
        LoggerReceivedContaining(LogLevel.Information, "Roles:  OK").Should().BeTrue(
            because: "the Roles:  OK token (with the two spaces, to align with Scopes:) is the documented marker for the app-role side");
        LoggerReceivedContaining(LogLevel.Information, "Effective inheritance: OK").Should().BeTrue();
    }

    [Fact]
    public async Task InheritanceSubcommand_WhenKindAllAllowedButNoGrants_LogsNothingToInherit_AndExitsOne()
    {
        // Arrange — the regression case the new command was built to detect. The config is the
        // wildcard, but the AllPrincipals OAuth2 grant phase silently skipped (e.g. wids missing
        // on the access token) so the blueprint SP has zero granted permissions. The legacy
        // command claimed this was "OK" because it only inspected config; this command must surface
        // the real broken state.
        SetupResolver(new Agent365Config { TenantId = ValidTenantId, AgentBlueprintId = ValidBlueprintId });
        _mockBlueprintService.ListInheritablePermissionsAsync(
            ValidTenantId, ValidBlueprintId, Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<(string, bool, bool)>
            {
                (GraphAppId, true, true)
            }));
        _mockBlueprintService.GetBlueprintSpGrantsAsync(
            ValidTenantId, ValidBlueprintId, Arg.Any<IEnumerable<string>>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<string, (string[], string[])>(StringComparer.OrdinalIgnoreCase)
            {
                [GraphAppId] = (Array.Empty<string>(), Array.Empty<string>())
            }));

        var parser = new CommandLineBuilder(BuildRootCommand()).Build();
        var exitCode = await parser.InvokeAsync("inheritance --agent-name test-agent", new TestConsole());

        exitCode.Should().Be(1,
            because: "kind=allAllowed with zero grants on the blueprint SP means agent identities inherit nothing — this is the exact broken state the command exists to detect");
        LoggerReceivedContaining(LogLevel.Warning, "no delegated permissions granted on blueprint SP").Should().BeTrue(
            because: "the warning must explicitly say where the grants are missing so the operator can correlate with their setup logs");
        LoggerReceivedContaining(LogLevel.Warning, "inheritance has nothing to inherit").Should().BeTrue(
            because: "this exact phrase appears in docs/issue threads — operators searching for it must find this output");
        LoggerReceivedContaining(LogLevel.Information, "a365 setup requirements").Should().BeTrue(
            because: "the remediation hint must mention setup requirements — the most common cause is a missing wids claim and that's what setup requirements checks");
    }

    [Fact]
    public async Task InheritanceSubcommand_WhenLegacyEnumeratedEntry_LogsBroken_AndExitsOne()
    {
        // Arrange — older blueprints have inheritablePermissions entries that enumerate specific
        // scopes/roles instead of using kind=allAllowed. These break under the new MAC visibility
        // contract. The command must flag them as BROKEN and direct the operator to re-run setup
        // permissions, which now writes kind=allAllowed everywhere.
        SetupResolver(new Agent365Config { TenantId = ValidTenantId, AgentBlueprintId = ValidBlueprintId });
        _mockBlueprintService.ListInheritablePermissionsAsync(
            ValidTenantId, ValidBlueprintId, Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<(string, bool, bool)>
            {
                // ScopesAllAllowed=false simulates a legacy enumerated entry on the scopes side.
                (GraphAppId, false, true)
            }));
        _mockBlueprintService.GetBlueprintSpGrantsAsync(
            ValidTenantId, ValidBlueprintId, Arg.Any<IEnumerable<string>>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<string, (string[], string[])>(StringComparer.OrdinalIgnoreCase)
            {
                [GraphAppId] = (new[] { "User.Read" }, new[] { "Application.Read.All" })
            }));

        var parser = new CommandLineBuilder(BuildRootCommand()).Build();
        var exitCode = await parser.InvokeAsync("inheritance --agent-name test-agent", new TestConsole());

        exitCode.Should().Be(1,
            because: "a legacy enumerated entry will not propagate scopes correctly under the wildcard inheritance model — the operator must reconcile it");
        LoggerReceivedContaining(LogLevel.Warning, "kind is not allAllowed").Should().BeTrue(
            because: "the warning must name the underlying problem (kind), not just say 'something is wrong'");
        LoggerReceivedContaining(LogLevel.Warning, "Effective inheritance: BROKEN").Should().BeTrue(
            because: "BROKEN is the documented severity tier for non-wildcard entries — operators map this token to dashboards");
    }

    [Fact]
    public async Task InheritanceSubcommand_WhenMultipleResourcesAndOneBroken_ExitsOne_AndReportsCount()
    {
        // Arrange — two resources: one fully effective, one with kind=allAllowed but no grants.
        // The command summary must show "1 of 2 effective" and exit 1 because partial effectiveness
        // is still a broken state from the perspective of any agent identity that uses the missing
        // resource.
        SetupResolver(new Agent365Config { TenantId = ValidTenantId, AgentBlueprintId = ValidBlueprintId });
        _mockBlueprintService.ListInheritablePermissionsAsync(
            ValidTenantId, ValidBlueprintId, Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<(string, bool, bool)>
            {
                (GraphAppId, true, true),
                (ObservabilityAppId, true, true)
            }));
        _mockBlueprintService.GetBlueprintSpGrantsAsync(
            ValidTenantId, ValidBlueprintId, Arg.Any<IEnumerable<string>>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<string, (string[], string[])>(StringComparer.OrdinalIgnoreCase)
            {
                [GraphAppId] = (new[] { "User.Read" }, new[] { "Application.Read.All" }),
                [ObservabilityAppId] = (Array.Empty<string>(), Array.Empty<string>())
            }));

        var parser = new CommandLineBuilder(BuildRootCommand()).Build();
        var exitCode = await parser.InvokeAsync("inheritance --agent-name test-agent", new TestConsole());

        exitCode.Should().Be(1,
            because: "any resource with broken inheritance breaks the agent identity for that resource — the exit code must reflect partial failure");
        LoggerReceivedContaining(LogLevel.Information, "1 of 2 resource(s) have effective inheritance").Should().BeTrue(
            because: "the summary line is the single most important diagnostic — it gives operators a structured count they can grep for");
    }

    [Fact]
    public async Task InheritanceSubcommand_WhenBlueprintServiceThrows_LogsError_AndExitsOne()
    {
        // Arrange — Graph read can fail (transient network, token cache issue, missing scope).
        // The handler must catch and surface, not crash the CLI with an unhandled exception that
        // dumps a stack trace.
        SetupResolver(new Agent365Config { TenantId = ValidTenantId, AgentBlueprintId = ValidBlueprintId });
        _mockBlueprintService.ListInheritablePermissionsAsync(
            ValidTenantId, ValidBlueprintId, Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<List<(string ResourceAppId, bool ScopesAllAllowed, bool RolesAllAllowed)>>>(_ =>
                throw new InvalidOperationException("Graph API failure"));

        var parser = new CommandLineBuilder(BuildRootCommand()).Build();
        var exitCode = await parser.InvokeAsync("inheritance --agent-name test-agent", new TestConsole());

        exitCode.Should().Be(1,
            because: "every failure path must set exit code 1 — letting an unhandled exception escape would corrupt the System.CommandLine exit-code contract");
        LoggerReceivedContaining(LogLevel.Error, "Failed to query inheritable permissions").Should().BeTrue(
            because: "the error message must clearly identify the operation that failed so operators can correlate with logs");
    }
}
