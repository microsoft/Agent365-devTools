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
/// Unit tests for CopilotStudio subcommand
/// </summary>
[Collection("Sequential")]
public class CopilotStudioSubcommandTests
{
    private readonly ILogger _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly CommandExecutor _mockExecutor;
    private readonly GraphApiService _mockGraphApiService;
    private readonly AgentBlueprintService _mockBlueprintService;

    public CopilotStudioSubcommandTests()
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
    public void CreateCommand_ShouldHaveCorrectName()
    {
        // Act
        var command = CopilotStudioSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService,
            _mockBlueprintService);

        // Assert
        command.Should().NotBeNull();
        command.Name.Should().Be("copilotstudio");
    }

    [Fact]
    public void CreateCommand_ShouldHaveConfigOption()
    {
        // Act
        var command = CopilotStudioSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService,
            _mockBlueprintService);

        // Assert
        var configOption = command.Options.FirstOrDefault(o => o.Name == "config");
        configOption.Should().NotBeNull();
        configOption!.Aliases.Should().Contain("--config");
        configOption.Aliases.Should().Contain("-c");
    }

    [Fact]
    public void CreateCommand_ShouldHaveVerboseOption()
    {
        // Act
        var command = CopilotStudioSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService,
            _mockBlueprintService);

        // Assert
        var verboseOption = command.Options.FirstOrDefault(o => o.Name == "verbose");
        verboseOption.Should().NotBeNull();
        verboseOption!.Aliases.Should().Contain("--verbose");
        verboseOption.Aliases.Should().Contain("-v");
    }

    [Fact]
    public void CreateCommand_ShouldHaveDryRunOption()
    {
        // Act
        var command = CopilotStudioSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService,
            _mockBlueprintService);

        // Assert
        var dryRunOption = command.Options.FirstOrDefault(o => o.Name == "dry-run");
        dryRunOption.Should().NotBeNull();
    }

    [Fact]
    public void CreateCommand_Description_ShouldMentionPowerPlatformApi()
    {
        // Act
        var command = CopilotStudioSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService,
            _mockBlueprintService);

        // Assert
        command.Should().NotBeNull();
        command.Description.Should().Contain("Power Platform",
            "description should mention the Power Platform API");
        command.Description.Should().Contain("CopilotStudio.Copilots.Invoke",
            "description should mention the specific permission scope");
    }

    [Fact]
    public void CreateCommand_Description_ShouldMentionPrerequisites()
    {
        // Act
        var command = CopilotStudioSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService,
            _mockBlueprintService);

        // Assert
        command.Should().NotBeNull();
        command.Description.Should().Contain("Blueprint",
            "blueprint is a prerequisite for CopilotStudio permissions");
        command.Description.Should().Contain("Global Administrator",
            "Global Administrator permission should be mentioned as a requirement");
    }

    #endregion

    #region Validation Tests

    [Fact]
    public async Task ValidateAsync_WithValidConfig_PassesValidation()
    {
        // Arrange
        var config = new Agent365Config
        {
            AgentBlueprintId = "test-blueprint-id"
        };

        // Act
        var errors = await CopilotStudioSubcommand.ValidateAsync(config);

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_WithMissingBlueprintId_FailsValidation()
    {
        // Arrange
        var config = new Agent365Config
        {
            AgentBlueprintId = ""
        };

        // Act
        var errors = await CopilotStudioSubcommand.ValidateAsync(config);

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("Blueprint ID");
    }

    #endregion
}
