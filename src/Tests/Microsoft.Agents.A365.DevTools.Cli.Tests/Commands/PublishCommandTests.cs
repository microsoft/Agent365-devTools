// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Tests for PublishCommand exit code behavior.
/// Tests are limited to paths that exit before the interactive Console.ReadLine() prompts
/// in the publish flow. Paths that reach those prompts (--skip-graph, missing tenantId,
/// missing manifest file) require full HTTP/MOS mocking infrastructure to test reliably.
/// </summary>
public class PublishCommandTests
{
    private readonly ILogger<PublishCommand> _logger;
    private readonly IConfigService _configService;
    private readonly AgentPublishService _agentPublishService;
    private readonly GraphApiService _graphApiService;
    private readonly AgentBlueprintService _blueprintService;
    private readonly ManifestTemplateService _manifestTemplateService;

    public PublishCommandTests()
    {
        _logger = Substitute.For<ILogger<PublishCommand>>();
        _configService = Substitute.For<IConfigService>();

        // For concrete classes, create partial substitutes with correct constructor parameters
        // GraphApiService has a parameterless constructor
        _graphApiService = Substitute.ForPartsOf<GraphApiService>();

        // AgentPublishService needs (ILogger, GraphApiService)
        _agentPublishService = Substitute.ForPartsOf<AgentPublishService>(
            Substitute.For<ILogger<AgentPublishService>>(),
            _graphApiService);

        // AgentBlueprintService needs (ILogger, GraphApiService)
        _blueprintService = Substitute.ForPartsOf<AgentBlueprintService>(
            Substitute.For<ILogger<AgentBlueprintService>>(),
            _graphApiService);

        // ManifestTemplateService needs only ILogger
        _manifestTemplateService = Substitute.ForPartsOf<ManifestTemplateService>(
            Substitute.For<ILogger<ManifestTemplateService>>());
    }

    [Fact]
    public async Task PublishCommand_WithMissingBlueprintId_ShouldReturnExitCode1()
    {
        // Arrange - Return config with missing blueprintId (this is an error path)
        var config = new Agent365Config
        {
            AgentBlueprintId = null, // Missing blueprintId triggers error
            TenantId = "test-tenant",
            AgentBlueprintDisplayName = "Test Agent"
        };
        _configService.LoadAsync().Returns(config);

        var command = PublishCommand.CreateCommand(
            _logger,
            _configService,
            _agentPublishService,
            _graphApiService,
            _blueprintService,
            _manifestTemplateService);

        var root = new RootCommand();
        root.AddCommand(command);

        // Act
        var exitCode = await root.InvokeAsync("publish");

        // Assert
        exitCode.Should().Be(1, "missing blueprintId should return exit code 1");

        // Verify error was logged
        _logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("agentBlueprintId missing")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task PublishCommand_WithDryRun_ShouldReturnExitCode0()
    {
        // Arrange - Set up config for successful dry-run
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var manifestDir = Path.Combine(tempDir, "manifest");
        Directory.CreateDirectory(manifestDir);

        try
        {
            // Create minimal manifest files for dry-run
            var manifestPath = Path.Combine(manifestDir, "manifest.json");
            var agenticUserManifestPath = Path.Combine(manifestDir, "agenticUserTemplateManifest.json");
            await File.WriteAllTextAsync(manifestPath, "{\"id\":\"old-id\"}");
            await File.WriteAllTextAsync(agenticUserManifestPath, "{\"id\":\"old-id\"}");

            var config = new Agent365Config
            {
                AgentBlueprintId = "test-blueprint-id",
                AgentBlueprintDisplayName = "Test Agent",
                TenantId = "test-tenant",
                DeploymentProjectPath = tempDir
            };
            _configService.LoadAsync().Returns(config);

            var command = PublishCommand.CreateCommand(
                _logger,
                _configService,
                _agentPublishService,
                _graphApiService,
                _blueprintService,
                _manifestTemplateService);

            var root = new RootCommand();
            root.AddCommand(command);

            // Act - Run with --dry-run option
            var exitCode = await root.InvokeAsync("publish --dry-run");

            // Assert
            exitCode.Should().Be(0, "dry-run is a normal exit and should return exit code 0");

            // Verify dry-run log message was written
            _logger.Received().Log(
                LogLevel.Information,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("DRY RUN")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task PublishCommand_WithException_ShouldReturnExitCode1()
    {
        // Arrange - Simulate exception during config loading
        _configService.LoadAsync()
            .Returns<Agent365Config>(_ => throw new InvalidOperationException("Test exception"));

        var command = PublishCommand.CreateCommand(
            _logger,
            _configService,
            _agentPublishService,
            _graphApiService,
            _blueprintService,
            _manifestTemplateService);

        var root = new RootCommand();
        root.AddCommand(command);

        // Act
        var exitCode = await root.InvokeAsync("publish");

        // Assert
        exitCode.Should().Be(1, "exceptions should be caught and return exit code 1");

        // Verify exception was logged
        _logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Publish command failed")),
            Arg.Is<Exception>(ex => ex.Message == "Test exception"),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// Documents the four normal exit scenarios (exit code 0) and the main error scenarios (exit code 1).
    /// </summary>
    [Fact]
    public void PublishCommand_DocumentsNormalExitScenarios()
    {
        var normalExitScenarios = new[]
        {
            "Dry-run: --dry-run specified, manifest updated but not saved",
            "Skip Graph: --skip-graph specified, MOS publish succeeded",
            "Missing tenantId: MOS publish succeeded but tenantId unavailable for Graph operations",
            "Complete success: MOS publish and Graph operations both succeeded"
        };

        var errorExitScenarios = new[]
        {
            "Missing blueprintId in configuration",
            "Failed to extract manifest templates",
            "Manifest file not found",
            "MOS API call failed",
            "Graph API operations failed",
            "Exception thrown during execution"
        };

        normalExitScenarios.Should().HaveCount(4, "there are exactly 4 normal exit scenarios");
        errorExitScenarios.Length.Should().BeGreaterThan(5, "there are many error exit scenarios");
    }
}
