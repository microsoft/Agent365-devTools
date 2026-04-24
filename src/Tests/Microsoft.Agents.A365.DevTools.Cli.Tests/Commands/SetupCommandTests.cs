// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ITeamsGraphBackendConfigurator _mockBackendConfigurator;
    private readonly AzureAuthValidator _mockAuthValidator;
    private readonly PlatformDetector _mockPlatformDetector;
    private readonly GraphApiService _mockGraphApiService;
    private readonly AgentBlueprintService _mockBlueprintService;
    private readonly IClientAppValidator _mockClientAppValidator;
    private readonly BlueprintLookupService _mockBlueprintLookupService;
    private readonly FederatedCredentialService _mockFederatedCredentialService;
    private readonly IConfirmationProvider _mockConfirmationProvider;

    public SetupCommandTests()
    {
        _mockLogger = Substitute.For<ILogger<SetupCommand>>();
        _mockConfigService = Substitute.For<IConfigService>();
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        // Full mock — ForPartsOf would fall through to real CommandExecutor.ExecuteAsync and spawn real processes
        _mockExecutor = Substitute.For<CommandExecutor>(mockExecutorLogger);
        _mockExecutor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty }));
        var mockPlatformDetectorLogger = Substitute.For<ILogger<PlatformDetector>>();
        _mockPlatformDetector = Substitute.ForPartsOf<PlatformDetector>(mockPlatformDetectorLogger);
        _mockBackendConfigurator = Substitute.For<ITeamsGraphBackendConfigurator>();
        // Full mock — both virtual methods are always stubbed so the real az CLI is never spawned
        _mockAuthValidator = Substitute.For<AzureAuthValidator>(NullLogger<AzureAuthValidator>.Instance, _mockExecutor);
        _mockAuthValidator.ValidateAuthenticationAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        _mockAuthValidator.GetAppServiceTokenAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        _mockGraphApiService = Substitute.For<GraphApiService>();
        _mockBlueprintService = Substitute.ForPartsOf<AgentBlueprintService>(Substitute.For<ILogger<AgentBlueprintService>>(), _mockGraphApiService);
        _mockClientAppValidator = Substitute.For<IClientAppValidator>();
        _mockBlueprintLookupService = Substitute.ForPartsOf<BlueprintLookupService>(Substitute.For<ILogger<BlueprintLookupService>>(), _mockGraphApiService);
        _mockFederatedCredentialService = Substitute.ForPartsOf<FederatedCredentialService>(Substitute.For<ILogger<FederatedCredentialService>>(), _mockGraphApiService);
        _mockConfirmationProvider = Substitute.For<IConfirmationProvider>();
        _mockConfirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(true);
    }

    [Fact]
    public async Task SetupAllCommand_DryRun_ValidConfig_OnlyValidatesConfig()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "tenant",
            AgentIdentityDisplayName = "agent",
            DeploymentProjectPath = ".",
            AgentBlueprintDisplayName = "TestBlueprint"
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));

        var command = SetupCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockBackendConfigurator,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator, _mockConfirmationProvider);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("all --dry-run", testConsole);

        // Assert
        Assert.Equal(0, result);

        // Dry-run mode loads config to display the plan (real values, not placeholders)
        await _mockConfigService.ReceivedWithAnyArgs(1).LoadAsync(Arg.Any<string>(), Arg.Any<string>());
        // ...but must not call any Azure or Bot services
        await _mockBackendConfigurator.DidNotReceiveWithAnyArgs().SetBackendConfigurationAsync(default!, default!);
    }

    [Fact]
    public async Task SetupAllCommand_WithAgentName_DryRun_SucceedsWithoutConfigFile()
    {
        // Arrange — no config file stub needed; --agent-name bootstrap path skips LoadAsync
        // and also skips the Graph lookup (dry-run only detects tenant)
        _mockExecutor.ExecuteAsync(
                Arg.Is<string>(s => s == "az"),
                Arg.Is<string>(s => s.StartsWith("account show", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult
            {
                ExitCode = 0,
                StandardOutput = "{\"tenantId\":\"dry-run-tenant-id\"}",
                StandardError = string.Empty
            }));

        var command = SetupCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockBackendConfigurator,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator, _mockConfirmationProvider);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("all --agent-name MyAgent --dry-run", testConsole);

        // Assert
        Assert.Equal(0, result);

        // Bootstrap dry-run must not load the config file or call any Azure services
        await _mockConfigService.DidNotReceiveWithAnyArgs().LoadAsync(Arg.Any<string>(), Arg.Any<string>());
        await _mockGraphApiService.DidNotReceiveWithAnyArgs().FindApplicationByDisplayNameAsync(default!, default!, default);
        await _mockBackendConfigurator.DidNotReceiveWithAnyArgs().SetBackendConfigurationAsync(default!, default!);
    }

    [Fact]
    public void SetupCommand_HasRequiredSubcommands()
    {
        // Arrange & Act
        var command = SetupCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockBackendConfigurator,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator, _mockConfirmationProvider);

        // Assert - Verify all required subcommands exist
        var subcommandNames = command.Subcommands.Select(c => c.Name).ToList();

        subcommandNames.Should().Contain("requirements", "Setup should have requirements subcommand");
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
            _mockBackendConfigurator,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator, _mockConfirmationProvider);

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
            _mockBackendConfigurator,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator, _mockConfirmationProvider);

        // Assert - Command structure should support clear error messaging
        command.Should().NotBeNull();
        command.Description.Should().NotBeNullOrEmpty("Setup command should have helpful description");

        foreach (var subcommand in command.Subcommands)
        {
            subcommand.Description.Should().NotBeNullOrEmpty($"Subcommand {subcommand.Name} should have description");
        }
    }

    [Fact]
    public async Task BlueprintSubcommand_DryRun_CompletesSuccessfully()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "tenant",
            AgentIdentityDisplayName = "agent",
            DeploymentProjectPath = ".",
            AgentBlueprintDisplayName = "TestBlueprint"
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));

        var command = SetupCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockBackendConfigurator,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator, _mockConfirmationProvider);

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
            AgentIdentityDisplayName = "agent",
            DeploymentProjectPath = "."
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));

        // requirementChecksOverride: [] — bypass real pwsh/az processes in unit tests
        var command = SetupCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockBackendConfigurator,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockGraphApiService,
            _mockBlueprintService,
            _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator, _mockConfirmationProvider,
            requirementChecksOverride: []);

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
            AgentIdentityDisplayName = "agent",
            DeploymentProjectPath = "."
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));

        // requirementChecksOverride: [] — bypass real pwsh/az processes in unit tests
        var command = SetupCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockBackendConfigurator,
            _mockAuthValidator,
            _mockPlatformDetector,
            _mockGraphApiService,
            _mockBlueprintService,
            _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator, _mockConfirmationProvider,
            requirementChecksOverride: []);

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
