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
            SubscriptionId = "sub-id",
            ClientAppId = "client-app-id",
            Location = "eastus",
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
    public void PrintDryRunPlan_IncludesGraphPermissions()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        AnyLogContains("Microsoft Graph").Should().BeTrue(because: "Microsoft Graph is a required permission resource");
    }

    [Fact]
    public void PrintDryRunPlan_IncludesAgent365ToolsPermissions()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        AnyLogContains("Agent 365 Tools").Should().BeTrue(because: "Agent 365 Tools is a required permission resource");
    }

    [Fact]
    public void PrintDryRunPlan_IncludesAgentRegistrationStep()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        AnyLogContains("Agent Registration").Should().BeTrue(because: "agent registration is a required setup step");
    }
}
