// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using NSubstitute;
using System.Linq;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class QueryEntraCommandTests
{
    private readonly ILogger<QueryEntraCommand> _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly CommandExecutor _mockExecutor;
    private readonly GraphApiService _mockGraphApiService;
    private readonly AgentBlueprintService _mockBlueprintService;

    public QueryEntraCommandTests()
    {
        _mockLogger = Substitute.For<ILogger<QueryEntraCommand>>();
        _mockConfigService = Substitute.For<IConfigService>();
        // Create CommandExecutor with a mock logger dependency
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = new CommandExecutor(mockExecutorLogger);
        _mockGraphApiService = Substitute.For<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), _mockExecutor);
        _mockBlueprintService = Substitute.ForPartsOf<AgentBlueprintService>(Substitute.For<ILogger<AgentBlueprintService>>(), _mockGraphApiService);
    }

    [Fact]
    public void QueryEntraCommand_Should_Be_Created()
    {
        // Act
        var command = QueryEntraCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        // Assert
        Assert.NotNull(command);
        Assert.Equal("query-entra", command.Name);
        Assert.Equal("Query Microsoft Entra ID for agent information (scopes, permissions, consent status)", command.Description);
    }

    [Fact]
    public void QueryEntraCommand_Should_Have_Correct_Subcommands()
    {
        // Arrange
        var command = QueryEntraCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        // Assert
        Assert.Equal(3, command.Subcommands.Count);
        Assert.Contains(command.Subcommands, c => c.Name == "blueprint-scopes");
        Assert.Contains(command.Subcommands, c => c.Name == "instance-scopes");
        Assert.Contains(command.Subcommands, c => c.Name == "inheritance");
    }

    [Fact]
    public void QueryEntraCommand_Should_Have_BlueprintScopes_Subcommand()
    {
        // Arrange
        var command = QueryEntraCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        // Act
        var blueprintScopesSubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "blueprint-scopes");

        // Assert
        Assert.NotNull(blueprintScopesSubcommand);
        // Pins the user-facing CLI description. The subcommand reports the permissions currently
        // granted on the blueprint service principal (oauth2PermissionGrants + appRoleAssignments) —
        // the same surface the Entra portal "API permissions" blade shows. This is distinct from
        // `inheritance` which renders the policy + grants reconciliation verdict. The description
        // must reflect the granted-surface semantics; reverting to "declared" wording would falsely
        // imply requiredResourceAccess, which setup deliberately leaves empty for blueprints
        // (see BatchPermissionsOrchestrator), and would make the command appear to have zero value.
        Assert.Equal("List delegated and application permissions currently granted on the agent blueprint service principal (the view shown in the Entra portal 'API permissions' blade)", blueprintScopesSubcommand.Description);
    }

    [Fact]
    public void QueryEntraCommand_Should_Have_InstanceScopes_Subcommand()
    {
        // Arrange
        var command = QueryEntraCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        // Act
        var instanceScopesSubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "instance-scopes");

        // Assert
        Assert.NotNull(instanceScopesSubcommand);
        Assert.Equal("List configured scopes and consent status for the agent instance", instanceScopesSubcommand.Description);
    }

    [Fact]
    public void QueryEntraCommand_Should_Have_Inheritance_Subcommand()
    {
        // Arrange
        var command = QueryEntraCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        // Act
        var inheritanceSubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "inheritance");

        // Assert — the inheritance subcommand is the user-facing verification entry point for
        // the kind=allAllowed config (issue: ensure blueprint inheritable permissions are at the
        // wildcard form on both scopes and roles). It must surface --agent-name and --tenant-id
        // for consistency with the other subcommands so config-free invocations work.
        Assert.NotNull(inheritanceSubcommand);
        Assert.Equal("Verify the blueprint's inheritablePermissions are set to kind=allAllowed for both scopes and roles", inheritanceSubcommand.Description);
        Assert.Contains(inheritanceSubcommand.Options, o => o.Name == "agent-name");
        Assert.Contains(inheritanceSubcommand.Options, o => o.Name == "tenant-id");
    }

}
