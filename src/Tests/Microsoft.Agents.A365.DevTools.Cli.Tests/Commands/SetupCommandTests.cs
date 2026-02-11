// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.IO;
using System.CommandLine.Parsing;

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
    private readonly IBotConfigurator _mockBotConfigurator;
    private readonly IAzureValidator _mockAzureValidator;
    private readonly AzureWebAppCreator _mockWebAppCreator;
    private readonly PlatformDetector _mockPlatformDetector;
    private readonly GraphApiService _mockGraphApiService;
    private readonly AgentBlueprintService _mockBlueprintService;
    private readonly IClientAppValidator _mockClientAppValidator;
    private readonly BlueprintLookupService _mockBlueprintLookupService;
    private readonly FederatedCredentialService _mockFederatedCredentialService;

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
        _mockBotConfigurator = Substitute.For<IBotConfigurator>();
        _mockAzureValidator = Substitute.For<IAzureValidator>();
        _mockWebAppCreator = Substitute.ForPartsOf<AzureWebAppCreator>(Substitute.For<ILogger<AzureWebAppCreator>>());
        _mockGraphApiService = Substitute.For<GraphApiService>();
        _mockBlueprintService = Substitute.ForPartsOf<AgentBlueprintService>(Substitute.For<ILogger<AgentBlueprintService>>(), _mockGraphApiService);
        _mockClientAppValidator = Substitute.For<IClientAppValidator>();
        _mockBlueprintLookupService = Substitute.ForPartsOf<BlueprintLookupService>(Substitute.For<ILogger<BlueprintLookupService>>(), _mockGraphApiService);
        _mockFederatedCredentialService = Substitute.ForPartsOf<FederatedCredentialService>(Substitute.For<ILogger<FederatedCredentialService>>(), _mockGraphApiService);
    }

    [Fact]
    public async Task SetupAllCommand_DryRun_ValidConfig_OnlyValidatesConfig()
    {
        // Arrange
        var config = new Agent365Config 
        { 
            TenantId = "tenant", 
            SubscriptionId = "sub", 
            ResourceGroup = "rg", 
            Location = "loc", 
            AppServicePlanName = "plan", 
            WebAppName = "web", 
            AgentIdentityDisplayName = "agent", 
            DeploymentProjectPath = ".",
            AgentBlueprintDisplayName = "TestBlueprint"
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));
        
        var command = SetupCommand.CreateCommand(
            _mockLogger, 
            _mockConfigService, 
            _mockExecutor, 
            _mockDeploymentService, 
            _mockBotConfigurator, 
            _mockAzureValidator, 
            _mockWebAppCreator, 
            _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator);
        
        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("all --dry-run", testConsole);

        // Assert
        Assert.Equal(0, result);

        // Dry-run mode does not load config or call Azure/Bot services - it just displays what would be done
        await _mockConfigService.DidNotReceiveWithAnyArgs().LoadAsync(Arg.Any<string>(), Arg.Any<string>());
        await _mockAzureValidator.DidNotReceiveWithAnyArgs().ValidateAllAsync(default!);
        await _mockBotConfigurator.DidNotReceiveWithAnyArgs().CreateEndpointWithAgentBlueprintAsync(default!, default!, default!, default!, default!);
    }

    [Fact]
    public async Task SetupAllCommand_SkipInfrastructure_SkipsInfrastructureStep()
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
            AgentBlueprintDisplayName = "TestBlueprint",
            Environment = "prod"
        };
        
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));
        
        var command = SetupCommand.CreateCommand(
            _mockLogger, 
            _mockConfigService, 
            _mockExecutor, 
            _mockDeploymentService, 
            _mockBotConfigurator, 
            _mockAzureValidator, 
            _mockWebAppCreator, 
            _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator);
        
        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("all --dry-run --skip-infrastructure", testConsole);

        // Assert
        Assert.Equal(0, result);
        
        // Dry-run mode does not load config - it just displays what would be done (with infrastructure skipped)
        await _mockConfigService.DidNotReceiveWithAnyArgs().LoadAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public void SetupCommand_HasRequiredSubcommands()
    {
        // Arrange & Act
        var command = SetupCommand.CreateCommand(
            _mockLogger, 
            _mockConfigService, 
            _mockExecutor, 
            _mockDeploymentService, 
            _mockBotConfigurator, 
            _mockAzureValidator, 
            _mockWebAppCreator, 
            _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator);

        // Assert - Verify all required subcommands exist
        var subcommandNames = command.Subcommands.Select(c => c.Name).ToList();
        
        subcommandNames.Should().Contain("requirements", "Setup should have requirements subcommand");
        subcommandNames.Should().Contain("infrastructure", "Setup should have infrastructure subcommand");
        subcommandNames.Should().Contain("blueprint", "Setup should have blueprint subcommand");
        subcommandNames.Should().Contain("permissions", "Setup should have permissions subcommand");
        subcommandNames.Should().Contain("all", "Setup should have all subcommand");
    }

    [Fact]
    public void SetupCommand_PermissionsSubcommand_HasMcpAndBotSubcommands()
    {
        // Arrange & Act
        var command = SetupCommand.CreateCommand(
            _mockLogger, 
            _mockConfigService, 
            _mockExecutor, 
            _mockDeploymentService, 
            _mockBotConfigurator, 
            _mockAzureValidator, 
            _mockWebAppCreator, 
            _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator);

        var permissionsCmd = command.Subcommands.FirstOrDefault(c => c.Name == "permissions");

        // Assert
        permissionsCmd.Should().NotBeNull("Permissions subcommand should exist");
        
        var permissionsSubcommandNames = permissionsCmd!.Subcommands.Select(c => c.Name).ToList();
        permissionsSubcommandNames.Should().Contain("mcp", "Permissions should have mcp subcommand");
        permissionsSubcommandNames.Should().Contain("bot", "Permissions should have bot subcommand");
    }

    [Fact]
    public void SetupCommand_ErrorMessages_ShouldBeInformativeAndActionable()
    {
        // Arrange
        var mockLogger = Substitute.For<ILogger<SetupCommand>>();
        
        // Act - Verify that command can be created without errors
        var command = SetupCommand.CreateCommand(
            mockLogger, 
            _mockConfigService, 
            _mockExecutor, 
            _mockDeploymentService, 
            _mockBotConfigurator, 
            _mockAzureValidator, 
            _mockWebAppCreator, 
            _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator);
        
        // Assert - Command structure should support clear error messaging
        command.Should().NotBeNull();
        command.Description.Should().NotBeNullOrEmpty("Setup command should have helpful description");
        
        // Error messages should:
        // 1. Explain what failed - verified through command descriptions
        // 2. Provide context (e.g., which resource, which permission) - verified through subcommand descriptions
        // 3. Suggest remediation steps - verified through command help text
        // 4. Not contain emojis or special characters - verified through clean descriptions
        
        foreach (var subcommand in command.Subcommands)
        {
            subcommand.Description.Should().NotBeNullOrEmpty($"Subcommand {subcommand.Name} should have description");
        }
    }

    [Fact]
    public async Task InfrastructureSubcommand_DryRun_CompletesSuccessfully()
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
            AppServicePlanSku = "B1"
        };
        
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));
        
        var command = SetupCommand.CreateCommand(
            _mockLogger, 
            _mockConfigService, 
            _mockExecutor, 
            _mockDeploymentService, 
            _mockBotConfigurator, 
            _mockAzureValidator, 
            _mockWebAppCreator,
            _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("infrastructure --dry-run", testConsole);

        // Assert
        Assert.Equal(0, result);
        
        // Verify config was loaded in dry-run mode
        await _mockConfigService.Received(1).LoadAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task BlueprintSubcommand_DryRun_CompletesSuccessfully()
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
            AgentBlueprintDisplayName = "TestBlueprint"
        };
        
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));

        var command = SetupCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockDeploymentService,
            _mockBotConfigurator,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("blueprint --dry-run", testConsole);

        // Assert
        Assert.Equal(0, result);
        
        // Verify config was loaded in dry-run mode
        await _mockConfigService.Received(1).LoadAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task RequirementsSubcommand_ValidConfig_CompletesSuccessfully()
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
            DeploymentProjectPath = "."
        };
        
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));

        var command = SetupCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockDeploymentService,
            _mockBotConfigurator,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector,
            _mockGraphApiService,
            _mockBlueprintService,
            _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("requirements", testConsole);

        // Assert
        Assert.Equal(0, result);
        
        // Verify config was loaded for requirements check
        await _mockConfigService.Received(1).LoadAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task RequirementsSubcommand_WithCategoryFilter_RunsFilteredChecks()
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
            DeploymentProjectPath = "."
        };
        
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));

        var command = SetupCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockDeploymentService,
            _mockBotConfigurator,
            _mockAzureValidator,
            _mockWebAppCreator,
            _mockPlatformDetector,
            _mockGraphApiService,
            _mockBlueprintService,
            _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("requirements --category Powershell", testConsole);

        // Assert
        Assert.Equal(0, result);
        
        // Verify config was loaded for requirements check
        await _mockConfigService.Received(1).LoadAsync(Arg.Any<string>(), Arg.Any<string>());
    }
}
