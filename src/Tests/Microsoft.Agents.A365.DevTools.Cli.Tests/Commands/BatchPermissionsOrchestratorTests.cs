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
/// Unit tests for BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync.
/// Focused on the non-fatal phase-independence contract: each phase failure
/// must not prevent subsequent phases from running.
/// </summary>
public class BatchPermissionsOrchestratorTests : IDisposable
{
    private readonly GraphApiService _graph;
    private readonly AgentBlueprintService _blueprintService;
    private readonly CommandExecutor _executor;
    private readonly ILogger _logger;

    public BatchPermissionsOrchestratorTests()
    {
        _logger = NullLogger.Instance;
        _graph = Substitute.ForPartsOf<GraphApiService>();
        _blueprintService = Substitute.ForPartsOf<AgentBlueprintService>(
            Substitute.For<ILogger<AgentBlueprintService>>(), _graph);
        _executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        // Suppress real browser launches and 180s consent polls during orchestrator tests.
        // Reset in Dispose so state does not leak into other test classes.
        BrowserHelper.OpenUrlOverrideForTests = (_, _) => { };
        AdminConsentHelper.BypassConsentChecksForTests = true;
    }

    public void Dispose()
    {
        BrowserHelper.OpenUrlOverrideForTests = null;
        AdminConsentHelper.BypassConsentChecksForTests = false;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// When no specs are supplied the orchestrator returns success immediately
    /// without making any service calls. This guards against empty-state panics
    /// and ensures callers with no resources to configure do not trigger
    /// unnecessary Graph authentication.
    /// </summary>
    [Fact]
    public async Task ConfigureAllPermissions_EmptySpecs_ReturnsTrueWithoutCallingServices()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "tenant-id",
            AgentBlueprintId = "app-id"
        };

        // Act
        var (blueprintUpdated, inheritedConfigured, consentGranted, consentUrl) =
            await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                _graph, _blueprintService, config,
                blueprintAppId: "app-id",
                tenantId: "tenant-id",
                specs: Array.Empty<ResourcePermissionSpec>(),
                _logger,
                setupResults: null,
                ct: default);

        // Assert
        blueprintUpdated.Should().BeTrue();
        inheritedConfigured.Should().BeTrue();
        consentGranted.Should().BeTrue();
        consentUrl.Should().BeNull();

        // No Graph calls should be made for an empty spec list
        await _graph.DidNotReceive().GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }

    /// <summary>
    /// When Phase 1 fails (Graph authentication unavailable), Phase 2 is skipped
    /// but Phase 3 still runs and returns a non-null consent URL for non-admins.
    ///
    /// This is the key non-admin contract: even with no Graph access the caller
    /// always receives a URL to present to the tenant administrator, rather than
    /// getting an exception or an empty result with no recovery path.
    /// </summary>
    [Fact]
    public async Task ConfigureAllPermissions_WhenPhase1AuthFails_Phase2SkippedAndPhase3ReturnsConsentUrl()
    {
        // Arrange — GraphGetAsync returns null, simulating delegated auth failure in Phase 1
        _graph.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns((JsonDocument?)null);

        // Phase 3 checks whether the current user is an admin; return DoesNotHaveRole (non-admin path)
        _graph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RoleCheckResult.DoesNotHaveRole);

        var config = new Agent365Config
        {
            TenantId = "tenant-123",
            AgentBlueprintId = "blueprint-app-id",
            ClientAppId = "client-app-id"
        };

        // Include a Microsoft Graph spec so Phase 3 builds a consent URL.
        // GrantAdminConsentAsync only generates a URL for Graph scopes (non-Graph resources
        // use inheritable permissions, not the /v2.0/adminconsent URL).
        var specs = new[]
        {
            new ResourcePermissionSpec(
                AuthenticationConstants.MicrosoftGraphResourceAppId,
                "Microsoft Graph",
                new[] { "Mail.ReadWrite" },
                SetInheritable: true)
        };

        // Act
        var (blueprintUpdated, inheritedConfigured, consentGranted, consentUrl) =
            await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                _graph, _blueprintService, config,
                blueprintAppId: "blueprint-app-id",
                tenantId: "tenant-123",
                specs: specs,
                _logger,
                setupResults: null,
                ct: default);

        // Assert — Phase 1 failed, Phase 2 was skipped
        blueprintUpdated.Should().BeFalse("Phase 1 auth failure should mark blueprint permissions as not updated");
        inheritedConfigured.Should().BeFalse("Phase 2 must be skipped when Phase 1 fails");

        // Phase 3 ran and returned a consent URL for the non-admin user
        consentGranted.Should().BeFalse("non-admin cannot grant consent interactively");
        consentUrl.Should().NotBeNullOrWhiteSpace("non-admin must always receive a consent URL for the tenant admin");
        consentUrl.Should().Contain("tenant-123", "consent URL must be scoped to the correct tenant");
        consentUrl.Should().Contain("blueprint-app-id", "consent URL must reference the blueprint application");

        // state parameter must be a random GUID (not the old hardcoded "xyz123")
        var stateMatch = System.Text.RegularExpressions.Regex.Match(consentUrl!, @"[?&]state=([^&]+)");
        stateMatch.Success.Should().BeTrue(because: "consent URL must include a state parameter for CSRF protection");
        Guid.TryParse(stateMatch.Groups[1].Value, out _).Should().BeTrue(
            because: "state parameter must be a random GUID, not a hardcoded value like 'xyz123'");
    }

    /// <summary>
    /// The orchestrator now relies exclusively on the unified /v2.0/adminconsent URL (no direct
    /// POST to /oauth2PermissionGrants). When specs include non-Graph resources (Messaging Bot
    /// API, Observability API, etc.) the consent URL must fully-qualify those scopes as
    /// api://{appId}/{scope} so a single browser prompt covers every delegated permission.
    /// This test exercises the non-admin path so URL construction is validated without
    /// triggering a real browser launch.
    /// </summary>
    [Fact]
    public async Task ConfigureAllPermissions_NonAdmin_BuildsUnifiedConsentUrlCoveringAllResources()
    {
        // Arrange — Phase 1 succeeds but the user is not a tenant admin, so Phase 3 returns
        // the consent URL without opening a browser. This validates URL construction across
        // Graph + non-Graph resources.
        _graph.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(JsonDocument.Parse("{\"id\":\"user-id\"}"));

        _graph.LookupServicePrincipalByAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(null));

        _graph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RoleCheckResult.DoesNotHaveRole));

        // Prevent real network calls: Phase 1 resource SP resolution and Phase 2a inheritable
        // permission writes must not reach Azure endpoints in CI. Return null SPs (not found)
        // and simulate an insufficient-privileges failure so both phases skip cleanly without
        // making any real HTTP requests.
        _graph.EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>(null));

        _blueprintService.SetInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((ok: false, alreadyExists: false, error: (string?)"Insufficient privileges")));

        var config = new Agent365Config
        {
            TenantId = "tenant-id",
            AgentBlueprintId = "blueprint-app-id"
        };

        var specs = new[]
        {
            new ResourcePermissionSpec(
                AuthenticationConstants.MicrosoftGraphResourceAppId,
                "Microsoft Graph",
                new[] { "Mail.ReadWrite" },
                SetInheritable: true),
            new ResourcePermissionSpec(
                ConfigConstants.MessagingBotApiAppId,
                "Messaging Bot API",
                new[] { "BotApi.Scope" },
                SetInheritable: true),
            new ResourcePermissionSpec(
                ConfigConstants.ObservabilityApiAppId,
                "Observability API",
                new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
                SetInheritable: true)
        };

        // Act
        var (_, _, consentGranted, consentUrl) =
            await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                _graph, _blueprintService, config,
                blueprintAppId: "blueprint-app-id",
                tenantId: "tenant-id",
                specs: specs,
                _logger,
                setupResults: null,
                ct: default);

        // Assert — the orchestrator no longer calls /oauth2PermissionGrants for any resource;
        // the unified consent URL must cover Graph + non-Graph scopes together.
        consentGranted.Should().BeFalse(
            because: "non-admin cannot grant consent interactively");
        consentUrl.Should().NotBeNullOrWhiteSpace(
            because: "every delegated-scope path now goes through /v2.0/adminconsent");
        consentUrl.Should().Contain("tenant-id",
            because: "the consent URL must be scoped to the correct tenant");
        consentUrl.Should().Contain("blueprint-app-id",
            because: "the consent URL must target the blueprint application");
        consentUrl.Should().Contain("Mail.ReadWrite",
            because: "Graph scopes must be included in the unified consent URL");
        consentUrl.Should().Contain("botapi.skype.com",
            because: "non-Graph resources with a canonical identifierUri (e.g. Messaging Bot API) must use that URI in the unified consent URL — using api://{appId} for resources that publish their own identifierUri produces a scope mismatch and AAD rejects the consent");
        consentUrl.Should().Contain(ConfigConstants.ObservabilityApiAppId,
            because: "resources without a friendly identifierUri must be encoded as api://{appId}/{scope}, and the Observability API identifierUri embeds its appId — every delegated resource must be covered by a single browser prompt");

        await _graph.DidNotReceive().CreateOrUpdateOauth2PermissionGrantWithDetailsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }

    // The old GA-Phase-2b tests (`ConfigureAllPermissions_WhenGaDeclines_SkipsPhase2bAndReturnsNoConsentUrl`
    // and `ConfigureAllPermissions_WhenGaAndNoConfirmationProvider_GrantsFiredSilently`) were removed
    // when Phase 2b was retired. The orchestrator no longer issues a confirmation prompt and no longer
    // calls /oauth2PermissionGrants — every delegated-scope path now goes through /v2.0/adminconsent,
    // which is covered by `ConfigureAllPermissions_NonAdmin_BuildsUnifiedConsentUrlCoveringAllResources`
    // above and by `ConfigureAllPermissions_WhenPhase1AuthFails_Phase2SkippedAndPhase3ReturnsConsentUrl`
    // for the Graph-only non-admin path. The admin (browser-launching) path is not unit-tested to
    // avoid invoking a real browser in CI.

    // ---- PowerShell S2S fallback tests ----
    // Use valid GUIDs for tenantId/blueprintAppId so PowerShellS2SRunner GUID validation passes.
    private const string S2STenantId = "00000000-0000-0000-0000-000000000001";
    private const string S2SBlueprintAppId = "00000000-0000-0000-0000-000000000002";
    private const string S2SBlueprintSpObjectId = "sp-object-id";

    private void ArrangeS2SPhase1AndAdminCheck()
    {
        _graph.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(JsonDocument.Parse("{\"id\":\"user-id\"}"));

        _graph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RoleCheckResult.HasRole);

        _blueprintService.GrantAppRoleAssignmentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string[]>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Models.AppRoleGrantResult(AllSucceeded: false, AllAlreadyAssigned: false)));

        // Prevent real network calls: Phase 1 resource SP resolution and Phase 2a inheritable
        // permission writes must not reach Azure endpoints in CI (ForPartsOf calls real implementations
        // for unmocked methods). knownBlueprintSpObjectId pre-fills the blueprint SP so
        // LookupServicePrincipalByAppIdAsync is skipped; EnsureServicePrincipalForAppIdAsync
        // covers resource SPs. Null resource SP causes PerformS2SGrantsAsync to skip the
        // GrantAppRoleAssignmentAsync call and set allS2SOk=false, which is equivalent to false
        // from GrantAppRoleAssignmentAsync — BlueprintS2SOutcome=Failed either way.
        _graph.EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>(null));

        _blueprintService.SetInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((ok: false, alreadyExists: false, error: (string?)"Insufficient privileges")));
    }

    private static ResourcePermissionSpec[] S2SSpec() =>
    [
        new ResourcePermissionSpec(
            ConfigConstants.ObservabilityApiAppId,
            "Observability API",
            new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
            SetInheritable: false,
            AppRoleScopes: new[] { ConfigConstants.ObservabilityApiOtelWriteScope })
    ];

    /// <summary>
    /// When the programmatic Graph API path for S2S fails (e.g. token lacks
    /// AppRoleAssignment.ReadWrite.All even for a GA) and pwsh executes the
    /// fallback script successfully, BlueprintS2SOutcome must be set to Granted
    /// so the Action Required block is suppressed in the setup summary.
    /// </summary>
    [Fact]
    public async Task ConfigureAllPermissions_WhenS2SFailsAndPwshSucceeds_SetsBlueprintS2SOutcomeGranted()
    {
        // Arrange
        ArrangeS2SPhase1AndAdminCheck();
        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<bool>())
            .Returns(new CommandResult { ExitCode = 0 });

        var setupResults = new SetupResults();

        // Act
        await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
            _graph, _blueprintService,
            new Agent365Config { TenantId = S2STenantId, AgentBlueprintId = S2SBlueprintAppId },
            blueprintAppId: S2SBlueprintAppId, tenantId: S2STenantId,
            specs: S2SSpec(), _logger, setupResults, ct: default,
            knownBlueprintSpObjectId: S2SBlueprintSpObjectId,
            commandExecutor: _executor);

        // Assert
        setupResults.BlueprintS2SOutcome.Should().Be(GrantOutcome.Granted,
            because: "when pwsh executes the S2S script successfully the Action Required block must be suppressed");
    }

    /// <summary>
    /// When the programmatic path fails and pwsh exits non-zero (e.g. exit code 2 from the
    /// in-script Microsoft.Graph module check, or any other script failure), BlueprintS2SOutcome
    /// must remain Failed so the Action Required block still surfaces — the user needs to
    /// install the modules / fix the underlying issue and re-run.
    /// </summary>
    [Fact]
    public async Task ConfigureAllPermissions_WhenS2SFailsAndPwshExitsNonZero_OutcomeRemainsFailed()
    {
        // Arrange
        ArrangeS2SPhase1AndAdminCheck();
        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<bool>())
            .Returns(new CommandResult { ExitCode = 2 });

        var setupResults = new SetupResults();

        // Act
        await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
            _graph, _blueprintService,
            new Agent365Config { TenantId = S2STenantId, AgentBlueprintId = S2SBlueprintAppId },
            blueprintAppId: S2SBlueprintAppId, tenantId: S2STenantId,
            specs: S2SSpec(), _logger, setupResults, ct: default,
            knownBlueprintSpObjectId: S2SBlueprintSpObjectId,
            commandExecutor: _executor);

        // Assert
        setupResults.BlueprintS2SOutcome.Should().Be(GrantOutcome.Failed,
            because: "a non-zero pwsh exit code means the fallback could not complete — Action Required must remain visible");
    }

    /// <summary>
    /// Backward-compat contract: when no commandExecutor is supplied (e.g. callers that have not
    /// been updated, or unattended/non-interactive runs), the PowerShell fallback is not attempted
    /// and BlueprintS2SOutcome remains Failed exactly as before this feature was added.
    /// </summary>
    [Fact]
    public async Task ConfigureAllPermissions_WhenNoCommandExecutor_PwshFallbackNotAttempted()
    {
        // Arrange
        ArrangeS2SPhase1AndAdminCheck();

        var setupResults = new SetupResults();

        // Act — commandExecutor intentionally omitted
        await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
            _graph, _blueprintService,
            new Agent365Config { TenantId = S2STenantId, AgentBlueprintId = S2SBlueprintAppId },
            blueprintAppId: S2SBlueprintAppId, tenantId: S2STenantId,
            specs: S2SSpec(), _logger, setupResults, ct: default,
            knownBlueprintSpObjectId: S2SBlueprintSpObjectId);

        // Assert
        setupResults.BlueprintS2SOutcome.Should().Be(GrantOutcome.Failed,
            because: "without a commandExecutor the PowerShell fallback is not attempted and outcome stays Failed");

        await _executor.DidNotReceive().ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<bool>());
    }

    /// <summary>
    /// When the caller is a non-admin and the spec list includes S2S (AppRoleScopes) entries,
    /// ConfigureAllPermissionsAsync must set BlueprintS2SOutcome = Failed so that
    /// DisplaySetupSummary surfaces the PowerShell S2S hand-off block in the Action Required
    /// section — just like it does for a GA whose Graph API call returns 403.
    /// </summary>
    [Fact]
    public async Task ConfigureAllPermissions_NonAdmin_WithS2SSpecs_SetsBlueprintS2SOutcomeFailed()
    {
        // Arrange
        _graph.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(JsonDocument.Parse("{\"id\":\"user-id\"}"));

        _graph.LookupServicePrincipalByAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(null));

        _graph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RoleCheckResult.DoesNotHaveRole));

        _graph.EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>(null));

        _blueprintService.SetInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((ok: false, alreadyExists: false, error: (string?)"Insufficient privileges")));

        var setupResults = new SetupResults();

        // Act
        await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
            _graph, _blueprintService,
            new Agent365Config { TenantId = S2STenantId, AgentBlueprintId = S2SBlueprintAppId },
            blueprintAppId: S2SBlueprintAppId, tenantId: S2STenantId,
            specs: S2SSpec(), _logger, setupResults, ct: default);

        // Assert
        setupResults.BlueprintS2SOutcome.Should().Be(GrantOutcome.Failed,
            because: "a non-admin user cannot complete S2S app role assignment directly — the outcome must be marked Failed so DisplaySetupSummary surfaces the PowerShell hand-off block");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // Idempotency tracking — TenantWideConsentAlreadyExisted and BlueprintS2SAlreadyAssigned.
    //
    // These flags drive the "already granted" vs "granted" wording in the Blueprint Permission
    // Grants summary row. They are user-visible — if a future change forgets to set them when
    // a re-run was fully idempotent, the user has no way to tell that nothing changed and may
    // think every run is performing real work. These tests lock the orchestrator's contract.
    // ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When every S2S spec's required app roles are already assigned on the blueprint SP,
    /// PerformS2SGrantsAsync must mark the outcome Granted AND set BlueprintS2SAlreadyAssigned
    /// so the setup summary renders "already granted  S2S app roles" instead of "granted".
    /// </summary>
    [Fact]
    public async Task ConfigureAllPermissions_AllS2SAlreadyAssigned_SetsBlueprintS2SAlreadyAssignedTrue()
    {
        // Arrange — Phase 1 succeeds, every S2S grant call reports AllAlreadyAssigned=true.
        ArrangeS2SPhase1AndAdminCheck();
        _blueprintService.GrantAppRoleAssignmentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string[]>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Models.AppRoleGrantResult(
                AllSucceeded: true, AllAlreadyAssigned: true)));

        var setupResults = new SetupResults();

        // Act
        await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
            _graph, _blueprintService,
            new Agent365Config { TenantId = S2STenantId, AgentBlueprintId = S2SBlueprintAppId },
            blueprintAppId: S2SBlueprintAppId, tenantId: S2STenantId,
            specs: S2SSpec(), _logger, setupResults, ct: default,
            knownBlueprintSpObjectId: S2SBlueprintSpObjectId,
            commandExecutor: _executor);

        // Assert — both contract bits must be set together: Granted (success) AND AlreadyAssigned
        // (no new POST). If a future refactor decouples them, the summary will silently regress.
        setupResults.BlueprintS2SOutcome.Should().Be(GrantOutcome.Granted,
            because: "every requested role was already in place — the operation succeeded by way of the idempotent skip");
        setupResults.BlueprintS2SAlreadyAssigned.Should().BeTrue(
            because: "this is the load-bearing signal for 'already granted' wording in DisplaySetupSummary — without it the summary cannot tell idempotent re-runs from first-time grants");
    }

    /// <summary>
    /// When at least one S2S spec needed a new POST (AllAlreadyAssigned=false), the outcome
    /// is Granted but BlueprintS2SAlreadyAssigned must stay false so the summary correctly
    /// reports "granted" (not "already granted") for runs that did real work.
    /// </summary>
    [Fact]
    public async Task ConfigureAllPermissions_S2SNewlyGranted_SetsBlueprintS2SAlreadyAssignedFalse()
    {
        // Arrange — grant succeeds but at least one role was newly created (AllAlreadyAssigned=false).
        ArrangeS2SPhase1AndAdminCheck();
        _blueprintService.GrantAppRoleAssignmentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string[]>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Models.AppRoleGrantResult(
                AllSucceeded: true, AllAlreadyAssigned: false)));

        var setupResults = new SetupResults();

        // Act
        await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
            _graph, _blueprintService,
            new Agent365Config { TenantId = S2STenantId, AgentBlueprintId = S2SBlueprintAppId },
            blueprintAppId: S2SBlueprintAppId, tenantId: S2STenantId,
            specs: S2SSpec(), _logger, setupResults, ct: default,
            knownBlueprintSpObjectId: S2SBlueprintSpObjectId,
            commandExecutor: _executor);

        // Assert
        setupResults.BlueprintS2SOutcome.Should().Be(GrantOutcome.Granted);
        setupResults.BlueprintS2SAlreadyAssigned.Should().BeFalse(
            because: "at least one role was newly POSTed, so this run did real work — the summary must report 'granted', not 'already granted'");
    }

    /// <summary>
    /// When the consent pre-check observes that every required scope is already granted
    /// (BypassConsentChecksForTests simulates the az-cli pre-check returning true),
    /// GrantAdminConsentAsync must set TenantWideConsentAlreadyExisted=true and return
    /// without launching the browser. This is the bug fix that motivates the whole PR.
    /// </summary>
    [Fact]
    public async Task ConfigureAllPermissions_TenantConsentAlreadyExists_SetsTenantWideConsentAlreadyExistedTrue()
    {
        // Arrange — Phase 1 must resolve the resource SP so the pre-check loop has a spec
        // to iterate over. BypassConsentChecksForTests (set in the ctor) makes the per-spec
        // CheckConsentExistsAsync return true, exercising the "already consented" branch.
        _graph.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(JsonDocument.Parse("{\"id\":\"user-id\"}"));

        _graph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RoleCheckResult.HasRole);

        _graph.EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>("resource-sp-id"));

        _blueprintService.SetInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((ok: true, alreadyExists: true, error: (string?)null)));

        // Use a delegated-only spec so PerformS2SGrantsAsync is a no-op and we isolate the
        // tenant-wide-consent code path.
        var delegatedSpec = new[]
        {
            new ResourcePermissionSpec(
                ConfigConstants.ObservabilityApiAppId,
                "Observability API",
                new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
                SetInheritable: false)
        };

        var setupResults = new SetupResults();

        // Act
        await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
            _graph, _blueprintService,
            new Agent365Config { TenantId = S2STenantId, AgentBlueprintId = S2SBlueprintAppId },
            blueprintAppId: S2SBlueprintAppId, tenantId: S2STenantId,
            specs: delegatedSpec, _logger, setupResults, ct: default,
            knownBlueprintSpObjectId: S2SBlueprintSpObjectId,
            commandExecutor: _executor);

        // Assert — pre-check succeeded, so the browser-launching consent flow must be skipped.
        setupResults.TenantWideConsentAlreadyExisted.Should().BeTrue(
            because: "this is the signal DisplaySetupSummary reads to render 'already granted' — without it, re-runs visually look indistinguishable from first-time setup even when nothing changed");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // CR-007: Phase 2a aggregation that sets InheritablePermissionsAlreadyExisted.
    //
    // The orchestrator-side aggregation at BatchPermissionsOrchestrator.cs:166-174 is what
    // drives the row-3 "already configured" wording in the setup summary. A previous run of
    // `a365 setup all` rendered "configured" even on idempotent re-runs because this flag was
    // never set in the ConfigureInheritedPermissionsAsync path — the only writer lived in a
    // separate code path used by legacy `setup blueprint` subcommands. These tests lock the
    // aggregation's contract:
    //   - true only when EVERY inheritable spec is both successful AND was already in place
    //   - false if ANY spec was newly written OR ANY spec failed (the user's rule:
    //     "Even if one permission was newly granted, we should say configured")
    // ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Arranges Phase 1 success + Phase 2a inheritable-permissions mock with the supplied
    /// (set, verify) tuples per spec. The orchestrator's foreach loop runs through all specs;
    /// the aggregation it performs after the loop is what these tests verify.
    /// </summary>
    private static ResourcePermissionSpec InheritableSpec(string appId, string name) =>
        new(appId, name, new[] { "scope1" }, SetInheritable: true);

    private void ArrangePhase1ForInheritablePermissions()
    {
        // Phase 1 must succeed enough to reach Phase 2a. The orchestrator uses GraphGetAsync
        // for the /me lookup and EnsureServicePrincipalForAppIdAsync for resource SPs.
        _graph.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(JsonDocument.Parse("{\"id\":\"user-id\"}"));

        _graph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RoleCheckResult.HasRole);

        _graph.EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>("resource-sp-id"));
    }

    [Fact]
    public async Task ConfigureAllPermissions_AllInheritableSpecsAlreadyExisted_SetsInheritablePermissionsAlreadyExistedTrue()
    {
        // Arrange — every inheritable spec reports alreadyExists=true and verifies as kind=allAllowed.
        // This is the idempotent-re-run scenario the user reported as buggy ("Summary says configured").
        ArrangePhase1ForInheritablePermissions();

        _blueprintService.SetInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((ok: true, alreadyExists: true, error: (string?)null)));

        _blueprintService.VerifyInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult((exists: true, scopesAllAllowed: true, rolesAllAllowed: true, error: (string?)null)));

        var setupResults = new SetupResults();

        // Act — three inheritable specs, all already in place.
        await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
            _graph, _blueprintService,
            new Agent365Config { TenantId = S2STenantId, AgentBlueprintId = S2SBlueprintAppId },
            blueprintAppId: S2SBlueprintAppId, tenantId: S2STenantId,
            specs: new[]
            {
                InheritableSpec(ConfigConstants.ObservabilityApiAppId, "Observability API"),
                InheritableSpec(ConfigConstants.MessagingBotApiAppId, "Messaging Bot API"),
                InheritableSpec("11111111-1111-1111-1111-111111111111", "Custom Resource"),
            },
            _logger, setupResults, ct: default,
            knownBlueprintSpObjectId: S2SBlueprintSpObjectId,
            commandExecutor: _executor);

        // Assert
        setupResults.InheritablePermissionsAlreadyExisted.Should().BeTrue(
            because: "every inheritable spec succeeded AND was already in place — this is the load-bearing signal for the row-3 'already configured' wording");
    }

    [Fact]
    public async Task ConfigureAllPermissions_OneInheritableSpecNewlyWritten_SetsInheritablePermissionsAlreadyExistedFalse()
    {
        // Arrange — 2 specs report alreadyExists=true, 1 reports alreadyExists=false (newly written).
        // The user's rule: "Even if one permission was newly granted, we should say configured".
        ArrangePhase1ForInheritablePermissions();

        var firstResourceAppId = ConfigConstants.ObservabilityApiAppId;
        _blueprintService.SetInheritablePermissionsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<string>(rid => rid == firstResourceAppId),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((ok: true, alreadyExists: false, error: (string?)null)));

        _blueprintService.SetInheritablePermissionsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<string>(rid => rid != firstResourceAppId),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((ok: true, alreadyExists: true, error: (string?)null)));

        _blueprintService.VerifyInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult((exists: true, scopesAllAllowed: true, rolesAllAllowed: true, error: (string?)null)));

        var setupResults = new SetupResults();

        // Act
        await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
            _graph, _blueprintService,
            new Agent365Config { TenantId = S2STenantId, AgentBlueprintId = S2SBlueprintAppId },
            blueprintAppId: S2SBlueprintAppId, tenantId: S2STenantId,
            specs: new[]
            {
                InheritableSpec(firstResourceAppId, "Observability API"),
                InheritableSpec(ConfigConstants.MessagingBotApiAppId, "Messaging Bot API"),
            },
            _logger, setupResults, ct: default,
            knownBlueprintSpObjectId: S2SBlueprintSpObjectId,
            commandExecutor: _executor);

        // Assert
        setupResults.InheritablePermissionsAlreadyExisted.Should().BeFalse(
            because: "at least one inheritable spec was newly written, so this run did real work — the summary must report 'configured', not 'already configured'");
    }

    [Fact]
    public async Task ConfigureAllPermissions_OneInheritableSpecFailed_SetsInheritablePermissionsAlreadyExistedFalse()
    {
        // Arrange — 1 spec reports alreadyExists=true and verifies, 1 spec fails verification.
        // A failed spec breaks the "all already existed" claim regardless of its alreadyExisted value.
        // Without this assertion, a refactor that simplified the All() to drop the r.configured
        // clause would silently regress (failed-but-pre-existing would wrongly land on "already configured").
        ArrangePhase1ForInheritablePermissions();

        var failingResourceAppId = ConfigConstants.MessagingBotApiAppId;

        _blueprintService.SetInheritablePermissionsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<string>(rid => rid != failingResourceAppId),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((ok: true, alreadyExists: true, error: (string?)null)));

        // Failing spec — returns ok=false with an error (e.g. 403 Insufficient privileges).
        _blueprintService.SetInheritablePermissionsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<string>(rid => rid == failingResourceAppId),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((ok: false, alreadyExists: false, error: (string?)"Insufficient privileges")));

        _blueprintService.VerifyInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult((exists: true, scopesAllAllowed: true, rolesAllAllowed: true, error: (string?)null)));

        var setupResults = new SetupResults();

        // Act
        await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
            _graph, _blueprintService,
            new Agent365Config { TenantId = S2STenantId, AgentBlueprintId = S2SBlueprintAppId },
            blueprintAppId: S2SBlueprintAppId, tenantId: S2STenantId,
            specs: new[]
            {
                InheritableSpec(ConfigConstants.ObservabilityApiAppId, "Observability API"),
                InheritableSpec(failingResourceAppId, "Messaging Bot API"),
            },
            _logger, setupResults, ct: default,
            knownBlueprintSpObjectId: S2SBlueprintSpObjectId,
            commandExecutor: _executor);

        // Assert
        setupResults.InheritablePermissionsAlreadyExisted.Should().BeFalse(
            because: "a failed inheritable spec breaks the 'all already existed' claim — the aggregation must require r.configured AND r.alreadyExisted for every spec, not just r.alreadyExisted");
    }
}
