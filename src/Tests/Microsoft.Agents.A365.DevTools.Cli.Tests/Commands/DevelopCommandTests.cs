// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using NSubstitute;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class DevelopCommandTests
{
    private readonly ILogger _mockLogger;
    private readonly ConfigService _mockConfigService;
    private readonly CommandExecutor _mockCommandExecutor;
    private readonly AuthenticationService _mockAuthService;

    public DevelopCommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        
        // For concrete classes, we need to create partial substitutes to avoid ILogger mocking issues
        var mockConfigLogger = Substitute.For<ILogger<ConfigService>>();
        _mockConfigService = Substitute.ForPartsOf<ConfigService>(mockConfigLogger);
        
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockCommandExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);
        
        var mockAuthLogger = Substitute.For<ILogger<AuthenticationService>>();
        _mockAuthService = Substitute.ForPartsOf<AuthenticationService>(mockAuthLogger);
        
    }

    [Fact]
    public void CreateCommand_ReturnsCommandWithCorrectName()
    {
        // Act
        var command = DevelopCommand.CreateCommand(_mockLogger, _mockConfigService, _mockCommandExecutor, _mockAuthService);

        // Assert
        Assert.Equal("develop", command.Name);
        Assert.Equal("Manage MCP tool servers for agent development", command.Description);
    }

    [Fact]
    public void CreateCommand_HasFourSubcommands()
    {
        // Act
        var command = DevelopCommand.CreateCommand(_mockLogger, _mockConfigService, _mockCommandExecutor, _mockAuthService);

        // Assert
        Assert.Equal(4, command.Subcommands.Count);
        
        var subcommandNames = command.Subcommands.Select(sc => sc.Name).ToList();
        Assert.Contains("list-available", subcommandNames);
        Assert.Contains("list-configured", subcommandNames);
        Assert.Contains("add-mcp-servers", subcommandNames);
        Assert.Contains("remove-mcp-servers", subcommandNames);
    }

    [Fact]
    public void ListAvailableSubcommand_HasCorrectOptions()
    {
        // Act
        var command = DevelopCommand.CreateCommand(_mockLogger, _mockConfigService, _mockCommandExecutor, _mockAuthService);
        var subcommand = command.Subcommands.First(sc => sc.Name == "list-available");

        // Assert
        var optionNames = subcommand.Options.Select(opt => opt.Name).ToList();
        Assert.Contains("config", optionNames);
        Assert.Contains("dry-run", optionNames);
        Assert.Contains("skip-auth", optionNames);
    }

    [Fact]
    public void ListConfiguredSubcommand_HasCorrectOptions()
    {
        // Act
        var command = DevelopCommand.CreateCommand(_mockLogger, _mockConfigService, _mockCommandExecutor, _mockAuthService);
        var subcommand = command.Subcommands.First(sc => sc.Name == "list-configured");

        // Assert
        var optionNames = subcommand.Options.Select(opt => opt.Name).ToList();
        Assert.Contains("config", optionNames);
        Assert.Contains("dry-run", optionNames);
    }

    [Fact]
    public void AddMcpServersSubcommand_HasCorrectArgumentsAndOptions()
    {
        // Act
        var command = DevelopCommand.CreateCommand(_mockLogger, _mockConfigService, _mockCommandExecutor, _mockAuthService);
        var subcommand = command.Subcommands.First(sc => sc.Name == "add-mcp-servers");

        // Assert
        Assert.Single(subcommand.Arguments);
        Assert.Equal("servers", subcommand.Arguments[0].Name);
        Assert.Equal(2, subcommand.Options.Count);
        
        var optionNames = subcommand.Options.Select(opt => opt.Name).ToList();
        Assert.Contains("config", optionNames);
        Assert.Contains("dry-run", optionNames);
    }

    [Fact]
    public void RemoveMcpServersSubcommand_HasCorrectArgumentsAndOptions()
    {
        // Act
        var command = DevelopCommand.CreateCommand(_mockLogger, _mockConfigService, _mockCommandExecutor, _mockAuthService);
        var subcommand = command.Subcommands.First(sc => sc.Name == "remove-mcp-servers");

        // Assert
        Assert.Single(subcommand.Arguments);
        Assert.Equal("servers", subcommand.Arguments[0].Name);
        Assert.Equal(2, subcommand.Options.Count);
        
        var optionNames = subcommand.Options.Select(opt => opt.Name).ToList();
        Assert.Contains("config", optionNames);
        Assert.Contains("dry-run", optionNames);
    }
}