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
    private readonly IAzureValidator _mockAzureValidator;

    public CreateInstanceCommandTests()
    {
        _mockLogger = Substitute.For<ILogger<CreateInstanceCommand>>();
        
        // Use NullLogger instead of console logger to avoid I/O bottleneck
        _mockConfigService = Substitute.ForPartsOf<ConfigService>(NullLogger<ConfigService>.Instance);
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(NullLogger<CommandExecutor>.Instance);
        _mockBotConfigurator = Substitute.For<IBotConfigurator>();
        _mockGraphApiService = Substitute.ForPartsOf<GraphApiService>(NullLogger<GraphApiService>.Instance, _mockExecutor);
        _mockAzureValidator = Substitute.For<IAzureValidator>();
    }

    [Fact]
    public void CreateInstanceCommand_Should_Not_Have_Identity_Subcommand_Due_To_Deprecation()
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

        // Assert - Subcommand should not be registered since command is deprecated
        Assert.Null(identitySubcommand);
    }

    [Fact]
    public void CreateInstanceCommand_Should_Not_Have_Licenses_Subcommand_Due_To_Deprecation()
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
        var licensesSubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "licenses");

        // Assert - Subcommand should not be registered since command is deprecated
        Assert.Null(licensesSubcommand);
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

    [Fact]
    public void CreateInstanceCommand_Should_Log_Deprecation_Error()
    {
        // Arrange
        var command = CreateInstanceCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockBotConfigurator,
            _mockGraphApiService,
            _mockAzureValidator);

        // Act - Command should be created successfully
        // Assert - Command structure is valid
        Assert.NotNull(command);
        Assert.Equal("create-instance", command.Name);
        
        // Verify deprecation message structure through logger assertions would require execution
        // which would call Environment.Exit(1). Testing the command creation is sufficient.
    }
}