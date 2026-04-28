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
    public void PrintDryRunPlan_InheritablePermissions_AreSkipped()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        // Phase 2a (inheritable permissions) is skipped in all authMode values to avoid Global Admin
        // involvement. The dry-run communicates this explicitly rather than listing specific APIs.
        AnyLogContains("skipped").Should().BeTrue(because: "inheritable permissions are skipped in all authMode values to avoid Global Admin involvement");
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
    /// OBO mode must show delegated grants and must not surface admin-consent or AllPrincipals
    /// instructions — the whole point of OBO authMode is to avoid Global Admin involvement.
    /// </summary>
    [Fact]
    public void PrintDryRunPlan_AuthModeObo_ShowsDelegatedGrants_NoAdminConsentOrAllPrincipals()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger, authMode: "obo");

        AnyLogContains("delegated").Should().BeTrue(because: "OBO mode applies principal-scoped delegated grants");
        AnyLogContains("admin consent").Should().BeFalse(because: "OBO mode must not require admin consent");
        AnyLogContains("AllPrincipals").Should().BeFalse(because: "OBO mode must not create AllPrincipals grants");
    }

    /// <summary>
    /// S2S mode must show application permissions and must not show delegated grants
    /// — there is no user context in S2S so delegated scopes are not applicable.
    /// </summary>
    [Fact]
    public void PrintDryRunPlan_AuthModeS2s_ShowsAppPerms_NoDelegatedGrants()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger, authMode: "s2s");

        AnyLogContains("application permissions").Should().BeTrue(because: "S2S mode applies app role assignments");
        AnyLogContains("Delegated").Should().BeFalse(because: "S2S mode must not show delegated grants — no user context");
        AnyLogContains("admin consent").Should().BeFalse(because: "S2S mode falls back to PowerShell instructions; no interactive admin consent prompt");
    }

    /// <summary>
    /// Both mode must surface both delegated grants (OBO) and application permissions (S2S).
    /// </summary>
    [Fact]
    public void PrintDryRunPlan_AuthModeBoth_ShowsBothGrantRows()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger, authMode: "both");

        AnyLogContains("delegated").Should().BeTrue(because: "Both mode includes OBO delegated grants");
        AnyLogContains("application permissions").Should().BeTrue(because: "Both mode includes S2S app permissions");
        AnyLogContains("admin consent").Should().BeFalse(because: "Both mode must not require interactive admin consent");
    }

    /// <summary>
    /// Null authMode must default to OBO behaviour — the plan shows delegated grants
    /// and must not imply admin consent is required.
    /// </summary>
    [Fact]
    public void PrintDryRunPlan_NullAuthMode_TreatedAsObo()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger, authMode: null);

        AnyLogContains("delegated").Should().BeTrue(because: "null authMode defaults to OBO — delegated grants must appear");
        AnyLogContains("admin consent").Should().BeFalse(because: "null authMode defaults to OBO — admin consent must not be required");
    }

    /// <summary>
    /// All authMode values must suppress the inheritable-permissions (Phase 2a) step
    /// to avoid Global Admin involvement.
    /// </summary>
    [Theory]
    [InlineData("obo")]
    [InlineData("s2s")]
    [InlineData("both")]
    [InlineData(null)]
    public void PrintDryRunPlan_AllAuthModes_SkipInheritablePermissions(string? authMode)
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger, authMode: authMode);

        AnyLogContains("skipped").Should().BeTrue(because: $"authmode '{authMode ?? "null (obo)"}' must skip inheritable permissions to avoid Global Admin involvement");
    }
}
