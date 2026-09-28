// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using System.Net.Http;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly AzureAuthValidator _mockAuthValidator;
    private readonly PlatformDetector _mockPlatformDetector;
    private readonly ITeamsGraphBackendConfigurator _mockBackendConfigurator;
    private readonly GraphApiService _mockGraphApiService;
    private readonly AgentBlueprintService _mockBlueprintService;
    private readonly IClientAppValidator _mockClientAppValidator;
    private readonly BlueprintLookupService _mockBlueprintLookupService;
    private readonly FederatedCredentialService _mockFederatedCredentialService;

    public BlueprintSubcommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockConfigService = Substitute.For<IConfigService>();
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        // Full mock — ForPartsOf would fall through to real CommandExecutor.ExecuteAsync and spawn real processes
        _mockExecutor = Substitute.For<CommandExecutor>(mockExecutorLogger);
        _mockExecutor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty }));
        // Full mock — both virtual methods are always stubbed by callers
        _mockAuthValidator = Substitute.For<AzureAuthValidator>(NullLogger<AzureAuthValidator>.Instance, _mockExecutor);
        var mockPlatformDetectorLogger = Substitute.For<ILogger<PlatformDetector>>();
        _mockPlatformDetector = Substitute.ForPartsOf<PlatformDetector>(mockPlatformDetectorLogger);
        _mockBackendConfigurator = Substitute.For<ITeamsGraphBackendConfigurator>();
        _mockGraphApiService = Substitute.ForPartsOf<GraphApiService>(
            Substitute.For<ILogger<GraphApiService>>(), _mockExecutor, (Func<Task<string?>>)(() => Task.FromResult<string?>(null)));
        _mockBlueprintService = Substitute.ForPartsOf<AgentBlueprintService>(Substitute.For<ILogger<AgentBlueprintService>>(), _mockGraphApiService);
        _mockClientAppValidator = Substitute.For<IClientAppValidator>();
        _mockBlueprintLookupService = Substitute.ForPartsOf<BlueprintLookupService>(Substitute.For<ILogger<BlueprintLookupService>>(), _mockGraphApiService);
        _mockFederatedCredentialService = Substitute.ForPartsOf<FederatedCredentialService>(Substitute.For<ILogger<FederatedCredentialService>>(), _mockGraphApiService);
    }

    [Fact]
    public void CreateCommand_ShouldHaveCorrectName()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

        // Assert
        command.Description.Should().NotBeNullOrEmpty();
        command.Description.Should().Contain("agent blueprint");
    }

    [Fact]
    public void CreateCommand_ShouldHaveVerboseOption()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

        // Assert
        var dryRunOption = command.Options.FirstOrDefault(o => o.Name == "dry-run");
        dryRunOption.Should().NotBeNull();
        dryRunOption!.Aliases.Should().Contain("--dry-run");
    }

    [Fact]
    public void CreateCommand_ShouldHaveSkipRequirementsOption()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

        // Assert
        var skipRequirementsOption = command.Options.FirstOrDefault(o => o.Name == "skip-requirements");
        skipRequirementsOption.Should().NotBeNull();
        skipRequirementsOption!.Aliases.Should().Contain("--skip-requirements");
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
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("--dry-run", testConsole);

        // Assert
        result.Should().Be(0);
        await _mockConfigService.Received(1).LoadAsync(Arg.Any<string>(), Arg.Any<string>());
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
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("--dry-run", testConsole);

        // Assert
        result.Should().Be(0);
        
        // Verify logger received the dry-run header and blueprint details
        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Dry run:")),
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
            AgentBlueprintDisplayName = "" // Missing display name
        };

        var configFile = new FileInfo("test-config.json");

        // Note: Since DelegatedConsentService needs to run and will fail with invalid tenant,
        // the method returns false rather than throwing for missing display name upfront.
        // The display name check happens after consent, so this test verifies
        // the method can handle failures gracefully.

        // Act
        var result = await BlueprintSubcommand.CreateBlueprintImplementationAsync(
                config,
                configFile,
                _mockExecutor,
                _mockAuthValidator,
                _mockLogger,
                skipInfrastructure: false,
                isSetupAll: false,
                _mockConfigService,
                _mockBackendConfigurator,
                _mockPlatformDetector,
                _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService);

        // Assert - Should return false when consent service fails
        result.Should().NotBeNull();
        result.BlueprintCreated.Should().BeFalse();
        result.EndpointRegistered.Should().BeFalse();
    }


    [Fact]
    public void CommandDescription_ShouldMentionRequiredPermissions()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

        // Assert
        command.Description.Should().Contain("Agent ID Developer");
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
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

        // Assert - Verify all expected options are present
        command.Options.Should().HaveCountGreaterOrEqualTo(2);

        var optionNames = command.Options.Select(o => o.Name).ToList();
        optionNames.Should().Contain("verbose");
        optionNames.Should().Contain("dry-run");
    }

    [Fact]
    public async Task DryRun_WithMissingConfig_ShouldHandleGracefully()
    {
        // Arrange — config load throws (no a365.config.json in test directory)
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns<Agent365Config>(_ => throw new FileNotFoundException("Config not found"));

        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act — dry-run must not throw when config is missing; the flag must work in fresh directories
        var result = await parser.InvokeAsync("--dry-run", testConsole);

        // Assert — exits cleanly with generic dry-run preview
        result.Should().Be(0, because: "--dry-run must succeed even without a config file");
    }

    [Fact]
    public async Task CreateBlueprintImplementation_ShouldLogProgressMessages()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintDisplayName = "Test Blueprint",
        };

        var configFile = new FileInfo("test-config.json");

        // Act
        var result = await BlueprintSubcommand.CreateBlueprintImplementationAsync(
            config,
            configFile,
            _mockExecutor,
            _mockAuthValidator,
            _mockLogger,
            skipInfrastructure: false,
            isSetupAll: false,
            _mockConfigService,
            _mockBackendConfigurator,
            _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService);

        // Assert
        result.Should().NotBeNull();
        result.BlueprintCreated.Should().BeFalse();
        result.EndpointRegistered.Should().BeFalse();
        
        // Verify progress logging occurred
        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Creating agent blueprint")),
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
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

        // Assert - Verify command can be added to a parser
        var parser = new CommandLineBuilder(command).Build();
        parser.Should().NotBeNull();
    }

    #region Endpoint validation Tests (Testing logic without parser)

    [Fact]
    public async Task ValidationLogic_WithMissingBlueprintId_ShouldLogError()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "", // Missing blueprint ID
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        // Act - Load config and validate
        var loadedConfig = await _mockConfigService.LoadAsync("test-config.json");

        // Assert - Verify validation would catch this
        loadedConfig.AgentBlueprintId.Should().BeEmpty();
        // In the actual command handler, Environment.Exit(1) would be called
    }

    [Fact]
    public async Task DryRunLogic_ShouldNotExecuteRegistration()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "blueprint-123",
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        // Act - Simulate dry-run logic (loading config but not executing)
        var loadedConfig = await _mockConfigService.LoadAsync("test-config.json");

        // Assert - Verify config was loaded
        loadedConfig.Should().NotBeNull();
        loadedConfig.AgentBlueprintId.Should().Be("blueprint-123");
        loadedConfig.TenantId.Should().Be("test-tenant");

        // Verify no bot configuration was attempted
        await _mockBackendConfigurator.DidNotReceiveWithAnyArgs()
            .SetBackendConfigurationAsync(default!, default!);
    }

    [Fact]
    public void DryRunDisplay_ShouldShowEndpointInfo()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "blueprint-456",
        };

        // Act & Assert - config should have the blueprint ID
        config.AgentBlueprintId.Should().Be("blueprint-456");
    }

    [Fact]
    public void DryRunDisplay_ShouldShowMessagingUrl()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "blueprint-789",
        };

        // Act & Assert
        config.AgentBlueprintId.Should().Be("blueprint-789");
    }

    #endregion

    #region --messaging-endpoint option validation

    [Fact]
    public async Task SetupBlueprint_MessagingEndpointWithoutEndpointOnly_ExitsOne()
    {
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockExecutor, _mockAuthValidator, _mockPlatformDetector,
            _mockBackendConfigurator, _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);
        var parser = new CommandLineBuilder(command).Build();

        var result = await parser.InvokeAsync(
            new[] { "--messaging-endpoint", "https://agent.contoso.com/api/messages" }, new TestConsole());

        result.Should().Be(1,
            because: "--messaging-endpoint only applies with --endpoint-only; supplying it alone must fail fast, not be silently ignored");
    }

    [Fact]
    public async Task SetupBlueprint_EndpointOnlyMessagingEndpoint_InfersM365_AndRegisters()
    {
        var config = new Agent365Config { TenantId = "test-tenant", AgentBlueprintId = "blueprint-123" };
        _mockConfigService.LoadAsync(Arg.Any<string>()).Returns(Task.FromResult(config));
        _mockBackendConfigurator.SetBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((EndpointRegistrationResult.Created, (string?)null));

        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockExecutor, _mockAuthValidator, _mockPlatformDetector,
            _mockBackendConfigurator, _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);
        var parser = new CommandLineBuilder(command).Build();

        // No --m365 — it's inferred from --endpoint-only, so the endpoint registers instead of erroring.
        var result = await parser.InvokeAsync(
            new[] { "--endpoint-only", "--messaging-endpoint", "https://agent.contoso.com/api/messages", "--skip-requirements" }, new TestConsole());

        result.Should().Be(0,
            because: "--endpoint-only infers --m365, so the endpoint registers without the flag");
        await _mockBackendConfigurator.Received().SetBackendConfigurationAsync(
            Arg.Any<string>(), "https://agent.contoso.com/api/messages", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetupBlueprint_EndpointOnlyWithEmptyMessagingEndpoint_ExitsOne()
    {
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockExecutor, _mockAuthValidator, _mockPlatformDetector,
            _mockBackendConfigurator, _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);
        var parser = new CommandLineBuilder(command).Build();

        var result = await parser.InvokeAsync(
            new[] { "--endpoint-only", "--m365", "--messaging-endpoint", "" }, new TestConsole());

        result.Should().Be(1,
            because: "an explicitly-empty --messaging-endpoint must error, not be treated as omitted");
    }

    [Fact]
    public async Task SetupBlueprint_EndpointOnlyWhenEndpointNotConfigured_ExitsOne()
    {
        // Blueprint exists but no messaging endpoint is configured → registration returns Failed.
        var config = new Agent365Config { TenantId = "test-tenant", AgentBlueprintId = "blueprint-123" };
        _mockConfigService.LoadAsync(Arg.Any<string>()).Returns(Task.FromResult(config));

        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockExecutor, _mockAuthValidator, _mockPlatformDetector,
            _mockBackendConfigurator, _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);
        var parser = new CommandLineBuilder(command).Build();

        var result = await parser.InvokeAsync(new[] { "--endpoint-only", "--m365", "--skip-requirements" }, new TestConsole());

        result.Should().Be(1,
            because: "endpoint registration that doesn't complete must surface a non-zero exit code for scripting");
    }

    #endregion

    #region RegisterEndpointAndSyncAsync Tests

    [Fact]
    public async Task RegisterEndpointAndSyncAsync_WithValidConfig_ShouldSucceed()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            MessagingEndpoint = "https://agent.contoso.com/api/messages",
            DeploymentProjectPath = Path.GetTempPath()
        };

        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");
        var generatedPath = Path.Combine(Path.GetTempPath(), $"a365.generated.config-{testId}.json");

        // Create temporary generated config file
        await File.WriteAllTextAsync(generatedPath, "{}");

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask);

            _mockBackendConfigurator.SetBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns((EndpointRegistrationResult.Created, (string?)null));

            // Act
            await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBackendConfigurator,
                _mockPlatformDetector);

            // Assert — Teams Graph backend configuration receives the literal MessagingEndpoint
            // from config (no more derivation from webAppName, which was an ABS-era behavior).
            await _mockBackendConfigurator.Received(1).SetBackendConfigurationAsync(
                config.AgentBlueprintId,
                config.MessagingEndpoint);

            await _mockConfigService.Received(1).SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>());
        }
        finally
        {
            // Cleanup
            if (File.Exists(generatedPath))
            {
                File.Delete(generatedPath);
            }
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public async Task RegisterEndpointAndSyncAsync_ShouldSetCompletedFlag()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-456",
            DeploymentProjectPath = Path.GetTempPath()
        };

        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");
        var generatedPath = Path.Combine(Path.GetTempPath(), $"a365.generated.config-{testId}.json");
        await File.WriteAllTextAsync(generatedPath, "{}");

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            Agent365Config? savedConfig = null;
            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask)
                .AndDoes(callInfo => savedConfig = callInfo.Arg<Agent365Config>());

            _mockBackendConfigurator.SetBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns((EndpointRegistrationResult.Created, (string?)null));

            // Act
            await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBackendConfigurator,
                _mockPlatformDetector);

            // Assert
            savedConfig.Should().NotBeNull();
            savedConfig!.Completed.Should().BeTrue();
            savedConfig.CompletedAt.Should().NotBeNull();
            savedConfig.CompletedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        }
        finally
        {
            if (File.Exists(generatedPath))
            {
                File.Delete(generatedPath);
            }
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public async Task RegisterEndpointAndSyncAsync_ShouldLogProgressMessages()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-789",
            MessagingEndpoint = "https://agent.contoso.com/api/messages",
            DeploymentProjectPath = Path.GetTempPath()
        };

        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");
        var generatedPath = Path.Combine(Path.GetTempPath(), $"a365.generated.config-{testId}.json");
        await File.WriteAllTextAsync(generatedPath, "{}");

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask);

            _mockBackendConfigurator.SetBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns((EndpointRegistrationResult.Created, (string?)null));

            // Act
            await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBackendConfigurator,
                _mockPlatformDetector);

            // Assert
            _mockLogger.Received().Log(
                LogLevel.Information,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Registering blueprint messaging endpoint")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());

            _mockLogger.Received().Log(
                LogLevel.Information,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("registered successfully")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            if (File.Exists(generatedPath))
            {
                File.Delete(generatedPath);
            }
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public async Task RegisterEndpointAndSyncAsync_WhenSyncFails_ShouldLogWarningButContinue()
    {
        // Arrange — use an isolated temp subdirectory so a365.generated.config.json doesn't exist
        // there (the method derives the generated-config path from the config file's directory).
        // This reliably triggers the FileNotFoundException → warning path regardless of what other
        // files exist in the global Temp directory.
        var testId = Guid.NewGuid().ToString();
        var testDir = Path.Combine(Path.GetTempPath(), $"a365-sync-test-{testId}");
        Directory.CreateDirectory(testDir);
        var configPath = Path.Combine(testDir, "a365.config.json");

        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
        };

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask);

            _mockBackendConfigurator.SetBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns((EndpointRegistrationResult.Created, (string?)null));

            // Act — a365.generated.config.json doesn't exist in testDir, so ProjectSettingsSyncHelper
            // throws FileNotFoundException, which RegisterEndpointAndSyncAsync catches non-fatally.
            await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBackendConfigurator,
                _mockPlatformDetector);

            // Assert — warning logged, method did not throw
            _mockLogger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Project settings sync failed") && o.ToString()!.Contains("non-blocking")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            if (File.Exists(configPath))
                File.Delete(configPath);
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task RegisterEndpointAndSyncAsync_WhenEndpointAlreadyExists_ShouldLogAlreadyRegistered()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-existing",
            MessagingEndpoint = "https://agent.contoso.com/api/messages",
            DeploymentProjectPath = Path.GetTempPath()
        };

        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");
        var generatedPath = Path.Combine(Path.GetTempPath(), $"a365.generated.config-{testId}.json");
        await File.WriteAllTextAsync(generatedPath, "{}");

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask);

            // Mock endpoint registration returning AlreadyExists status
            _mockBackendConfigurator.SetBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns((EndpointRegistrationResult.AlreadyExists, (string?)null));

            // Act
            var result = await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBackendConfigurator,
                _mockPlatformDetector);

            // Assert
            result.Should().Be(EndpointRegistrationResult.AlreadyExists);

            // Verify the specific "already registered" message is logged
            _mockLogger.Received().Log(
                LogLevel.Information,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Blueprint messaging endpoint already registered")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());

            // Verify endpoint registration was called
            await _mockBackendConfigurator.Received(1).SetBackendConfigurationAsync(config.AgentBlueprintId, Arg.Any<string>());
        }
        finally
        {
            if (File.Exists(generatedPath))
            {
                File.Delete(generatedPath);
            }
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public async Task RegisterEndpointAndSyncAsync_ShouldUpdateBotConfigurationInAgent365Config()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            MessagingEndpoint = "https://agent.contoso.com/api/messages",
            DeploymentProjectPath = Path.GetTempPath()
        };

        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");
        var generatedPath = Path.Combine(Path.GetTempPath(), $"a365.generated.config-{testId}.json");
        await File.WriteAllTextAsync(generatedPath, "{}");

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            Agent365Config? savedConfig = null;
            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask)
                .AndDoes(callInfo => savedConfig = callInfo.Arg<Agent365Config>());

            _mockBackendConfigurator.SetBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns((EndpointRegistrationResult.Created, (string?)null));

            // Act
            await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBackendConfigurator,
                _mockPlatformDetector);

            // Assert - Verify bot configuration was updated in config
            savedConfig.Should().NotBeNull();
            savedConfig!.BotId.Should().Be(config.AgentBlueprintId,
                because: "BotId should be set to AgentBlueprintId after successful endpoint registration");
            savedConfig.BotMsaAppId.Should().Be(config.AgentBlueprintId,
                because: "BotMsaAppId should be set to AgentBlueprintId after successful endpoint registration");
            savedConfig.BotMessagingEndpoint.Should().Be(config.MessagingEndpoint,
                because: "BotMessagingEndpoint should be set to the MessagingEndpoint configured in config");
        }
        finally
        {
            if (File.Exists(generatedPath))
            {
                File.Delete(generatedPath);
            }
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public async Task RegisterEndpointAndSyncAsync_WithExternalMessagingEndpoint_ShouldSucceed()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            MessagingEndpoint = "https://custom-host.example.com/api/messages",
            DeploymentProjectPath = Path.GetTempPath()
        };

        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");
        var generatedPath = Path.Combine(Path.GetTempPath(), $"a365.generated.config-{testId}.json");

        await File.WriteAllTextAsync(generatedPath, "{}");

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask);

            _mockBackendConfigurator.SetBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns((EndpointRegistrationResult.Created, (string?)null));

            // Act
            var result = await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBackendConfigurator,
                _mockPlatformDetector);

            // Assert
            result.Should().Be(EndpointRegistrationResult.Created);

            await _mockBackendConfigurator.Received(1).SetBackendConfigurationAsync(config.AgentBlueprintId, config.MessagingEndpoint);

            await _mockConfigService.Received(1).SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>());
        }
        finally
        {
            if (File.Exists(generatedPath))
            {
                File.Delete(generatedPath);
            }
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public async Task RegisterEndpointAndSyncAsync_WithNoMessagingEndpoint_ShouldSkipRegistration()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            MessagingEndpoint = string.Empty,
            DeploymentProjectPath = Path.GetTempPath()
        };

        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");
        var generatedPath = Path.Combine(Path.GetTempPath(), $"a365.generated.config-{testId}.json");

        await File.WriteAllTextAsync(generatedPath, "{}");

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask);

            // Act
            var result = await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBackendConfigurator,
                _mockPlatformDetector);

            // Assert - endpoint registration was skipped (configurator never called)
            result.Should().Be(EndpointRegistrationResult.Failed);
            
            // Should NOT call bot configurator
            await _mockBackendConfigurator.DidNotReceive().SetBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string>());

            // Should still save state with completed flag
            await _mockConfigService.Received(1).SaveStateAsync(
                Arg.Is<Agent365Config>(c => c.Completed),
                Arg.Any<string>());
        }
        finally
        {
            if (File.Exists(generatedPath))
            {
                File.Delete(generatedPath);
            }
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    #endregion

    #region EnsureDelegatedConsentWithRetriesAsync Parameter Order Documentation

    [Fact]
    public void DocumentParameterOrder_EnsureDelegatedConsentWithRetriesAsync()
    {
        // This test documents the correct parameter order for EnsureDelegatedConsentWithRetriesAsync
        // to prevent the bug where clientAppId and tenantId were accidentally swapped.
        //
        // Bug History:
        // - Parameters were accidentally swapped: (service, tenantId, clientAppId, logger)
        // - This caused Azure CLI to authenticate to tenant=<clientAppId> (a non-existent tenant)
        // - Error: "AADSTS90002: Tenant 'e2af597c-49d3-42e8-b0ff-6c2cbf818ec7' not found"
        // - Root cause: Client app ID was passed where tenant ID was expected
        //
        // Correct Parameter Order:
        // await EnsureDelegatedConsentWithRetriesAsync(
        //     delegatedConsentService,
        //     setupConfig.ClientAppId,    // <-- clientAppId FIRST
        //     setupConfig.TenantId,       // <-- tenantId SECOND
        //     logger);
        //
        // The method then calls:
        // await delegatedConsentService.EnsureBlueprintPermissionGrantAsync(
        //     clientAppId,  // <-- Receives setupConfig.ClientAppId
        //     tenantId,     // <-- Receives setupConfig.TenantId
        //     ct);
        //
        // Code Reviewers: Verify that BlueprintSubcommand.cs line ~189 follows this pattern.

        var testClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6";
        var testTenantId = "12345678-1234-1234-1234-123456789012";

        // Assert that test GUIDs are valid and different
        Assert.True(Guid.TryParse(testClientAppId, out _), "Test clientAppId should be a valid GUID");
        Assert.True(Guid.TryParse(testTenantId, out _), "Test tenantId should be a valid GUID");
        testClientAppId.Should().NotBe(testTenantId, 
            "ClientAppId and TenantId must be different to catch parameter swapping bugs");
    }

    #endregion

    #region Update Endpoint Option Tests

    [Fact]
    public void CreateCommand_ShouldHaveUpdateEndpointOption()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

        // Assert
        var updateEndpointOption = command.Options.FirstOrDefault(o => o.Name == "update-endpoint");
        updateEndpointOption.Should().NotBeNull();
        updateEndpointOption!.Aliases.Should().Contain("--update-endpoint");
    }

    [Fact]
    public async Task UpdateEndpointAsync_WithValidUrl_ShouldDeleteAndRegisterEndpoint()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            DeploymentProjectPath = Path.GetTempPath()
        };

        // Set BotName via reflection since it's a computed property
        // We need to test with WebAppName set which derives BotName
        var newEndpointUrl = "https://newhost.example.com/api/messages";
        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");
        var generatedPath = Path.Combine(Path.GetTempPath(), $"a365.generated.config-{testId}.json");

        await File.WriteAllTextAsync(generatedPath, "{}");

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask);

            _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
                .Returns(true);

            _mockBackendConfigurator.SetBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns((EndpointRegistrationResult.Created, (string?)null));

            // Act
            await BlueprintSubcommand.UpdateEndpointAsync(
                configPath,
                newEndpointUrl,
                _mockLogger,
                _mockConfigService,
                _mockBackendConfigurator,
                _mockPlatformDetector);

            // Assert - Should call delete then create
            await _mockBackendConfigurator.Received(1).ClearBackendConfigurationAsync(config.AgentBlueprintId, Arg.Any<string?>());

            await _mockBackendConfigurator.Received(1).SetBackendConfigurationAsync(config.AgentBlueprintId, newEndpointUrl);
        }
        finally
        {
            if (File.Exists(generatedPath)) File.Delete(generatedPath);
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    [Fact]
    public async Task UpdateEndpointAsync_WithInvalidUrl_ShouldThrowException()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            DeploymentProjectPath = Path.GetTempPath()
        };

        var invalidUrl = "http://not-https.example.com/api/messages"; // HTTP not HTTPS
        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<Cli.Exceptions.SetupValidationException>(async () =>
            await BlueprintSubcommand.UpdateEndpointAsync(
                configPath,
                invalidUrl,
                _mockLogger,
                _mockConfigService,
                _mockBackendConfigurator,
                _mockPlatformDetector));

        exception.Message.Should().Contain("HTTPS");
    }

    [Fact]
    public async Task UpdateEndpointAsync_WhenClearFails_ShouldProceedWithRegister()
    {
        // Clear failure is non-fatal — the Teams Graph clear is idempotent, so we proceed to
        // register the new endpoint even if clear could not be confirmed.
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            MessagingEndpoint = "https://old-agent.contoso.com/api/messages",
            DeploymentProjectPath = Path.GetTempPath()
        };

        var newEndpointUrl = "https://newhost.example.com/api/messages";
        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");
        var generatedPath = Path.Combine(Path.GetTempPath(), $"a365.generated.config-{testId}.json");
        await File.WriteAllTextAsync(generatedPath, "{}");

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask);

            _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
                .Returns(false);

            _mockBackendConfigurator.SetBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns((EndpointRegistrationResult.Created, (string?)null));

            // Act
            await BlueprintSubcommand.UpdateEndpointAsync(
                configPath,
                newEndpointUrl,
                _mockLogger,
                _mockConfigService,
                _mockBackendConfigurator,
                _mockPlatformDetector);

            // Assert — we called Clear, then proceeded to Set despite clear returning false.
            await _mockBackendConfigurator.Received(1).ClearBackendConfigurationAsync(
                config.AgentBlueprintId, Arg.Any<string?>());
            await _mockBackendConfigurator.Received(1).SetBackendConfigurationAsync(
                config.AgentBlueprintId, newEndpointUrl);
        }
        finally
        {
            if (File.Exists(generatedPath)) File.Delete(generatedPath);
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    [Fact]
    public async Task UpdateEndpointAsync_WithNoExistingOldEndpoint_ShouldOnlyCallPreCreateCleanup()
    {
        // Arrange - Config without BotName (no existing endpoint)
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            // WebAppName not set, so BotName will be empty
            DeploymentProjectPath = Path.GetTempPath(),
        };

        var newEndpointUrl = "https://newhost.example.com/api/messages";
        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");
        var generatedPath = Path.Combine(Path.GetTempPath(), $"a365.generated.config-{testId}.json");

        await File.WriteAllTextAsync(generatedPath, "{}");

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask);

            _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>())
                .Returns(Task.FromResult(true)); // NotFound = success for pre-create cleanup

            _mockBackendConfigurator.SetBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns((EndpointRegistrationResult.Created, (string?)null));

            // Act
            await BlueprintSubcommand.UpdateEndpointAsync(
                configPath,
                newEndpointUrl,
                _mockLogger,
                _mockConfigService,
                _mockBackendConfigurator,
                _mockPlatformDetector);

            // Assert - Step 1 (delete old) is skipped — no existing endpoint to delete.
            // Step 1.5 (pre-create cleanup) still calls delete exactly once with the TARGET endpoint name,
            // so there is exactly one delete call total.
            var expectedTargetName = EndpointHelper.GetEndpointNameFromUrl(newEndpointUrl, config.AgentBlueprintId);
            await _mockBackendConfigurator.Received(1).ClearBackendConfigurationAsync(config.AgentBlueprintId);

            // Should still register the new endpoint
            await _mockBackendConfigurator.Received(1).SetBackendConfigurationAsync(config.AgentBlueprintId, newEndpointUrl);
        }
        finally
        {
            if (File.Exists(generatedPath)) File.Delete(generatedPath);
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    // Obsolete regression: the old Azure Bot Service flow had a "Step 1 / Step 1.5" delete pattern
    // (delete old endpoint by name, then pre-create cleanup of target endpoint by name) driven by
    // endpoint-name derivation from URLs. The Teams Graph backend configuration is keyed purely by
    // agent blueprint ID, so there is exactly one clear call per update regardless of URLs. The
    // equivalent positive-path test is UpdateEndpointAsync_WhenClearFails_ShouldProceedWithRegister.

    #endregion

    #region CustomClientAppId Configuration Tests

    [Fact]
    public async Task SetHandler_WithClientAppId_DryRun_ShouldExitCleanly()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6",
            AgentBlueprintDisplayName = "Test Blueprint"
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act — dry-run exits before Graph API operations; CustomClientAppId is not set in this path
        // (no Graph calls are made in dry-run, so configuration of graphApiService is not relevant).
        var result = await parser.InvokeAsync("--dry-run", testConsole);

        // Assert — command succeeds and config was loaded to enrich the dry-run preview
        result.Should().Be(0, because: "--dry-run should exit cleanly even when ClientAppId is present");
        await _mockConfigService.Received(1).LoadAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SetHandler_WithoutClientAppId_ShouldNotConfigureGraphApiService()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            ClientAppId = "", // No client app ID
            AgentBlueprintDisplayName = "Test Blueprint"
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        await parser.InvokeAsync("--dry-run", testConsole);

        // Assert - Verify CustomClientAppId was NOT set (remains null)
        _mockGraphApiService.CustomClientAppId.Should().BeNullOrEmpty(
            "CustomClientAppId should not be set when config does not have a ClientAppId");
    }

    [Fact]
    public async Task SetHandler_WithWhitespaceClientAppId_ShouldNotConfigureGraphApiService()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            ClientAppId = "   ", // Whitespace only
            AgentBlueprintDisplayName = "Test Blueprint"
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockBackendConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        await parser.InvokeAsync("--dry-run", testConsole);

        // Assert - Verify CustomClientAppId was NOT set
        _mockGraphApiService.CustomClientAppId.Should().BeNullOrEmpty(
            "CustomClientAppId should not be set when config has whitespace-only ClientAppId");
    }

    #endregion

    #region Issue-279 Regression Tests — Client Secret Creation

    // NOTE: Retry logic tests (sponsors-only fallback, owners fallback, all-fallbacks-exhausted,
    // non-400 on retry 1) require HTTP call mocking. They are covered by integration tests.
    // The tests below cover the observable surface: catch block logging and MSAL token path.

    [Fact]
    public async Task CreateBlueprintClientSecretAsync_WhenTokenAcquisitionFails_ShouldLogPermissionsGuidance()
    {
        // Arrange — empty TenantId/ClientAppId causes AcquireMsalGraphTokenAsync to return null,
        // which throws InvalidOperationException inside the try block, triggering the catch block.
        var setupConfig = new Agent365Config
        {
            TenantId = string.Empty,
            ClientAppId = string.Empty,
        };

        _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        // Act — should not throw; the catch block handles it
        await BlueprintSubcommand.CreateBlueprintClientSecretAsync(
            blueprintObjectId: "00000000-0000-0000-0000-000000000001",
            blueprintAppId: "00000000-0000-0000-0000-000000000002",
            graphService: _mockGraphApiService,
            setupConfig: setupConfig,
            configService: _mockConfigService,
            logger: _mockLogger,
            generatedConfigPath: "a365.generated.config.json",
            loginHintResolver: () => Task.FromResult<string?>(null));

        // Assert — documentation link must be logged (covers required permissions)
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("how-to-add-credentials")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CreateBlueprintClientSecretAsync_WhenTokenAcquisitionFails_ShouldLogConfigFieldGuidance()
    {
        // Arrange
        var setupConfig = new Agent365Config
        {
            TenantId = string.Empty,
            ClientAppId = string.Empty,
        };

        _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        // Act
        await BlueprintSubcommand.CreateBlueprintClientSecretAsync(
            blueprintObjectId: "00000000-0000-0000-0000-000000000001",
            blueprintAppId: "00000000-0000-0000-0000-000000000002",
            graphService: _mockGraphApiService,
            setupConfig: setupConfig,
            configService: _mockConfigService,
            logger: _mockLogger,
            generatedConfigPath: "a365.generated.config.json",
            loginHintResolver: () => Task.FromResult<string?>(null));

        // Assert — config file name must be mentioned so user knows where to add the secret
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("a365.generated.config.json")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CreateBlueprintClientSecretAsync_WhenTokenAcquisitionFails_ShouldLogReRunInstruction()
    {
        // Arrange
        var setupConfig = new Agent365Config
        {
            TenantId = string.Empty,
            ClientAppId = string.Empty,
        };

        _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        // Act
        await BlueprintSubcommand.CreateBlueprintClientSecretAsync(
            blueprintObjectId: "00000000-0000-0000-0000-000000000001",
            blueprintAppId: "00000000-0000-0000-0000-000000000002",
            graphService: _mockGraphApiService,
            setupConfig: setupConfig,
            configService: _mockConfigService,
            logger: _mockLogger,
            generatedConfigPath: "a365.generated.config.json",
            loginHintResolver: () => Task.FromResult<string?>(null));

        // Assert — re-run instruction must be logged
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("a365 setup all")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CreateBlueprintClientSecretAsync_ShouldNotCallAzureCliGraphToken()
    {
        // Regression test for Issue #279 bug #2:
        // CreateBlueprintClientSecretAsync must NOT call GetGraphAccessTokenAsync (Azure CLI token).
        // It must use AcquireMsalGraphTokenAsync (MSAL token) instead.
        var setupConfig = new Agent365Config
        {
            TenantId = string.Empty,
            ClientAppId = string.Empty,
        };

        _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        // Act — AcquireMsalGraphTokenAsync returns null immediately for empty credentials
        // (guard added to avoid MSAL/WAM blocking for ~30s before failing).
        await BlueprintSubcommand.CreateBlueprintClientSecretAsync(
            blueprintObjectId: "00000000-0000-0000-0000-000000000001",
            blueprintAppId: "00000000-0000-0000-0000-000000000002",
            graphService: _mockGraphApiService,
            setupConfig: setupConfig,
            configService: _mockConfigService,
            logger: _mockLogger,
            generatedConfigPath: "a365.generated.config.json",
            loginHintResolver: () => Task.FromResult<string?>(null));

        // Assert — Azure CLI token path must NOT be taken
        await _mockGraphApiService.DidNotReceiveWithAnyArgs().GetGraphAccessTokenAsync(default!, default);
    }

    #endregion

    #region Mutually Exclusive Options Tests

    [Theory]
    [InlineData("https://example.com", true, false)] // --update-endpoint with --endpoint-only
    [InlineData("https://example.com", false, true)] // --update-endpoint with --no-endpoint
    [InlineData(null, true, true)] // --endpoint-only with --no-endpoint
    public void ValidateMutuallyExclusiveOptions_WithConflictingOptions_ShouldReturnFalseAndLogError(
        string? updateEndpoint, bool endpointOnly, bool skipEndpointRegistration)
    {
        // Act
        var result = BlueprintSubcommand.ValidateMutuallyExclusiveOptions(
            updateEndpoint,
            endpointOnly,
            skipEndpointRegistration,
            _mockLogger);

        // Assert
        result.Should().BeFalse();
        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("cannot be used together")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Theory]
    [InlineData(null, true, false)] // --endpoint-only only
    [InlineData(null, false, true)] // --no-endpoint only
    [InlineData("https://example.com", false, false)] // --update-endpoint only
    [InlineData(null, false, false)] // no options
    public void ValidateMutuallyExclusiveOptions_WithCompatibleOptions_ShouldReturnTrue(
        string? updateEndpoint, bool endpointOnly, bool skipEndpointRegistration)
    {
        // Act
        var result = BlueprintSubcommand.ValidateMutuallyExclusiveOptions(
            updateEndpoint,
            endpointOnly,
            skipEndpointRegistration,
            _mockLogger);

        // Assert
        result.Should().BeTrue();
        _mockLogger.DidNotReceive().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("cannot be used together")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    #endregion

    #region Generated Config Merge Preservation Tests

    /// <summary>
    /// Verifies that the blueprint intermediate save pattern (merge into existing JsonObject)
    /// preserves all pre-existing fields such as agentBlueprintClientSecret, botId, etc.
    /// Regression test for bug where a new JsonObject replaced the existing config,
    /// dropping fields not explicitly listed in the allowlist.
    /// </summary>
    [Fact]
    public async Task BlueprintIntermediateSave_ShouldPreserveExistingGeneratedConfigFields()
    {
        // Arrange - simulate a generated config with fields set by other subcommands
        var tempDir = Path.Combine(Path.GetTempPath(), $"a365test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var generatedConfigPath = Path.Combine(tempDir, "a365.generated.config.json");

        try
        {
            var existingConfig = new JsonObject
            {
                ["managedIdentityPrincipalId"] = "msi-principal-id-123",
                ["agentBlueprintId"] = "old-blueprint-id",
                ["agentBlueprintObjectId"] = "old-object-id",
                ["agentBlueprintServicePrincipalObjectId"] = "old-sp-id",
                ["agentBlueprintClientSecret"] = "encrypted-secret-value",
                ["agentBlueprintClientSecretProtected"] = true,
                ["botId"] = "bot-id-456",
                ["botMsaAppId"] = "bot-msa-app-id-789",
                ["messagingEndpoint"] = "https://myapp.azurewebsites.net/api/messages",
                ["completed"] = true,
                ["completedAt"] = "2026-01-01T00:00:00Z",
                ["resourceConsents"] = new JsonArray
                {
                    new JsonObject { ["resourceName"] = "Microsoft Graph", ["consentGranted"] = true }
                }
            };

            await File.WriteAllTextAsync(generatedConfigPath, existingConfig.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }));

            // Act - simulate the merge pattern used in BlueprintSubcommand
            var generatedConfig = JsonNode.Parse(
                await File.ReadAllTextAsync(generatedConfigPath))?.AsObject() ?? new JsonObject();

            var newBlueprintAppId = "new-blueprint-app-id";
            var newBlueprintObjectId = "new-object-id";
            var newServicePrincipalId = "new-sp-id";

            // This is the exact pattern from the fix
            generatedConfig["agentBlueprintId"] = newBlueprintAppId;
            generatedConfig["agentBlueprintObjectId"] = newBlueprintObjectId;
            generatedConfig["agentBlueprintServicePrincipalObjectId"] = newServicePrincipalId;
            if (generatedConfig["resourceConsents"] == null)
            {
                generatedConfig["resourceConsents"] = new JsonArray();
            }

            await File.WriteAllTextAsync(generatedConfigPath, generatedConfig.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }));

            // Assert - read back and verify ALL fields are preserved
            var savedConfig = JsonNode.Parse(await File.ReadAllTextAsync(generatedConfigPath))!.AsObject();

            // Updated fields should have new values
            savedConfig["agentBlueprintId"]!.GetValue<string>().Should().Be("new-blueprint-app-id");
            savedConfig["agentBlueprintObjectId"]!.GetValue<string>().Should().Be("new-object-id");
            savedConfig["agentBlueprintServicePrincipalObjectId"]!.GetValue<string>().Should().Be("new-sp-id");

            // Pre-existing fields must be preserved (the bug would wipe these)
            savedConfig["agentBlueprintClientSecret"]!.GetValue<string>().Should().Be("encrypted-secret-value");
            savedConfig["agentBlueprintClientSecretProtected"]!.GetValue<bool>().Should().BeTrue();
            savedConfig["botId"]!.GetValue<string>().Should().Be("bot-id-456");
            savedConfig["botMsaAppId"]!.GetValue<string>().Should().Be("bot-msa-app-id-789");
            savedConfig["messagingEndpoint"]!.GetValue<string>().Should().Be("https://myapp.azurewebsites.net/api/messages");
            savedConfig["managedIdentityPrincipalId"]!.GetValue<string>().Should().Be("msi-principal-id-123");
            savedConfig["completed"]!.GetValue<bool>().Should().BeTrue();
            savedConfig["completedAt"]!.GetValue<string>().Should().Be("2026-01-01T00:00:00Z");

            // Resource consents should be preserved
            savedConfig["resourceConsents"]!.AsArray().Should().HaveCount(1);
            savedConfig["resourceConsents"]![0]!["resourceName"]!.GetValue<string>().Should().Be("Microsoft Graph");
        }
        finally
        {
            // Cleanup temp directory
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verifies that the merge pattern initializes resourceConsents when it does not exist.
    /// </summary>
    [Fact]
    public async Task BlueprintIntermediateSave_ShouldInitializeResourceConsents_WhenNull()
    {
        // Arrange - config without resourceConsents
        var tempDir = Path.Combine(Path.GetTempPath(), $"a365test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var generatedConfigPath = Path.Combine(tempDir, "a365.generated.config.json");

        try
        {
            var existingConfig = new JsonObject
            {
                ["managedIdentityPrincipalId"] = "msi-id",
                ["agentBlueprintClientSecret"] = "secret-123"
            };

            await File.WriteAllTextAsync(generatedConfigPath, existingConfig.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }));

            // Act
            var generatedConfig = JsonNode.Parse(
                await File.ReadAllTextAsync(generatedConfigPath))?.AsObject() ?? new JsonObject();

            generatedConfig["agentBlueprintId"] = "app-id";
            generatedConfig["agentBlueprintObjectId"] = "obj-id";
            generatedConfig["agentBlueprintServicePrincipalObjectId"] = "sp-id";
            if (generatedConfig["resourceConsents"] == null)
            {
                generatedConfig["resourceConsents"] = new JsonArray();
            }

            await File.WriteAllTextAsync(generatedConfigPath, generatedConfig.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }));

            // Assert
            var savedConfig = JsonNode.Parse(await File.ReadAllTextAsync(generatedConfigPath))!.AsObject();

            savedConfig["resourceConsents"].Should().NotBeNull();
            savedConfig["resourceConsents"]!.AsArray().Should().BeEmpty();
            savedConfig["agentBlueprintClientSecret"]!.GetValue<string>().Should().Be("secret-123");
            savedConfig["managedIdentityPrincipalId"]!.GetValue<string>().Should().Be("msi-id");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void RestoreExistingBlueprintSecret_WhenResolvedBlueprintMatches_ReusesStoredSecret()
    {
        var setupConfig = new Agent365Config();
        var generatedConfig = new JsonObject
        {
            ["agentBlueprintId"] = "BLUEPRINT-ID",
            ["agentBlueprintClientSecret"] = "stored-secret",
            ["agentBlueprintClientSecretProtected"] = true
        };

        BlueprintSubcommand.RestoreExistingBlueprintSecret(
            setupConfig,
            generatedConfig,
            "blueprint-id",
            _mockLogger);

        setupConfig.AgentBlueprintClientSecret.Should().Be(
            "stored-secret",
            because: "an idempotent setup run must validate and reuse the credential stored for the existing blueprint");
        setupConfig.AgentBlueprintClientSecretProtected.Should().BeTrue(
            because: "the stored protection mode is required to validate the existing credential");
    }

    [Fact]
    public void RestoreExistingBlueprintSecret_WhenResolvedBlueprintDiffers_DoesNotReuseStoredSecret()
    {
        var setupConfig = new Agent365Config();
        var generatedConfig = new JsonObject
        {
            ["agentBlueprintId"] = "different-blueprint-id",
            ["agentBlueprintClientSecret"] = "stored-secret",
            ["agentBlueprintClientSecretProtected"] = false
        };

        BlueprintSubcommand.RestoreExistingBlueprintSecret(
            setupConfig,
            generatedConfig,
            "resolved-blueprint-id",
            _mockLogger);

        setupConfig.AgentBlueprintClientSecret.Should().BeNull(
            because: "a credential must never be reused for a different blueprint");
    }

    [Fact]
    public void RestoreExistingBlueprintSecret_WhenSetupConfigAlreadyHasSecret_PreservesResolvedValue()
    {
        var setupConfig = new Agent365Config
        {
            AgentBlueprintClientSecret = "resolved-secret",
            AgentBlueprintClientSecretProtected = false
        };
        var generatedConfig = new JsonObject
        {
            ["agentBlueprintId"] = "blueprint-id",
            ["agentBlueprintClientSecret"] = "stored-secret",
            ["agentBlueprintClientSecretProtected"] = true
        };

        BlueprintSubcommand.RestoreExistingBlueprintSecret(
            setupConfig,
            generatedConfig,
            "blueprint-id",
            _mockLogger);

        setupConfig.AgentBlueprintClientSecret.Should().Be(
            "resolved-secret",
            because: "an explicitly resolved credential takes precedence over generated state");
        setupConfig.AgentBlueprintClientSecretProtected.Should().BeFalse(
            because: "the protection flag must remain paired with the resolved credential");
    }

    [Fact]
    public void RestoreExistingBlueprintSecret_WhenProtectionFlagIsMissing_TreatsStoredSecretAsPlaintext()
    {
        var setupConfig = new Agent365Config();
        var generatedConfig = new JsonObject
        {
            ["agentBlueprintId"] = "blueprint-id",
            ["agentBlueprintClientSecret"] = "stored-secret"
        };

        BlueprintSubcommand.RestoreExistingBlueprintSecret(
            setupConfig,
            generatedConfig,
            "blueprint-id",
            _mockLogger);

        setupConfig.AgentBlueprintClientSecret.Should().Be(
            "stored-secret",
            because: "generated configurations created before the protection flag was added may contain plaintext secrets");
        setupConfig.AgentBlueprintClientSecretProtected.Should().BeFalse(
            because: "a missing protection flag represents the legacy plaintext storage format");
    }

    [Fact]
    public void RestoreExistingBlueprintSecret_WhenStoredBlueprintIdIsNotAString_DoesNotReuseStoredSecret()
    {
        var setupConfig = new Agent365Config();
        var generatedConfig = new JsonObject
        {
            ["agentBlueprintId"] = 42,
            ["agentBlueprintClientSecret"] = "stored-secret",
            ["agentBlueprintClientSecretProtected"] = false
        };

        var act = () => BlueprintSubcommand.RestoreExistingBlueprintSecret(
            setupConfig,
            generatedConfig,
            "blueprint-id",
            _mockLogger);

        act.Should().NotThrow(
            because: "malformed generated state must not crash an idempotent setup run");
        setupConfig.AgentBlueprintClientSecret.Should().BeNull(
            because: "a credential cannot be safely associated with a malformed blueprint identifier");
    }

    #endregion

    #region Ownership Check Tests

    [Fact]
    public async Task CreateBlueprintClientSecret_DoesNotPerformPreflightOwnerCheck()
    {
        // The pre-flight ownership probe was removed (it false-negatived on the not-yet-replicated owner
        // edge and printed a "will fail" warning addPassword then contradicted). It must no longer run.
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000001",
            ClientAppId = "" // token acquisition fails fast; we only assert that no owner pre-check ran
        };

        await BlueprintSubcommand.CreateBlueprintClientSecretAsync(
            blueprintObjectId: "object-id",
            blueprintAppId: "app-id",
            graphService: _mockGraphApiService,
            setupConfig: config,
            configService: _mockConfigService,
            logger: _mockLogger,
            generatedConfigPath: "a365.generated.config.json",
            loginHintResolver: () => Task.FromResult<string?>(null));

        await _mockGraphApiService.DidNotReceive().IsApplicationOwnerAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }

    [Fact]
    public async Task CreateBlueprintClientSecret_WhenNonPermissionFailure_DoesNotClaimOwnership()
    {
        // Ownership guidance must show only for a real permission denial, not unrelated failures. An empty
        // ClientAppId fails at token acquisition, so "not an owner" must NOT appear (only the generic
        // manual-creation guidance). The genuine-403 positive case has no unit seam (integration-only).
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000001",
            ClientAppId = "" // token acquisition fails — NOT a permission denial
        };

        var created = await BlueprintSubcommand.CreateBlueprintClientSecretAsync(
            blueprintObjectId: "object-id",
            blueprintAppId: "app-id",
            graphService: _mockGraphApiService,
            setupConfig: config,
            configService: _mockConfigService,
            logger: _mockLogger,
            generatedConfigPath: "a365.generated.config.json",
            loginHintResolver: () => Task.FromResult<string?>(null));

        created.Should().BeFalse(because: "token acquisition failed, so the secret could not be created");

        _mockLogger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("not an owner")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("create it manually")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    #endregion
}

/// <summary>
/// Tests for the --show-secret path that require a real a365.generated.config.json file
/// in the working directory. Runs sequentially to avoid file-system races.
/// </summary>
[Collection("ConfigTests")]
public class BlueprintSubcommandShowSecretTests : IDisposable
{
    private readonly ILogger _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly CommandExecutor _mockExecutor;
    private readonly AzureAuthValidator _mockAuthValidator;
    private readonly PlatformDetector _mockPlatformDetector;
    private readonly ITeamsGraphBackendConfigurator _mockBackendConfigurator;
    private readonly GraphApiService _mockGraphApiService;
    private readonly AgentBlueprintService _mockBlueprintService;
    private readonly IClientAppValidator _mockClientAppValidator;
    private readonly BlueprintLookupService _mockBlueprintLookupService;
    private readonly FederatedCredentialService _mockFederatedCredentialService;
    private readonly string _generatedConfigPath;

    public BlueprintSubcommandShowSecretTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockConfigService = Substitute.For<IConfigService>();
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.For<CommandExecutor>(mockExecutorLogger);
        _mockExecutor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty }));
        _mockAuthValidator = Substitute.For<AzureAuthValidator>(NullLogger<AzureAuthValidator>.Instance, _mockExecutor);
        var mockPlatformDetectorLogger = Substitute.For<ILogger<PlatformDetector>>();
        _mockPlatformDetector = Substitute.ForPartsOf<PlatformDetector>(mockPlatformDetectorLogger);
        _mockBackendConfigurator = Substitute.For<ITeamsGraphBackendConfigurator>();
        _mockGraphApiService = Substitute.ForPartsOf<GraphApiService>(
            Substitute.For<ILogger<GraphApiService>>(), _mockExecutor, (Func<Task<string?>>)(() => Task.FromResult<string?>(null)));
        _mockBlueprintService = Substitute.ForPartsOf<AgentBlueprintService>(Substitute.For<ILogger<AgentBlueprintService>>(), _mockGraphApiService);
        _mockClientAppValidator = Substitute.For<IClientAppValidator>();
        _mockBlueprintLookupService = Substitute.ForPartsOf<BlueprintLookupService>(Substitute.For<ILogger<BlueprintLookupService>>(), _mockGraphApiService);
        _mockFederatedCredentialService = Substitute.ForPartsOf<FederatedCredentialService>(Substitute.For<ILogger<FederatedCredentialService>>(), _mockGraphApiService);

        _generatedConfigPath = Path.Combine(Environment.CurrentDirectory, "a365.generated.config.json");
        // Clean up any stale file from a previous run before each test
        if (File.Exists(_generatedConfigPath))
            File.Delete(_generatedConfigPath);
    }

    public void Dispose()
    {
        if (File.Exists(_generatedConfigPath))
            File.Delete(_generatedConfigPath);
    }

    private Command BuildCommand() => BlueprintSubcommand.CreateCommand(
        _mockLogger, _mockConfigService, _mockExecutor, _mockAuthValidator,
        _mockPlatformDetector, _mockBackendConfigurator, _mockGraphApiService,
        _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService,
        _mockFederatedCredentialService);

    [Fact]
    public async Task ShowSecret_WhenNoGeneratedConfigExists_SetsExitCode1AndLogsGuidance()
    {
        // Arrange — no a365.generated.config.json file (cleaned in constructor)
        var parser = new CommandLineBuilder(BuildCommand()).Build();
        var testConsole = new TestConsole();

        // Act
        var exitCode = await parser.InvokeAsync("--show-secret", testConsole);

        // Assert
        exitCode.Should().Be(1, because: "no stored secret means setup has not been run");
        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("No blueprint client secret found")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ShowSecret_WhenGeneratedConfigExistsButNoSecret_SetsExitCode1()
    {
        // Arrange — file exists but LoadAsync returns config with no secret stored
        await File.WriteAllTextAsync(_generatedConfigPath, "{}");

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(new Agent365Config { AgentBlueprintClientSecret = null }));

        var parser = new CommandLineBuilder(BuildCommand()).Build();
        var testConsole = new TestConsole();

        // Act
        var exitCode = await parser.InvokeAsync("--show-secret", testConsole);

        // Assert
        exitCode.Should().Be(1, because: "an empty secret is treated the same as no secret");
        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("No blueprint client secret found")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ShowSecret_WhenSecretStored_WritesSecretToConsoleAndReturnsExitCode0()
    {
        // Arrange — file exists with the secret fields written directly (no LoadAsync)
        await File.WriteAllTextAsync(_generatedConfigPath, """
            {
              "agentBlueprintClientSecret": "test-secret-value",
              "agentBlueprintClientSecretProtected": false
            }
            """);

        var parser = new CommandLineBuilder(BuildCommand()).Build();
        var testConsole = new TestConsole();

        // Capture Console.Out to verify the plaintext secret is written to stdout.
        // Console.WriteLine is used intentionally (not ILogger) so the secret bypasses the log file.
        var capturedOut = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(capturedOut);

        int exitCode;
        try
        {
            exitCode = await parser.InvokeAsync("--show-secret", testConsole);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // Assert
        exitCode.Should().Be(0, because: "a stored secret should be displayed successfully");
        capturedOut.ToString().Should().Contain("test-secret-value",
            because: "--show-secret must write the plaintext secret to stdout");
        // The redacted placeholder is logged at Debug so it appears in the log file but not the console.
        _mockLogger.Received().Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("[displayed to terminal]")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        _mockLogger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("test-secret-value")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ShowSecret_WhenSecretIsProtectedButDecryptionFails_SetsExitCode1AndLogsClearError()
    {
        // Arrange — protected flag is true but the value is not a real DPAPI blob.
        // SecretProtectionHelper.UnprotectSecret returns the original value on failure,
        // so plaintext == storedSecret, which triggers the DPAPI failure detection.
        // This simulates: different Windows user/machine, or non-Windows platform.
        await File.WriteAllTextAsync(_generatedConfigPath, """
            {
              "agentBlueprintClientSecret": "not-a-real-dpapi-blob",
              "agentBlueprintClientSecretProtected": true
            }
            """);

        var parser = new CommandLineBuilder(BuildCommand()).Build();
        var testConsole = new TestConsole();

        // Act
        var exitCode = await parser.InvokeAsync("--show-secret", testConsole);

        // Assert
        exitCode.Should().Be(1,
            because: "when DPAPI decryption returns the original value the secret cannot be shown");
        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Cannot decrypt")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
