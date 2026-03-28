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
/// Tests must run sequentially because the constructor redirects Console.In (global state)
/// to auto-answer interactive prompts without blocking.
/// </summary>
[CollectionDefinition("PublishCommandTests", DisableParallelization = true)]
public class PublishCommandTestCollection { }

[Collection("PublishCommandTests")]
public class PublishCommandTests : IDisposable
{
    private readonly ILogger<PublishCommand> _logger;
    private readonly IConfigService _configService;
    private readonly ManifestTemplateService _manifestTemplateService;
    private readonly TextReader _originalConsoleIn = Console.In;

    public PublishCommandTests()
    {
        _logger = Substitute.For<ILogger<PublishCommand>>();
        _configService = Substitute.For<IConfigService>();
        _manifestTemplateService = Substitute.ForPartsOf<ManifestTemplateService>(
            Substitute.For<ILogger<ManifestTemplateService>>());

        // Auto-answer interactive prompts: "n" = skip editor, "" = press Enter to continue.
        Console.SetIn(new StringReader("n\n\n"));
    }

    public void Dispose() => Console.SetIn(_originalConsoleIn);

    [Fact]
    public async Task PublishCommand_WithMissingBlueprintId_ShouldReturnExitCode1()
    {
        var config = new Agent365Config
        {
            AgentBlueprintId = null,
            TenantId = "test-tenant",
            AgentBlueprintDisplayName = "Test Agent"
        };
        _configService.LoadAsync().Returns(config);

        var root = new RootCommand();
        root.AddCommand(PublishCommand.CreateCommand(_logger, _configService, _manifestTemplateService));

        var exitCode = await root.InvokeAsync("publish");

        exitCode.Should().Be(1, "missing blueprintId should return exit code 1");
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
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var manifestDir = Path.Combine(tempDir, "manifest");
        Directory.CreateDirectory(manifestDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(manifestDir, "manifest.json"), "{\"id\":\"old-id\"}");
            await File.WriteAllTextAsync(Path.Combine(manifestDir, "agenticUserTemplateManifest.json"), "{\"id\":\"old-id\"}");

            _configService.LoadAsync().Returns(new Agent365Config
            {
                AgentBlueprintId = "test-blueprint-id",
                AgentBlueprintDisplayName = "Test Agent",
                TenantId = "test-tenant",
                DeploymentProjectPath = tempDir
            });

            var root = new RootCommand();
            root.AddCommand(PublishCommand.CreateCommand(_logger, _configService, _manifestTemplateService));

            var exitCode = await root.InvokeAsync("publish --dry-run");

            exitCode.Should().Be(0, "dry-run should return exit code 0");
            _logger.Received().Log(
                LogLevel.Information,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("DRY RUN")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task PublishCommand_WithValidConfig_CreatesZipAndReturnsExitCode0()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var manifestDir = Path.Combine(tempDir, "manifest");
        Directory.CreateDirectory(manifestDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(manifestDir, "manifest.json"), "{\"id\":\"old-id\"}");
            await File.WriteAllTextAsync(Path.Combine(manifestDir, "agenticUserTemplateManifest.json"), "{\"agentIdentityBlueprintId\":\"old-id\"}");

            _configService.LoadAsync().Returns(new Agent365Config
            {
                AgentBlueprintId = "test-blueprint-id",
                AgentBlueprintDisplayName = "Test Agent",
                TenantId = "test-tenant",
                DeploymentProjectPath = tempDir
            });

            var root = new RootCommand();
            root.AddCommand(PublishCommand.CreateCommand(_logger, _configService, _manifestTemplateService));

            var exitCode = await root.InvokeAsync("publish");

            exitCode.Should().Be(0, "successful publish should return exit code 0");
            File.Exists(Path.Combine(manifestDir, "manifest.zip")).Should().BeTrue("manifest.zip should be created");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task PublishCommand_WithDisplayNameExceeding30Chars_LogsWarning()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var manifestDir = Path.Combine(tempDir, "manifest");
        Directory.CreateDirectory(manifestDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(manifestDir, "manifest.json"), "{\"id\":\"old-id\"}");
            await File.WriteAllTextAsync(Path.Combine(manifestDir, "agenticUserTemplateManifest.json"), "{\"agentIdentityBlueprintId\":\"old-id\"}");

            _configService.LoadAsync().Returns(new Agent365Config
            {
                AgentBlueprintId = "test-blueprint-id",
                AgentBlueprintDisplayName = "This Display Name Is Way Too Long For Short",
                TenantId = "test-tenant",
                DeploymentProjectPath = tempDir
            });

            var root = new RootCommand();
            root.AddCommand(PublishCommand.CreateCommand(_logger, _configService, _manifestTemplateService));

            var exitCode = await root.InvokeAsync("publish");

            exitCode.Should().Be(0);
            _logger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("EXCEEDS 30 chars")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task PublishCommand_WithException_ShouldReturnExitCode1()
    {
        _configService.LoadAsync()
            .Returns<Agent365Config>(_ => throw new InvalidOperationException("Test exception"));

        var root = new RootCommand();
        root.AddCommand(PublishCommand.CreateCommand(_logger, _configService, _manifestTemplateService));

        var exitCode = await root.InvokeAsync("publish");

        exitCode.Should().Be(1, "exceptions should return exit code 1");
        _logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Publish command failed")),
            Arg.Is<Exception>(ex => ex.Message == "Test exception"),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
