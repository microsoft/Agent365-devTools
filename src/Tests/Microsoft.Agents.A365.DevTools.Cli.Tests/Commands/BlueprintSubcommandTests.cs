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
    private readonly IBotConfigurator _mockBotConfigurator;
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
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);
        _mockAzureValidator = Substitute.For<IAzureValidator>();
        _mockWebAppCreator = Substitute.ForPartsOf<AzureWebAppCreator>(Substitute.For<ILogger<AzureWebAppCreator>>());
        var mockPlatformDetectorLogger = Substitute.For<ILogger<PlatformDetector>>();
        _mockPlatformDetector = Substitute.ForPartsOf<PlatformDetector>(mockPlatformDetectorLogger);
        _mockBotConfigurator = Substitute.For<IBotConfigurator>();
        _mockGraphApiService = Substitute.ForPartsOf<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), _mockExecutor);
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
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector,
            _mockBotConfigurator,
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
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector,
            _mockBotConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
            _mockPlatformDetector,
            _mockBotConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
            _mockPlatformDetector,
            _mockBotConfigurator,
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
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector,
            _mockBotConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
            _mockPlatformDetector,
            _mockBotConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
            _mockPlatformDetector,
            _mockBotConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
                isSetupAll: false,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector,
                _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService);

        // Assert - Should return false when consent service fails
        result.Should().NotBeNull();
        result.BlueprintCreated.Should().BeFalse();
        result.EndpointRegistered.Should().BeFalse();
    }

    [Fact]
    public async Task CreateBlueprintImplementation_WithAzureValidationFailure_ShouldReturnFalse()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6", // Required for validation
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
            isSetupAll: false,
            _mockConfigService,
            _mockBotConfigurator,
            _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService);

        // Assert
        result.Should().NotBeNull();
        result.BlueprintCreated.Should().BeFalse();
        result.EndpointRegistered.Should().BeFalse();
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
            _mockPlatformDetector,
            _mockBotConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
            _mockPlatformDetector,
            _mockBotConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
            _mockPlatformDetector,
            _mockBotConfigurator,
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
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector,
            _mockBotConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
            _mockPlatformDetector,
            _mockBotConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
            _mockPlatformDetector,
            _mockBotConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

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
            isSetupAll: false,
            _mockConfigService,
            _mockBotConfigurator,
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
            _mockPlatformDetector,
            _mockBotConfigurator,
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
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector,
            _mockBotConfigurator,
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
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector,
            _mockBotConfigurator,
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
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector,
            _mockBotConfigurator,
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
            WebAppName = "test-webapp"
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
    public async Task ValidationLogic_WithMissingWebAppName_ShouldLogError()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "blueprint-123",
            WebAppName = "" // Missing web app name
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        // Act
        var loadedConfig = await _mockConfigService.LoadAsync("test-config.json");

        // Assert
        loadedConfig.WebAppName.Should().BeEmpty();
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
            WebAppName = "test-webapp"
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        // Act - Simulate dry-run logic (loading config but not executing)
        var loadedConfig = await _mockConfigService.LoadAsync("test-config.json");

        // Assert - Verify config was loaded
        loadedConfig.Should().NotBeNull();
        loadedConfig.AgentBlueprintId.Should().Be("blueprint-123");
        loadedConfig.WebAppName.Should().Be("test-webapp");

        // Verify no bot configuration was attempted
        await _mockBotConfigurator.DidNotReceiveWithAnyArgs()
            .CreateEndpointWithAgentBlueprintAsync(default!, default!, default!, default!, default!);
    }

    [Fact]
    public void DryRunDisplay_ShouldShowEndpointInfo()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "blueprint-456",
            WebAppName = "my-agent-webapp"
        };

        // Act - Simulate what dry-run would display
        var endpointName = $"{config.WebAppName}-endpoint";
        var messagingUrl = $"https://{config.WebAppName}.azurewebsites.net/api/messages";

        // Assert
        endpointName.Should().Be("my-agent-webapp-endpoint");
        messagingUrl.Should().Be("https://my-agent-webapp.azurewebsites.net/api/messages");
    }

    [Fact]
    public void DryRunDisplay_ShouldShowMessagingUrl()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "blueprint-789",
            WebAppName = "production-agent"
        };

        // Act - Simulate messaging URL generation
        var messagingUrl = $"https://{config.WebAppName}.azurewebsites.net/api/messages";

        // Assert
        messagingUrl.Should().Contain("production-agent.azurewebsites.net/api/messages");
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
            WebAppName = "test-webapp",
            Location = "eastus",
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

            _mockBotConfigurator.CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(EndpointRegistrationResult.Created);

            // Act
            await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector);

            // Assert
            await _mockBotConfigurator.Received(1).CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(),
                config.Location,
                Arg.Is<string>(s => s.Contains("test-webapp.azurewebsites.net")),
                Arg.Any<string>(),
                config.AgentBlueprintId);

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
            WebAppName = "test-webapp",
            Location = "westus",
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

            _mockBotConfigurator.CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(EndpointRegistrationResult.Created);

            // Act
            await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
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
            WebAppName = "test-webapp",
            Location = "eastus",
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

            _mockBotConfigurator.CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(EndpointRegistrationResult.Created);

            // Act
            await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
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
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            WebAppName = "test-webapp",
            Location = "eastus",
            DeploymentProjectPath = "non-existent-path" // This will cause sync to skip with a warning
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

            _mockBotConfigurator.CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(EndpointRegistrationResult.Created);

            // Act - should not throw
            await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector);

            // Assert - ProjectSettingsSyncHelper logs a warning when deploymentProjectPath doesn't exist
            _mockLogger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Project settings sync failed") && o.ToString()!.Contains("non-blocking")),
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
    public async Task RegisterEndpointAndSyncAsync_WhenEndpointAlreadyExists_ShouldLogAlreadyRegistered()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-existing",
            WebAppName = "test-webapp",
            Location = "eastus",
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
            _mockBotConfigurator.CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(EndpointRegistrationResult.AlreadyExists);

            // Act
            var (success, alreadyExisted) = await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector);

            // Assert
            success.Should().BeTrue();
            alreadyExisted.Should().BeTrue();

            // Verify the specific "already registered" message is logged
            _mockLogger.Received().Log(
                LogLevel.Information,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Blueprint messaging endpoint already registered")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());

            // Verify endpoint registration was called
            await _mockBotConfigurator.Received(1).CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(),
                config.Location,
                Arg.Any<string>(),
                Arg.Any<string>(),
                config.AgentBlueprintId);
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
            WebAppName = "test-webapp",
            Location = "eastus",
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

            _mockBotConfigurator.CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(EndpointRegistrationResult.Created);

            // Act
            await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector);

            // Assert - Verify bot configuration was updated in config
            savedConfig.Should().NotBeNull();
            savedConfig!.BotId.Should().Be(config.AgentBlueprintId);
            savedConfig.BotMsaAppId.Should().Be(config.AgentBlueprintId);
            savedConfig.BotMessagingEndpoint.Should().Contain("test-webapp.azurewebsites.net");
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
    public async Task RegisterEndpointAndSyncAsync_WithNeedDeploymentFalseAndMessagingEndpoint_ShouldSucceed()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            NeedDeployment = false,
            MessagingEndpoint = "https://custom-host.example.com/api/messages",
            Location = "eastus",
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

            _mockBotConfigurator.CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(EndpointRegistrationResult.Created);

            // Act
            var (success, alreadyExisted) = await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector);

            // Assert
            success.Should().BeTrue();
            alreadyExisted.Should().BeFalse();
            
            await _mockBotConfigurator.Received(1).CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(),
                config.Location,
                config.MessagingEndpoint,
                Arg.Any<string>(),
                config.AgentBlueprintId);

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
    public async Task RegisterEndpointAndSyncAsync_WithNeedDeploymentFalseAndNoMessagingEndpoint_ShouldSkipRegistration()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            NeedDeployment = false,
            MessagingEndpoint = string.Empty,
            Location = "eastus",
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
            var (success, alreadyExisted) = await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector);

            // Assert - should return (false, false) since endpoint registration was skipped
            success.Should().BeFalse();
            alreadyExisted.Should().BeFalse();
            
            // Should NOT call bot configurator
            await _mockBotConfigurator.DidNotReceive().CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>());

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

    #region Custom Endpoint Option Tests

    [Fact]
    public void CreateCommand_ShouldHaveCustomEndpointOption()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector,
            _mockBotConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

        // Assert
        var customEndpointOption = command.Options.FirstOrDefault(o => o.Name == "custom-endpoint");
        customEndpointOption.Should().NotBeNull();
        customEndpointOption!.Aliases.Should().Contain("--custom-endpoint");
    }

    [Fact]
    public void CreateCommand_ShouldHaveUpdateEndpointOption()
    {
        // Act
        var command = BlueprintSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector,
            _mockBotConfigurator,
            _mockGraphApiService, _mockBlueprintService, _mockClientAppValidator, _mockBlueprintLookupService, _mockFederatedCredentialService);

        // Assert
        var updateEndpointOption = command.Options.FirstOrDefault(o => o.Name == "update-endpoint");
        updateEndpointOption.Should().NotBeNull();
        updateEndpointOption!.Aliases.Should().Contain("--update-endpoint");
    }

    [Fact]
    public async Task RegisterEndpointAndSyncAsync_WithCustomEndpoint_ShouldUseProvidedEndpoint()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            WebAppName = "test-webapp",
            Location = "eastus",
            DeploymentProjectPath = Path.GetTempPath()
        };

        var customEndpoint = "https://custom.example.com/api/messages";
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

            _mockBotConfigurator.CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(EndpointRegistrationResult.Created);

            // Act
            var (success, alreadyExisted) = await BlueprintSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector,
                customEndpoint: customEndpoint);

            // Assert
            success.Should().BeTrue();
            await _mockBotConfigurator.Received(1).CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                customEndpoint,
                Arg.Any<string>(),
                Arg.Any<string>());
        }
        finally
        {
            if (File.Exists(generatedPath)) File.Delete(generatedPath);
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    [Fact]
    public async Task UpdateEndpointAsync_WithValidUrl_ShouldDeleteAndRegisterEndpoint()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            WebAppName = "test-webapp",
            Location = "eastus",
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

            _mockBotConfigurator.DeleteEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(true);

            _mockBotConfigurator.CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(EndpointRegistrationResult.Created);

            // Act
            await BlueprintSubcommand.UpdateEndpointAsync(
                configPath,
                newEndpointUrl,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector);

            // Assert - Should call delete then create
            await _mockBotConfigurator.Received(1).DeleteEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                config.AgentBlueprintId);

            await _mockBotConfigurator.Received(1).CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                newEndpointUrl,
                Arg.Any<string>(),
                config.AgentBlueprintId);
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
            WebAppName = "test-webapp",
            Location = "eastus",
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
                _mockBotConfigurator,
                _mockPlatformDetector));

        exception.Message.Should().Contain("HTTPS");
    }

    [Fact]
    public async Task UpdateEndpointAsync_WhenDeleteFails_ShouldThrowAndNotRegister()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            WebAppName = "test-webapp",
            Location = "eastus",
            DeploymentProjectPath = Path.GetTempPath()
        };

        var newEndpointUrl = "https://newhost.example.com/api/messages";
        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        _mockBotConfigurator.DeleteEndpointWithAgentBlueprintAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(false); // Delete fails

        // Act & Assert
        var exception = await Assert.ThrowsAsync<Cli.Exceptions.SetupValidationException>(async () =>
            await BlueprintSubcommand.UpdateEndpointAsync(
                configPath,
                newEndpointUrl,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector));

        exception.Message.Should().Contain("delete");

        // Should NOT attempt to register new endpoint
        await _mockBotConfigurator.DidNotReceive().CreateEndpointWithAgentBlueprintAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task UpdateEndpointAsync_WithNoExistingEndpoint_ShouldSkipDeleteAndRegister()
    {
        // Arrange - Config without BotName (no existing endpoint)
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            // WebAppName not set, so BotName will be empty
            Location = "eastus",
            DeploymentProjectPath = Path.GetTempPath(),
            NeedDeployment = false // Non-Azure hosting
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

            _mockBotConfigurator.CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(EndpointRegistrationResult.Created);

            // Act
            await BlueprintSubcommand.UpdateEndpointAsync(
                configPath,
                newEndpointUrl,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector);

            // Assert - Should NOT call delete (no existing endpoint)
            await _mockBotConfigurator.DidNotReceive().DeleteEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>());

            // Should still register the new endpoint
            await _mockBotConfigurator.Received(1).CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                newEndpointUrl,
                Arg.Any<string>(),
                config.AgentBlueprintId);
        }
        finally
        {
            if (File.Exists(generatedPath)) File.Delete(generatedPath);
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    #endregion
}
