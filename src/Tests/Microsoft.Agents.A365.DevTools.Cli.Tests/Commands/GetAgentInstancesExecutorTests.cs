// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Commands.DevelopSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class GetAgentInstancesExecutorTests
{
    private readonly ILogger _mockLogger;
    private readonly AgentBlueprintService _mockBlueprintService;

    public GetAgentInstancesExecutorTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        var graphService = Substitute.For<GraphApiService>();
        _mockBlueprintService = Substitute.For<AgentBlueprintService>(
            NullLogger<AgentBlueprintService>.Instance, graphService);
    }

    [Fact]
    public async Task ExecuteAsync_WithNullBlueprintId_ReturnsFalse()
    {
        var executor = new GetAgentInstancesExecutor(_mockLogger, _mockBlueprintService);

        var result = await executor.ExecuteAsync(null!, null, CancellationToken.None);

        result.Should().BeFalse(because: "null blueprint ID is not valid");
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyBlueprintId_ReturnsFalse()
    {
        var executor = new GetAgentInstancesExecutor(_mockLogger, _mockBlueprintService);

        var result = await executor.ExecuteAsync("", null, CancellationToken.None);

        result.Should().BeFalse(because: "empty blueprint ID is not valid");
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidGuidBlueprintId_ReturnsFalse()
    {
        var executor = new GetAgentInstancesExecutor(_mockLogger, _mockBlueprintService);

        var result = await executor.ExecuteAsync("not-a-guid", "00000000-0000-0000-0000-000000000001", CancellationToken.None);

        result.Should().BeFalse(because: "blueprint ID must be a valid GUID");
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidTenantId_ReturnsFalse()
    {
        var executor = new GetAgentInstancesExecutor(_mockLogger, _mockBlueprintService);

        var result = await executor.ExecuteAsync(
            "00000000-0000-0000-0000-000000000001",
            "not-a-guid",
            CancellationToken.None);

        result.Should().BeFalse(because: "tenant ID must be a valid GUID");
    }

    [Fact]
    public async Task ExecuteAsync_WithValidInputs_NoInstances_ReturnsTrue()
    {
        var tenantId = "00000000-0000-0000-0000-000000000001";
        var blueprintId = "00000000-0000-0000-0000-000000000002";

        _mockBlueprintService
            .GetAgentInstancesForBlueprintAsync(tenantId, blueprintId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentInstanceInfo>>(Array.Empty<AgentInstanceInfo>()));

        var executor = new GetAgentInstancesExecutor(_mockLogger, _mockBlueprintService);
        var result = await executor.ExecuteAsync(blueprintId, tenantId, CancellationToken.None);

        result.Should().BeTrue(because: "no instances is a valid successful result");
    }

    [Fact]
    public async Task ExecuteAsync_WithValidInputs_WithInstances_ReturnsTrue()
    {
        var tenantId = "00000000-0000-0000-0000-000000000001";
        var blueprintId = "00000000-0000-0000-0000-000000000002";

        var instances = new List<AgentInstanceInfo>
        {
            new() { IdentitySpId = "sp-id-1", DisplayName = "Agent 1", AgentUserId = "user-1" },
            new() { IdentitySpId = "sp-id-2", DisplayName = "Agent 2", AgentUserId = null },
        };

        _mockBlueprintService
            .GetAgentInstancesForBlueprintAsync(tenantId, blueprintId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentInstanceInfo>>(instances));

        var executor = new GetAgentInstancesExecutor(_mockLogger, _mockBlueprintService);
        var result = await executor.ExecuteAsync(blueprintId, tenantId, CancellationToken.None);

        result.Should().BeTrue(because: "listing instances should succeed");
    }

    [Fact]
    public async Task ExecuteAsync_WhenServiceThrows_ReturnsFalse()
    {
        var tenantId = "00000000-0000-0000-0000-000000000001";
        var blueprintId = "00000000-0000-0000-0000-000000000002";

        _mockBlueprintService
            .GetAgentInstancesForBlueprintAsync(tenantId, blueprintId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Graph API failure"));

        var executor = new GetAgentInstancesExecutor(_mockLogger, _mockBlueprintService);
        var result = await executor.ExecuteAsync(blueprintId, tenantId, CancellationToken.None);

        result.Should().BeFalse(because: "service exception should cause failure");
    }
}

public class GetAgentInstancesSubcommandTests
{
    private readonly ILogger _mockLogger;
    private readonly IAgent365ToolingService _mockToolingService;
    private readonly AgentBlueprintService _mockBlueprintService;

    public GetAgentInstancesSubcommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockToolingService = Substitute.For<IAgent365ToolingService>();
        var graphService = Substitute.For<GraphApiService>();
        _mockBlueprintService = Substitute.For<AgentBlueprintService>(
            NullLogger<AgentBlueprintService>.Instance, graphService);
    }

    [Fact]
    public void CreateCommand_WithBlueprintService_IncludesGetAgentInstances()
    {
        var command = DevelopMcpCommand.CreateCommand(
            _mockLogger, _mockToolingService, agentBlueprintService: _mockBlueprintService);

        command.Subcommands.Select(sc => sc.Name).Should().Contain(
            "get-agent-instances",
            because: "providing the blueprint service should register the get-agent-instances subcommand");
    }

    [Fact]
    public void CreateCommand_WithoutBlueprintService_DoesNotIncludeGetAgentInstances()
    {
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);

        command.Subcommands.Select(sc => sc.Name).Should().NotContain(
            "get-agent-instances",
            because: "get-agent-instances requires the blueprint service");
    }

    [Fact]
    public void GetAgentInstancesSubcommand_HasCorrectOptions()
    {
        var command = DevelopMcpCommand.CreateCommand(
            _mockLogger, _mockToolingService, agentBlueprintService: _mockBlueprintService);
        var subcommand = command.Subcommands.First(sc => sc.Name == "get-agent-instances");

        var optionNames = subcommand.Options.Select(o => o.Name).ToList();
        optionNames.Should().Contain("blueprint-id");
        optionNames.Should().Contain("tenant-id");
        optionNames.Should().Contain("verbose");

        var blueprintOption = subcommand.Options.First(o => o.Name == "blueprint-id");
        blueprintOption.IsRequired.Should().BeTrue(because: "--blueprint-id is required for get-agent-instances");
        blueprintOption.Aliases.Should().Contain("-b");
    }
}
