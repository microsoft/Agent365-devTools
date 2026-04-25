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
/// Tests for PublishCommand non-DW dry-run behavior — Phase A.
/// Verifies correct template selection, field substitution preview, and that no files are written.
/// </summary>
[CollectionDefinition("NonDwPublishCommandDryRunTests", DisableParallelization = true)]
public class NonDwPublishCommandDryRunTestCollection { }

[Collection("NonDwPublishCommandDryRunTests")]
public class NonDwPublishCommandDryRunTests : IDisposable
{
    private readonly ILogger<PublishCommand> _logger;
    private readonly IConfigService _configService;
    private readonly ManifestTemplateService _manifestTemplateService;
    private readonly TextReader _originalConsoleIn = Console.In;

    public NonDwPublishCommandDryRunTests()
    {
        _logger = Substitute.For<ILogger<PublishCommand>>();
        _configService = Substitute.For<IConfigService>();
        _manifestTemplateService = Substitute.ForPartsOf<ManifestTemplateService>(
            Substitute.For<ILogger<ManifestTemplateService>>());

        Console.SetIn(new StringReader("n\n\n"));
    }

    public void Dispose() => Console.SetIn(_originalConsoleIn);

    private static Agent365Config BuildNonDwConfig(
        string clientAppId = "11111111-1111-1111-1111-111111111111") =>
        new()
        {
            ClientAppId = clientAppId,
            AiTeammate = false,
            TenantId = "tenant-id",
            AgentIdentityDisplayName = "My Agent",
            DeploymentProjectPath = "./app"
        };

    [Fact]
    public async Task Publish_NonDwDryRun_ViaFlag_ReturnsExitCode0()
    {
        // AiTeammate not set in config — driven by flag only
        var config = new Agent365Config
        {
            ClientAppId = "11111111-1111-1111-1111-111111111111",
            AiTeammate = null,
            TenantId = "tenant-id",
            AgentIdentityDisplayName = "My Agent",
            DeploymentProjectPath = "./app"
        };
        _configService.LoadAsync().Returns(config);
        _configService.LoadAsync(Arg.Any<string>()).Returns(config);

        var root = new RootCommand();
        root.AddCommand(PublishCommand.CreateCommand(_logger, _configService, _manifestTemplateService));

        var exitCode = await root.InvokeAsync("publish --dry-run --aiteammate false");

        exitCode.Should().Be(0, "non-DW dry-run via flag should succeed");
    }

    [Fact]
    public async Task Publish_NonDwDryRun_ViaConfigAiTeammate_ReturnsExitCode0()
    {
        var config = BuildNonDwConfig();
        _configService.LoadAsync().Returns(config);
        _configService.LoadAsync(Arg.Any<string>()).Returns(config);

        var root = new RootCommand();
        root.AddCommand(PublishCommand.CreateCommand(_logger, _configService, _manifestTemplateService));

        var exitCode = await root.InvokeAsync("publish --dry-run");

        exitCode.Should().Be(0, "non-DW dry-run via config aiTeammate should succeed");
    }

[Fact]
    public async Task Publish_NonDwDryRun_LogsClientAppIdAsSourceOfTruth()
    {
        const string clientAppId = "aaaabbbb-cccc-dddd-eeee-ffffffffffff";
        var config = BuildNonDwConfig(clientAppId: clientAppId);
        _configService.LoadAsync().Returns(config);
        _configService.LoadAsync(Arg.Any<string>()).Returns(config);

        var root = new RootCommand();
        root.AddCommand(PublishCommand.CreateCommand(_logger, _configService, _manifestTemplateService));

        await root.InvokeAsync("publish --dry-run");

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains(clientAppId)),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Publish_NonDwDryRun_LogsZipContentsWithoutAgenticUserManifest()
    {
        var config = BuildNonDwConfig();
        _configService.LoadAsync().Returns(config);
        _configService.LoadAsync(Arg.Any<string>()).Returns(config);

        var root = new RootCommand();
        root.AddCommand(PublishCommand.CreateCommand(_logger, _configService, _manifestTemplateService));

        await root.InvokeAsync("publish --dry-run");

        // Should mention color.png
        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("color.png")),
            null,
            Arg.Any<Func<object, Exception?, string>>());

        // Must NOT mention agenticUserTemplateManifest.json
        _logger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("agenticUserTemplateManifest")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Publish_NonDwWithoutDryRun_ReturnsExitCode1()
    {
        var config = BuildNonDwConfig();
        _configService.LoadAsync().Returns(config);
        _configService.LoadAsync(Arg.Any<string>()).Returns(config);

        var root = new RootCommand();
        root.AddCommand(PublishCommand.CreateCommand(_logger, _configService, _manifestTemplateService));

        var exitCode = await root.InvokeAsync("publish");

        exitCode.Should().Be(1, "non-DW publish without --dry-run should return 1 until Phase B is implemented");
    }

    [Fact]
    public async Task Publish_DigitalWorkerPath_IsUnaffectedByChanges()
    {
        // Verify DW path still requires blueprintId (no regression)
        var config = new Agent365Config
        {
            AgentBlueprintId = null,
            AiTeammate = true,
            TenantId = "tenant-id",
            ClientAppId = "client-app-id"
        };
        _configService.LoadAsync().Returns(config);
        _configService.LoadAsync(Arg.Any<string>()).Returns(config);

        var root = new RootCommand();
        root.AddCommand(PublishCommand.CreateCommand(_logger, _configService, _manifestTemplateService));

        var exitCode = await root.InvokeAsync("publish");

        exitCode.Should().Be(1, "DW path without blueprintId should still return 1");
    }

    [Fact]
    public async Task Publish_BlueprintNonDwDryRun_ViaFlag_ReturnsExitCode0()
    {
        var config = new Agent365Config
        {
            AiTeammate = null,
            TenantId = "tenant-id",
            ClientAppId = "client-app-id",
            AgentIdentityDisplayName = "My Agent"
        };
        _configService.LoadAsync().Returns(config);
        _configService.LoadAsync(Arg.Any<string>()).Returns(config);

        var root = new RootCommand();
        root.AddCommand(PublishCommand.CreateCommand(_logger, _configService, _manifestTemplateService));

        var exitCode = await root.InvokeAsync("publish --dry-run --aiteammate false --use-blueprint");

        exitCode.Should().Be(0, "blueprint non-DW dry-run via flags should succeed");
    }

    [Fact]
    public async Task Publish_BlueprintNonDwDryRun_ViaConfig_ReturnsExitCode0()
    {
        var config = new Agent365Config
        {
            AiTeammate = false,
            UseBlueprint = true,
            TenantId = "tenant-id",
            ClientAppId = "client-app-id",
            AgentIdentityDisplayName = "My Agent"
        };
        _configService.LoadAsync().Returns(config);
        _configService.LoadAsync(Arg.Any<string>()).Returns(config);

        var root = new RootCommand();
        root.AddCommand(PublishCommand.CreateCommand(_logger, _configService, _manifestTemplateService));

        var exitCode = await root.InvokeAsync("publish --dry-run");

        exitCode.Should().Be(0, "blueprint non-DW dry-run via config should succeed");
    }

    [Fact]
    public async Task Publish_BlueprintNonDwDryRun_LogsBlueprintId()
    {
        const string blueprintId = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";
        var config = new Agent365Config
        {
            AiTeammate = false,
            UseBlueprint = true,
            TenantId = "tenant-id",
            ClientAppId = "client-app-id",
            AgentBlueprintId = blueprintId,
            AgentIdentityDisplayName = "My Agent"
        };
        _configService.LoadAsync().Returns(config);
        _configService.LoadAsync(Arg.Any<string>()).Returns(config);

        var root = new RootCommand();
        root.AddCommand(PublishCommand.CreateCommand(_logger, _configService, _manifestTemplateService));

        await root.InvokeAsync("publish --dry-run");

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains(blueprintId)),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Publish_BlueprintNonDwDryRun_DoesNotLogManifestOrZip()
    {
        var config = new Agent365Config
        {
            AiTeammate = false,
            UseBlueprint = true,
            TenantId = "tenant-id",
            ClientAppId = "client-app-id",
            AgentIdentityDisplayName = "My Agent"
        };
        _configService.LoadAsync().Returns(config);
        _configService.LoadAsync(Arg.Any<string>()).Returns(config);

        var root = new RootCommand();
        root.AddCommand(PublishCommand.CreateCommand(_logger, _configService, _manifestTemplateService));

        await root.InvokeAsync("publish --dry-run");

        _logger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("manifest.nondw.json")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }
}
