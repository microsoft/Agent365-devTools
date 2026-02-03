// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Tests for DeploymentService, focusing on validation and error handling
/// </summary>
public class DeploymentServiceTests
{
    private readonly ILogger<DeploymentService> _logger;
    private readonly CommandExecutor _mockExecutor;
    private readonly PlatformDetector _mockPlatformDetector;
    private readonly ILogger<DotNetBuilder> _dotnetLogger;
    private readonly ILogger<NodeBuilder> _nodeLogger;
    private readonly ILogger<PythonBuilder> _pythonLogger;
    private readonly DeploymentService _deploymentService;

    public DeploymentServiceTests()
    {
        _logger = Substitute.For<ILogger<DeploymentService>>();

        var executorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(executorLogger);

        var detectorLogger = Substitute.For<ILogger<PlatformDetector>>();
        _mockPlatformDetector = Substitute.ForPartsOf<PlatformDetector>(detectorLogger);

        _dotnetLogger = Substitute.For<ILogger<DotNetBuilder>>();
        _nodeLogger = Substitute.For<ILogger<NodeBuilder>>();
        _pythonLogger = Substitute.For<ILogger<PythonBuilder>>();

        _deploymentService = new DeploymentService(
            _logger,
            _mockExecutor,
            _mockPlatformDetector,
            _dotnetLogger,
            _nodeLogger,
            _pythonLogger);
    }

    [Fact]
    public async Task DeployAsync_NonExistentProjectPath_FailsImmediately()
    {
        // Arrange
        var config = new DeploymentConfiguration
        {
            ResourceGroup = "test-rg",
            AppName = "TestWebApp",
            ProjectPath = "C:\\NonExistent\\Path",
            DeploymentZip = "app.zip",
            PublishOutputPath = "publish",
            Platform = ProjectPlatform.DotNet
        };

        // Act
        var act = async () => await _deploymentService.DeployAsync(config, verbose: false);

        // Assert - Should fail immediately with DirectoryNotFoundException
        await act.Should().ThrowAsync<DirectoryNotFoundException>()
            .WithMessage("*not found*");
    }

    /// <summary>
    /// Tests that .NET platform returns correct linuxFxVersion format.
    /// </summary>
    [Fact]
    public void GetLinuxFxVersionForPlatform_DotNet_ReturnsCorrectFormat()
    {
        // Act
        var result = DeploymentService.GetLinuxFxVersionForPlatform(ProjectPlatform.DotNet);

        // Assert
        result.Should().Be("DOTNETCORE|8.0");
        result.Should().StartWith("DOTNETCORE|", "to ensure Azure selects .NET container");
    }

    /// <summary>
    /// Tests that Python platform returns correct linuxFxVersion format.
    /// </summary>
    [Fact]
    public void GetLinuxFxVersionForPlatform_Python_ReturnsCorrectFormat()
    {
        // Act
        var result = DeploymentService.GetLinuxFxVersionForPlatform(ProjectPlatform.Python);

        // Assert
        result.Should().Be("PYTHON|3.11");
        result.Should().StartWith("PYTHON|");
    }

    /// <summary>
    /// Tests that Node.js platform returns correct linuxFxVersion format.
    /// </summary>
    [Fact]
    public void GetLinuxFxVersionForPlatform_NodeJs_ReturnsCorrectFormat()
    {
        // Act
        var result = DeploymentService.GetLinuxFxVersionForPlatform(ProjectPlatform.NodeJs);

        // Assert
        result.Should().Be("NODE|20-lts");
        result.Should().StartWith("NODE|");
    }

    /// <summary>
    /// Tests that Unknown platform defaults to .NET 8.0.
    /// </summary>
    [Fact]
    public void GetLinuxFxVersionForPlatform_Unknown_DefaultsToDotNet8()
    {
        // Act
        var result = DeploymentService.GetLinuxFxVersionForPlatform(ProjectPlatform.Unknown);

        // Assert
        result.Should().Be("DOTNETCORE|8.0", "Unknown platform should default to .NET 8.0 to avoid PHP container selection");
    }
}
