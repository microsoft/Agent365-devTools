// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Tests for <see cref="BatchPermissionsOrchestrator.EnsureMissingResourceSpsAsync"/> and
/// the two URL/command builders the helper depends on.
///
/// <para>
/// Issue #429 history: the first attempt used per-app <c>/v2.0/adminconsent</c> with
/// <c>{appId}/.default</c> — fails with AADSTS65003 ("first party token-to-self") on the
/// MCP audiences. The helper now shells out to <c>az ad sp create --id {appId}</c> using
/// the operator's GA-privileged az login, parses the returned JSON for the SP id, and
/// trusts az when an id is present (no Graph re-poll). When the operator declines, az
/// fails, the GUID guard rejects, or <c>--skip-sp-provisioning</c> is set, the helper
/// records a <see cref="MissingSpAction"/> on <see cref="SetupResults"/> so the setup
/// summary's Action Required block surfaces both the az command AND the per-SP
/// blueprint-as-client consent URL — together they are a complete recovery without
/// re-running <c>a365 setup all</c>.
/// </para>
///
/// <para>
/// Tests that exercise the helper set <see cref="BatchPermissionsOrchestrator.BypassSpProvisioningForTests"/>
/// to <c>false</c> via a try/finally so they do not leak state into other tests. The default
/// is <c>false</c> so the helper runs in production; tests for the broader
/// <c>ConfigureAllPermissionsAsync</c> flow flip it to <c>true</c> in their setup.
/// </para>
/// </summary>
[Collection("Sequential")]
public class BatchPermissionsOrchestratorMissingSpTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string BlueprintAppId = "22222222-2222-2222-2222-222222222222";
    private const string MailMcpAppId = "16b1878d-62c7-4009-aa25-68989d63bbad";
    private const string MailMcpSpObjectId = "96f7de40-d3bb-49e1-8358-37909ebb5bab";
    private const string TeamsMcpAppId = "ce5029ee-c1d3-45c0-bdcc-efb5a4245687";

    private readonly GraphApiService _graph = Substitute.For<GraphApiService>();
    private readonly ILogger _logger = NullLogger.Instance;
    private readonly CommandExecutor _executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

    [Fact]
    public async Task EmptyMissingSpecs_NoOpAndNoGraphOrExecutorCalls()
    {
        using var bypass = TemporarilyDisableSpProvisioningBypass();
        var resolvedSpAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await BatchPermissionsOrchestrator.EnsureMissingResourceSpsAsync(
            _graph, TenantId, BlueprintAppId,
            missingSpecs: Array.Empty<ResourcePermissionSpec>(),
            resolvedSpAppIds: resolvedSpAppIds,
            permScopes: Array.Empty<string>(),
            skipSpProvisioning: false,
            _logger,
            setupResults: null,
            ct: CancellationToken.None,
            commandExecutor: _executor);

        resolvedSpAppIds.Should().BeEmpty();
        await _graph.DidNotReceive().LookupServicePrincipalByAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
        await _executor.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreflightFindsSp_AddsToResolvedSetAndDoesNotRunAz()
    {
        using var bypass = TemporarilyDisableSpProvisioningBypass();

        _graph
            .LookupServicePrincipalByAppIdAsync(TenantId, MailMcpAppId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(MailMcpSpObjectId));

        var resolvedSpAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var setupResults = new SetupResults();
        var missing = new[]
        {
            new ResourcePermissionSpec(MailMcpAppId, "Work IQ Mail MCP", new[] { "Tools.ListInvoke.All" }, SetInheritable: true)
        };

        await BatchPermissionsOrchestrator.EnsureMissingResourceSpsAsync(
            _graph, TenantId, BlueprintAppId, missing, resolvedSpAppIds,
            permScopes: Array.Empty<string>(),
            skipSpProvisioning: false,
            _logger,
            setupResults: setupResults,
            ct: CancellationToken.None,
            commandExecutor: _executor);

        resolvedSpAppIds.Should().Contain(MailMcpAppId,
            because: "the pre-flight Graph lookup found the SP — the operator must have consented to it between Phase 1 and now, so the helper records it and skips the az shell-out");
        setupResults.MissingSpActions.Should().BeEmpty(
            because: "the resource was successfully resolved without any operator intervention — no Action Required entry needed");
        await _executor.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SkipSpProvisioning_True_RecordsMissingSpActionAndDoesNotRunAz()
    {
        using var bypass = TemporarilyDisableSpProvisioningBypass();

        _graph
            .LookupServicePrincipalByAppIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(null));

        var resolvedSpAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var setupResults = new SetupResults();
        var missing = new[]
        {
            new ResourcePermissionSpec(TeamsMcpAppId, "Work IQ Teams MCP", new[] { "Tools.ListInvoke.All" }, SetInheritable: true)
        };

        await BatchPermissionsOrchestrator.EnsureMissingResourceSpsAsync(
            _graph, TenantId, BlueprintAppId, missing, resolvedSpAppIds,
            permScopes: Array.Empty<string>(),
            skipSpProvisioning: true,
            _logger,
            setupResults: setupResults,
            ct: CancellationToken.None,
            commandExecutor: _executor);

        resolvedSpAppIds.Should().BeEmpty(
            because: "with --skip-sp-provisioning set the helper must not provision anything in-line");
        await _executor.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        setupResults.MissingSpActions.Should().ContainSingle(a => a.ResourceAppId == TeamsMcpAppId,
            because: "the operator needs the recovery steps in the Action Required block — moving from Warnings to MissingSpActions is the whole point of the rework");
        var entry = setupResults.MissingSpActions.Single();
        entry.AzCreateCommand.Should().Be($"az ad sp create --id {TeamsMcpAppId}",
            because: "the recovery's step 1 is the same az command the helper would have run interactively — single source of truth for the format");
        entry.PerSpConsentUrl.Should().Contain($"client_id={BlueprintAppId}",
            because: "step 2 grants the BLUEPRINT consent for the resource scope — using the resource as client would hit AADSTS65003 'first party token-to-self' for these MCP apps");
        entry.PerSpConsentUrl.Should().Contain(Uri.EscapeDataString($"{TeamsMcpAppId}/Tools.ListInvoke.All"),
            because: "the scope param targets the resource SP that step 1 just created");
        setupResults.Warnings.Should().BeEmpty(
            because: "the rework moved missing-SP messaging out of the noisy main-output Warnings block and into the focused Action Required block at the end");
    }

    [Fact]
    public async Task NullCommandExecutor_FallsBackToWarningPathAndDoesNotPrompt()
    {
        // Without an executor the helper has no way to shell out to az; it must behave the
        // same as --skip-sp-provisioning: record the Action Required entry, no prompts,
        // no provisioning.
        using var bypass = TemporarilyDisableSpProvisioningBypass();

        _graph
            .LookupServicePrincipalByAppIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(null));

        var confirmer = Substitute.For<IConfirmationProvider>();
        var resolvedSpAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var setupResults = new SetupResults();
        var missing = new[]
        {
            new ResourcePermissionSpec(TeamsMcpAppId, "Work IQ Teams MCP", new[] { "Tools.ListInvoke.All" }, SetInheritable: true)
        };

        await BatchPermissionsOrchestrator.EnsureMissingResourceSpsAsync(
            _graph, TenantId, BlueprintAppId, missing, resolvedSpAppIds,
            permScopes: Array.Empty<string>(),
            skipSpProvisioning: false,
            _logger,
            setupResults: setupResults,
            ct: CancellationToken.None,
            commandExecutor: null,
            confirmationProvider: confirmer);

        resolvedSpAppIds.Should().BeEmpty();
        setupResults.MissingSpActions.Should().ContainSingle(a => a.ResourceAppId == TeamsMcpAppId);
        // No prompt fires — there is no executor to provision anyway, so a confirmation
        // would be misleading. (NSubstitute does not accept a 'because' on Received().)
        await confirmer.DidNotReceive().ConfirmAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task ConfirmationProviderReturnsFalse_RecordsActionAndDoesNotRunAz()
    {
        // Interactive path with a confirmation provider that declines the per-SP prompt.
        // The helper must respect that: no az shell-out, MissingSpActions populated, appId
        // NOT added to resolvedSpAppIds. Mirrors what an operator typing 'n' would produce.
        using var bypass = TemporarilyDisableSpProvisioningBypass();

        _graph
            .LookupServicePrincipalByAppIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(null));

        var declining = Substitute.For<IConfirmationProvider>();
        declining.ConfirmAsync(Arg.Any<string>()).Returns(Task.FromResult(false));

        var resolvedSpAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var setupResults = new SetupResults();
        var missing = new[]
        {
            new ResourcePermissionSpec(TeamsMcpAppId, "Work IQ Teams MCP", new[] { "Tools.ListInvoke.All" }, SetInheritable: true)
        };

        await BatchPermissionsOrchestrator.EnsureMissingResourceSpsAsync(
            _graph, TenantId, BlueprintAppId, missing, resolvedSpAppIds,
            permScopes: Array.Empty<string>(),
            skipSpProvisioning: false,
            _logger,
            setupResults: setupResults,
            ct: CancellationToken.None,
            commandExecutor: _executor,
            confirmationProvider: declining);

        resolvedSpAppIds.Should().BeEmpty(
            because: "the operator declined — the helper must not stamp the appId into the resolved set without provisioning evidence");
        await _executor.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        setupResults.MissingSpActions.Should().ContainSingle(a => a.ResourceAppId == TeamsMcpAppId,
            because: "declining still leaves the operator with an unresolved resource — the Action Required block must list the manual recovery steps");
        await declining.Received().ConfirmAsync(Arg.Is<string>(s => s.Contains("Provision via 'az ad sp create'?") && s.Contains("[y/N]")));
    }

    [Fact]
    public async Task ConfirmationProviderReturnsTrue_AzExitsZeroWithSpJson_AddsAppIdToResolvedSet()
    {
        // The happy path: operator accepts the prompt, az exits 0 with the SP JSON in
        // stdout. The helper parses the id directly from az output (no Graph re-poll) and
        // adds the appId to resolvedSpAppIds. MissingSpActions stays empty for this spec.
        using var bypass = TemporarilyDisableSpProvisioningBypass();

        _graph
            .LookupServicePrincipalByAppIdAsync(TenantId, TeamsMcpAppId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(null));

        // az returns the real shape we saw from the operator's manual run: a JSON object
        // with "id" set to the new SP's object id. Anything else (oauth2PermissionScopes,
        // servicePrincipalNames, etc.) is irrelevant to the helper's success check.
        _executor
            .ExecuteAsync("az", Arg.Is<string>(s => s.Contains("ad sp create") && s.Contains(TeamsMcpAppId)),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult
            {
                ExitCode = 0,
                StandardOutput = "{\"id\":\"d42a47bf-9727-444c-ae57-17bd588613cd\",\"appId\":\"" + TeamsMcpAppId + "\"}"
            }));

        var accepting = Substitute.For<IConfirmationProvider>();
        accepting.ConfirmAsync(Arg.Any<string>()).Returns(Task.FromResult(true));

        var resolvedSpAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var setupResults = new SetupResults();
        var missing = new[]
        {
            new ResourcePermissionSpec(TeamsMcpAppId, "Work IQ Teams MCP", new[] { "Tools.ListInvoke.All" }, SetInheritable: true)
        };

        await BatchPermissionsOrchestrator.EnsureMissingResourceSpsAsync(
            _graph, TenantId, BlueprintAppId, missing, resolvedSpAppIds,
            permScopes: Array.Empty<string>(),
            skipSpProvisioning: false,
            _logger,
            setupResults: setupResults,
            ct: CancellationToken.None,
            commandExecutor: _executor,
            confirmationProvider: accepting);

        resolvedSpAppIds.Should().Contain(TeamsMcpAppId,
            because: "az returned the SP JSON with an id — that is authoritative evidence the SP exists, so the caller's URL build must include this resource");
        setupResults.MissingSpActions.Should().BeEmpty(
            because: "the SP was provisioned successfully — no recovery steps belong in Action Required for this resource");
        // The helper trusts az output and does NOT issue a follow-up Graph lookup for the
        // newly created SP. Only the pre-flight lookup should have fired.
        await _graph.Received(1).LookupServicePrincipalByAppIdAsync(
            TenantId, TeamsMcpAppId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }

    [Fact]
    public async Task AzCommandFailsWithNonZeroExit_RecordsActionAndDoesNotAddAppIdToResolvedSet()
    {
        using var bypass = TemporarilyDisableSpProvisioningBypass();

        _graph
            .LookupServicePrincipalByAppIdAsync(TenantId, TeamsMcpAppId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(null));

        _executor
            .ExecuteAsync("az", Arg.Is<string>(s => s.Contains("ad sp create")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "Insufficient privileges to complete the operation." }));

        var accepting = Substitute.For<IConfirmationProvider>();
        accepting.ConfirmAsync(Arg.Any<string>()).Returns(Task.FromResult(true));

        var resolvedSpAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var setupResults = new SetupResults();
        var missing = new[]
        {
            new ResourcePermissionSpec(TeamsMcpAppId, "Work IQ Teams MCP", new[] { "Tools.ListInvoke.All" }, SetInheritable: true)
        };

        await BatchPermissionsOrchestrator.EnsureMissingResourceSpsAsync(
            _graph, TenantId, BlueprintAppId, missing, resolvedSpAppIds,
            permScopes: Array.Empty<string>(),
            skipSpProvisioning: false,
            _logger,
            setupResults: setupResults,
            ct: CancellationToken.None,
            commandExecutor: _executor,
            confirmationProvider: accepting);

        resolvedSpAppIds.Should().BeEmpty(
            because: "az failed — there is no SP to record; including the appId would poison the unified URL with the same AADSTS650052 we are trying to avoid");
        setupResults.MissingSpActions.Should().ContainSingle(a => a.ResourceAppId == TeamsMcpAppId,
            because: "operator needs the recovery steps in the Action Required block so they can run the az command manually (potentially after fixing whatever blocked it: bad az login, tenant policy, etc.)");
    }

    [Fact]
    public async Task NonGuidResourceAppId_SkippedWithMissingSpActionAndAzNotInvoked()
    {
        // Safety guard: a custom permission with a malformed appId reaching this helper must
        // not be interpolated into a shell command. Guard rejects it, records the recovery
        // entry, and continues with remaining specs.
        using var bypass = TemporarilyDisableSpProvisioningBypass();

        _graph
            .LookupServicePrincipalByAppIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(null));

        var accepting = Substitute.For<IConfirmationProvider>();
        accepting.ConfirmAsync(Arg.Any<string>()).Returns(Task.FromResult(true));

        var resolvedSpAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var setupResults = new SetupResults();
        var missing = new[]
        {
            new ResourcePermissionSpec("not-a-guid; rm -rf /", "Malicious", new[] { "x" }, SetInheritable: true)
        };

        await BatchPermissionsOrchestrator.EnsureMissingResourceSpsAsync(
            _graph, TenantId, BlueprintAppId, missing, resolvedSpAppIds,
            permScopes: Array.Empty<string>(),
            skipSpProvisioning: false,
            _logger,
            setupResults: setupResults,
            ct: CancellationToken.None,
            commandExecutor: _executor,
            confirmationProvider: accepting);

        await _executor.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        // Guard must reject the spec BEFORE prompting; otherwise the operator could approve
        // a shell injection by mistake. (NSubstitute does not accept 'because' on Received().)
        await accepting.DidNotReceive().ConfirmAsync(Arg.Any<string>());
        setupResults.MissingSpActions.Should().ContainSingle(a => a.ResourceName == "Malicious",
            because: "the malformed spec is still missing — Action Required must list the recovery steps so the operator can fix the manifest or custom permission entry");
    }

    [Fact]
    public void BuildAzAdSpCreateCommand_ProducesExpectedShape()
    {
        var cmd = BatchPermissionsOrchestrator.BuildAzAdSpCreateCommand(TeamsMcpAppId);

        cmd.Should().Be($"az ad sp create --id {TeamsMcpAppId}",
            because: "the live 'Running: ...' log line and the Action Required step 1 both surface this exact command; one source of truth keeps them aligned");
    }

    [Fact]
    public void BuildPerSpBlueprintConsentUrl_KeysClientIdOnBlueprintAndScopeOnResource()
    {
        // The previous "consent the MCP app to itself" pattern fails with AADSTS65003
        // (first party token-to-self). This URL has the blueprint as the CLIENT and the
        // resource as the SCOPE target — a normal cross-app consent that Entra accepts.
        var spec = new ResourcePermissionSpec(TeamsMcpAppId, "Work IQ Teams MCP", new[] { "Tools.ListInvoke.All" }, SetInheritable: true);
        var url = BatchPermissionsOrchestrator.BuildPerSpBlueprintConsentUrl(TenantId, BlueprintAppId, spec);

        url.Should().StartWith($"https://login.microsoftonline.com/{TenantId}/v2.0/adminconsent",
            because: "the per-SP recovery URL targets the v2 admin-consent endpoint scoped to the operator's tenant");
        url.Should().Contain($"client_id={BlueprintAppId}",
            because: "the BLUEPRINT must be the client so this is a normal cross-app consent — using the resource as client would hit AADSTS65003 token-to-self");
        url.Should().Contain(Uri.EscapeDataString($"{TeamsMcpAppId}/Tools.ListInvoke.All"),
            because: "the scope param must qualify the requested permission under the resource SP that step 1 (az ad sp create) just provisioned");
    }

    [Fact]
    public void TryExtractSpIdFromAzOutput_ValidJsonWithId_ReturnsId()
    {
        var json = "{\"id\":\"d42a47bf-9727-444c-ae57-17bd588613cd\",\"appId\":\"" + TeamsMcpAppId + "\"}";

        var spId = BatchPermissionsOrchestrator.TryExtractSpIdFromAzOutput(json);

        spId.Should().Be("d42a47bf-9727-444c-ae57-17bd588613cd",
            because: "az ad sp create returns the SP JSON in stdout; the 'id' property is the SP object id and is authoritative evidence the SP exists");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"unrelated\":\"value\"}")]
    [InlineData("{\"id\": 42}")]   // id present but not a string
    public void TryExtractSpIdFromAzOutput_InvalidOrMissingId_ReturnsNull(string? azOutput)
    {
        var spId = BatchPermissionsOrchestrator.TryExtractSpIdFromAzOutput(azOutput);

        spId.Should().BeNull(
            because: "the helper falls back to the warning path only when az output is unparseable or missing the id field — all of these cases must produce null so the caller does not mistakenly add the appId to the resolved set");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test scaffolding
    //
    // Flips BypassSpProvisioningForTests off for the duration of one test and
    // restores it on Dispose. The default (false) is the production value; tests
    // for the broader orchestrator flow flip it ON in their setup, so this scope
    // ensures we're explicit about wanting the helper to actually run.
    // ─────────────────────────────────────────────────────────────────────────

    private static IDisposable TemporarilyDisableSpProvisioningBypass()
    {
        var prior = BatchPermissionsOrchestrator.BypassSpProvisioningForTests;
        BatchPermissionsOrchestrator.BypassSpProvisioningForTests = false;
        return new RestoreOnDispose(() => BatchPermissionsOrchestrator.BypassSpProvisioningForTests = prior);
    }

    private sealed class RestoreOnDispose : IDisposable
    {
        private readonly Action _restore;
        public RestoreOnDispose(Action restore) { _restore = restore; }
        public void Dispose() => _restore();
    }
}
