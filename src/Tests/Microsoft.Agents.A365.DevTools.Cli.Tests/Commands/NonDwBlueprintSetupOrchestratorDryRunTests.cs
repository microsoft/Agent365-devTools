// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Tests for NonDwBlueprintSetupOrchestrator.PrintDryRunPlan.
/// Assertions pin requirements (what information must appear), not presentation
/// (how it is phrased), so that wording changes do not cause false failures.
/// </summary>
public class NonDwBlueprintSetupOrchestratorDryRunTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();

    private static Agent365Config BuildConfig(
        string displayName = "My Agent",
        string tenantId = "tenant-id",
        string? blueprintId = null) =>
        new()
        {
            AgentIdentityDisplayName = displayName,
            TenantId = tenantId,
            AiTeammate = false,
            UseBlueprint = true,
            ClientAppId = "client-app-id",
            DeploymentProjectPath = "./app",
            AgentBlueprintId = blueprintId
        };

    private bool AnyLogContains(string value) =>
        _logger.ReceivedCalls()
            .Any(c => c.GetArguments()[2]?.ToString()?.Contains(value, StringComparison.OrdinalIgnoreCase) == true);

    [Fact]
    public void PrintDryRunPlan_LogsHeader()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        // Header identifies this as a dry run
        AnyLogContains("Dry run").Should().BeTrue(because: "output must identify itself as a dry run");
    }

    [Fact]
    public void PrintDryRunPlan_IncludesRunWithoutDryRunInstruction()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        // Footer tells the user how to execute for real
        AnyLogContains("--dry-run").Should().BeTrue(because: "footer must tell the user to run without --dry-run");
    }

    [Fact]
    public void PrintDryRunPlan_WithoutExistingBlueprint_ShowsCreate()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(blueprintId: null), _logger);

        // Blueprint creation path: must mention multi-tenant (factual attribute of the app registration)
        AnyLogContains("multi-tenant").Should().BeTrue(because: "new blueprint is created as multi-tenant");
    }

    [Fact]
    public void PrintDryRunPlan_WithExistingBlueprint_ShowsReuse()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(blueprintId: "existing-bp-id"), _logger);

        // Reuse path: must surface the existing blueprint ID so the user can verify
        AnyLogContains("existing-bp-id").Should().BeTrue(because: "existing blueprint ID must appear so the user can verify the correct one is used");
    }

    [Fact]
    public void PrintDryRunPlan_WithExistingBlueprint_DoesNotShowCreate()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(blueprintId: "existing-bp-id"), _logger);

        // Reuse path must not imply a new blueprint will be created
        AnyLogContains("multi-tenant").Should().BeFalse(because: "reuse path must not suggest a new blueprint will be created");
    }

    [Fact]
    public void PrintDryRunPlan_IncludesDisplayName()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(displayName: "Contoso Agent"), _logger);

        AnyLogContains("Contoso Agent").Should().BeTrue(because: "agent display name must appear so the user can confirm the correct agent");
    }

    [Fact]
    public void PrintDryRunPlan_BlueprintPermissions_AreConfigured()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        // Blueprint permissions must be stamped in the non-DW flow so MAC and other dependent
        // systems can see the agent's permission set. Issue #417 — same spec set is applied to
        // both the blueprint and the agent identity SP. The label is now unified with the DW
        // flow as "Inheritable Permissions" because both flows call SetInheritablePermissionsAsync.
        AnyLogContains("Inheritable Permissions").Should().BeTrue(because: "non-DW setup must stamp permissions on the blueprint via SetInheritablePermissionsAsync so MAC can see them (issue #417); label is unified with DW");
        AnyLogContains("Observability API").Should().BeTrue(because: "Observability API is part of the non-DW spec set stamped on the blueprint");
        AnyLogContains("Power Platform API").Should().BeTrue(because: "Power Platform API is part of the non-DW spec set stamped on the blueprint");
    }

    [Fact]
    public void PrintDryRunPlan_DelegatedGrants_ArePlannedForAgentIdentity()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        // Default (null/obo) authMode grants principal-scoped delegated permissions to the
        // agent identity SP instead of using AllPrincipals grants on the blueprint.
        AnyLogContains("delegated").Should().BeTrue(because: "default OBO authMode applies principal-scoped delegated grants to the agent identity SP");
    }

    [Fact]
    public void PrintDryRunPlan_DoesNotIncludeMessagingBotApi()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        AnyLogContains("Messaging Bot API").Should().BeFalse(because: "Messaging Bot API is DW-only and must not appear in non-DW dry-run output");
    }

    [Fact]
    public void PrintDryRunPlan_DoesNotIncludeMicrosoftGraph()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        AnyLogContains("Microsoft Graph").Should().BeFalse(because: "Microsoft Graph is DW-only and must not appear in non-DW dry-run output");
    }

    [Fact]
    public void PrintDryRunPlan_IncludesAgentRegistrationStep()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        AnyLogContains("Agent Registration").Should().BeTrue(because: "agent registration is a required setup step");
    }

    // ── authMode dry-run output ────────────────────────────────────────────────

    /// <summary>
    /// OBO mode must show delegated (principal-scoped) grants on the agent identity SP.
    /// authMode controls only the agent-identity grant style; the blueprint step is independent
    /// and always uses AllPrincipals grants on the blueprint (issue #417).
    /// </summary>
    [Fact]
    public void PrintDryRunPlan_AuthModeObo_ShowsDelegatedGrantsOnAgentIdentity()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger, authMode: "obo");

        AnyLogContains("delegated").Should().BeTrue(because: "OBO mode applies principal-scoped delegated grants to the agent identity SP");
    }

    /// <summary>
    /// S2S mode must show application permissions on the agent identity SP and must not show
    /// delegated grants — there is no user context in S2S so delegated scopes don't apply.
    /// authMode only affects the agent-identity step; the blueprint step is independent.
    /// </summary>
    [Fact]
    public void PrintDryRunPlan_AuthModeS2s_ShowsAppPermsOnAgentIdentity_NoDelegatedGrants()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger, authMode: "s2s");

        AnyLogContains("S2S app roles").Should().BeTrue(because: "S2S mode applies app role assignments to the agent identity SP");
        AnyLogContains("delegated").Should().BeFalse(because: "S2S mode must not show delegated grants on the agent identity SP — no user context");
    }

    /// <summary>
    /// Both mode must surface both delegated grants and application permissions on the
    /// agent identity SP.
    /// </summary>
    [Fact]
    public void PrintDryRunPlan_AuthModeBoth_ShowsBothGrantRowsOnAgentIdentity()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger, authMode: "both");

        AnyLogContains("delegated").Should().BeTrue(because: "Both mode includes OBO delegated grants on the agent identity SP");
        AnyLogContains("S2S app roles").Should().BeTrue(because: "Both mode includes S2S app role assignments on the agent identity SP");
    }

    /// <summary>
    /// Null authMode must default to OBO behaviour — the agent-identity step shows
    /// delegated grants.
    /// </summary>
    [Fact]
    public void PrintDryRunPlan_NullAuthMode_TreatedAsObo()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger, authMode: null);

        AnyLogContains("delegated").Should().BeTrue(because: "null authMode defaults to OBO — delegated grants must appear on the agent identity SP");
    }

    /// <summary>
    /// All authMode values must stamp the same permission set on the blueprint (issue #417).
    /// The blueprint step is independent of authMode — authMode only changes how grants are
    /// applied to the agent identity SP.
    /// </summary>
    [Theory]
    [InlineData("obo")]
    [InlineData("s2s")]
    [InlineData("both")]
    [InlineData(null)]
    public void PrintDryRunPlan_AllAuthModes_ConfigureBlueprintPermissions(string? authMode)
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger, authMode: authMode);

        AnyLogContains("Inheritable Permissions").Should().BeTrue(because: $"authmode '{authMode ?? "null (obo)"}' must stamp blueprint permissions via SetInheritablePermissionsAsync — the blueprint step is independent of authMode (issue #417)");
    }

    // ── --agent-registration-only dry-run ─────────────────────────────────────

    [Fact]
    public void PrintDryRunPlan_AgentRegistrationOnly_SkipMessageReferencesSteps1Through4()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger, agentRegistrationOnly: true);

        AnyLogContains("Steps 1-4").Should().BeTrue(
            because: "--agent-registration-only skips Prerequisites, Blueprint, Inheritable Permissions, and Blueprint Permission Grants — exactly 4 steps in the new blueprint-grouped layout (grants come before Agent identity so all blueprint-side rows are contiguous)");
        AnyLogContains("Blueprint Permission Grants").Should().BeTrue(
            because: "the skip summary must enumerate Blueprint Permission Grants by name so users know grants are not re-attempted in registration-only mode");
        // Note: deliberately no negative assertion against the old pre-reorder "Steps 1-3" label.
        // The positive Steps 1-4 + "Blueprint Permission Grants" assertions above already lock the
        // current contract; an absence check against an obsolete string is a stale-string guard,
        // not a requirement, and it would fail under any future relabel (e.g. "Phases 1-4") without
        // indicating the contract is broken.
    }

    [Fact]
    public void PrintDryRunPlan_AgentRegistrationOnly_ShowsAllRemainingSteps()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger, agentRegistrationOnly: true);

        AnyLogContains("Agent identity").Should().BeTrue(
            because: "step 5 (agent identity) must appear when --agent-registration-only is set");
        AnyLogContains("Blueprint Permission Grants").Should().BeTrue(
            because: "the 'Steps 1-4 are skipped' summary must enumerate Blueprint Permission Grants as one of the skipped steps so users know grants are not re-attempted");
        AnyLogContains("Agent Registration").Should().BeTrue(
            because: "step 6 (agent registration) is the primary purpose of the flag");
        AnyLogContains("Messaging endpoint").Should().BeTrue(
            because: "step 7 must appear even in agent-registration-only mode");
        AnyLogContains("Project settings").Should().BeTrue(
            because: "step 8 (project settings) must appear even in agent-registration-only mode");
    }

    [Fact]
    public void PrintDryRunPlan_AgentRegistrationOnly_WithExistingAgentId_ShowsReuseAndId()
    {
        var config = BuildConfig();
        config.AgenticAppId = "existing-agent-sp-id";

        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(config, _logger, agentRegistrationOnly: true);

        AnyLogContains("existing-agent-sp-id").Should().BeTrue(
            because: "when AgenticAppId is set the existing agent identity ID must appear so the user can verify the correct SP is used");
        AnyLogContains("reuse").Should().BeTrue(
            because: "existing agent identity must be labelled 'reuse', not 'create'");
    }
}
