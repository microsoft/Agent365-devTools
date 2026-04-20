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
/// Tests for NonDwSetupOrchestrator.PrintDryRunPlan — Phase A dry-run output.
/// Verifies that the plan is printed with correct values from config and no Azure API calls are made.
/// </summary>
public class NonDwSetupOrchestratorDryRunTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();

    private static Agent365Config BuildConfig(
        string displayName = "My Agent",
        string resourceGroup = "rg-test",
        string webAppName = "webapp-myagent",
        string appServicePlanName = "asp-myagent",
        string appServicePlanSku = "B1",
        string? messagingEndpoint = null,
        bool needDeployment = true,
        bool needAzureOpenAI = false,
        string? azureOpenAIName = null,
        string? azureOpenAILocation = null,
        string? azureOpenAIModelDeploymentName = null) =>
        new()
        {
            AgentIdentityDisplayName = displayName,
            ResourceGroup = resourceGroup,
            WebAppName = webAppName,
            AppServicePlanName = appServicePlanName,
            AppServicePlanSku = appServicePlanSku,
            MessagingEndpoint = messagingEndpoint ?? string.Empty,
            NeedDeployment = needDeployment,
            NeedAzureOpenAI = needAzureOpenAI,
            AzureOpenAIName = azureOpenAIName,
            AzureOpenAILocation = azureOpenAILocation,
            AzureOpenAIModelDeploymentName = azureOpenAIModelDeploymentName,
            AiTeammate = false,
            TenantId = "tenant-id",
            SubscriptionId = "sub-id",
            ClientAppId = "client-app-id",
            Location = "eastus",
            DeploymentProjectPath = "./app"
        };

    [Fact]
    public void PrintDryRunPlan_LogsHeader()
    {
        var config = BuildConfig();

        NonDwSetupOrchestrator.PrintDryRunPlan(config, _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("dry run") && o.ToString()!.Contains("no changes")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_IncludesAgentDisplayName()
    {
        var config = BuildConfig(displayName: "Contoso Sales Agent");

        NonDwSetupOrchestrator.PrintDryRunPlan(config, _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Contoso Sales Agent")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_IncludesResourceGroupName()
    {
        var config = BuildConfig(resourceGroup: "rg-contoso-prod");

        NonDwSetupOrchestrator.PrintDryRunPlan(config, _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("rg-contoso-prod")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_IncludesTeamsClientIds()
    {
        var config = BuildConfig();

        NonDwSetupOrchestrator.PrintDryRunPlan(config, _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains(NonDwSetupOrchestrator.TeamsDesktopMobileClientId)),
            null,
            Arg.Any<Func<object, Exception?, string>>());

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains(NonDwSetupOrchestrator.TeamsWebClientId)),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_IncludesGraphPermissions()
    {
        var config = BuildConfig();

        NonDwSetupOrchestrator.PrintDryRunPlan(config, _logger);

        // At least User.Read should appear
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
        var config = BuildConfig();

        NonDwSetupOrchestrator.PrintDryRunPlan(config, _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("McpServers.Mail.All")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_IncludesOboConnectionName()
    {
        var config = BuildConfig();

        NonDwSetupOrchestrator.PrintDryRunPlan(config, _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains(NonDwSetupOrchestrator.OboConnectionName)),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_WithNeedDeployment_IncludesWebAppName()
    {
        var config = BuildConfig(webAppName: "webapp-contoso", needDeployment: true);

        NonDwSetupOrchestrator.PrintDryRunPlan(config, _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("webapp-contoso")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_WithNeedDeploymentFalse_SkipsInfrastructure()
    {
        var config = BuildConfig(
            webAppName: "webapp-contoso",
            needDeployment: false,
            messagingEndpoint: "https://my-bot.example.com/api/messages");

        NonDwSetupOrchestrator.PrintDryRunPlan(config, _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Skip") && o.ToString()!.Contains("Deployment")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_WithNeedAzureOpenAI_IncludesAoaiResource()
    {
        var config = BuildConfig(
            needAzureOpenAI: true,
            azureOpenAIName: "aoai-contoso",
            azureOpenAILocation: "swedencentral",
            azureOpenAIModelDeploymentName: "gpt-4.1");

        NonDwSetupOrchestrator.PrintDryRunPlan(config, _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("aoai-contoso")),
            null,
            Arg.Any<Func<object, Exception?, string>>());

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("gpt-4.1")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_WithoutNeedAzureOpenAI_DoesNotIncludeAoaiLine()
    {
        var config = BuildConfig(needAzureOpenAI: false, azureOpenAIName: "aoai-should-not-appear");

        NonDwSetupOrchestrator.PrintDryRunPlan(config, _logger);

        _logger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("aoai-should-not-appear")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_DerivesMessagingEndpointFromWebAppName_WhenEndpointNotSet()
    {
        var config = BuildConfig(webAppName: "webapp-mybot", messagingEndpoint: null, needDeployment: true);

        NonDwSetupOrchestrator.PrintDryRunPlan(config, _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("webapp-mybot.azurewebsites.net/api/messages")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_UsesExplicitMessagingEndpoint_WhenSet()
    {
        var config = BuildConfig(messagingEndpoint: "https://custom.endpoint.example.com/api/messages");

        NonDwSetupOrchestrator.PrintDryRunPlan(config, _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("custom.endpoint.example.com")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void PrintDryRunPlan_IncludesRunWithoutDryRunInstruction()
    {
        var config = BuildConfig();

        NonDwSetupOrchestrator.PrintDryRunPlan(config, _logger);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("--dry-run")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }
}
