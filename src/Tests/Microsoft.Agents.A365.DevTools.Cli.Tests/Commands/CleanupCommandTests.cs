// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
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
    private readonly CommandExecutor _mockExecutor;

    public CleanupCommandTests()
    {
        _mockLogger = Substitute.For<ILogger<CleanupCommand>>();
        _mockConfigService = Substitute.For<IConfigService>();
        
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);

        // Default executor behavior for tests: return success for any external command to avoid launching real CLI tools
        _mockExecutor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty }));
    }

    [Fact(Skip = "Test requires interactive confirmation - cleanup commands now enforce user confirmation instead of --force")]
    public async Task CleanupAzure_WithValidConfig_ShouldExecuteResourceDeleteCommands()
    {
        // Arrange
        var config = CreateValidConfig();
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockExecutor);
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
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockExecutor);
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
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockExecutor);
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
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockExecutor);
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
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockExecutor);
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
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockExecutor);

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
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockExecutor);

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
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockExecutor);
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
        
        var command = CleanupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockExecutor);
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
}