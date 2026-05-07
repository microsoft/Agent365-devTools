// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
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
    private readonly GraphApiService _mockGraphApiService;

    public CreateInstanceCommandTests()
    {
        _mockLogger = Substitute.For<ILogger<CreateInstanceCommand>>();

        // Use NullLogger instead of console logger to avoid I/O bottleneck
        _mockConfigService = Substitute.ForPartsOf<ConfigService>(NullLogger<ConfigService>.Instance);
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(NullLogger<CommandExecutor>.Instance);
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
            _mockGraphApiService);

        // Act - Command should be created successfully
        // Assert - Command is named "create-instance" for use as "a365 create-instance"
        Assert.NotNull(command);
        Assert.Equal("create-instance", command.Name);
    }

    [Fact]
    public async Task CreateInstance_WhenConfigFileNotFound_ShouldReturnExitCode2()
    {
        // Arrange — ConfigFileNotFoundException.ExitCode is 2 (configuration error).
        // Scripts checking $LASTEXITCODE must distinguish missing config (2) from general errors (1).
        var mockConfigService = Substitute.For<IConfigService>();
        mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromException<Agent365Config>(new ConfigFileNotFoundException()));

        var command = CreateInstanceCommand.CreateCommand(
            _mockLogger, mockConfigService, _mockExecutor, _mockGraphApiService);

        // Act
        var result = await command.InvokeAsync(new[] { "create-instance" });

        // Assert
        Assert.Equal(2, result);
    }
}
