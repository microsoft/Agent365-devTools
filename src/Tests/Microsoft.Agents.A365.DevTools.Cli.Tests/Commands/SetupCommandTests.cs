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
}
