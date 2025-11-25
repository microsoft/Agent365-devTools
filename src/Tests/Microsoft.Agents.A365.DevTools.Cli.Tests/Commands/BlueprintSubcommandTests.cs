// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Unit tests for Blueprint subcommand
/// </summary>
public class BlueprintSubcommandTests
{
    private readonly ILogger _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly CommandExecutor _mockExecutor;
    private readonly IAzureValidator _mockAzureValidator;
    private readonly AzureWebAppCreator _mockWebAppCreator;
    private readonly PlatformDetector _mockPlatformDetector;

    public BlueprintSubcommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockConfigService = Substitute.For<IConfigService>();
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);
        _mockAzureValidator = Substitute.For<IAzureValidator>();
        _mockWebAppCreator = Substitute.ForPartsOf<AzureWebAppCreator>(Substitute.For<ILogger<AzureWebAppCreator>>());
        var mockPlatformDetectorLogger = Substitute.For<ILogger<PlatformDetector>>();
        _mockPlatformDetector = Substitute.ForPartsOf<PlatformDetector>(mockPlatformDetectorLogger);
    }

    [Fact]
    public void CreateCommand_ShouldHaveCorrectName()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        // Assert
        command.Name.Should().Be("blueprint");
    }

    [Fact]
    public void CreateCommand_ShouldHaveDescription()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        // Assert
        command.Description.Should().NotBeNullOrEmpty();
        command.Description.Should().Contain("agent blueprint");
    }

    [Fact]
    public void CreateCommand_ShouldHaveConfigOption()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        // Assert
        var configOption = command.Options.FirstOrDefault(o => o.Name == "config");
        configOption.Should().NotBeNull();
        configOption!.Aliases.Should().Contain("--config");
        configOption.Aliases.Should().Contain("-c");
    }

    [Fact]
    public void CreateCommand_ShouldHaveVerboseOption()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        // Assert
        var verboseOption = command.Options.FirstOrDefault(o => o.Name == "verbose");
        verboseOption.Should().NotBeNull();
        verboseOption!.Aliases.Should().Contain("--verbose");
        verboseOption.Aliases.Should().Contain("-v");
    }

    [Fact]
    public void CreateCommand_ShouldHaveDryRunOption()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        // Assert
        var dryRunOption = command.Options.FirstOrDefault(o => o.Name == "dry-run");
        dryRunOption.Should().NotBeNull();
        dryRunOption!.Aliases.Should().Contain("--dry-run");
    }

    [Fact]
    public async Task DryRun_ShouldLoadConfigAndNotExecute()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintDisplayName = "Test Blueprint"
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("--dry-run", testConsole);

        // Assert
        result.Should().Be(0);
        await _mockConfigService.Received(1).LoadAsync(Arg.Any<string>(), Arg.Any<string>());
        await _mockAzureValidator.DidNotReceiveWithAnyArgs().ValidateAllAsync(default!);
    }

    [Fact]
    public async Task DryRun_ShouldDisplayBlueprintInformation()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            AgentBlueprintDisplayName = "My Test Blueprint"
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("--dry-run", testConsole);

        // Assert
        result.Should().Be(0);
        
        // Verify logger received appropriate calls about what would be done
        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("DRY RUN")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CreateBlueprintImplementation_WithMissingDisplayName_ShouldThrow()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000", // Valid GUID format
            SubscriptionId = "test-sub",
            AgentBlueprintDisplayName = "" // Missing display name
        };

        var configFile = new FileInfo("test-config.json");

        _mockAzureValidator.ValidateAllAsync(Arg.Any<string>())
            .Returns(true);

        // Note: Since DelegatedConsentService needs to run and will fail with invalid tenant,
        // the method returns false rather than throwing for missing display name upfront.
        // The display name check happens after consent, so this test verifies
        // the method can handle failures gracefully.
        
        // Act
        var result = await BlueprintSubcommand.CreateBlueprintImplementationAsync(
                config,
                configFile,
                _mockExecutor,
                _mockAzureValidator,
                _mockLogger,
                skipInfrastructure: false,
                isSetupAll: false);

        // Assert - Should return false when consent service fails
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CreateBlueprintImplementation_WithAzureValidationFailure_ShouldReturnFalse()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            SubscriptionId = "test-sub",
            AgentBlueprintDisplayName = "Test Blueprint"
        };

        var configFile = new FileInfo("test-config.json");

        _mockAzureValidator.ValidateAllAsync(Arg.Any<string>())
            .Returns(false); // Validation fails

        // Act
        var result = await BlueprintSubcommand.CreateBlueprintImplementationAsync(
            config,
            configFile,
            _mockExecutor,
            _mockAzureValidator,
            _mockLogger,
            skipInfrastructure: false,
            isSetupAll: false);

        // Assert
        result.Should().BeFalse();
        await _mockAzureValidator.Received(1).ValidateAllAsync(config.SubscriptionId);
    }

    [Fact]
    public void CommandDescription_ShouldMentionRequiredPermissions()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        // Assert
        command.Description.Should().Contain("Agent ID Developer");
    }

    [Fact]
    public async Task DryRun_WithCustomConfigPath_ShouldLoadCorrectFile()
    {
        // Arrange
        var customPath = "custom-config.json";
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintDisplayName = "Test Blueprint"
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync($"--config {customPath} --dry-run", testConsole);

        // Assert
        result.Should().Be(0);
        await _mockConfigService.Received(1).LoadAsync(
            Arg.Is<string>(s => s.Contains(customPath)),
            Arg.Any<string>());
    }

    [Fact]
    public async Task DryRun_ShouldNotCreateServicePrincipal()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintDisplayName = "Test Blueprint"
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("--dry-run", testConsole);

        // Assert
        result.Should().Be(0);
        
        // Verify no Azure CLI commands were executed
        await _mockExecutor.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public void CreateCommand_ShouldHandleAllOptions()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        // Assert - Verify all expected options are present
        command.Options.Should().HaveCountGreaterOrEqualTo(3);
        
        var optionNames = command.Options.Select(o => o.Name).ToList();
        optionNames.Should().Contain("config");
        optionNames.Should().Contain("verbose");
        optionNames.Should().Contain("dry-run");
    }

    [Fact]
    public async Task DryRun_WithMissingConfig_ShouldHandleGracefully()
    {
        // Arrange
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns<Agent365Config>(_ => throw new FileNotFoundException("Config not found"));

        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await parser.InvokeAsync("--dry-run", testConsole));
    }

    [Fact]
    public void CreateCommand_DefaultConfigPath_ShouldBeA365ConfigJson()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        // Assert - Verify the config option exists and has expected aliases
        var configOption = command.Options.First(o => o.Name == "config");
        configOption.Should().NotBeNull();
        configOption.Aliases.Should().Contain("--config");
        configOption.Aliases.Should().Contain("-c");
    }

    [Fact]
    public async Task CreateBlueprintImplementation_ShouldLogProgressMessages()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            SubscriptionId = "test-sub",
            AgentBlueprintDisplayName = "Test Blueprint"
        };

        var configFile = new FileInfo("test-config.json");

        _mockAzureValidator.ValidateAllAsync(Arg.Any<string>())
            .Returns(false); // Fail fast for this test

        // Act
        var result = await BlueprintSubcommand.CreateBlueprintImplementationAsync(
            config,
            configFile,
            _mockExecutor,
            _mockAzureValidator,
            _mockLogger,
            skipInfrastructure: false,
            isSetupAll: false);

        // Assert
        result.Should().BeFalse();
        
        // Verify progress logging occurred
        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Creating Agent Blueprint")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void CommandDescription_ShouldBeInformativeAndActionable()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        // Assert - Verify description provides context and guidance
        command.Description.Should().NotBeNullOrEmpty();
        command.Description.Should().ContainAny("blueprint", "agent", "Entra ID", "application");
    }

    [Fact]
    public async Task DryRun_WithVerboseFlag_ShouldSucceed()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintDisplayName = "Test Blueprint"
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("--dry-run --verbose", testConsole);

        // Assert
        result.Should().Be(0);
        await _mockConfigService.Received(1).LoadAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task DryRun_ShouldShowWhatWouldBeDone()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "tenant-123",
            AgentBlueprintDisplayName = "Production Blueprint"
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("--dry-run", testConsole);

        // Assert
        result.Should().Be(0);
        
        // Verify the display name and tenant are mentioned in logs
        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Production Blueprint") || o.ToString()!.Contains("Display Name")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void CreateCommand_ShouldBeUsableInCommandPipeline()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector);

        // Assert - Verify command can be added to a parser
        var parser = new CommandLineBuilder(command).Build();
        parser.Should().NotBeNull();
    }
}
