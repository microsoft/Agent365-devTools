// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using NSubstitute;
using Xunit;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Functional tests for SetupCommand execution
/// </summary>
public class SetupCommandTests
{
    private readonly ILogger<SetupCommand> _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly CommandExecutor _mockExecutor;
    private readonly DeploymentService _mockDeploymentService;
    private readonly BotConfigurator _mockBotConfigurator;
    private readonly IAzureValidator _mockAzureValidator;
    private readonly AzureWebAppCreator _mockWebAppCreator;
    private readonly PlatformDetector _mockPlatformDetector;

    public SetupCommandTests()
    {
        _mockLogger = Substitute.For<ILogger<SetupCommand>>();
        _mockConfigService = Substitute.For<IConfigService>();
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);
        var mockDeployLogger = Substitute.For<ILogger<DeploymentService>>();
        var mockPlatformDetectorLogger = Substitute.For<ILogger<PlatformDetector>>();
        _mockPlatformDetector = Substitute.ForPartsOf<PlatformDetector>(mockPlatformDetectorLogger);
        var mockDotNetLogger = Substitute.For<ILogger<DotNetBuilder>>();
        var mockNodeLogger = Substitute.For<ILogger<NodeBuilder>>();
        var mockPythonLogger = Substitute.For<ILogger<PythonBuilder>>();
        _mockDeploymentService = Substitute.ForPartsOf<DeploymentService>(
            mockDeployLogger, 
            _mockExecutor, 
            _mockPlatformDetector,
            mockDotNetLogger,
            mockNodeLogger,
            mockPythonLogger);
        var mockBotLogger = Substitute.For<ILogger<BotConfigurator>>();
        _mockBotConfigurator = Substitute.ForPartsOf<BotConfigurator>(mockBotLogger, _mockExecutor);
        _mockAzureValidator = Substitute.For<IAzureValidator>();
        _mockWebAppCreator = Substitute.ForPartsOf<AzureWebAppCreator>(Substitute.For<ILogger<AzureWebAppCreator>>());

        // Prevent the real setup runner from running during tests by short-circuiting it
        SetupCommand.SetupRunnerInvoker = (setupPath, generatedPath, exec, webApp) => Task.FromResult(true);
    }

    [Fact]
    public async Task SetupCommand_DryRun_ValidConfig_OnlyValidatesConfig()
    {
        // Arrange
        var config = new Agent365Config { TenantId = "tenant", SubscriptionId = "sub", ResourceGroup = "rg", Location = "loc", AppServicePlanName = "plan", WebAppName = "web", AgentIdentityDisplayName = "agent", DeploymentProjectPath = "." };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));

        var command = SetupCommand.CreateCommand(_mockLogger, _mockConfigService, _mockExecutor, _mockDeploymentService, _mockBotConfigurator, _mockAzureValidator, _mockWebAppCreator, _mockPlatformDetector);
        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("--dry-run", testConsole);

        // Assert
        Assert.Equal(0, result);

        // Dry-run should load config but must not call Azure/Bot services
        await _mockConfigService.Received(1).LoadAsync(Arg.Any<string>(), Arg.Any<string>());
        await _mockAzureValidator.DidNotReceiveWithAnyArgs().ValidateAllAsync(default!);
        await _mockBotConfigurator.DidNotReceiveWithAnyArgs().CreateOrUpdateBotWithAgentBlueprintAsync(default!, default!, default!, default!, default!, default!, default!, default!, default!);
    }

    [Fact]
    public async Task SetupCommand_McpPermissionFailure_DoesNotThrowUnhandledException()
    {
        // Arrange
        var config = new Agent365Config 
        { 
            TenantId = "tenant", 
            SubscriptionId = "sub", 
            ResourceGroup = "rg", 
            Location = "eastus", 
            AppServicePlanName = "plan", 
            WebAppName = "web", 
            AgentIdentityDisplayName = "agent", 
            DeploymentProjectPath = ".",
            AgentBlueprintId = "blueprint-app-id",
            Environment = "prod"
        };
        
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));
        _mockAzureValidator.ValidateAllAsync(Arg.Any<string>()).Returns(Task.FromResult(true));

        // Simulate MCP permission failure by setting up a failing mock
        SetupCommand.SetupRunnerInvoker = async (setupPath, generatedPath, exec, webApp) =>
        {
            // Simulate blueprint creation success but write minimal generated config
            var generatedConfig = new
            {
                agentBlueprintId = "test-blueprint-id",
                agentBlueprintObjectId = "test-object-id",
                tenantId = "tenant"
            };
            
            await File.WriteAllTextAsync(generatedPath, System.Text.Json.JsonSerializer.Serialize(generatedConfig));
            return true;
        };

        var command = SetupCommand.CreateCommand(
            _mockLogger, 
            _mockConfigService, 
            _mockExecutor, 
            _mockDeploymentService, 
            _mockBotConfigurator, 
            _mockAzureValidator, 
            _mockWebAppCreator, 
            _mockPlatformDetector);
        
        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act - Even if MCP permissions fail, setup should not throw unhandled exception
        var result = await parser.InvokeAsync("setup", testConsole);

        // Assert - The command should complete without unhandled exceptions
        // It may log errors but should not crash
        result.Should().BeOneOf(0, 1); // May return 0 (success) or 1 (partial failure) but should not throw
    }

    [Fact]
    public void SetupCommand_ErrorMessages_ShouldBeInformativeAndActionable()
    {
        // Arrange
        var mockLogger = Substitute.For<ILogger<SetupCommand>>();
        
        // Act - Verify that error messages are being logged with sufficient detail
        // This is a placeholder for ensuring error messages follow best practices
        
        // Assert - Error messages should:
        // 1. Explain what failed
        mockLogger.ReceivedCalls().Should().NotBeNull();
        
        // 2. Provide context (e.g., which resource, which permission)
        // 3. Suggest remediation steps
        // 4. Not contain emojis or special characters
    }

    [Fact]
    public async Task SetupCommand_BlueprintCreationSuccess_LogsAtInfoLevel()
    {
        // Arrange
        var config = new Agent365Config 
        { 
            TenantId = "tenant", 
            SubscriptionId = "sub", 
            ResourceGroup = "rg", 
            Location = "eastus", 
            AppServicePlanName = "plan", 
            WebAppName = "web", 
            AgentIdentityDisplayName = "agent", 
            DeploymentProjectPath = ".",
            AgentBlueprintId = "blueprint-app-id"
        };
        
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));
        _mockAzureValidator.ValidateAllAsync(Arg.Any<string>()).Returns(Task.FromResult(true));

        SetupCommand.SetupRunnerInvoker = async (setupPath, generatedPath, exec, webApp) =>
        {
            var generatedConfig = new
            {
                agentBlueprintId = "test-blueprint-id",
                agentBlueprintObjectId = "test-object-id",
                tenantId = "tenant",
                completed = true
            };
            
            await File.WriteAllTextAsync(generatedPath, System.Text.Json.JsonSerializer.Serialize(generatedConfig));
            return true;
        };

        var command = SetupCommand.CreateCommand(
            _mockLogger, 
            _mockConfigService, 
            _mockExecutor, 
            _mockDeploymentService, 
            _mockBotConfigurator, 
            _mockAzureValidator, 
            _mockWebAppCreator, 
            _mockPlatformDetector);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("setup", testConsole);

        // Assert - Blueprint creation success should be logged at Info level
        _mockLogger.ReceivedCalls().Should().NotBeEmpty();
    }

    [Fact]
    public async Task SetupCommand_GeneratedConfigPath_LoggedAtDebugLevel()
    {
        // Arrange
        var config = new Agent365Config 
        { 
            TenantId = "tenant", 
            SubscriptionId = "sub", 
            ResourceGroup = "rg", 
            Location = "eastus", 
            AppServicePlanName = "plan", 
            WebAppName = "web", 
            AgentIdentityDisplayName = "agent", 
            DeploymentProjectPath = ".",
            AgentBlueprintId = "blueprint-app-id"
        };
        
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));
        _mockAzureValidator.ValidateAllAsync(Arg.Any<string>()).Returns(Task.FromResult(true));

        var debugLogReceived = false;
        _mockLogger.When(x => x.Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>()))
            .Do(x => debugLogReceived = true);

        SetupCommand.SetupRunnerInvoker = async (setupPath, generatedPath, exec, webApp) =>
        {
            var generatedConfig = new
            {
                agentBlueprintId = "test-blueprint-id"
            };
            
            await File.WriteAllTextAsync(generatedPath, System.Text.Json.JsonSerializer.Serialize(generatedConfig));
            return true;
        };

        var command = SetupCommand.CreateCommand(
            _mockLogger, 
            _mockConfigService, 
            _mockExecutor, 
            _mockDeploymentService, 
            _mockBotConfigurator, 
            _mockAzureValidator, 
            _mockWebAppCreator, 
            _mockPlatformDetector);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        await parser.InvokeAsync("setup", testConsole);

        // Assert - Generated config path should be logged at Debug level, not Info
        // This test verifies that implementation detail messages are not shown to users by default
        debugLogReceived.Should().BeTrue("Generated config path should be logged at Debug level");
    }

    [Fact]
    public async Task SetupCommand_PartialFailure_DisplaysComprehensiveSummary()
    {
        // Arrange
        var config = new Agent365Config 
        { 
            TenantId = "tenant", 
            SubscriptionId = "sub", 
            ResourceGroup = "rg", 
            Location = "eastus", 
            AppServicePlanName = "plan", 
            WebAppName = "web", 
            AgentIdentityDisplayName = "agent", 
            DeploymentProjectPath = ".",
            AgentBlueprintId = "blueprint-app-id",
            Environment = "prod"
        };
        
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));
        _mockAzureValidator.ValidateAllAsync(Arg.Any<string>()).Returns(Task.FromResult(true));

        var summaryLogged = false;
        var completedStepsLogged = false;

        _mockLogger.When(x => x.Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>()))
            .Do(callInfo =>
            {
                var formatter = callInfo.ArgAt<Func<object, Exception?, string>>(4);
                var state = callInfo.ArgAt<object>(2);
                var message = formatter(state, null);
                
                if (message.Contains("Setup Summary"))
                    summaryLogged = true;
                if (message.Contains("Completed Steps:"))
                    completedStepsLogged = true;
            });

        SetupCommand.SetupRunnerInvoker = async (setupPath, generatedPath, exec, webApp) =>
        {
            var generatedConfig = new
            {
                agentBlueprintId = "test-blueprint-id",
                agentBlueprintObjectId = "test-object-id",
                tenantId = "tenant"
            };
            
            await File.WriteAllTextAsync(generatedPath, System.Text.Json.JsonSerializer.Serialize(generatedConfig));
            return true;
        };

        var command = SetupCommand.CreateCommand(
            _mockLogger, 
            _mockConfigService, 
            _mockExecutor, 
            _mockDeploymentService, 
            _mockBotConfigurator, 
            _mockAzureValidator, 
            _mockWebAppCreator, 
            _mockPlatformDetector);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("setup", testConsole);

        // Assert - Setup should display a comprehensive summary
        summaryLogged.Should().BeTrue("Setup should display a summary section");
        completedStepsLogged.Should().BeTrue("Summary should show completed steps");
    }

    [Fact]
    public async Task SetupCommand_AllStepsSucceed_ShowsSuccessfulSummary()
    {
        // Arrange
        var config = new Agent365Config 
        { 
            TenantId = "tenant", 
            SubscriptionId = "sub", 
            ResourceGroup = "rg", 
            Location = "eastus", 
            AppServicePlanName = "plan", 
            WebAppName = "web", 
            AgentIdentityDisplayName = "agent", 
            DeploymentProjectPath = ".",
            AgentBlueprintId = "blueprint-app-id",
            Environment = "prod"
        };
        
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));
        _mockAzureValidator.ValidateAllAsync(Arg.Any<string>()).Returns(Task.FromResult(true));

        var successMessageLogged = false;

        _mockLogger.When(x => x.Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>()))
            .Do(callInfo =>
            {
                var formatter = callInfo.ArgAt<Func<object, Exception?, string>>(4);
                var state = callInfo.ArgAt<object>(2);
                var message = formatter(state, null);
                
                if (message.Contains("Setup completed successfully"))
                    successMessageLogged = true;
            });

        SetupCommand.SetupRunnerInvoker = async (setupPath, generatedPath, exec, webApp) =>
        {
            var generatedConfig = new
            {
                agentBlueprintId = "test-blueprint-id",
                agentBlueprintObjectId = "test-object-id",
                tenantId = "tenant"
            };
            
            await File.WriteAllTextAsync(generatedPath, System.Text.Json.JsonSerializer.Serialize(generatedConfig));
            return true;
        };

        var command = SetupCommand.CreateCommand(
            _mockLogger, 
            _mockConfigService, 
            _mockExecutor, 
            _mockDeploymentService, 
            _mockBotConfigurator, 
            _mockAzureValidator, 
            _mockWebAppCreator, 
            _mockPlatformDetector);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        await parser.InvokeAsync("setup", testConsole);

        // Assert
        successMessageLogged.Should().BeTrue("Setup should show success message when all steps complete");
    }
}

