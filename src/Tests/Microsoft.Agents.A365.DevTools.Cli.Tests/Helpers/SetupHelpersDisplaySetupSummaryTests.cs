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

        SetupHelpers.DisplaySetupSummary(results, logger, isDw: false);

        logger.AllOutput.Should().Contain("Action Required",
            because: "when delegated grants are pending the summary must flag an action item");
    }

    [Fact]
    public void DisplaySetupSummary_PendingDelegatedAction_EmitsPowerShellBlock()
    {
        var logger = new CapturingLogger();
        var results = BuildDelegatedPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger, isDw: false);

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

        SetupHelpers.DisplaySetupSummary(results, logger, isDw: false);

        logger.AllOutput.Should().Contain(AgentSpId,
            because: "the PowerShell block must include the agent identity SP object ID so the admin targets the correct principal");
    }

    [Fact]
    public void DisplaySetupSummary_PendingDelegatedAction_EmbedsTenantId()
    {
        var logger = new CapturingLogger();
        var results = BuildDelegatedPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger, isDw: false);

        logger.AllOutput.Should().Contain(TenantId,
            because: "the Connect-MgGraph call must include -TenantId so the admin targets the correct tenant");
    }

    [Fact]
    public void DisplaySetupSummary_PendingDelegatedAction_EmitsRequiredRoles()
    {
        var logger = new CapturingLogger();
        var results = BuildDelegatedPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger, isDw: false);

        logger.AllOutput.Should().Contain(AuthenticationConstants.DelegatedGrantRequiredRoles,
            because: "the required role must be surfaced so the admin knows which Entra role is needed");
    }

    // ── pendingS2SAction (non-DW path) ─────────────────────────────────────────

    [Fact]
    public void DisplaySetupSummary_PendingS2SAction_NonDw_EmitsActionRequiredHeader()
    {
        var logger = new CapturingLogger();
        var results = BuildS2SPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger, isDw: false);

        logger.AllOutput.Should().Contain("Action Required",
            because: "when S2S grants are pending the summary must flag an action item");
    }

    [Fact]
    public void DisplaySetupSummary_PendingS2SAction_NonDw_EmitsPowerShellBlock()
    {
        var logger = new CapturingLogger();
        var results = BuildS2SPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger, isDw: false);

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

        SetupHelpers.DisplaySetupSummary(results, logger, isDw: false);

        logger.AllOutput.Should().Contain(AgentSpId,
            because: "the non-DW S2S block must use the agent identity SP object ID, not the blueprint app ID");
    }

    [Fact]
    public void DisplaySetupSummary_PendingS2SAction_NonDw_EmitsRequiredRoles()
    {
        var logger = new CapturingLogger();
        var results = BuildS2SPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger, isDw: false);

        logger.AllOutput.Should().Contain(AuthenticationConstants.S2SGrantRequiredRoles,
            because: "the required S2S role must be surfaced so the admin knows which Entra role is needed");
    }

    [Fact]
    public void DisplaySetupSummary_PendingS2SAction_NonDw_EmbedsTenantId()
    {
        var logger = new CapturingLogger();
        var results = BuildS2SPendingResults();

        SetupHelpers.DisplaySetupSummary(results, logger, isDw: false);

        logger.AllOutput.Should().Contain(TenantId,
            because: "the Connect-MgGraph call must include -TenantId so the admin targets the correct tenant");
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
        AgentIdentityDelegatedGrantPending = true,
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
        EffectiveAuthMode = "s2s",
        S2SAppRoleGranted = false,
        BatchPermissionsPhase1Completed = true,
        BatchPermissionsPhase2Completed = true,
    };
}
