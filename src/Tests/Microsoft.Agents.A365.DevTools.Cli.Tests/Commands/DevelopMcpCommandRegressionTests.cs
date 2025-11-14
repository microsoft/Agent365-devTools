// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using NSubstitute;
using FluentAssertions;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Core regression tests for the MCP commands focusing on critical scenarios
/// These tests ensure key functionality works and prevent regressions from architectural changes
/// </summary>
public class DevelopMcpCommandRegressionTests
{
    private readonly ILogger _mockLogger;
    private readonly IAgent365ToolingService _mockToolingService;
    private readonly Command _command;

    public DevelopMcpCommandRegressionTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockToolingService = Substitute.For<IAgent365ToolingService>();
        _command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);
    }

    [Fact]
    public async Task DryRunMode_NeverCallsActualServices()
    {
        // This test ensures dry-run mode is properly implemented across all commands
        // and prevents accidental service calls during dry runs

        // Arrange & Act - Test all dry run scenarios  
        var dryRunCommands = new[]
        {
            new[] { "list-environments", "--dry-run" },
            new[] { "list-servers", "-e", "test-env", "--dry-run" },
            new[] { "publish", "-e", "test-env", "-s", "test-server", "--dry-run" },
            new[] { "unpublish", "-e", "test-env", "-s", "test-server", "--dry-run" },
            new[] { "approve", "-s", "test-server", "--dry-run" },
            new[] { "block", "-s", "test-server", "--dry-run" }
        };

        foreach (var commandArgs in dryRunCommands)
        {
            var result = await _command.InvokeAsync(commandArgs);
            result.Should().Be(0, $"Command {string.Join(" ", commandArgs)} should succeed");
        }

        // Verify no service methods were called
        await _mockToolingService.DidNotReceive().ListEnvironmentsAsync();
        await _mockToolingService.DidNotReceive().ListServersAsync(Arg.Any<string>());
        await _mockToolingService.DidNotReceive().PublishServerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PublishMcpServerRequest>());
        await _mockToolingService.DidNotReceive().UnpublishServerAsync(Arg.Any<string>(), Arg.Any<string>());
        await _mockToolingService.DidNotReceive().ApproveServerAsync(Arg.Any<string>());
        await _mockToolingService.DidNotReceive().BlockServerAsync(Arg.Any<string>());
    }

    [Theory]
    [InlineData("list-servers", "-e", "test-env")]
    [InlineData("list-servers", "--environment-id", "test-env")]
    [InlineData("publish", "-e", "test-env", "-s", "test-server")]
    [InlineData("publish", "--environment-id", "test-env", "--server-name", "test-server")]
    [InlineData("unpublish", "-e", "test-env", "-s", "test-server")]
    [InlineData("approve", "-s", "test-server")]
    [InlineData("approve", "--server-name", "test-server")]
    [InlineData("block", "-s", "test-server")]
    [InlineData("block", "--server-name", "test-server")]
    public async Task AzureCliStyleParameters_AreAcceptedCorrectly(string command, params string[] args)
    {
        // This test ensures we maintain Azure CLI compatibility with named options
        // Regression test: Prevents reverting back to positional arguments

        // Arrange  
        _mockToolingService.ListServersAsync(Arg.Any<string>()).Returns(new DataverseMcpServersResponse());
        _mockToolingService.PublishServerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PublishMcpServerRequest>())
            .Returns(new PublishMcpServerResponse { Status = "Success" });
        _mockToolingService.UnpublishServerAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _mockToolingService.ApproveServerAsync(Arg.Any<string>()).Returns(true);
        _mockToolingService.BlockServerAsync(Arg.Any<string>()).Returns(true);

        var fullCommand = new List<string> { command };
        fullCommand.AddRange(args);
        fullCommand.Add("--dry-run"); // Use dry run to avoid actual service calls

        // Act
        var result = await _command.InvokeAsync(fullCommand.ToArray());

        // Assert
        result.Should().Be(0, $"Azure CLI style command should be accepted: {string.Join(" ", fullCommand)}");
    }

    [Fact] 
    public async Task ServiceIntegration_PublishCommand_PassesCorrectParameters()
    {
        // Core functionality test: Ensures publish command integration works correctly
        
        // Arrange
        var testEnvId = "test-environment-123";
        var testServerName = "msdyn_TestServer";
        var testAlias = "test-alias";
        var testDisplayName = "Test Server Display Name";

        var mockResponse = new PublishMcpServerResponse
        {
            Status = "Success",
            Message = "Server published successfully"
        };

        _mockToolingService.PublishServerAsync(testEnvId, testServerName, Arg.Any<PublishMcpServerRequest>())
            .Returns(mockResponse);

        // Act
        var result = await _command.InvokeAsync(new[] 
        { 
            "publish", 
            "--environment-id", testEnvId,
            "--server-name", testServerName,
            "--alias", testAlias,
            "--display-name", testDisplayName
        });

        // Assert
        result.Should().Be(0);
        
        await _mockToolingService.Received(1).PublishServerAsync(
            testEnvId,
            testServerName,
            Arg.Is<PublishMcpServerRequest>(req => 
                req.Alias == testAlias && 
                req.DisplayName == testDisplayName)
        );
    }

    [Fact]
    public async Task ServiceIntegration_UnpublishCommand_PassesCorrectParameters()
    {
        // Core functionality test: Ensures unpublish command integration works correctly
        
        // Arrange
        var testEnvId = "test-environment-456";
        var testServerName = "msdyn_TestServer";

        _mockToolingService.UnpublishServerAsync(testEnvId, testServerName).Returns(true);

        // Act
        var result = await _command.InvokeAsync(new[] 
        { 
            "unpublish", 
            "-e", testEnvId,
            "-s", testServerName
        });

        // Assert
        result.Should().Be(0);
        await _mockToolingService.Received(1).UnpublishServerAsync(testEnvId, testServerName);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("block")]
    public async Task NewCommands_ApproveAndBlock_WorkCorrectly(string commandName)
    {
        // Regression test: Ensures newly implemented approve/block commands function properly
        
        // Arrange
        var testServerName = "msdyn_TestServer";

        _mockToolingService.ApproveServerAsync(testServerName).Returns(true);
        _mockToolingService.BlockServerAsync(testServerName).Returns(true);

        // Act
        var result = await _command.InvokeAsync(new[] { commandName, "-s", testServerName });

        // Assert
        result.Should().Be(0);
        
        if (commandName == "approve")
        {
            await _mockToolingService.Received(1).ApproveServerAsync(testServerName);
        }
        else
        {
            await _mockToolingService.Received(1).BlockServerAsync(testServerName);
        }
    }

    [Fact]
    public void CommandStructure_HasNoPositionalArguments()
    {
        // Critical regression test: Ensures we don't accidentally revert to positional arguments
        // This was a key architectural decision to follow Azure CLI patterns
        
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);

        // Assert
        foreach (var subcommand in command.Subcommands)
        {
            subcommand.Arguments.Should().BeEmpty(
                $"Subcommand '{subcommand.Name}' must not have positional arguments - Azure CLI compliance requires named options only");
        }
    }

    [Fact]
    public void CommandStructure_AllSubcommandsHaveConsistentOptions()
    {
        // Regression test: Ensures consistent option patterns across all commands
        
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);

        // Assert
        foreach (var subcommand in command.Subcommands)
        {
            var options = subcommand.Options.ToList();
            
            // All commands should have config option
            options.Should().Contain(o => o.Name == "config", 
                $"Subcommand '{subcommand.Name}' should have --config option");
            
            // All commands should have dry-run option  
            options.Should().Contain(o => o.Name == "dry-run",
                $"Subcommand '{subcommand.Name}' should have --dry-run option");

            // Config option should have -c alias
            var configOption = options.First(o => o.Name == "config");
            configOption.Aliases.Should().Contain("-c",
                $"Config option in '{subcommand.Name}' should have -c alias");
        }
    }
}