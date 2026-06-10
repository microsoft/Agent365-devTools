// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Tests for BlueprintSubcommand.EnsureAdminConsentAsync, which delegates the standalone
/// `setup blueprint` consent + inheritable-permissions flow to BatchPermissionsOrchestrator.
/// Regression guard for #452: inheritable permissions must be configured independently of the OAuth2
/// grant (a non-admin grant failure must not skip them).
/// </summary>
[Collection("Sequential")]
public class BlueprintConsentDelegationTests : IDisposable
{
    private const string TenantId = "00000000-0000-0000-0000-000000000001";
    private const string BlueprintAppId = "00000000-0000-0000-0000-000000000002";
    private const string BlueprintSpObjectId = "00000000-0000-0000-0000-000000000003";

    private readonly GraphApiService _graph;
    private readonly AgentBlueprintService _blueprintService;
    private readonly CommandExecutor _executor;
    private readonly ILogger _logger = NullLogger.Instance;

    public BlueprintConsentDelegationTests()
    {
        _graph = Substitute.ForPartsOf<GraphApiService>();
        _blueprintService = Substitute.ForPartsOf<AgentBlueprintService>(
            Substitute.For<ILogger<AgentBlueprintService>>(), _graph);
        _executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        // Suppress real browser launches and the 180s consent poll during these tests.
        BrowserHelper.OpenUrlOverrideForTests = (_, _) => { };
        AdminConsentHelper.BypassConsentChecksForTests = true;
        BatchPermissionsOrchestrator.BypassSpProvisioningForTests = true;
    }

    public void Dispose()
    {
        BrowserHelper.OpenUrlOverrideForTests = null;
        AdminConsentHelper.BypassConsentChecksForTests = false;
        BatchPermissionsOrchestrator.BypassSpProvisioningForTests = false;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// deferConsent: true (the `setup all` path) must short-circuit: a neutral, non-failure result and
    /// no Graph calls, leaving consent to the batch orchestrator.
    /// </summary>
    [Fact]
    public async Task EnsureAdminConsent_WhenDeferConsent_ReturnsNeutralResultWithoutCallingGraph()
    {
        var config = new Agent365Config { TenantId = TenantId, AgentBlueprintId = BlueprintAppId };

        var (consentSuccess, consentUrl, inheritableConfigured, inheritableError) =
            await BlueprintSubcommand.EnsureAdminConsentAsync(
                _logger, _executor, _graph, _blueprintService,
                TenantId, BlueprintAppId, BlueprintSpObjectId, config,
                ct: default, deferConsent: true);

        consentSuccess.Should().BeFalse(because: "consent is deferred to the batch orchestrator, not granted in this step");
        consentUrl.Should().BeEmpty(because: "this step produces no consent URL when deferring");
        inheritableConfigured.Should().BeTrue(because: "deferring is not a failure — AllSubcommand must not add a spurious inheritable-permissions warning");
        inheritableError.Should().BeNull();

        await _graph.DidNotReceive().IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// #452 core: Graph inheritable permissions are configured via the orchestrator's dedicated phase,
    /// not skipped by an OAuth2 grant failure. With them succeeding, the result reports configured + no error.
    /// </summary>
    [Fact]
    public async Task EnsureAdminConsent_WhenInheritableSucceeds_ReportsConfiguredWithNoError()
    {
        ArrangePhase1Success();

        _blueprintService.SetInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((ok: true, alreadyExists: false, error: (string?)null)));
        _blueprintService.VerifyInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult((exists: true, scopesAllAllowed: true, rolesAllAllowed: true, error: (string?)null)));

        var config = new Agent365Config { TenantId = TenantId, AgentBlueprintId = BlueprintAppId };

        var (_, consentUrl, inheritableConfigured, inheritableError) =
            await BlueprintSubcommand.EnsureAdminConsentAsync(
                _logger, _executor, _graph, _blueprintService,
                TenantId, BlueprintAppId, BlueprintSpObjectId, config,
                ct: default, deferConsent: false);

        inheritableConfigured.Should().BeTrue(
            because: "#452: Graph inheritable permissions must be configured via the orchestrator phase, not skipped by an OAuth2 grant failure");
        inheritableError.Should().BeNull(
            because: "inheritable permissions succeeded, so no error should be reported");
        consentUrl.Should().Contain(BlueprintAppId, because: "a consent URL referencing the blueprint application is always produced");
    }

    /// <summary>
    /// #452 decoupling: when inheritable permissions genuinely fail, the error is reported based on the
    /// inheritable outcome — not conflated with the separate OAuth2 consent grant.
    /// </summary>
    [Fact]
    public async Task EnsureAdminConsent_WhenInheritableFails_ReportsInheritableErrorIndependentOfConsent()
    {
        ArrangePhase1Success();

        _blueprintService.SetInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((ok: false, alreadyExists: false, error: (string?)"insufficient role")));

        var config = new Agent365Config { TenantId = TenantId, AgentBlueprintId = BlueprintAppId };

        var (_, _, inheritableConfigured, inheritableError) =
            await BlueprintSubcommand.EnsureAdminConsentAsync(
                _logger, _executor, _graph, _blueprintService,
                TenantId, BlueprintAppId, BlueprintSpObjectId, config,
                ct: default, deferConsent: false);

        inheritableConfigured.Should().BeFalse(
            because: "the inheritable-permissions write failed, so it must be reported as not configured");
        inheritableError.Should().NotBeNullOrWhiteSpace(
            because: "#452: a genuine inheritable-permissions failure must surface an inheritable-permissions error (not be silently swallowed or mislabeled)");
    }

    /// <summary>
    /// #452-adjacent: standalone `setup blueprint` renders no batch summary, so when consent isn't granted
    /// the consent URL must be surfaced inline. Phase-1 auth failure drives the not-granted path; a spy
    /// logger captures the handoff (it's a log side-effect, not a return value).
    /// </summary>
    [Fact]
    public async Task EnsureAdminConsent_WhenConsentNotGranted_SurfacesConsentUrlHandoff()
    {
        var spyLogger = Substitute.For<ILogger>();
        spyLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        // GraphGetAsync returns null → orchestrator Phase 1 fails → consent not granted, hand-off URL returned.
        _graph.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns((JsonDocument?)null);
        _graph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RoleCheckResult.DoesNotHaveRole);

        var config = new Agent365Config { TenantId = TenantId, AgentBlueprintId = BlueprintAppId };

        var (consentSuccess, consentUrl, _, _) =
            await BlueprintSubcommand.EnsureAdminConsentAsync(
                spyLogger, _executor, _graph, _blueprintService,
                TenantId, BlueprintAppId, BlueprintSpObjectId, config,
                ct: default, deferConsent: false);

        consentSuccess.Should().BeFalse(because: "consent could not be granted/verified");
        consentUrl.Should().Contain(BlueprintAppId, because: "the hand-off URL must reference the blueprint application");
        spyLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Ask a tenant administrator to open this URL")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>Phase 1 (auth + resource SP resolution) succeeds, so the orchestrator reaches Phase 2a/3.</summary>
    private void ArrangePhase1Success()
    {
        _graph.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(JsonDocument.Parse("{\"id\":\"user-id\"}"));
        _graph.EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>("graph-sp-id"));
        _graph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RoleCheckResult.HasRole);
    }
}
