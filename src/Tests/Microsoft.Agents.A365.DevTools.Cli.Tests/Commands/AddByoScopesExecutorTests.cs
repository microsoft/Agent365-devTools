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

public class AddByoScopesExecutorTests
{
    private readonly ILogger _mockLogger;
    private readonly IAgent365ToolingService _mockToolingService;
    private readonly AgentBlueprintService _mockBlueprintService;
    private readonly GraphApiService _mockGraphApiService;

    public AddByoScopesExecutorTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockToolingService = Substitute.For<IAgent365ToolingService>();
        _mockGraphApiService = Substitute.For<GraphApiService>();
        _mockBlueprintService = Substitute.For<AgentBlueprintService>(
            NullLogger<AgentBlueprintService>.Instance, _mockGraphApiService);
    }

    [Fact]
    public async Task ExecuteAsync_WithNeitherBlueprintNorInstances_ReturnsFalse()
    {
        var executor = new AddByoScopesExecutor(_mockLogger, _mockToolingService, _mockBlueprintService, _mockGraphApiService);

        var result = await executor.ExecuteAsync("ext_server1", null, null, "00000000-0000-0000-0000-000000000001", false, CancellationToken.None);

        result.Should().BeFalse(because: "at least one of --blueprint-id or --agent-instances is required");
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyServerNames_ReturnsFalse()
    {
        var executor = new AddByoScopesExecutor(_mockLogger, _mockToolingService, _mockBlueprintService, _mockGraphApiService);

        var result = await executor.ExecuteAsync("", "00000000-0000-0000-0000-000000000001", null, "00000000-0000-0000-0000-000000000002", false, CancellationToken.None);

        result.Should().BeFalse(because: "empty server names is not valid");
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidBlueprintId_ReturnsFalse()
    {
        var executor = new AddByoScopesExecutor(_mockLogger, _mockToolingService, _mockBlueprintService, _mockGraphApiService);

        var result = await executor.ExecuteAsync("ext_server1", "not-a-guid", null, "00000000-0000-0000-0000-000000000001", false, CancellationToken.None);

        result.Should().BeFalse(because: "blueprint ID must be a valid GUID");
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidTenantId_ReturnsFalse()
    {
        var executor = new AddByoScopesExecutor(_mockLogger, _mockToolingService, _mockBlueprintService, _mockGraphApiService);

        var result = await executor.ExecuteAsync(
            "ext_server1",
            "00000000-0000-0000-0000-000000000001",
            null,
            "not-a-guid",
            false,
            CancellationToken.None);

        result.Should().BeFalse(because: "tenant ID must be a valid GUID");
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidAgentInstanceId_ReturnsFalse()
    {
        var executor = new AddByoScopesExecutor(_mockLogger, _mockToolingService, _mockBlueprintService, _mockGraphApiService);

        var result = await executor.ExecuteAsync(
            "ext_server1",
            null,
            "not-a-guid",
            "00000000-0000-0000-0000-000000000001",
            false,
            CancellationToken.None);

        result.Should().BeFalse(because: "agent instance SP IDs must be valid GUIDs");
    }

    [Fact]
    public async Task ExecuteAsync_DryRun_WithBlueprint_ReturnsTrue()
    {
        var tenantId = "00000000-0000-0000-0000-000000000001";
        var blueprintId = "00000000-0000-0000-0000-000000000002";

        var instances = new List<AgentInstanceInfo>
        {
            new() { IdentitySpId = "00000000-0000-0000-0000-000000000003", DisplayName = "Agent 1" },
        };

        _mockBlueprintService
            .GetAgentInstancesForBlueprintAsync(tenantId, blueprintId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentInstanceInfo>>(instances));

        var executor = new AddByoScopesExecutor(_mockLogger, _mockToolingService, _mockBlueprintService, _mockGraphApiService);
        var result = await executor.ExecuteAsync("ext_server1", blueprintId, null, tenantId, dryRun: true, CancellationToken.None);

        result.Should().BeTrue(because: "dry run should succeed without making API calls");

        // Verify no tooling or Graph calls were made
        await _mockToolingService.DidNotReceive().GetMcpServerAppIdByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_BlueprintReturnsNoInstances_ReturnsFalse()
    {
        var tenantId = "00000000-0000-0000-0000-000000000001";
        var blueprintId = "00000000-0000-0000-0000-000000000002";

        _mockBlueprintService
            .GetAgentInstancesForBlueprintAsync(tenantId, blueprintId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentInstanceInfo>>(Array.Empty<AgentInstanceInfo>()));

        var executor = new AddByoScopesExecutor(_mockLogger, _mockToolingService, _mockBlueprintService, _mockGraphApiService);
        var result = await executor.ExecuteAsync("ext_server1", blueprintId, null, tenantId, false, CancellationToken.None);

        result.Should().BeFalse(because: "no resolved instances means nothing to grant");
    }

    [Fact]
    public async Task ExecuteAsync_AppIdLookupFails_ReturnsFalse()
    {
        var tenantId = "00000000-0000-0000-0000-000000000001";
        var instanceSpId = "00000000-0000-0000-0000-000000000003";

        _mockToolingService
            .GetMcpServerAppIdByNameAsync("ext_server1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<McpServerAppIdResponse?>(null));

        var executor = new AddByoScopesExecutor(_mockLogger, _mockToolingService, _mockBlueprintService, _mockGraphApiService);
        var result = await executor.ExecuteAsync("ext_server1", null, instanceSpId, tenantId, false, CancellationToken.None);

        result.Should().BeFalse(because: "failing to resolve PPMI app ID should cause failure");
    }
}

public class AddByoScopesSubcommandTests
{
    private readonly ILogger _mockLogger;
    private readonly IAgent365ToolingService _mockToolingService;
    private readonly AgentBlueprintService _mockBlueprintService;
    private readonly GraphApiService _mockGraphApiService;

    public AddByoScopesSubcommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockToolingService = Substitute.For<IAgent365ToolingService>();
        _mockGraphApiService = Substitute.For<GraphApiService>();
        _mockBlueprintService = Substitute.For<AgentBlueprintService>(
            NullLogger<AgentBlueprintService>.Instance, _mockGraphApiService);
    }

    [Fact]
    public void CreateCommand_WithBlueprintAndGraphServices_IncludesAddByoScopes()
    {
        var command = DevelopMcpCommand.CreateCommand(
            _mockLogger, _mockToolingService,
            graphApiService: _mockGraphApiService,
            agentBlueprintService: _mockBlueprintService);

        command.Subcommands.Select(sc => sc.Name).Should().Contain(
            "add-byo-scopes",
            because: "both blueprint and graph services are provided");
    }

    [Fact]
    public void CreateCommand_WithoutGraphService_DoesNotIncludeAddByoScopes()
    {
        var command = DevelopMcpCommand.CreateCommand(
            _mockLogger, _mockToolingService,
            agentBlueprintService: _mockBlueprintService);

        command.Subcommands.Select(sc => sc.Name).Should().NotContain(
            "add-byo-scopes",
            because: "add-byo-scopes requires the graph API service");
    }

    [Fact]
    public void CreateCommand_WithoutBlueprintService_DoesNotIncludeAddByoScopes()
    {
        var command = DevelopMcpCommand.CreateCommand(
            _mockLogger, _mockToolingService,
            graphApiService: _mockGraphApiService);

        command.Subcommands.Select(sc => sc.Name).Should().NotContain(
            "add-byo-scopes",
            because: "add-byo-scopes requires the blueprint service");
    }

    [Fact]
    public void AddByoScopesSubcommand_HasCorrectOptions()
    {
        var command = DevelopMcpCommand.CreateCommand(
            _mockLogger, _mockToolingService,
            graphApiService: _mockGraphApiService,
            agentBlueprintService: _mockBlueprintService);
        var subcommand = command.Subcommands.First(sc => sc.Name == "add-byo-scopes");

        var optionNames = subcommand.Options.Select(o => o.Name).ToList();
        optionNames.Should().Contain("server-names");
        optionNames.Should().Contain("blueprint-id");
        optionNames.Should().Contain("agent-instances");
        optionNames.Should().Contain("tenant-id");
        optionNames.Should().Contain("dry-run");
        optionNames.Should().Contain("verbose");

        var serverNamesOption = subcommand.Options.First(o => o.Name == "server-names");
        serverNamesOption.IsRequired.Should().BeTrue(because: "--server-names is required");
        serverNamesOption.Aliases.Should().Contain("-s");

        var blueprintOption = subcommand.Options.First(o => o.Name == "blueprint-id");
        blueprintOption.Aliases.Should().Contain("-b");

        var instancesOption = subcommand.Options.First(o => o.Name == "agent-instances");
        instancesOption.Aliases.Should().Contain("-i");
    }

    [Fact]
    public void AddByoScopesSubcommand_HasNoPositionalArguments()
    {
        var command = DevelopMcpCommand.CreateCommand(
            _mockLogger, _mockToolingService,
            graphApiService: _mockGraphApiService,
            agentBlueprintService: _mockBlueprintService);
        var subcommand = command.Subcommands.First(sc => sc.Name == "add-byo-scopes");

        subcommand.Arguments.Should().BeEmpty(
            because: "Azure CLI compliance requires named options only");
    }
}
