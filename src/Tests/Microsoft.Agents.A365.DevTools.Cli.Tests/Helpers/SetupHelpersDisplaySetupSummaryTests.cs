// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

/// <summary>
/// Tests for SetupHelpers.DisplaySetupSummary — Action Required blocks.
/// Focuses on the security-sensitive pendingDelegatedAction and pendingS2SAction branches
/// which encode consentType, scopes, tenant IDs, and SP IDs in user-facing output.
/// </summary>
public class SetupHelpersDisplaySetupSummaryTests
{
    private const string AgentSpId = "agent-sp-id-123";
    private const string TenantId = "tenant-id-456";
    private const string BlueprintId = "blueprint-app-id-789";

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _messages = [];
        public string AllOutput => string.Join("\n", _messages);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _messages.Add(formatter(state, exception));
    }

    // ── pendingDelegatedAction ─────────────────────────────────────────────────

    [Fact]
    public void DisplaySetupSummary_PendingDelegatedAction_EmitsActionRequiredHeader()
    {
        var logger = new CapturingLogger();
        var results = BuildDelegatedPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain("Action Required",
            because: "when delegated grants are pending the summary must flag an action item");
    }

    [Fact]
    public void DisplaySetupSummary_PendingDelegatedAction_EmitsPowerShellBlock()
    {
        var logger = new CapturingLogger();
        var results = BuildDelegatedPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain("Invoke-MgGraphRequest",
            because: "the delegated fallback must emit the Invoke-MgGraphRequest PowerShell command");
        logger.AllOutput.Should().Contain("oauth2PermissionGrants",
            because: "the PowerShell block must target the oauth2PermissionGrants endpoint");
        logger.AllOutput.Should().Contain("AllPrincipals",
            because: "the fallback uses a tenant-wide AllPrincipals grant — must be explicit so the admin knows scope");
    }

    [Fact]
    public void DisplaySetupSummary_PendingDelegatedAction_EmbedsAgentSpId()
    {
        var logger = new CapturingLogger();
        var results = BuildDelegatedPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain(AgentSpId,
            because: "the PowerShell block must include the agent identity SP object ID so the admin targets the correct principal");
    }

    [Fact]
    public void DisplaySetupSummary_PendingDelegatedAction_EmbedsTenantId()
    {
        var logger = new CapturingLogger();
        var results = BuildDelegatedPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain(TenantId,
            because: "the Connect-MgGraph call must include -TenantId so the admin targets the correct tenant");
    }

    [Fact]
    public void DisplaySetupSummary_PendingDelegatedAction_EmitsRequiredRoles()
    {
        var logger = new CapturingLogger();
        var results = BuildDelegatedPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain(AuthenticationConstants.DelegatedGrantRequiredRoles,
            because: "the required role must be surfaced so the admin knows which Entra role is needed");
    }

    // ── pendingS2SAction (non-DW path) ─────────────────────────────────────────

    [Fact]
    public void DisplaySetupSummary_PendingS2SAction_NonDw_EmitsActionRequiredHeader()
    {
        var logger = new CapturingLogger();
        var results = BuildS2SPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain("Action Required",
            because: "when S2S grants are pending the summary must flag an action item");
    }

    [Fact]
    public void DisplaySetupSummary_PendingS2SAction_NonDw_EmitsPowerShellBlock()
    {
        var logger = new CapturingLogger();
        var results = BuildS2SPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain("New-MgServicePrincipalAppRoleAssignment",
            because: "the S2S fallback must emit the app role assignment PowerShell command");
        logger.AllOutput.Should().Contain("Directory.Read.All",
            because: "Connect-MgGraph must request Directory.Read.All so Get-MgServicePrincipal works");
        logger.AllOutput.Should().Contain("AppRoleAssignment.ReadWrite.All",
            because: "Connect-MgGraph must request AppRoleAssignment.ReadWrite.All to assign roles");
    }

    [Fact]
    public void DisplaySetupSummary_PendingS2SAction_NonDw_EmbedsAgentSpId()
    {
        var logger = new CapturingLogger();
        var results = BuildS2SPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain(AgentSpId,
            because: "the non-DW S2S block must use the agent identity SP object ID, not the blueprint app ID");
    }

    [Fact]
    public void DisplaySetupSummary_PendingS2SAction_NonDw_EmitsRequiredRoles()
    {
        var logger = new CapturingLogger();
        var results = BuildS2SPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain(AuthenticationConstants.S2SGrantRequiredRoles,
            because: "the required S2S role must be surfaced so the admin knows which Entra role is needed");
    }

    [Fact]
    public void DisplaySetupSummary_PendingS2SAction_NonDw_EmbedsTenantId()
    {
        var logger = new CapturingLogger();
        var results = BuildS2SPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain(TenantId,
            because: "the Connect-MgGraph call must include -TenantId so the admin targets the correct tenant");
    }

    // ── pendingAdminAction (DW path) ──────────────────────────────────────────

    [Fact]
    public void DisplaySetupSummary_DwAdminConsentPending_ShowsActionRequired()
    {
        var logger = new CapturingLogger();
        const string consentUrl = "https://login.microsoftonline.com/tenant/v2.0/adminconsent?client_id=bp-id";
        var results = new SetupResults
        {
            IsNonDwBlueprintFlow = false,
            BlueprintCreated = true,
            BlueprintId = BlueprintId,
            TenantId = TenantId,
            TenantWideConsentOutcome = Cli.Models.GrantOutcome.Failed,
            BatchPermissionsPhase1Completed = true,
            BatchPermissionsPhase2Completed = true,
            CombinedConsentUrl = consentUrl,
        };

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain("Action Required",
            because: "when DW admin consent is pending the summary must show an action item");
        logger.AllOutput.Should().Contain(consentUrl,
            because: "the consent URL must appear in the action block so the admin can grant consent");
    }

    [Fact]
    public void DisplaySetupSummary_DwAdminConsentPending_WithS2SAlsoPending_ShowsConsentUrl()
    {
        var logger = new CapturingLogger();
        const string consentUrl = "https://login.microsoftonline.com/tenant/v2.0/adminconsent?client_id=bp-id";
        var results = new SetupResults
        {
            IsNonDwBlueprintFlow = false,
            BlueprintCreated = true,
            BlueprintId = BlueprintId,
            TenantId = TenantId,
            TenantWideConsentOutcome = Cli.Models.GrantOutcome.Failed,
            BlueprintS2SOutcome = Cli.Models.GrantOutcome.Failed,
            BatchPermissionsPhase1Completed = true,
            BatchPermissionsPhase2Completed = true,
            CombinedConsentUrl = consentUrl,
        };

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain(consentUrl,
            because: "consent URL must appear even when S2S grants are also pending — regression guard for pendingAdminAction condition");
    }

    // ── pendingAdminAction (non-DW path) ──────────────────────────────────────

    [Fact]
    public void DisplaySetupSummary_NonDwAdminConsentPending_WithConsentUrl_ShowsUrlNotPortalWalkthrough()
    {
        var logger = new CapturingLogger();
        const string consentUrl = "https://login.microsoftonline.com/tenant/v2.0/adminconsent?client_id=bp-id&scope=Foo";
        var results = new SetupResults
        {
            IsNonDwBlueprintFlow = true,
            BlueprintCreated = true,
            BlueprintId = BlueprintId,
            AgentIdentityCreated = true,
            AgentIdentityId = AgentSpId,
            TenantId = TenantId,
            EffectiveAuthMode = Cli.Models.AuthMode.Obo,
            TenantWideConsentOutcome = Cli.Models.GrantOutcome.Failed,
            BatchPermissionsPhase1Completed = true,
            BatchPermissionsPhase2Completed = true,
            CombinedConsentUrl = consentUrl,
        };

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain("Action Required",
            because: "non-DW non-admin runs leave AdminConsentGranted=false and must surface the hand-off as an action item — pre-Slice 5b this branch was unreachable because pendingAdminAction gated on !isNonDw");
        logger.AllOutput.Should().Contain(consentUrl,
            because: "Slice 5a generates a real combined consent URL for non-DW non-admin runs; the summary must print it so AID developers can hand it off directly instead of walking the admin through the Entra portal");
        logger.AllOutput.Should().NotContain("Option A — Entra portal",
            because: "the portal walkthrough is only a defensive fallback for the case where no consent URL was produced; when a URL is available the URL block must be used");
    }

    [Fact]
    public void DisplaySetupSummary_NonDwAdminConsentPending_NoConsentUrl_FallsBackToPortalWalkthrough()
    {
        var logger = new CapturingLogger();
        var results = new SetupResults
        {
            IsNonDwBlueprintFlow = true,
            BlueprintCreated = true,
            BlueprintId = BlueprintId,
            AgentIdentityCreated = true,
            AgentIdentityId = AgentSpId,
            TenantId = TenantId,
            EffectiveAuthMode = Cli.Models.AuthMode.Obo,
            TenantWideConsentOutcome = Cli.Models.GrantOutcome.Failed,
            BatchPermissionsPhase1Completed = true,
            BatchPermissionsPhase2Completed = true,
            // No CombinedConsentUrl / AdminConsentUrl — simulates ApplyConsentUrlsIfNeeded short-circuit.
        };

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain("Option A — Entra portal",
            because: "when no consent URL is available the non-DW summary must fall back to the LogNonDwAdminConsentInstructions portal walkthrough so the user still has a recovery path");
    }

    /// <summary>
    /// B2 regression — non-admin AID developer running `setup all` as OBO must see the consent URL
    /// surfaced as an action item. Pre-refactor, the orchestrator wrote a misleading
    /// S2SAppRoleGranted=false hint for non-admin runs that flipped isS2SFlow=true and suppressed
    /// the consent URL action item via the delegatedConsentApplicable gate. The refactor splits
    /// outcomes per grant type and derives applicability from EffectiveAuthMode instead.
    /// </summary>
    [Fact]
    public void DisplaySetupSummary_NonDwOboNonAdmin_BlueprintS2SAlsoFailed_StillShowsConsentUrl()
    {
        var logger = new CapturingLogger();
        const string consentUrl = "https://login.microsoftonline.com/tenant/v2.0/adminconsent?client_id=bp-id";
        var results = new SetupResults
        {
            IsNonDwBlueprintFlow = true,
            BlueprintCreated = true,
            BlueprintId = BlueprintId,
            AgentIdentityCreated = true,
            AgentIdentityId = AgentSpId,
            TenantId = TenantId,
            EffectiveAuthMode = Cli.Models.AuthMode.Obo,
            TenantWideConsentOutcome = Cli.Models.GrantOutcome.Failed,
            // Even if a future caller mis-attributes blueprint S2S as Failed on the non-admin path,
            // the URL must still be surfaced — applicability comes from the OBO intent, not from
            // whether some S2S grant happened to be attempted.
            BlueprintS2SOutcome = Cli.Models.GrantOutcome.Failed,
            BatchPermissionsPhase1Completed = true,
            BatchPermissionsPhase2Completed = true,
            CombinedConsentUrl = consentUrl,
        };

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain(consentUrl,
            because: "non-DW OBO non-admin runs must always surface the tenant-wide consent URL — regression guard for the B2 bug where a misleading S2S Failed hint suppressed the action item");
    }

    [Fact]
    public void DisplaySetupSummary_NonDwOboNonAdmin_AgentIdentityDelegatedPowerShellSuppressedWhenConsentUrlPending()
    {
        // Requirement: in a non-admin OBO non-DW run the tenant-wide consent URL hand-off (Action #1)
        // and the per-principal agent-identity PowerShell block (Action #2) both deliver the same
        // delegated scopes. Once the admin completes the consent URL, the agent identity inherits
        // the consent and the per-principal call is redundant. The summary must therefore suppress
        // the per-principal PowerShell block whenever the tenant consent URL hand-off is pending,
        // to avoid asking the admin to perform two equivalent operations.
        const string consentUrl = "https://login.microsoftonline.com/" + TenantId + "/v2.0/adminconsent?client_id=" + BlueprintId + "&scope=Agent365.Observability.OtelWrite";
        var logger = new CapturingLogger();
        var results = new SetupResults
        {
            IsNonDwBlueprintFlow = true,
            BlueprintCreated = true,
            BlueprintId = BlueprintId,
            AgentIdentityCreated = true,
            AgentIdentityId = AgentSpId,
            TenantId = TenantId,
            EffectiveAuthMode = Cli.Models.AuthMode.Obo,
            // Non-admin: consent URL hand-off pending AND per-principal grant also failed locally.
            TenantWideConsentOutcome = Cli.Models.GrantOutcome.Failed,
            AgentIdentityDelegatedOutcome = Cli.Models.GrantOutcome.Failed,
            BatchPermissionsPhase1Completed = true,
            BatchPermissionsPhase2Completed = true,
            CombinedConsentUrl = consentUrl,
        };

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain(consentUrl,
            because: "the tenant-wide consent URL is the canonical hand-off for OBO non-admin and must be present");
        logger.AllOutput.Should().NotContain("Agent identity delegated permissions",
            because: "once the tenant consent URL hand-off is pending the per-principal PowerShell block is redundant and must be suppressed to avoid asking the admin to perform two equivalent operations");
        logger.AllOutput.Should().NotContain("oauth2PermissionGrants",
            because: "the per-principal PowerShell payload references oauth2PermissionGrants; it must not appear when the tenant consent URL already covers the delegated scopes");
    }

    [Fact]
    public void DisplaySetupSummary_NonDwOboNonAdmin_RowGatedOnPhase2_ShowsPendingNotConfigured()
    {
        // Requirement: the "Inheritable Permissions" row must report the result of Phase 2
        // (SetInheritablePermissionsAsync — the actual write to the blueprint), not Phase 1
        // (service-principal lookup + token warm-up). A non-admin AID Dev whose Phase 2 call
        // returned 403 has Phase 1 completed but Phase 2 not completed; reporting "configured"
        // here would mislead the user into thinking inheritance is set when it is not.
        var logger = new CapturingLogger();
        var results = new SetupResults
        {
            IsNonDwBlueprintFlow = true,
            BlueprintCreated = true,
            BlueprintId = BlueprintId,
            AgentIdentityCreated = true,
            AgentIdentityId = AgentSpId,
            TenantId = TenantId,
            EffectiveAuthMode = Cli.Models.AuthMode.Obo,
            BatchPermissionsPhase1Completed = true,
            BatchPermissionsPhase2Completed = false,
            TenantWideConsentOutcome = Cli.Models.GrantOutcome.Failed,
        };

        SetupHelpers.DisplaySetupSummary(results, logger);

        logger.AllOutput.Should().Contain("Inheritable Permissions",
            because: "the row label is unified across DW and non-DW (both call SetInheritablePermissionsAsync) so users get one consistent name");
        logger.AllOutput.Should().NotContain("Blueprint Permissions",
            because: "the previous non-DW-only label has been retired in favour of the unified label");
        var lines = logger.AllOutput.Split('\n');
        var inheritableRow = System.Array.Find(lines, l => l.Contains("Inheritable Permissions"));
        inheritableRow.Should().NotBeNull(because: "a row labelled 'Inheritable Permissions' must be emitted");
        // Contract direction is locked from both sides: the row must NOT claim 'configured', AND
        // it MUST surface a pending/not-run signal so the user knows Phase 2 is still outstanding.
        inheritableRow!.Should().NotContain("configured",
            because: "Phase 2 did not complete so the row must NOT claim 'configured' — Phase 1 (SP resolution) configures nothing on the blueprint and is not a sufficient signal");
        inheritableRow!.ToLowerInvariant().Should().MatchRegex("pending|not run|notrun",
            because: "the row must positively surface that Phase 2 has not yet run so the user knows the AllPrincipals grants are still required");
    }

    [Fact]
    public void DisplaySetupSummary_NonDwAdmin_Phase2Completed_RowReportsConfigured()
    {
        // Requirement: when SetInheritablePermissionsAsync (Phase 2) succeeds, the row must
        // report "configured". This is the positive counterpart of the gating test above.
        var logger = new CapturingLogger();
        var results = new SetupResults
        {
            IsNonDwBlueprintFlow = true,
            BlueprintCreated = true,
            BlueprintId = BlueprintId,
            AgentIdentityCreated = true,
            AgentIdentityId = AgentSpId,
            TenantId = TenantId,
            EffectiveAuthMode = Cli.Models.AuthMode.Obo,
            BatchPermissionsPhase1Completed = true,
            BatchPermissionsPhase2Completed = true,
            TenantWideConsentOutcome = Cli.Models.GrantOutcome.Granted,
        };

        SetupHelpers.DisplaySetupSummary(results, logger);

        var lines = logger.AllOutput.Split('\n');
        var inheritableRow = System.Array.Find(lines, l => l.Contains("Inheritable Permissions"));
        inheritableRow.Should().NotBeNull();
        inheritableRow!.Should().Contain("configured",
            because: "Phase 2 completed successfully (real inheritance write), so the row must reflect that");
    }

    [Fact]
    public void DisplaySetupSummary_DwAdmin_TenantConsentGranted_PermissionGrantsRowLabel()
    {
        // Regression test: the "Blueprint Permission Grants" label rename must hold on the DW
        // admin-consent-success path. The label is shared between DW and non-DW (the rename was
        // applied for both) — if a future refactor reverts the DW side to plain "Permission Grants"
        // this assertion will catch it before users see inconsistent terminology across flows.
        var logger = new CapturingLogger();
        var results = new SetupResults
        {
            IsNonDwBlueprintFlow = false,
            BlueprintCreated = true,
            BlueprintId = BlueprintId,
            TenantId = TenantId,
            BatchPermissionsPhase1Completed = true,
            BatchPermissionsPhase2Completed = true,
            TenantWideConsentOutcome = Cli.Models.GrantOutcome.Granted,
        };

        SetupHelpers.DisplaySetupSummary(results, logger);

        var lines = logger.AllOutput.Split('\n');
        var grantsRow = System.Array.Find(lines, l => l.Contains("Blueprint Permission Grants"));
        grantsRow.Should().NotBeNull(
            because: "the renamed 'Blueprint Permission Grants' label must appear on the DW admin-consent-success path");
        grantsRow!.Should().Contain("granted",
            because: "tenant-wide delegated consent succeeded — the row must positively report the grant outcome");
        logger.AllOutput.Should().NotMatchRegex(@"^\s*\d+\.\s+Permission Grants\s",
            because: "the pre-rename 'Permission Grants' label (without the 'Blueprint' prefix) must not reappear");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static SetupResults BuildDelegatedPendingResults() => new()
    {
        IsNonDwBlueprintFlow = true,
        BlueprintCreated = true,
        BlueprintId = BlueprintId,
        AgentIdentityCreated = true,
        AgentIdentityId = AgentSpId,
        TenantId = TenantId,
        EffectiveAuthMode = Cli.Models.AuthMode.Obo,
        // Tenant-wide consent already granted by an admin — the only remaining gap is the
        // per-principal grant that failed locally. This is the narrow case where the PowerShell
        // hand-off block must still appear, because the tenant consent URL hand-off would be a
        // no-op (already done) and the per-principal grant is the only path forward.
        TenantWideConsentOutcome = Cli.Models.GrantOutcome.Granted,
        AgentIdentityDelegatedOutcome = Cli.Models.GrantOutcome.Failed,
        BatchPermissionsPhase1Completed = true,
        BatchPermissionsPhase2Completed = true,
    };

    private static SetupResults BuildS2SPendingResults() => new()
    {
        IsNonDwBlueprintFlow = true,
        BlueprintCreated = true,
        BlueprintId = BlueprintId,
        AgentIdentityCreated = true,
        AgentIdentityId = AgentSpId,
        TenantId = TenantId,
        EffectiveAuthMode = Cli.Models.AuthMode.S2s,
        AgentIdentityS2SOutcome = Cli.Models.GrantOutcome.Failed,
        BatchPermissionsPhase1Completed = true,
        BatchPermissionsPhase2Completed = true,
    };
}
