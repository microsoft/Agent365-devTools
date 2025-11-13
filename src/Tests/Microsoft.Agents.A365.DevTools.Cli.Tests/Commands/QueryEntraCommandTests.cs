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

    public QueryEntraCommandTests()
    {
        _mockLogger = Substitute.For<ILogger<QueryEntraCommand>>();
        _mockConfigService = Substitute.For<IConfigService>();
        // Create CommandExecutor with a mock logger dependency
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = new CommandExecutor(mockExecutorLogger);
        _mockGraphApiService = Substitute.For<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), _mockExecutor);
    }

    [Fact]
    public void QueryEntraCommand_Should_Be_Created()
    {
        // Act
        var command = QueryEntraCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService);

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
            _mockGraphApiService);

        // Assert
        Assert.Equal(2, command.Subcommands.Count);
        Assert.Contains(command.Subcommands, c => c.Name == "blueprint-scopes");
        Assert.Contains(command.Subcommands, c => c.Name == "instance-scopes");
    }

    [Fact]
    public void QueryEntraCommand_Should_Have_BlueprintScopes_Subcommand()
    {
        // Arrange
        var command = QueryEntraCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService);

        // Act
        var blueprintScopesSubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "blueprint-scopes");

        // Assert
        Assert.NotNull(blueprintScopesSubcommand);
        Assert.Equal("List configured scopes and consent status for the agent blueprint", blueprintScopesSubcommand.Description);
    }

    [Fact]
    public void QueryEntraCommand_Should_Have_InstanceScopes_Subcommand()
    {
        // Arrange
        var command = QueryEntraCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService);

        // Act
        var instanceScopesSubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "instance-scopes");

        // Assert
        Assert.NotNull(instanceScopesSubcommand);
        Assert.Equal("List configured scopes and consent status for the agent instance", instanceScopesSubcommand.Description);
    }

    [Fact]
    public void QueryEntraCommand_BlueprintScopes_Should_Have_Config_Option()
    {
        // Arrange
        var command = QueryEntraCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService);

        // Act
        var blueprintScopesSubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "blueprint-scopes");
        var configOption = blueprintScopesSubcommand?.Options.FirstOrDefault(o => o.Name == "config");

        // Assert
        Assert.NotNull(blueprintScopesSubcommand);
        Assert.NotNull(configOption);
        Assert.Equal("Configuration file path", configOption.Description);
    }

    [Fact]
    public void QueryEntraCommand_InstanceScopes_Should_Have_Config_Option()
    {
        // Arrange
        var command = QueryEntraCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService);

        // Act
        var instanceScopesSubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "instance-scopes");
        var configOption = instanceScopesSubcommand?.Options.FirstOrDefault(o => o.Name == "config");

        // Assert
        Assert.NotNull(instanceScopesSubcommand);
        Assert.NotNull(configOption);
        Assert.Equal("Configuration file path", configOption.Description);
    }
}
