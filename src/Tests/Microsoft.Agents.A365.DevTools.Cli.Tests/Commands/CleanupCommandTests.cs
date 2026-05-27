// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using NSubstitute;
using Xunit;
using Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

[Collection("ConsoleOutput")]
public class CleanupCommandTests
{
    private readonly ILogger<CleanupCommand> _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly ITeamsGraphBackendConfigurator _mockBackendConfigurator;
    private readonly CommandExecutor _mockExecutor;
    private readonly GraphApiService _graphApiService;
    private readonly AgentBlueprintService _agentBlueprintService;
    private readonly FederatedCredentialService _federatedCredentialService;
    private readonly IMicrosoftGraphTokenProvider _mockTokenProvider;
    private readonly IConfirmationProvider _mockConfirmationProvider;
    private readonly IPrerequisiteRunner _mockPrerequisiteRunner;
    private readonly AzureAuthValidator _mockAuthValidator;

    public CleanupCommandTests()
    {
        _mockLogger = Substitute.For<ILogger<CleanupCommand>>();
        _mockConfigService = Substitute.For<IConfigService>();
        
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        // Full mock — ForPartsOf would fall through to real CommandExecutor.ExecuteAsync and spawn real processes
        _mockExecutor = Substitute.For<CommandExecutor>(mockExecutorLogger);

        // Default executor behavior for tests: return success for any external command to avoid launching real CLI tools
        _mockExecutor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty }));
        _mockBackendConfigurator = Substitute.For<ITeamsGraphBackendConfigurator>();
        
        // Create a mock token provider for GraphApiService
        _mockTokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
        
        // Configure token provider to return a test token
        _mockTokenProvider.GetMgGraphAccessTokenAsync(
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>())
            .Returns("test-token");
        
        // Create a real GraphApiService instance with mocked dependencies.
        // Pass a no-op loginHintResolver to prevent AzCliHelper.ResolveLoginHintAsync from spawning
        // a real "az account show" process during test setup.
        // Pass a TestHttpMessageHandler (returns 404 when queue empty) instead of null to avoid
        // real HTTPS calls to graph.microsoft.com — the handler returns immediately, no network needed.
        var mockGraphLogger = Substitute.For<ILogger<GraphApiService>>();
        _graphApiService = new GraphApiService(mockGraphLogger, _mockExecutor, Substitute.For<IAuthenticationService>(), new TestHttpMessageHandler(), _mockTokenProvider,
            loginHintResolver: () => Task.FromResult<string?>(null));
        
        // Create AgentBlueprintService wrapping GraphApiService
        var mockBlueprintLogger = Substitute.For<ILogger<AgentBlueprintService>>();
        _agentBlueprintService = new AgentBlueprintService(mockBlueprintLogger, _graphApiService);
        
        // Create FederatedCredentialService wrapping GraphApiService
        var mockFicLogger = Substitute.For<ILogger<FederatedCredentialService>>();
        _federatedCredentialService = new FederatedCredentialService(mockFicLogger, _graphApiService);
        
        // Mock confirmation provider - default to confirming (for most tests)
        _mockConfirmationProvider = Substitute.For<IConfirmationProvider>();
        _mockConfirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(true);
        _mockConfirmationProvider.ConfirmWithTypedResponseAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _mockPrerequisiteRunner = Substitute.For<IPrerequisiteRunner>();
        _mockPrerequisiteRunner.RunAsync(
                Arg.Any<IEnumerable<IRequirementCheck>>(),
                Arg.Any<Agent365Config>(),
                Arg.Any<ILogger>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        // Full mock — both virtual methods (ValidateAuthenticationAsync, GetAppServiceTokenAsync) are
        // always stubbed by callers, so ForPartsOf would only add risk of real auth code running.
        _mockAuthValidator = Substitute.For<AzureAuthValidator>(NullLogger<AzureAuthValidator>.Instance, _mockExecutor);
    }

    [Fact(Skip = "Test requires interactive confirmation - cleanup commands now enforce user confirmation instead of --force")]
    public async Task CleanupAzure_WithValidConfig_ShouldExecuteResourceDeleteCommands()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup", "azure" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        Assert.Equal(0, result);
        
        // Azure resource deletion has been removed - no commands to verify
    }

    [Fact]
    public async Task CleanupInstance_WithValidConfig_ShouldReturnSuccess()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Task.FromResult(true));
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup", "instance" };

        var originalIn = Console.In;
        try
        {
            // Provide confirmation input in case the command prompts for it
            // Some implementations may prompt multiple times; provide multiple affirmative lines to be safe
            Console.SetIn(new StringReader("y\ny\n"));

            // Act
            var result = await command.InvokeAsync(args);

            // Assert
            Assert.Equal(0, result); // Should succeed
            // Test behavior: Instance cleanup currently succeeds (placeholder implementation)
            // When actual cleanup is implemented, this test can be enhanced
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [Fact(Skip = "Test requires interactive confirmation - cleanup commands now enforce user confirmation instead of --force")]
    public async Task Cleanup_WithoutSubcommand_ShouldExecuteCompleteCleanup()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        Assert.Equal(0, result); // Should succeed

        // Test behavior: Default cleanup (without subcommand) performs complete cleanup
        // Verify blueprint deletion
        await _mockExecutor.Received().ExecuteAsync(
            "az",
            Arg.Is<string>(args => args.Contains("ad app delete") && args.Contains(config.AgentBlueprintId!)),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        
        // Azure resource deletion has been removed
    }

    [Fact(Skip = "Test requires interactive confirmation - cleanup commands now enforce user confirmation instead of --force")]
    public async Task CleanupAzure_WithMissingWebAppName_ShouldStillExecuteCommand()
    {
        // Arrange
        var config = CreateConfigWithMissingWebApp(); // Create config without web app name
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup", "azure" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        Assert.Equal(0, result);
        
        // Azure resource deletion has been removed
    }

    [Fact]
    public async Task CleanupCommand_WithInvalidConfigFile_ShouldReturnError()
    {
        // Arrange
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromException<Agent365Config>(new FileNotFoundException("Config not found")));

        _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Task.FromResult(false));

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup", "azure" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        // Config load failure returns exit code 1: LoadConfigAsync catches the exception and
        // returns null, then the azure cleanup handler explicitly exits with code 1 on null config.
        Assert.Equal(1, result);
        
        // Verify no Azure CLI commands are executed when config loading fails
        await _mockExecutor.DidNotReceive().ExecuteAsync(
            "az", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupAzure_WhenConfigFileNotFound_ShouldReturnExitCode2()
    {
        // Arrange — ConfigFileNotFoundException.ExitCode is 2 (configuration error).
        // Scripts checking $LASTEXITCODE must distinguish missing config (2) from general errors (1).
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromException<Agent365Config>(new ConfigFileNotFoundException()));

        _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Task.FromResult(false));

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup", "azure" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        result.Should().Be(2,
            because: "ConfigFileNotFoundException propagates with ExitCode=2; the outer catch sets context.ExitCode = ex.ExitCode");
    }

    [Fact]
    public void CleanupCommand_ShouldHaveCorrectSubcommands()
    {
        // Arrange & Act
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);

        // Assert - Verify command structure (what users see)
        Assert.Equal("cleanup", command.Name);
        Assert.Contains("ALL resources", command.Description); // Updated description for default-to-complete pattern
        
        // Verify selective cleanup subcommands exist
        var subcommandNames = command.Subcommands.Select(sc => sc.Name).ToList();
        Assert.Contains("blueprint", subcommandNames);
        Assert.Contains("azure", subcommandNames);
        Assert.Contains("instance", subcommandNames);
        
        // Note: "all" subcommand removed - default cleanup (no subcommand) now performs complete cleanup
    }

    [Fact]
    public void CleanupCommand_ShouldHaveDefaultHandlerOptions()
    {
        // Arrange & Act
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);

        // Assert - Verify parent command does not expose removed options
        var optionNames = command.Options.Select(opt => opt.Name).ToList();
        // Force option has been removed to enforce interactive confirmation
        Assert.DoesNotContain("force", optionNames);
    }

    [Fact]
    public void CleanupSubcommands_ShouldHaveRequiredOptions()
    {
        // Arrange & Act
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var blueprintCommand = command.Subcommands.First(sc => sc.Name == "blueprint");

        // Assert - Verify user-facing options
        var optionNames = blueprintCommand.Options.Select(opt => opt.Name).ToList();
        // Force option has been removed to enforce interactive confirmation
        Assert.DoesNotContain("force", optionNames);
    }

    [Fact]
    public async Task CleanupBlueprint_WithValidConfig_ShouldReturnSuccess()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);
        _mockConfirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(true);

        var stubbedBlueprintService = CreateStubbedBlueprintService();
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, stubbedBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup", "blueprint" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        result.Should().Be(0);
    }

    private AgentBlueprintService CreateStubbedBlueprintService(
        IReadOnlyList<AgentInstanceInfo>? instances = null,
        bool deleteUserResult = true,
        bool deleteIdentityResult = true,
        bool deleteBlueprintResult = true)
    {
        var mockBlueprintLogger = Substitute.For<ILogger<AgentBlueprintService>>();
        var spyService = Substitute.ForPartsOf<AgentBlueprintService>(mockBlueprintLogger, _graphApiService);

        spyService.GetAgentInstancesForBlueprintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(instances ?? (IReadOnlyList<AgentInstanceInfo>)Array.Empty<AgentInstanceInfo>());

        spyService.DeleteAgentUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(deleteUserResult);

        spyService.DeleteAgentIdentityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(deleteIdentityResult);

        spyService.DeleteAgentBlueprintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(deleteBlueprintResult);

        return spyService;
    }

    /// <summary>
    /// Verifies that blueprint cleanup deletes agent instances before deleting the blueprint.
    /// Instance deletion order: agentic user first, then identity SP, then blueprint.
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithInstances_DeletesInstancesBeforeBlueprint()
    {
        // Arrange
        var config = CreateValidConfig();
        // Capture blueprint ID before the command clears it during config save
        var expectedBlueprintId = config.AgentBlueprintId!;
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);

        var instances = new List<AgentInstanceInfo>
        {
            new() { IdentitySpId = "sp-id-1", DisplayName = "Instance A", AgentUserId = "user-id-1" }
        };
        var spyService = CreateStubbedBlueprintService(instances: instances);

        _mockConfirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(true);

        var command = CleanupCommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockBackendConfigurator,
            _mockExecutor, spyService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup", "blueprint" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        result.Should().Be(0);

        await spyService.Received(1).DeleteAgentUserAsync(
            config.TenantId, "user-id-1", Arg.Any<CancellationToken>());

        await spyService.Received(1).DeleteAgentIdentityAsync(
            config.TenantId, "sp-id-1", Arg.Any<CancellationToken>());

        await spyService.Received(1).DeleteAgentBlueprintAsync(
            config.TenantId, expectedBlueprintId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that blueprint cleanup with no DW instances still deletes agent identity
    /// when AgenticAppId is present (data-driven cleanup — no IsNonDwBlueprint flag required).
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithNoInstances_ProceedsAsNormal()
    {
        // Arrange
        var config = CreateValidConfig();
        // Capture blueprint ID before the command clears it during config save
        var expectedBlueprintId = config.AgentBlueprintId!;
        var expectedIdentityId = config.AgenticAppId!;
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);

        var spyService = CreateStubbedBlueprintService(instances: Array.Empty<AgentInstanceInfo>());

        _mockConfirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(true);

        var command = CleanupCommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockBackendConfigurator,
            _mockExecutor, spyService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup", "blueprint" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        result.Should().Be(0);

        // No DW agentic users to delete (no instances)
        await spyService.DidNotReceive().DeleteAgentUserAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Requirement: CleanupCommand must always delete the agent identity when AgenticAppId is present,
        // regardless of DW/non-DW path — deletion is data-driven (config presence), not flag-based.
        // Previously this test asserted DidNotReceive; the requirement changed when the non-DW blueprint
        // path was added and identity deletion was unified across both paths.
        await spyService.Received(1).DeleteAgentIdentityAsync(
            config.TenantId, expectedIdentityId, Arg.Any<CancellationToken>());

        await spyService.Received(1).DeleteAgentBlueprintAsync(
            config.TenantId, expectedBlueprintId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that when an instance deletion fails, a warning is emitted and the
    /// blueprint is still deleted (warn-and-continue behaviour).
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_InstanceDeletionFails_WarnsAndContinuesToBlueprint()
    {
        // Arrange
        var config = CreateValidConfig();
        // Capture blueprint ID before the command clears it during config save
        var expectedBlueprintId = config.AgentBlueprintId!;
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);

        var instances = new List<AgentInstanceInfo>
        {
            new() { IdentitySpId = "sp-id-1", DisplayName = "Instance A", AgentUserId = "user-id-1" }
        };
        var spyService = CreateStubbedBlueprintService(
            instances: instances,
            deleteUserResult: false,
            deleteIdentityResult: true,
            deleteBlueprintResult: true);

        _mockConfirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(true);

        var command = CleanupCommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockBackendConfigurator,
            _mockExecutor, spyService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup", "blueprint" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert -- command succeeds overall
        result.Should().Be(0);

        // Blueprint is still deleted despite the instance failure
        await spyService.Received(1).DeleteAgentBlueprintAsync(
            config.TenantId, expectedBlueprintId, Arg.Any<CancellationToken>());

        // Verify a warning was logged about the failed agentic user deletion
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Failed to delete agentic user") && o.ToString()!.Contains("user-id-1")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Verify the orphan summary warning was emitted for the failed resource
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Orphaned agentic user") && o.ToString()!.Contains("user-id-1")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// Verifies that when instances are deleted successfully but the blueprint deletion fails,
    /// a warning is logged about the incomplete cleanup state.
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WhenBlueprintDeletionFailsWithInstances_LogsWarning()
    {
        // Arrange
        var config = CreateValidConfig();
        var expectedBlueprintId = config.AgentBlueprintId!;
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);

        var instances = new List<AgentInstanceInfo>
        {
            new() { IdentitySpId = "sp-id-1", DisplayName = "Instance A", AgentUserId = "user-id-1" }
        };
        var spyService = CreateStubbedBlueprintService(
            instances: instances,
            deleteUserResult: true,
            deleteIdentityResult: true,
            deleteBlueprintResult: false);

        _mockConfirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(true);

        var command = CleanupCommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockBackendConfigurator,
            _mockExecutor, spyService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup", "blueprint" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        result.Should().Be(0);

        await spyService.Received(1).DeleteAgentUserAsync(
            config.TenantId, "user-id-1", Arg.Any<CancellationToken>());

        await spyService.Received(1).DeleteAgentIdentityAsync(
            config.TenantId, "sp-id-1", Arg.Any<CancellationToken>());

        await spyService.Received(1).DeleteAgentBlueprintAsync(
            config.TenantId, expectedBlueprintId, Arg.Any<CancellationToken>());

        // Verify that a warning was logged about the blueprint deletion failure
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Blueprint deletion failed. The blueprint still exists in Entra ID.")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Verify that the retry guidance message is also logged
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("All agent instances were deleted. Retry 'a365 cleanup blueprint'")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    private static Agent365Config CreateValidConfig()
    {
        return new Agent365Config
        {
            TenantId = "test-tenant-id",
            MessagingEndpoint = "https://test-bot.example.com/api/messages",
            AgentBlueprintId = "test-blueprint-id",
            AgenticAppId = "test-identity-id",
            AgenticUserId = "test-user-id",
            AgentDescription = "test-agent-description"
        };
    }

    private static Agent365Config CreateConfigWithMissingWebApp()
    {
        return new Agent365Config
        {
            TenantId = "test-tenant-id",
        };
    }

    /// <summary>
    /// Verifies that user must confirm cleanup operations.
    /// If user declines first confirmation, cleanup should abort without deleting anything.
    /// </summary>
    [Fact]
    public async Task Cleanup_WhenUserDeclinesInitialConfirmation_ShouldAbortWithoutDeletingAnything()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);
        
        // User declines the initial "Are you sure?" confirmation
        _mockConfirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(false);

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        result.Should().Be(0); // Command completes successfully (just doesn't delete anything)
        
        // Verify NO delete operations were called - check bot configurator wasn't invoked
        await _mockBackendConfigurator.DidNotReceive().ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>());
    }

    /// <summary>
    /// Verifies that user must type "DELETE" to confirm cleanup.
    /// If user confirms but doesn't type "DELETE" exactly, cleanup should abort.
    /// </summary>
    [Fact]
    public async Task Cleanup_WhenUserConfirmsButDoesNotTypeDelete_ShouldAbortWithoutDeletingAnything()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        
        // User confirms first prompt but declines the "Type DELETE" confirmation
        _mockConfirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(true);
        _mockConfirmationProvider.ConfirmWithTypedResponseAsync(Arg.Any<string>(), "DELETE").Returns(false);

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        result.Should().Be(0);
        
        // Verify NO delete operations were called - check bot configurator wasn't invoked
        await _mockBackendConfigurator.DidNotReceive().ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>());
    }

    /// <summary>
    /// Verifies confirmation provider is called with correct prompts.
    /// This ensures the user-facing prompts remain consistent.
    /// </summary>
    [Fact]
    public async Task Cleanup_ShouldCallConfirmationProviderWithCorrectPrompts()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);

        // First confirmation passes, typed confirmation fails — command aborts after both prompts
        // without running the Azure deletion loop. Explicit stubs make intent clear regardless
        // of constructor defaults.
        _mockConfirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(true);
        _mockConfirmationProvider.ConfirmWithTypedResponseAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup" };

        // Act
        await command.InvokeAsync(args);

        // Assert — both prompts were shown with the correct text
        await _mockConfirmationProvider.Received(1).ConfirmAsync(Arg.Is<string>(s => s.Contains("DELETE ALL resources")));
        await _mockConfirmationProvider.Received(1).ConfirmWithTypedResponseAsync(Arg.Is<string>(s => s.Contains("Type 'DELETE'")), "DELETE");

        // Assert — abort path taken: no deletion should have started after the typed confirmation failed
        await _mockBackendConfigurator.DidNotReceive().ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>());
    }

    /// <summary>
    /// Verifies that cleanup command properly injects IConfirmationProvider.
    /// If this test fails after refactoring, it means the DI registration was broken.
    /// </summary>
    [Fact]
    public void CleanupCommand_ShouldAcceptConfirmationProviderParameter()
    {
        // Act & Assert - Should not throw
        var command = CleanupCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockBackendConfigurator,
            _mockExecutor,
            _agentBlueprintService,
            _mockConfirmationProvider,
            _federatedCredentialService,
            _mockAuthValidator);

        command.Should().NotBeNull();
        command.Name.Should().Be("cleanup");
    }

    /// <summary>
    /// Verifies that blueprint cleanup command has the --endpoint-only option.
    /// </summary>
    [Fact]
    public void CleanupBlueprint_ShouldHaveEndpointOnlyOption()
    {
        // Arrange & Act
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var blueprintCommand = command.Subcommands.First(sc => sc.Name == "blueprint");

        // Assert
        var optionNames = blueprintCommand.Options.Select(opt => opt.Name).ToList();
        Assert.Contains("endpoint-only", optionNames);
    }

    /// <summary>
    /// Verifies that blueprint cleanup with --endpoint-only flag only deletes the messaging endpoint
    /// and preserves the blueprint application.
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithEndpointOnly_ShouldOnlyDeleteMessagingEndpoint()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        // --m365 is required to opt in to Teams Graph backend configuration clearing.
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--m365" };

        // Simulate user confirmation with y
        var originalIn = Console.In;
        try
        {
            using var stringReader = new StringReader("y\n");
            Console.SetIn(stringReader);

            // Act
            var result = await command.InvokeAsync(args);

            // Assert
            Assert.Equal(0, result);

            // Verify endpoint deletion was called
            await _mockBackendConfigurator.Received(1).ClearBackendConfigurationAsync(config.AgentBlueprintId!, Arg.Any<string?>());

            // Verify blueprint deletion was NOT called (no az ad app delete command)
            await _mockExecutor.DidNotReceive().ExecuteAsync(
                "az",
                Arg.Is<string>(cmdArgs => cmdArgs.Contains("ad app delete")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    /// <summary>
    /// Verifies that blueprint cleanup with --endpoint-only flag shows appropriate error
    /// when blueprint ID is missing. The validation check happens before the user prompt,
    /// so no console input is needed.
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithEndpointOnlyAndNoBlueprintId_ShouldLogError()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            AgenticAppId = "test-identity-id",
            AgenticUserId = "test-user-id",
            AgentDescription = "test-agent-description"
            // No AgentBlueprintId set
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--m365" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        Assert.Equal(0, result); // Command completes but doesn't delete anything

        // Verify no deletion operations were called (because blueprint ID is missing)
        await _mockBackendConfigurator.DidNotReceive().ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>());
    }

    // Note: two previously existing tests were deleted as part of the ABS-to-TeamsGraph
    // migration because the guards they exercised were removed from the cleanup path:
    //   - CleanupBlueprint_WithEndpointOnlyAndNoBotName_ShouldLogInfo — BotName is no longer
    //     required; Teams Graph is keyed purely by Agent Blueprint ID.
    //   - CleanupBlueprint_WithEndpointOnlyAndMissingLocation_ShouldNotCallApiAndLogError —
    //     Location is only meaningful for ABS endpoint provisioning, which no longer applies.

    /// <summary>
    /// Verifies that blueprint cleanup with --endpoint-only flag handles API exceptions gracefully.
    /// When ClearBackendConfigurationAsync throws an exception, it should be caught and logged.
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithEndpointOnlyAndApiException_ShouldHandleGracefully()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("API connection failed")));

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        // --m365 is required to opt in to Teams Graph backend configuration clearing.
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--m365" };

        var originalIn = Console.In;
        try
        {
            using var stringReader = new StringReader("y\n");
            Console.SetIn(stringReader);

            // Act
            var result = await command.InvokeAsync(args);

            // Assert
            // Command completes (exception is caught) but must signal failure via non-zero exit code
            // so scripts and CI can detect the error.
            Assert.Equal(1, result);

            // Verify deletion was attempted
            await _mockBackendConfigurator.Received(1).ClearBackendConfigurationAsync(config.AgentBlueprintId!, Arg.Any<string?>());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    /// <summary>
    /// Verifies that blueprint cleanup with --endpoint-only flag handles whitespace-only blueprint ID.
    /// Complements CleanupBlueprint_WithEndpointOnlyAndNoBlueprintId_ShouldLogError by testing whitespace
    /// edge case, validating that IsNullOrWhiteSpace correctly rejects whitespace-only strings.
    /// The validation check happens before the user prompt, so no console input is needed.
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithEndpointOnlyAndWhitespaceBlueprint_ShouldLogError()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            AgentBlueprintId = "   ", // Whitespace-only blueprint ID
            AgenticAppId = "test-identity-id",
            AgenticUserId = "test-user-id",
            AgentDescription = "test-agent-description"
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--m365" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        Assert.Equal(0, result);

        // Verify no deletion operations were called since blueprint ID is invalid
        await _mockBackendConfigurator.DidNotReceive().ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>());
    }

    /// <summary>
    /// Verifies that blueprint cleanup with --endpoint-only flag handles invalid user input.
    /// When user enters something other than y/yes/n/no, cleanup should be cancelled.
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithEndpointOnlyAndInvalidInput_ShouldCancelCleanup()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--m365" };

        var originalIn = Console.In;
        try
        {
            // User enters invalid input like "maybe" or "123"
            using var stringReader = new StringReader("maybe\n");
            Console.SetIn(stringReader);

            // Act
            var result = await command.InvokeAsync(args);

            // Assert
            Assert.Equal(0, result);

            // Verify NO deletion was called because invalid input should cancel
            await _mockBackendConfigurator.DidNotReceive().ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    /// <summary>
    /// Verifies that blueprint cleanup with --endpoint-only flag handles 'n' (no) response.
    /// When user explicitly declines, cleanup should be cancelled.
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithEndpointOnlyAndNoResponse_ShouldCancelCleanup()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--m365" };

        var originalIn = Console.In;
        try
        {
            // User enters 'n' to decline
            using var stringReader = new StringReader("n\n");
            Console.SetIn(stringReader);

            // Act
            var result = await command.InvokeAsync(args);

            // Assert
            Assert.Equal(0, result);
            
            // Verify NO deletion was called because user declined
            await _mockBackendConfigurator.DidNotReceive().ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    /// <summary>
    /// Verifies the Entra-discovery fallback in ExecuteAllCleanupAsync:
    /// when AgenticAppId is absent from config, linked SPs are discovered via
    /// GetAgentInstancesForBlueprintAsync and deleted. This covers the bug where
    /// 'a365 cleanup' without '--agent-name' silently skipped agent identity deletion
    /// because AgenticAppId was not populated in config.
    /// </summary>
    [Fact]
    public async Task ExecuteAllCleanup_WhenAgenticAppIdEmpty_DeletesLinkedSpDiscoveredFromEntra()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            AgentBlueprintId = "test-blueprint-id",
            AgenticAppId = null  // Not in config — Entra discovery path must pick it up
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);

        var linkedInstance = new AgentInstanceInfo { IdentitySpId = "sp-entra-id", DisplayName = "Entra SP" };
        var stubbedBlueprintService = CreateStubbedBlueprintService(
            instances: new List<AgentInstanceInfo> { linkedInstance },
            deleteIdentityResult: true,
            deleteBlueprintResult: true);

        var command = CleanupCommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockBackendConfigurator,
            _mockExecutor, stubbedBlueprintService, _mockConfirmationProvider, _federatedCredentialService,
            _mockAuthValidator, graphApiService: _graphApiService);
        var args = new[] { "cleanup" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        result.Should().Be(0);
        // Requirement: when AgenticAppId is absent from config, the Entra-discovery path must locate
        // and delete linked identity SPs — previously they were silently skipped.
        await stubbedBlueprintService.Received(1).DeleteAgentIdentityAsync(
            config.TenantId, "sp-entra-id", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that when the same SP appears in both config.AgenticAppId and the Entra query
    /// result, DeleteAgentIdentityAsync is called only once — the deletedIdentityIds HashSet
    /// deduplicates it to prevent double-delete.
    /// </summary>
    [Fact]
    public async Task ExecuteAllCleanup_WhenSpInBothConfigAndEntra_DeletesIdentityOnlyOnce()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            AgentBlueprintId = "test-blueprint-id",
            AgenticAppId = "sp-config-id"  // Same ID as Entra result below
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);

        // Entra returns the same SP that is already in config — dedup must prevent double-delete.
        var linkedInstance = new AgentInstanceInfo { IdentitySpId = "sp-config-id", DisplayName = "Config SP" };
        var stubbedBlueprintService = CreateStubbedBlueprintService(
            instances: new List<AgentInstanceInfo> { linkedInstance },
            deleteIdentityResult: true,
            deleteBlueprintResult: true);

        var command = CleanupCommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockBackendConfigurator,
            _mockExecutor, stubbedBlueprintService, _mockConfirmationProvider, _federatedCredentialService,
            _mockAuthValidator, graphApiService: _graphApiService);
        var args = new[] { "cleanup" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        result.Should().Be(0);
        // Requirement: deletedIdentityIds dedup must prevent double-deletes when the same SP appears
        // in both config.AgenticAppId and GetAgentInstancesForBlueprintAsync results.
        await stubbedBlueprintService.Received(1).DeleteAgentIdentityAsync(
            config.TenantId, "sp-config-id", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that when GetAgentInstancesForBlueprintAsync throws, the exception is swallowed
    /// and the overall cleanup continues — the Entra discovery path is non-fatal.
    /// </summary>
    [Fact]
    public async Task ExecuteAllCleanup_WhenEntraQueryThrows_CleanupContinuesNonfatally()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            AgentBlueprintId = "test-blueprint-id",
            AgenticAppId = null
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);

        // Build stub manually so the query can be configured to throw.
        var mockBlueprintLogger = Substitute.For<ILogger<AgentBlueprintService>>();
        var stubbedBlueprintService = Substitute.ForPartsOf<AgentBlueprintService>(mockBlueprintLogger, _graphApiService);
        stubbedBlueprintService.GetAgentInstancesForBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<AgentInstanceInfo>>(
                new InvalidOperationException("Simulated Entra query failure")));
        stubbedBlueprintService.DeleteAgentIdentityAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        stubbedBlueprintService.DeleteAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var command = CleanupCommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockBackendConfigurator,
            _mockExecutor, stubbedBlueprintService, _mockConfirmationProvider, _federatedCredentialService,
            _mockAuthValidator, graphApiService: _graphApiService);
        var args = new[] { "cleanup" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        result.Should().Be(0, because: "Entra discovery failure is non-fatal; cleanup must complete");
        // AgenticAppId was empty and Entra query threw — no identity deletion should have occurred.
        await stubbedBlueprintService.DidNotReceive().DeleteAgentIdentityAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that blueprint cleanup with --endpoint-only flag handles empty input (just Enter).
    /// When user presses Enter without typing anything, cleanup should be cancelled (default is No).
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithEndpointOnlyAndEmptyInput_ShouldCancelCleanup()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBackendConfigurator.ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBackendConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider, _federatedCredentialService, _mockAuthValidator);
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--m365" };

        var originalIn = Console.In;
        try
        {
            // User just presses Enter (empty input)
            using var stringReader = new StringReader("\n");
            Console.SetIn(stringReader);

            // Act
            var result = await command.InvokeAsync(args);

            // Assert
            Assert.Equal(0, result);
            
            // Verify NO deletion was called because empty input defaults to cancel
            await _mockBackendConfigurator.DidNotReceive().ClearBackendConfigurationAsync(Arg.Any<string>(), Arg.Any<string?>());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }
}
