// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly AzureAuthValidator _mockAuthValidator;
    private readonly GraphApiService _mockGraphApiService;
    private readonly AgentBlueprintService _mockBlueprintService;

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
        
        _mockAuthValidator = Substitute.ForPartsOf<AzureAuthValidator>(NullLogger<AzureAuthValidator>.Instance, _mockExecutor);
        _mockGraphApiService = Substitute.ForPartsOf<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), _mockExecutor);
        _mockBlueprintService = Substitute.ForPartsOf<AgentBlueprintService>(Substitute.For<ILogger<AgentBlueprintService>>(), _mockGraphApiService);
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
            _mockAuthValidator,
            _mockGraphApiService, _mockBlueprintService);

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
            _mockAuthValidator,
            _mockGraphApiService, _mockBlueprintService);

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
            _mockAuthValidator,
            _mockGraphApiService, _mockBlueprintService);

        // Act
        var verboseOption = command.Options.FirstOrDefault(o => o.Name == "verbose");

        // Assert
        Assert.NotNull(verboseOption);
        Assert.Equal("Enable verbose logging", verboseOption.Description);
    }


    /// <summary>
    /// Regression: HandleDeploymentException must not wrap a DeployAppException in another DeployAppException.
    /// Wrapping caused the full az cli stderr (stored in the exception message) to be printed 3 times.
    /// </summary>
    [Fact]
    public void HandleDeploymentException_WithDeployAppException_RethrowsWithoutWrapping()
    {
        // Arrange
        var original = new DeployAppException("Site failed to start. Check runtime logs: https://myapp.scm.azurewebsites.net/api/logs/docker");
        var method = typeof(DeployCommand).GetMethod(
            "HandleDeploymentException",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        // Act
        var act = () => method!.Invoke(null, new object[] { original, _mockLogger });

        // Assert — must rethrow the same type without wrapping
        act.Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<DeployAppException>()
            .Where(ex => ReferenceEquals(ex, original), "the same instance must be rethrown, not a new wrapper");
    }

    /// <summary>
    /// Regression: HandleDeploymentException must wrap non-DeployAppException in DeployAppException.
    /// </summary>
    [Fact]
    public void HandleDeploymentException_WithGenericException_WrapsInDeployAppException()
    {
        // Arrange
        var original = new InvalidOperationException("Something unexpected");
        var method = typeof(DeployCommand).GetMethod(
            "HandleDeploymentException",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        // Act
        var act = () => method!.Invoke(null, new object[] { original, _mockLogger });

        // Assert — generic exceptions should be wrapped
        act.Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<DeployAppException>();
    }

    // NOTE: Integration tests that verify actual service invocation through command execution
    // are omitted here as they require complex mocking of logging infrastructure.
    // The command functionality is tested through integration/end-to-end tests when running
    // `a365 deploy` and observing output logs and Azure resources.
}
