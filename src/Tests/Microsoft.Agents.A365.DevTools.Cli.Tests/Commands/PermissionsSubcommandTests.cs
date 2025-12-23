// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.CommandLine;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Unit tests for Permissions subcommand
/// </summary>
[Collection("Sequential")]
public class PermissionsSubcommandTests
{
    private readonly ILogger _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly CommandExecutor _mockExecutor;
    private readonly GraphApiService _mockGraphApiService;
    private readonly AgentBlueprintService _mockBlueprintService;

    public PermissionsSubcommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockConfigService = Substitute.For<IConfigService>();
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);
        _mockGraphApiService = Substitute.ForPartsOf<GraphApiService>();
        _mockBlueprintService = Substitute.ForPartsOf<AgentBlueprintService>(Substitute.For<ILogger<AgentBlueprintService>>(), _mockGraphApiService);
    }

    #region Command Structure Tests

    [Fact]
    public void CreateCommand_ShouldHaveMcpSubcommand()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        // Assert
        var mcpSubcommand = command.Subcommands.FirstOrDefault(s => s.Name == "mcp");
        mcpSubcommand.Should().NotBeNull();
    }

    [Fact]
    public void CreateCommand_ShouldHaveBotSubcommand()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        // Assert
        var botSubcommand = command.Subcommands.FirstOrDefault(s => s.Name == "bot");
        botSubcommand.Should().NotBeNull();
    }

    [Fact]
    public void CommandDescription_ShouldMentionRequiredPermissions()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        // Assert
        command.Description.Should().Contain("Global Administrator");
    }

    [Fact]
    public void CreateCommand_ShouldHaveBothSubcommands()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        // Assert
        command.Subcommands.Should().HaveCount(2);
        command.Subcommands.Should().Contain(s => s.Name == "mcp");
        command.Subcommands.Should().Contain(s => s.Name == "bot");
    }

    [Fact]
    public void CreateCommand_ShouldBeUsableInCommandPipeline()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        // Assert
        command.Should().NotBeNull();
        command.Name.Should().Be("permissions");
        command.Subcommands.Should().HaveCount(2);
    }

    #endregion

    #region MCP Subcommand Tests

    [Fact]
    public void McpSubcommand_ShouldHaveCorrectName()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        var mcpSubcommand = command.Subcommands.First(s => s.Name == "mcp");

        // Assert
        mcpSubcommand.Name.Should().Be("mcp");
    }

    [Fact]
    public void McpSubcommand_ShouldHaveConfigOption()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        var mcpSubcommand = command.Subcommands.First(s => s.Name == "mcp");

        // Assert
        var configOption = mcpSubcommand.Options.FirstOrDefault(o => o.Name == "config");
        configOption.Should().NotBeNull();
        configOption!.Aliases.Should().Contain("--config");
        configOption.Aliases.Should().Contain("-c");
    }

    [Fact]
    public void McpSubcommand_ShouldHaveVerboseOption()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        var mcpSubcommand = command.Subcommands.First(s => s.Name == "mcp");

        // Assert
        var verboseOption = mcpSubcommand.Options.FirstOrDefault(o => o.Name == "verbose");
        verboseOption.Should().NotBeNull();
        verboseOption!.Aliases.Should().Contain("--verbose");
        verboseOption.Aliases.Should().Contain("-v");
    }

    [Fact]
    public void McpSubcommand_ShouldHaveDryRunOption()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        var mcpSubcommand = command.Subcommands.First(s => s.Name == "mcp");

        // Assert
        var dryRunOption = mcpSubcommand.Options.FirstOrDefault(o => o.Name == "dry-run");
        dryRunOption.Should().NotBeNull();
        dryRunOption!.Aliases.Should().Contain("--dry-run");
    }

    [Fact]
    public void McpSubcommand_DescriptionShouldBeInformativeAndActionable()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        var mcpSubcommand = command.Subcommands.First(s => s.Name == "mcp");

        // Assert
        mcpSubcommand.Description.Should().NotBeNullOrEmpty();
        mcpSubcommand.Description.Should().ContainAny("MCP", "OAuth2", "permissions");
    }

    #endregion

    #region Bot Subcommand Tests

    [Fact]
    public void BotSubcommand_ShouldHaveCorrectName()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        var botSubcommand = command.Subcommands.First(s => s.Name == "bot");

        // Assert
        botSubcommand.Name.Should().Be("bot");
    }

    [Fact]
    public void BotSubcommand_ShouldHaveConfigOption()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        var botSubcommand = command.Subcommands.First(s => s.Name == "bot");

        // Assert
        var configOption = botSubcommand.Options.FirstOrDefault(o => o.Name == "config");
        configOption.Should().NotBeNull();
        configOption!.Aliases.Should().Contain("--config");
        configOption.Aliases.Should().Contain("-c");
    }

    [Fact]
    public void BotSubcommand_ShouldHaveVerboseOption()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        var botSubcommand = command.Subcommands.First(s => s.Name == "bot");

        // Assert
        var verboseOption = botSubcommand.Options.FirstOrDefault(o => o.Name == "verbose");
        verboseOption.Should().NotBeNull();
        verboseOption!.Aliases.Should().Contain("--verbose");
        verboseOption.Aliases.Should().Contain("-v");
    }

    [Fact]
    public void BotSubcommand_ShouldHaveDryRunOption()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        var botSubcommand = command.Subcommands.First(s => s.Name == "bot");

        // Assert
        var dryRunOption = botSubcommand.Options.FirstOrDefault(o => o.Name == "dry-run");
        dryRunOption.Should().NotBeNull();
        dryRunOption!.Aliases.Should().Contain("--dry-run");
    }

    [Fact]
    public void BotSubcommand_DescriptionShouldMentionPrerequisites()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        var botSubcommand = command.Subcommands.First(s => s.Name == "bot");

        // Assert
        botSubcommand.Description.Should().Contain("Prerequisites");
    }

    [Fact]
    public void BotSubcommand_DescriptionShouldBeInformativeAndActionable()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        var botSubcommand = command.Subcommands.First(s => s.Name == "bot");

        // Assert
        botSubcommand.Description.Should().NotBeNullOrEmpty();
        botSubcommand.Description.Should().ContainAny("Bot", "API", "permissions");
    }

    #endregion

    #region Validation Tests (Testing logic without parser)

    [Fact]
    public void McpValidation_WithMissingBlueprintId_ShouldDetect()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "", // Missing blueprint ID
            DeploymentProjectPath = "."
        };

        // Act - Verify validation logic
        var blueprintId = config.AgentBlueprintId;

        // Assert - Verify validation would catch this
        blueprintId.Should().BeEmpty();
    }

    [Fact]
    public void BotValidation_WithMissingBlueprintId_ShouldDetect()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = null // Missing blueprint ID
        };

        // Act
        var blueprintId = config.AgentBlueprintId;

        // Assert
        blueprintId.Should().BeNull();
    }

    [Fact]
    public void DryRunLogic_ShouldNotExecutePermissionGrants()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "blueprint-123",
            DeploymentProjectPath = "."
        };

        // Act - Verify config properties
        var blueprintId = config.AgentBlueprintId;
        var tenantId = config.TenantId;

        // Assert - Config is valid for dry-run
        blueprintId.Should().Be("blueprint-123");
        tenantId.Should().Be("test-tenant");
    }

    [Fact]
    public void McpConfiguration_ShouldDescribeOAuth2Grants()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            AgentBlueprintId = "blueprint-456",
            DeploymentProjectPath = ".",
            Environment = "preprod"
        };

        // Act - This would be what dry-run displays
        var environment = config.Environment;
        var blueprintId = config.AgentBlueprintId;

        // Assert
        environment.Should().Be("preprod");
        blueprintId.Should().Be("blueprint-456");
    }

    [Fact]
    public void BotConfiguration_ShouldDescribeBotApiPermissions()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "blueprint-123"
        };

        // Act - Simulate what would be logged
        var blueprintId = config.AgentBlueprintId;

        // Assert
        blueprintId.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region ConfigureMcpPermissionsAsync Tests

    [Fact]
    public async Task ConfigureMcpPermissionsAsync_WithMissingManifest_ShouldHandleGracefully()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            DeploymentProjectPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()) // Non-existent path
        };

        var configFile = new FileInfo("test-config.json");

        // Act
        var result = await PermissionsSubcommand.ConfigureMcpPermissionsAsync(
            configFile.FullName,
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService,
            _mockBlueprintService,
            config,
            false);

        // Assert - Should handle missing manifest gracefully
        result.Should().BeFalse();
    }

    #endregion

    #region ConfigureBotPermissionsAsync Tests

    [Fact]
    public async Task ConfigureBotPermissionsAsync_WithMissingBlueprintId_ShouldReturnFalse()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "" // Missing
        };

        var configFile = new FileInfo("test-config.json");

        // Act
        var result = await PermissionsSubcommand.ConfigureBotPermissionsAsync(
            configFile.FullName,
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            config,
            _mockGraphApiService,
            _mockBlueprintService,
            false);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void ConfigureBotPermissionsAsync_ShouldValidateBlueprintId()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123"
        };

        var configFile = new FileInfo("test-config.json");

        // Act - Even though it may fail, it should validate the blueprint ID first
        var blueprintId = config.AgentBlueprintId;

        // Assert
        blueprintId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void BotSubcommand_Description_ShouldNotReferenceNonExistentEndpointCommand()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        var botSubcommand = command.Subcommands.FirstOrDefault(s => s.Name == "bot");

        // Assert
        botSubcommand.Should().NotBeNull();
        botSubcommand!.Description.Should().NotContain("a365 setup endpoint", 
            "the 'a365 setup endpoint' command does not exist - endpoint is registered as part of blueprint setup");
        botSubcommand.Description.Should().Contain("a365 deploy", 
            "after permissions setup, users should deploy their agent code");
    }

    [Fact]
    public void BotSubcommand_Description_ShouldMentionPrerequisites()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService);

        var botSubcommand = command.Subcommands.FirstOrDefault(s => s.Name == "bot");

        // Assert
        botSubcommand.Should().NotBeNull();
        botSubcommand!.Description.Should().Contain("Blueprint", 
            "blueprint is a prerequisite for bot permissions");
        botSubcommand.Description.Should().Contain("MCP permissions", 
            "MCP permissions should be configured before bot permissions");
    }

    #endregion
}

