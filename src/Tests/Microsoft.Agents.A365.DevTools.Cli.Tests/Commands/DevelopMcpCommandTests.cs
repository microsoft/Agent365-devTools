// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using NSubstitute;
using FluentAssertions;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class DevelopMcpCommandTests
{
    private readonly ILogger _mockLogger;
    private readonly IAgent365ToolingService _mockToolingService;

    public DevelopMcpCommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockToolingService = Substitute.For<IAgent365ToolingService>();
    }

    [Fact]
    public void CreateCommand_ReturnsCommandWithCorrectName()
    {
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);

        // Assert
        command.Name.Should().Be("develop-mcp");
        command.Description.Should().Be("Manage MCP servers in Dataverse environments");
    }

    [Fact]
    public void CreateCommand_HasAllExpectedSubcommands()
    {
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);

        // Assert
        command.Subcommands.Should().HaveCount(6);
        
        var subcommandNames = command.Subcommands.Select(sc => sc.Name).ToList();
        subcommandNames.Should().Contain(new[] 
        { 
            "list-environments", 
            "list-servers", 
            "publish", 
            "unpublish", 
            "approve", 
            "block" 
        });
    }

    [Fact]
    public void ListEnvironmentsSubcommand_HasCorrectOptionsAndAliases()
    {
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);
        var subcommand = command.Subcommands.First(sc => sc.Name == "list-environments");

        // Assert
        subcommand.Description.Should().Be("List all Dataverse environments available for MCP server management");
        
        var options = subcommand.Options.ToList();
        options.Should().HaveCount(2); // config, dry-run (plus help automatically)

        // Verify config option
        var configOption = options.FirstOrDefault(o => o.Name == "config");
        configOption.Should().NotBeNull();
        configOption!.Aliases.Should().Contain("-c");
        configOption.Aliases.Should().Contain("--config");

        // Verify dry-run option
        var dryRunOption = options.FirstOrDefault(o => o.Name == "dry-run");
        dryRunOption.Should().NotBeNull();
        dryRunOption!.Aliases.Should().Contain("--dry-run");
    }

    [Fact]
    public void ListServersSubcommand_HasCorrectOptionsWithAliases()
    {
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);
        var subcommand = command.Subcommands.First(sc => sc.Name == "list-servers");

        // Assert
        subcommand.Description.Should().Be("List MCP servers in a specific Dataverse environment");
        
        var options = subcommand.Options.ToList();
        options.Should().HaveCount(3); // environment-id, config, dry-run

        // Verify environment-id option with short alias
        var envOption = options.FirstOrDefault(o => o.Name == "environment-id");
        envOption.Should().NotBeNull();
        envOption!.Aliases.Should().Contain("-e");
        envOption.Aliases.Should().Contain("--environment-id");

        // Verify config option
        var configOption = options.FirstOrDefault(o => o.Name == "config");
        configOption.Should().NotBeNull();
        configOption!.Aliases.Should().Contain("-c");
    }

    [Fact]
    public void PublishSubcommand_HasCorrectOptionsWithAliases()
    {
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);
        var subcommand = command.Subcommands.First(sc => sc.Name == "publish");

        // Assert
        subcommand.Description.Should().Be("Publish an MCP server to a Dataverse environment");
        
        var options = subcommand.Options.ToList();
        
        // Verify all expected options exist
        var optionNames = options.Select(o => o.Name).ToList();
        optionNames.Should().Contain("environment-id");
        optionNames.Should().Contain("server-name");
        optionNames.Should().Contain("alias");
        optionNames.Should().Contain("display-name");
        optionNames.Should().Contain("config");
        optionNames.Should().Contain("dry-run");

        // Verify critical aliases for Azure CLI compliance
        var envOption = options.FirstOrDefault(o => o.Name == "environment-id");
        envOption!.Aliases.Should().Contain("-e");
        
        var serverOption = options.FirstOrDefault(o => o.Name == "server-name");
        serverOption!.Aliases.Should().Contain("-s");
        
        var aliasOption = options.FirstOrDefault(o => o.Name == "alias");
        aliasOption!.Aliases.Should().Contain("-a");
        
        var displayNameOption = options.FirstOrDefault(o => o.Name == "display-name");
        displayNameOption!.Aliases.Should().Contain("-d");
    }

    [Fact]
    public void UnpublishSubcommand_HasCorrectOptionsWithAliases()
    {
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);
        var subcommand = command.Subcommands.First(sc => sc.Name == "unpublish");

        // Assert
        subcommand.Description.Should().Be("Unpublish an MCP server from a Dataverse environment");
        
        var options = subcommand.Options.ToList();
        
        // Verify expected options
        var optionNames = options.Select(o => o.Name).ToList();
        optionNames.Should().Contain("environment-id");
        optionNames.Should().Contain("server-name");
        optionNames.Should().Contain("config");
        optionNames.Should().Contain("dry-run");

        // Verify Azure CLI style aliases
        var envOption = options.FirstOrDefault(o => o.Name == "environment-id");
        envOption!.Aliases.Should().Contain("-e");
        
        var serverOption = options.FirstOrDefault(o => o.Name == "server-name");
        serverOption!.Aliases.Should().Contain("-s");
    }

    [Fact]
    public void ApproveSubcommand_IsImplementedWithCorrectOptions()
    {
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);
        var subcommand = command.Subcommands.First(sc => sc.Name == "approve");

        // Assert
        subcommand.Description.Should().Be("Approve an MCP server");
        
        var options = subcommand.Options.ToList();
        var optionNames = options.Select(o => o.Name).ToList();
        optionNames.Should().Contain("server-name");
        optionNames.Should().Contain("config");
        optionNames.Should().Contain("dry-run");

        // Verify server-name has short alias
        var serverOption = options.FirstOrDefault(o => o.Name == "server-name");
        serverOption!.Aliases.Should().Contain("-s");
    }

    [Fact]
    public void BlockSubcommand_IsImplementedWithCorrectOptions()
    {
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);
        var subcommand = command.Subcommands.First(sc => sc.Name == "block");

        // Assert
        subcommand.Description.Should().Be("Block an MCP server");
        
        var options = subcommand.Options.ToList();
        var optionNames = options.Select(o => o.Name).ToList();
        optionNames.Should().Contain("server-name");
        optionNames.Should().Contain("config");
        optionNames.Should().Contain("dry-run");

        // Verify server-name has short alias
        var serverOption = options.FirstOrDefault(o => o.Name == "server-name");
        serverOption!.Aliases.Should().Contain("-s");
    }

    [Fact]
    public void AllSubcommands_SupportDryRunOption()
    {
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);

        // Assert - All subcommands should have dry-run option for safety
        foreach (var subcommand in command.Subcommands)
        {
            var dryRunOption = subcommand.Options.FirstOrDefault(o => o.Name == "dry-run");
            dryRunOption.Should().NotBeNull($"Subcommand '{subcommand.Name}' should have --dry-run option");
        }
    }

    [Fact]
    public void AllSubcommands_SupportConfigOption()
    {
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);

        // Assert - All subcommands should have config option for consistency
        foreach (var subcommand in command.Subcommands)
        {
            var configOption = subcommand.Options.FirstOrDefault(o => o.Name == "config");
            configOption.Should().NotBeNull($"Subcommand '{subcommand.Name}' should have --config option");
            configOption!.Aliases.Should().Contain("-c", $"Config option should have -c alias in '{subcommand.Name}'");
        }
    }

    [Theory]
    [InlineData("list-servers", "environment-id", "-e")]
    [InlineData("publish", "environment-id", "-e")]
    [InlineData("unpublish", "environment-id", "-e")]
    [InlineData("publish", "server-name", "-s")]
    [InlineData("unpublish", "server-name", "-s")]
    [InlineData("approve", "server-name", "-s")]
    [InlineData("block", "server-name", "-s")]
    public void CriticalOptions_HaveConsistentAliases(string subcommandName, string optionName, string expectedAlias)
    {
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);
        var subcommand = command.Subcommands.First(sc => sc.Name == subcommandName);
        var option = subcommand.Options.FirstOrDefault(o => o.Name == optionName);

        // Assert
        option.Should().NotBeNull($"Option '{optionName}' should exist in '{subcommandName}' command");
        option!.Aliases.Should().Contain(expectedAlias, 
            $"Option '{optionName}' in '{subcommandName}' should have alias '{expectedAlias}'");
    }

    [Fact] 
    public void NoSubcommands_UsePositionalArguments_OnlyOptions()
    {
        // This is a regression test to ensure we don't accidentally revert to positional arguments
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);

        // Assert
        foreach (var subcommand in command.Subcommands)
        {
            subcommand.Arguments.Should().BeEmpty(
                $"Subcommand '{subcommand.Name}' should not have positional arguments - use named options for Azure CLI compliance");
        }
    }
}
