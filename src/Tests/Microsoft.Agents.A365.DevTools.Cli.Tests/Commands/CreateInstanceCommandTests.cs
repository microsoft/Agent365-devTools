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
    private readonly IBotConfigurator _mockBotConfigurator;
    private readonly GraphApiService _mockGraphApiService;

    public CreateInstanceCommandTests()
    {
        _mockLogger = Substitute.For<ILogger<CreateInstanceCommand>>();

        // Use NullLogger instead of console logger to avoid I/O bottleneck
        _mockConfigService = Substitute.ForPartsOf<ConfigService>(NullLogger<ConfigService>.Instance);
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(NullLogger<CommandExecutor>.Instance);
        _mockBotConfigurator = Substitute.For<IBotConfigurator>();
        _mockGraphApiService = Substitute.ForPartsOf<GraphApiService>(NullLogger<GraphApiService>.Instance, _mockExecutor);
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
            _mockGraphApiService);

        // Act
        var identitySubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "identity");

        // Assert - Subcommand should be registered
        Assert.NotNull(identitySubcommand);
    }

    [Fact]
    public void CreateInstanceCommand_Should_Have_Licenses_Subcommand()
    {
        // Arrange
        var command = CreateInstanceCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockBotConfigurator,
            _mockGraphApiService);

        // Act
        var licensesSubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "licenses");

        // Assert - Subcommand should be registered
        Assert.NotNull(licensesSubcommand);
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
            _mockGraphApiService);

        // Act & Assert - Main command should have handler for running all steps
        Assert.NotNull(command.Handler);
    }

    [Fact]
    public void CreateInstanceCommand_Should_Be_Named_CreateInstance()
    {
        // Arrange
        var command = CreateInstanceCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockBotConfigurator,
            _mockGraphApiService);

        // Act - Command should be created successfully
        // Assert - Command is named "create-instance" for use as "a365 create-instance"
        Assert.NotNull(command);
        Assert.Equal("create-instance", command.Name);
    }
}