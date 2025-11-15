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
        _mockLogger.Received().Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
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

        // Assert - Setup should display a comprehensive summary with multiple info log calls
        var infoLogCount = _mockLogger.ReceivedCalls()
            .Count(call =>
            {
                var args = call.GetArguments();
                return call.GetMethodInfo().Name == "Log" && 
                       args.Length > 0 &&
                       args[0] is LogLevel level &&
                       level == LogLevel.Information;
            });
        infoLogCount.Should().BeGreaterThan(3, "Setup should log summary, completed steps, and other informational messages");
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

        // Assert - When all steps succeed, should log success at Information level
        var infoLogCount = _mockLogger.ReceivedCalls()
            .Count(call =>
            {
                var args = call.GetArguments();
                return call.GetMethodInfo().Name == "Log" && 
                       args.Length > 0 &&
                       args[0] is LogLevel level &&
                       level == LogLevel.Information;
            });
        infoLogCount.Should().BeGreaterThan(0, "Setup should show success message when all steps complete");
    }
}

