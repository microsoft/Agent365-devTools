// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Tests for CreateInstanceCommand functionality
/// </summary>
public class CreateInstanceCommandTests
{
    private readonly ILogger<CreateInstanceCommand> _mockLogger;
    private readonly ConfigService _mockConfigService;
    private readonly CommandExecutor _mockExecutor;
    private readonly BotConfigurator _mockBotConfigurator;
    private readonly GraphApiService _mockGraphApiService;
    private readonly IAzureValidator _mockAzureValidator;

    public CreateInstanceCommandTests()
    {
        _mockLogger = Substitute.For<ILogger<CreateInstanceCommand>>();
        
        // Use NullLogger instead of console logger to avoid I/O bottleneck
        _mockConfigService = Substitute.ForPartsOf<ConfigService>(NullLogger<ConfigService>.Instance);
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(NullLogger<CommandExecutor>.Instance);
        _mockBotConfigurator = Substitute.ForPartsOf<BotConfigurator>(NullLogger<BotConfigurator>.Instance, _mockExecutor);
        _mockGraphApiService = Substitute.ForPartsOf<GraphApiService>(NullLogger<GraphApiService>.Instance, _mockExecutor);
        
        _mockAzureValidator = Substitute.For<IAzureValidator>();
    }

    [Fact]
    public void CreateInstanceCommand_Should_Have_Identity_Subcommand()
    {
        // Arrange
        var command = CreateInstanceCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockBotConfigurator,
            _mockGraphApiService,
            _mockAzureValidator);

        // Act
        var identitySubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "identity");

        // Assert
        Assert.NotNull(identitySubcommand);
        Assert.Equal("Create Agent Identity and Agent User", identitySubcommand.Description);
    }

    [Fact]
    public void CreateInstanceCommand_Should_Have_License_Subcommand()
    {
        // Arrange
        var command = CreateInstanceCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockBotConfigurator,
            _mockGraphApiService,
            _mockAzureValidator);

        // Act
        var licenseSubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "licenses");

        // Assert
        Assert.NotNull(licenseSubcommand);
        Assert.Equal("Add licenses to Agent User", licenseSubcommand.Description);
    }

    [Fact]
    public void CreateInstanceCommand_Should_Not_Have_ATG_Subcommand()
    {
        // Arrange
        var command = CreateInstanceCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockBotConfigurator,
            _mockGraphApiService,
            _mockAzureValidator);

        // Act
        var atgSubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "atg");

        // Assert - ATG functionality should be completely removed
        Assert.Null(atgSubcommand);
    }

    [Fact]
    public void CreateInstanceCommand_Should_Have_Handler_For_Complete_Instance_Creation()
    {
        // Arrange
        var command = CreateInstanceCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockBotConfigurator,
            _mockGraphApiService,
            _mockAzureValidator);

        // Act & Assert - Main command should have handler for running all steps
        Assert.NotNull(command.Handler);
    }
}