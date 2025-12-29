// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class CleanupCommandTests
{
    private readonly ILogger<CleanupCommand> _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly IBotConfigurator _mockBotConfigurator;
    private readonly CommandExecutor _mockExecutor;
    private readonly GraphApiService _graphApiService;
    private readonly AgentBlueprintService _agentBlueprintService;
    private readonly IMicrosoftGraphTokenProvider _mockTokenProvider;
    private readonly IConfirmationProvider _mockConfirmationProvider;

    public CleanupCommandTests()
    {
        _mockLogger = Substitute.For<ILogger<CleanupCommand>>();
        _mockConfigService = Substitute.For<IConfigService>();
        
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);

        // Default executor behavior for tests: return success for any external command to avoid launching real CLI tools
        _mockExecutor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty }));
        _mockBotConfigurator = Substitute.For<IBotConfigurator>();
        
        // Create a mock token provider for GraphApiService
        _mockTokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
        
        // Configure token provider to return a test token
        _mockTokenProvider.GetMgGraphAccessTokenAsync(
            Arg.Any<string>(), 
            Arg.Any<IEnumerable<string>>(), 
            Arg.Any<bool>(), 
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns("test-token");
        
        // Create a real GraphApiService instance with mocked dependencies
        var mockGraphLogger = Substitute.For<ILogger<GraphApiService>>();
        _graphApiService = new GraphApiService(mockGraphLogger, _mockExecutor, null, _mockTokenProvider);
        
        // Create AgentBlueprintService wrapping GraphApiService
        var mockBlueprintLogger = Substitute.For<ILogger<AgentBlueprintService>>();
        _agentBlueprintService = new AgentBlueprintService(mockBlueprintLogger, _graphApiService);
        
        // Mock confirmation provider - default to confirming (for most tests)
        _mockConfirmationProvider = Substitute.For<IConfirmationProvider>();
        _mockConfirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(true);
        _mockConfirmationProvider.ConfirmWithTypedResponseAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
    }

    [Fact(Skip = "Test requires interactive confirmation - cleanup commands now enforce user confirmation instead of --force")]
    public async Task CleanupAzure_WithValidConfig_ShouldExecuteResourceDeleteCommands()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "azure", "--config", "test.json" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        Assert.Equal(0, result);
        
        // Verify Azure resource deletion commands are executed (command and arguments separately)
        await _mockExecutor.Received().ExecuteAsync(
            "az",
            Arg.Is<string>(args => args.Contains("webapp delete") && args.Contains(config.WebAppName)),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        
        await _mockExecutor.Received().ExecuteAsync(
            "az",
            Arg.Is<string>(args => args.Contains("appservice plan delete") && args.Contains(config.AppServicePlanName)),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupInstance_WithValidConfig_ShouldReturnSuccess()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBotConfigurator.DeleteEndpointWithAgentBlueprintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(true));
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "instance", "--config", "test.json" };

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

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "--config", "test.json" };

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
        
        // Verify Azure resource deletion
        await _mockExecutor.Received().ExecuteAsync(
            "az",
            Arg.Is<string>(args => args.Contains("webapp delete") && args.Contains(config.WebAppName)),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact(Skip = "Test requires interactive confirmation - cleanup commands now enforce user confirmation instead of --force")]
    public async Task CleanupAzure_WithMissingWebAppName_ShouldStillExecuteCommand()
    {
        // Arrange
        var config = CreateConfigWithMissingWebApp(); // Create config without web app name
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "azure", "--config", "test.json" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        Assert.Equal(0, result);
        
        // Test current behavior: Commands execute even with empty web app name 
        // (This exposes a potential improvement - command should validate before executing)
        await _mockExecutor.Received().ExecuteAsync(
            "az",
            Arg.Is<string>(args => args.Contains("webapp delete")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupCommand_WithInvalidConfigFile_ShouldReturnError()
    {
        // Arrange
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromException<Agent365Config>(new FileNotFoundException("Config not found")));

        _mockBotConfigurator.DeleteEndpointWithAgentBlueprintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "azure", "--config", "invalid.json" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        // Note: Current implementation catches exceptions and returns 0, but logs error
        // This tests the actual behavior, not ideal behavior
        Assert.Equal(0, result); 
        
        // Verify no Azure CLI commands are executed when config loading fails
        await _mockExecutor.DidNotReceive().ExecuteAsync(
            "az", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void CleanupCommand_ShouldHaveCorrectSubcommands()
    {
        // Arrange & Act
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);

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
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);

        // Assert - Verify parent command has options for default handler
        var optionNames = command.Options.Select(opt => opt.Name).ToList();
        Assert.Contains("config", optionNames);
        // Force option has been removed to enforce interactive confirmation
        Assert.DoesNotContain("force", optionNames);
    }

    [Fact]
    public void CleanupSubcommands_ShouldHaveRequiredOptions()
    {
        // Arrange & Act
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var blueprintCommand = command.Subcommands.First(sc => sc.Name == "blueprint");

        // Assert - Verify user-facing options
        var optionNames = blueprintCommand.Options.Select(opt => opt.Name).ToList();
        Assert.Contains("config", optionNames);
        // Force option has been removed to enforce interactive confirmation
        Assert.DoesNotContain("force", optionNames);
    }

    [Fact(Skip = "Requires interactive confirmation. Refactor command to allow test automation.")]
    public async Task CleanupBlueprint_WithValidConfig_ShouldReturnSuccess()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);

        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "blueprint", "--config", "test.json" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        Assert.Equal(0, result); // Success exit code
        
        // Test behavior: Blueprint cleanup currently succeeds (placeholder implementation)
        // When actual PowerShell integration is added, this test can be enhanced
    }

    private static Agent365Config CreateValidConfig()
    {
        return new Agent365Config
        {
            TenantId = "test-tenant-id",
            SubscriptionId = "test-subscription-id",
            ResourceGroup = "test-rg",
            Location = "eastus",
            WebAppName = "test-web-app",
            AppServicePlanName = "test-app-service-plan",
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
            SubscriptionId = "test-subscription-id",
            ResourceGroup = "test-rg",
            WebAppName = string.Empty, // Missing web app name
            AppServicePlanName = "test-app-service-plan"
        };
    }

    /// <summary>
    /// REGRESSION TEST: Verifies that user must confirm cleanup operations.
    /// If user declines first confirmation, cleanup should abort without deleting anything.
    /// </summary>
    [Fact]
    public async Task Cleanup_WhenUserDeclinesInitialConfirmation_ShouldAbortWithoutDeletingAnything()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBotConfigurator.DeleteEndpointWithAgentBlueprintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        
        // User declines the initial "Are you sure?" confirmation
        _mockConfirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(false);
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "--config", "test.json" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        result.Should().Be(0); // Command completes successfully (just doesn't delete anything)
        
        // Verify NO delete operations were called - check bot configurator wasn't invoked
        await _mockBotConfigurator.DidNotReceive().DeleteEndpointWithAgentBlueprintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    /// REGRESSION TEST: Verifies that user must type "DELETE" to confirm cleanup.
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
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "--config", "test.json" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        result.Should().Be(0);
        
        // Verify NO delete operations were called - check bot configurator wasn't invoked
        await _mockBotConfigurator.DidNotReceive().DeleteEndpointWithAgentBlueprintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    /// REGRESSION TEST: Verifies confirmation provider is called with correct prompts.
    /// This ensures the user-facing prompts remain consistent.
    /// </summary>
    [Fact]
    public async Task Cleanup_ShouldCallConfirmationProviderWithCorrectPrompts()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "--config", "test.json" };

        // Act
        await command.InvokeAsync(args);

        // Assert
        await _mockConfirmationProvider.Received(1).ConfirmAsync(Arg.Is<string>(s => s.Contains("DELETE ALL resources")));
        await _mockConfirmationProvider.Received(1).ConfirmWithTypedResponseAsync(Arg.Is<string>(s => s.Contains("Type 'DELETE'")), "DELETE");
    }

    /// <summary>
    /// REGRESSION TEST: Verifies that cleanup command properly injects IConfirmationProvider.
    /// If this test fails after refactoring, it means the DI registration was broken.
    /// </summary>
    [Fact]
    public void CleanupCommand_ShouldAcceptConfirmationProviderParameter()
    {
        // Act & Assert - Should not throw
        var command = CleanupCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockBotConfigurator,
            _mockExecutor,
            _agentBlueprintService,
            _mockConfirmationProvider);

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
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
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
        _mockBotConfigurator.DeleteEndpointWithAgentBlueprintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--config", "test.json" };

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
            await _mockBotConfigurator.Received(1).DeleteEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), 
                config.Location, 
                config.AgentBlueprintId!);
            
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
    /// TEST: Verifies that blueprint cleanup with --endpoint-only flag shows appropriate error
    /// when blueprint ID is missing.
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithEndpointOnlyAndNoBlueprintId_ShouldLogError()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            SubscriptionId = "test-subscription-id",
            ResourceGroup = "test-rg",
            Location = "eastus",
            WebAppName = "test-web-app",
            AppServicePlanName = "test-app-service-plan",
            AgenticAppId = "test-identity-id",
            AgenticUserId = "test-user-id",
            AgentDescription = "test-agent-description"
            // No AgentBlueprintId set
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--config", "test.json" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        Assert.Equal(0, result); // Command completes but doesn't delete anything
        
        // Verify no deletion operations were called
        await _mockBotConfigurator.DidNotReceive().DeleteEndpointWithAgentBlueprintAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    /// TEST: Verifies that blueprint cleanup with --endpoint-only flag shows appropriate info
    /// when no endpoint exists to clean up.
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithEndpointOnlyAndNoBotName_ShouldLogInfo()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            SubscriptionId = "test-subscription-id",
            ResourceGroup = "test-rg",
            Location = "eastus",
            WebAppName = string.Empty, // No WebAppName means no BotName
            AppServicePlanName = "test-app-service-plan",
            AgentBlueprintId = "test-blueprint-id",
            AgenticAppId = "test-identity-id",
            AgenticUserId = "test-user-id",
            AgentDescription = "test-agent-description"
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--config", "test.json" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        Assert.Equal(0, result);
        
        // Verify no deletion operations were called
        await _mockBotConfigurator.DidNotReceive().DeleteEndpointWithAgentBlueprintAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    /// TEST: Verifies that blueprint cleanup with --endpoint-only flag handles invalid/empty Location.
    /// The command should still proceed but may fail when calling the API with invalid location.
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithEndpointOnlyAndInvalidLocation_ShouldPassLocationToApi()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            SubscriptionId = "test-subscription-id",
            ResourceGroup = "test-rg",
            Location = string.Empty, // Invalid/empty location
            WebAppName = "test-web-app",
            AppServicePlanName = "test-app-service-plan",
            AgentBlueprintId = "test-blueprint-id",
            AgenticAppId = "test-identity-id",
            AgenticUserId = "test-user-id",
            AgentDescription = "test-agent-description"
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBotConfigurator.DeleteEndpointWithAgentBlueprintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(false); // API will likely fail with invalid location
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--config", "test.json" };

        var originalIn = Console.In;
        try
        {
            using var stringReader = new StringReader("y\n");
            Console.SetIn(stringReader);

            // Act
            var result = await command.InvokeAsync(args);

            // Assert
            Assert.Equal(0, result);
            
            // Verify deletion was attempted with the invalid location
            await _mockBotConfigurator.Received(1).DeleteEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), 
                string.Empty, // Should pass the empty location
                config.AgentBlueprintId!);
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    /// <summary>
    /// TEST: Verifies that blueprint cleanup with --endpoint-only flag handles API exceptions gracefully.
    /// When DeleteEndpointWithAgentBlueprintAsync throws an exception, it should be caught and logged.
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithEndpointOnlyAndApiException_ShouldHandleGracefully()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBotConfigurator.DeleteEndpointWithAgentBlueprintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("API connection failed")));
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--config", "test.json" };

        var originalIn = Console.In;
        try
        {
            using var stringReader = new StringReader("y\n");
            Console.SetIn(stringReader);

            // Act
            var result = await command.InvokeAsync(args);

            // Assert
            // Command should complete even if API throws exception (exception should be caught)
            Assert.Equal(0, result);
            
            // Verify deletion was attempted
            await _mockBotConfigurator.Received(1).DeleteEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), 
                config.Location, 
                config.AgentBlueprintId!);
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    /// <summary>
    /// TEST: Verifies that blueprint cleanup with --endpoint-only flag handles empty blueprint ID parameter.
    /// This is a duplicate check since CleanupBlueprint_WithEndpointOnlyAndNoBlueprintId_ShouldLogError exists,
    /// but this variant tests with whitespace-only string instead of null.
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithEndpointOnlyAndWhitespaceBlueprint_ShouldLogError()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            SubscriptionId = "test-subscription-id",
            ResourceGroup = "test-rg",
            Location = "eastus",
            WebAppName = "test-web-app",
            AppServicePlanName = "test-app-service-plan",
            AgentBlueprintId = "   ", // Whitespace-only blueprint ID
            AgenticAppId = "test-identity-id",
            AgenticUserId = "test-user-id",
            AgentDescription = "test-agent-description"
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--config", "test.json" };

        // Act
        var result = await command.InvokeAsync(args);

        // Assert
        Assert.Equal(0, result);
        
        // Verify no deletion operations were called since blueprint ID is invalid
        await _mockBotConfigurator.DidNotReceive().DeleteEndpointWithAgentBlueprintAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    /// TEST: Verifies that blueprint cleanup with --endpoint-only flag handles invalid user input.
    /// When user enters something other than y/yes/n/no, cleanup should be cancelled.
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithEndpointOnlyAndInvalidInput_ShouldCancelCleanup()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBotConfigurator.DeleteEndpointWithAgentBlueprintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--config", "test.json" };

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
            await _mockBotConfigurator.DidNotReceive().DeleteEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    /// <summary>
    /// TEST: Verifies that blueprint cleanup with --endpoint-only flag handles 'n' (no) response.
    /// When user explicitly declines, cleanup should be cancelled.
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithEndpointOnlyAndNoResponse_ShouldCancelCleanup()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBotConfigurator.DeleteEndpointWithAgentBlueprintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--config", "test.json" };

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
            await _mockBotConfigurator.DidNotReceive().DeleteEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    /// <summary>
    /// TEST: Verifies that blueprint cleanup with --endpoint-only flag handles empty input (just Enter).
    /// When user presses Enter without typing anything, cleanup should be cancelled (default is No).
    /// </summary>
    [Fact]
    public async Task CleanupBlueprint_WithEndpointOnlyAndEmptyInput_ShouldCancelCleanup()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        _mockBotConfigurator.DeleteEndpointWithAgentBlueprintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockBotConfigurator, _mockExecutor, _agentBlueprintService, _mockConfirmationProvider);
        var args = new[] { "cleanup", "blueprint", "--endpoint-only", "--config", "test.json" };

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
            await _mockBotConfigurator.DidNotReceive().DeleteEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }
}
