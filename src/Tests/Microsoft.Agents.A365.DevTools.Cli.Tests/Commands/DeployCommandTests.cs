// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Regression tests for DeployCommand subcommand functionality
/// </summary>
public class DeployCommandTests
{
    private readonly ILogger<DeployCommand> _mockLogger;
    private readonly ConfigService _mockConfigService;
    private readonly CommandExecutor _mockExecutor;
    private readonly DeploymentService _mockDeploymentService;
    private readonly IAzureValidator _mockAzureValidator;

    public DeployCommandTests()
    {
        _mockLogger = Substitute.For<ILogger<DeployCommand>>();
        
        // For concrete classes, we need to create real instances with mocked dependencies
        var mockConfigLogger = Substitute.For<ILogger<ConfigService>>();
        _mockConfigService = Substitute.ForPartsOf<ConfigService>(mockConfigLogger);
        
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);
        
        var mockDeployLogger = Substitute.For<ILogger<DeploymentService>>();
        var mockPlatformDetectorLogger = Substitute.For<ILogger<PlatformDetector>>();
        var mockPlatformDetector = Substitute.ForPartsOf<PlatformDetector>(mockPlatformDetectorLogger);
        var mockDotNetLogger = Substitute.For<ILogger<DotNetBuilder>>();
        var mockNodeLogger = Substitute.For<ILogger<NodeBuilder>>();
        var mockPythonLogger = Substitute.For<ILogger<PythonBuilder>>();
        _mockDeploymentService = Substitute.ForPartsOf<DeploymentService>(
            mockDeployLogger, 
            _mockExecutor, 
            mockPlatformDetector,
            mockDotNetLogger,
            mockNodeLogger,
            mockPythonLogger);
        
        _mockAzureValidator = Substitute.For<IAzureValidator>();
    }

    [Fact]
    public void UpdateCommand_Should_Not_Have_Atg_Subcommand()
    {
        // Arrange
        var command = DeployCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockDeploymentService,
            _mockAzureValidator);

        // Act
        var atgSubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "atg");

        // Assert - ATG subcommand was removed
        Assert.Null(atgSubcommand);
    }

    [Fact]
    public void UpdateCommand_Should_Have_Config_Option_With_Default()
    {
        // Arrange
        var command = DeployCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockDeploymentService,
            _mockAzureValidator);

        // Act
        var configOption = command.Options.FirstOrDefault(o => o.Name == "config");

        // Assert - Config option exists with default value
        Assert.NotNull(configOption);
        Assert.Equal("Path to the configuration file (default: a365.config.json)", configOption.Description);
    }

    [Fact]
    public void UpdateCommand_Should_Have_Verbose_Option()
    {
        // Arrange
        var command = DeployCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockDeploymentService,
            _mockAzureValidator);

        // Act
        var verboseOption = command.Options.FirstOrDefault(o => o.Name == "verbose");

        // Assert
        Assert.NotNull(verboseOption);
        Assert.Equal("Enable verbose logging", verboseOption.Description);
    }


    // NOTE: Integration tests that verify actual service invocation through command execution
    // are omitted here as they require complex mocking of logging infrastructure.
    // The command functionality is tested through integration/end-to-end tests when running
    // `a365 deploy` and observing output logs and Azure resources.
}
