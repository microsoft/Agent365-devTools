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
        command.Subcommands.Should().HaveCount(8);

        var subcommandNames = command.Subcommands.Select(sc => sc.Name).ToList();
        subcommandNames.Should().Contain(new[]
        {
            "list-environments",
            "list-servers",
            "publish",
            "unpublish",
            "approve",
            "block",
            "package-mcp-server",
            "register-external-mcp-server"
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
        options.Should().HaveCount(2); // dry-run, verbose (plus help automatically)

        // Verify dry-run option
        var dryRunOption = options.FirstOrDefault(o => o.Name == "dry-run");
        dryRunOption.Should().NotBeNull();
        dryRunOption!.Aliases.Should().Contain("--dry-run");

        // Verify verbose option
        var verboseOption = options.FirstOrDefault(o => o.Name == "verbose");
        verboseOption.Should().NotBeNull();
        verboseOption!.Aliases.Should().Contain("-v");
        verboseOption!.Aliases.Should().Contain("--verbose");
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
        options.Should().HaveCount(3); // environment-id, dry-run, verbose

        // Verify environment-id option with short alias
        var envOption = options.FirstOrDefault(o => o.Name == "environment-id");
        envOption.Should().NotBeNull();
        envOption!.Aliases.Should().Contain("-e");
        envOption.Aliases.Should().Contain("--environment-id");

        // Verify verbose option
        var verboseOption = options.FirstOrDefault(o => o.Name == "verbose");
        verboseOption.Should().NotBeNull();
        verboseOption!.Aliases.Should().Contain("-v");
        verboseOption!.Aliases.Should().Contain("--verbose");
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
        optionNames.Should().Contain("dry-run");

        // Verify Azure CLI style aliases
        var envOption = options.FirstOrDefault(o => o.Name == "environment-id");
        envOption!.Aliases.Should().Contain("-e");
        
        var serverOption = options.FirstOrDefault(o => o.Name == "server-name");
        serverOption!.Aliases.Should().Contain("-s");
    }

    [Fact]
    public void PackageMcpServerSubcommand_HasCorrectOptions()
    {
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);
        var subcommand = command.Subcommands.First(sc => sc.Name == "package-mcp-server");

        // Assert
        subcommand.Description.Should().Be("Generate MCP server package for submission on Microsoft admin center");

        var options = subcommand.Options.ToList();
        options.Should().HaveCount(6); // serverName, developerName, iconUrl, outputPath, dry-run, verbose

        var optionNames = options.Select(o => o.Name).ToList();
        optionNames.Should().Contain("server-name");
        optionNames.Should().Contain("developer-name");
        optionNames.Should().Contain("icon-url");
        optionNames.Should().Contain("output-path");
        optionNames.Should().Contain("dry-run");
        optionNames.Should().Contain("verbose");

        options.First(o => o.Name == "server-name").IsRequired.Should().BeTrue();
        options.First(o => o.Name == "developer-name").IsRequired.Should().BeTrue();
        options.First(o => o.Name == "icon-url").IsRequired.Should().BeTrue();
        options.First(o => o.Name == "output-path").IsRequired.Should().BeTrue();
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

    [Theory]
    [InlineData("register-external-mcp-server")]
    public void ConfigDependentSubcommands_SupportConfigOption(string subcommandName)
    {
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);
        var subcommand = command.Subcommands.First(sc => sc.Name == subcommandName);

        // Assert
        var configOption = subcommand.Options.FirstOrDefault(o => o.Name == "config");
        configOption.Should().NotBeNull($"Subcommand '{subcommandName}' should have --config option");
        configOption!.Aliases.Should().Contain("-c", $"Config option should have -c alias in '{subcommandName}'");
    }

    [Fact]
    public void RegisterExternalMcpServerSubcommand_HasAllExpectedOptions()
    {
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);
        var subcommand = command.Subcommands.First(sc => sc.Name == "register-external-mcp-server");

        // Assert
        subcommand.Description.Should().Be("Register an external MCP server with Entra, ExternalIDP, or NoAuth authentication");

        var options = subcommand.Options.ToList();
        var optionNames = options.Select(o => o.Name).ToList();

        optionNames.Should().Contain("server-name");
        optionNames.Should().Contain("server-url");
        optionNames.Should().Contain("auth-type");
        optionNames.Should().Contain("idp-authorization-url");
        optionNames.Should().Contain("idp-token-url");
        optionNames.Should().Contain("idp-scopes");
        optionNames.Should().Contain("idp-client-id");
        optionNames.Should().Contain("idp-client-secret");
        optionNames.Should().Contain("api-key-location");
        optionNames.Should().Contain("api-key-name");
        optionNames.Should().Contain("tools");
        optionNames.Should().Contain("input-file");
        optionNames.Should().Contain("remote-scopes");
        optionNames.Should().Contain("tenant-id");
        optionNames.Should().Contain("service-tree-id");
        optionNames.Should().Contain("config");
        optionNames.Should().Contain("publisher");
        optionNames.Should().Contain("description");
        optionNames.Should().Contain("dry-run");
        optionNames.Should().Contain("verbose");

        // Verify critical aliases
        var serverNameOption = options.First(o => o.Name == "server-name");
        serverNameOption.Aliases.Should().Contain("-s");
        serverNameOption.Aliases.Should().Contain("--server-name");

        var serverUrlOption = options.First(o => o.Name == "server-url");
        serverUrlOption.Aliases.Should().Contain("-u");

        var authTypeOption = options.First(o => o.Name == "auth-type");
        authTypeOption.Aliases.Should().Contain("-a");

        var inputFileOption = options.First(o => o.Name == "input-file");
        inputFileOption.Aliases.Should().Contain("-f");

        var tenantIdOption = options.First(o => o.Name == "tenant-id");
        tenantIdOption.Aliases.Should().Contain("-t");

        var configOption = options.First(o => o.Name == "config");
        configOption.Aliases.Should().Contain("-c");

        var verboseOption = options.First(o => o.Name == "verbose");
        verboseOption.Aliases.Should().Contain("-v");
    }


    [Theory]
    [InlineData("list-servers", "environment-id", "-e")]
    [InlineData("publish", "environment-id", "-e")]
    [InlineData("unpublish", "environment-id", "-e")]
    [InlineData("publish", "server-name", "-s")]
    [InlineData("unpublish", "server-name", "-s")]
    [InlineData("approve", "server-name", "-s")]
    [InlineData("block", "server-name", "-s")]
    [InlineData("register-external-mcp-server", "server-name", "-s")]
    [InlineData("register-external-mcp-server", "server-url", "-u")]
    [InlineData("register-external-mcp-server", "auth-type", "-a")]
    [InlineData("register-external-mcp-server", "input-file", "-f")]
    [InlineData("register-external-mcp-server", "tenant-id", "-t")]
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
