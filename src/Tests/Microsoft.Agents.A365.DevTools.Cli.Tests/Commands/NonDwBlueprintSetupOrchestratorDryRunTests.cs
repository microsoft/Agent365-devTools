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
/// Tests for NonDwBlueprintSetupOrchestrator.PrintDryRunPlan — Phase A dry-run output.
/// Verifies that the plan is printed with correct values from config and no API calls are made.
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

    [Fact]
    public void PrintDryRunPlan_LogsHeader()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("dry run") && o.ToString()!.Contains("no changes")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_WithoutExistingBlueprint_ShowsCreate()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(blueprintId: null), _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Create Blueprint") && o.ToString()!.Contains("multi-tenant")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_WithExistingBlueprint_ShowsReuse()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(blueprintId: "existing-bp-id"), _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Reuse Blueprint") && o.ToString()!.Contains("existing-bp-id")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_IncludesDisplayName()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(displayName: "Contoso Agent"), _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Contoso Agent")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_IncludesGraphPermissions()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("User.Read")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_IncludesAgent365ToolsPermissions()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("McpServers.Mail.All")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_IncludesTenantId()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(tenantId: "my-tenant-id"), _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("my-tenant-id")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_IncludesAgentInstanceRegistration()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Agent Instance") && o.ToString()!.Contains("Graph API")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_MentionsNoManifest()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("no manifest")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_IncludesRunWithoutDryRunInstruction()
    {
        NonDwBlueprintSetupOrchestrator.PrintDryRunPlan(BuildConfig(), _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("--dry-run")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }
}
