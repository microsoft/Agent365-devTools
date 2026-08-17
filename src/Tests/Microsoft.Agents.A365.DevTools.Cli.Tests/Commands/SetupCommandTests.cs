// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Functional tests for SetupCommand execution.
/// Placed in "ConfigTests" because <see cref="SetupAll_DwBootstrap_WithNullClientAppId_ReturnsExitCode1"/>
/// mutates <see cref="Console.In"/> — a process-global — and must not run in parallel with
/// <see cref="Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers.SetupHelpersBootstrapTests"/>,
/// which does the same.
/// </summary>
[Collection("ConfigTests")]
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

        // requirementChecksOverride: [] — bypass real pwsh/az processes in unit tests.
        // The override path skips config resolution entirely: no LoadAsync call is made.
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

        // Assert — requirements should complete successfully (no failing checks in the override).
        Assert.Equal(0, result);
        // Config must not be loaded — the override path bypasses it entirely.
        await _mockConfigService.DidNotReceiveWithAnyArgs().LoadAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task RequirementsSubcommand_WithCategoryFilter_RunsFilteredChecks()
    {
        // Arrange — requirementChecksOverride: [] bypasses real pwsh/az processes.
        // The override path skips config resolution entirely: no LoadAsync call is made.
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

        // Assert — requirements should complete successfully (no failing checks in the override).
        Assert.Equal(0, result);
        // Config must not be loaded — the override path bypasses it entirely.
        await _mockConfigService.DidNotReceiveWithAnyArgs().LoadAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task RequirementsSubcommand_WithFailingCheck_ReturnsExitCode1()
    {
        // Arrange — supply one failing check via override so no real az/pwsh processes run.
        var failingCheck = Substitute.For<IRequirementCheck>();
        failingCheck.Name.Returns("TestCheck");
        failingCheck.Description.Returns("A failing test check");
        failingCheck.Category.Returns("Test");
        failingCheck.CheckAsync(Arg.Any<Agent365Config>(), Arg.Any<ILogger>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RequirementCheckResult.Failure("test failure", "fix it")));

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
            requirementChecksOverride: [failingCheck]);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("requirements", testConsole);

        // Assert — a failing check must propagate exit code 1.
        Assert.Equal(1, result);
    }

    /// <summary>
    /// Verifies that <c>setup all --agent-name</c> in DW bootstrap mode exits with code 1
    /// when the client app ID cannot be resolved (Entra lookup returns null, user cancels prompt).
    /// Guards against the regression where setup would continue with an empty <c>ClientAppId</c>.
    /// </summary>
    [Fact]
    public async Task SetupAll_DwBootstrap_WithNullClientAppId_ReturnsExitCode1()
    {
        // Arrange — bootstrap mode (--agent-name without --aiteammate false), no dry-run.
        _mockExecutor.ExecuteAsync(
                Arg.Is<string>(s => s == "az"),
                Arg.Is<string>(s => s.StartsWith("account show", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult
            {
                ExitCode = 0,
                StandardOutput = "{\"tenantId\":\"bootstrap-tenant-id\"}",
                StandardError = string.Empty
            }));

        // NSubstitute returns null by default for Task<string?> — FindApplicationByDisplayNameAsync
        // returns null, simulating the app not existing in Entra.

        var command = SetupCommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockExecutor, _mockBackendConfigurator,
            _mockAuthValidator, _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService,
            _mockFederatedCredentialService, _mockClientAppValidator, _mockConfirmationProvider,
            requirementChecksOverride: []);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        var originalIn = Console.In;
        Console.SetIn(new StringReader("\n")); // user presses Enter to cancel the client app ID prompt
        try
        {
            // Act
            var result = await parser.InvokeAsync("all --agent-name TestAgent", testConsole);

            // Assert
            result.Should().Be(1,
                because: "DW bootstrap must abort with exit code 1 when the client app ID " +
                         "cannot be resolved, rather than continuing setup with an empty ClientAppId");
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    /// <summary>
    /// Verifies that bare <c>--aiteammate</c> (no value) routes to the AI Teammate (DW) plan
    /// even when <c>a365.config.json</c> has <c>AiTeammate = false</c>.
    /// Catches regressions where <c>FindResultFor</c> always returns null, which would cause
    /// the flag to be treated as "not set" and the config value to take precedence.
    /// </summary>
    [Fact]
    public async Task SetupAll_WithBareAiteammate_RoutesToAiTeammateDryRunPlan()
    {
        // Arrange — config says blueprint agent; bare --aiteammate must override to AI Teammate.
        var config = new Agent365Config
        {
            TenantId = "tenant",
            AgentIdentityDisplayName = "agent",
            AgentBlueprintDisplayName = "TestBlueprint",
            DeploymentProjectPath = ".",
            AiTeammate = false
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));

        var command = SetupCommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockExecutor, _mockBackendConfigurator,
            _mockAuthValidator, _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService,
            _mockFederatedCredentialService, _mockClientAppValidator, _mockConfirmationProvider);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("all --aiteammate --dry-run", testConsole);

        // Assert
        result.Should().Be(0, because: "bare --aiteammate is a valid flag and dry-run exits 0");
        // AI Teammate (DW) plan logs "Azure hosting" at step 2.
        // Blueprint plan uses "Blueprint" at step 2 and never logs "Azure hosting".
        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Azure hosting")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// Verifies that omitting <c>--aiteammate</c> respects <c>AiTeammate = false</c> from config
    /// and shows the blueprint plan (not the AI Teammate plan).
    /// </summary>
    [Fact]
    public async Task SetupAll_WithAiteammateOmitted_RespectsConfigBlueprintFlag()
    {
        // Arrange — blueprint agent config; no flag means "respect config" → blueprint plan.
        var config = new Agent365Config
        {
            TenantId = "tenant",
            AgentIdentityDisplayName = "agent",
            AgentBlueprintDisplayName = "TestBlueprint",
            DeploymentProjectPath = ".",
            AiTeammate = false
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));

        var command = SetupCommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockExecutor, _mockBackendConfigurator,
            _mockAuthValidator, _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService,
            _mockFederatedCredentialService, _mockClientAppValidator, _mockConfirmationProvider);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act
        var result = await parser.InvokeAsync("all --dry-run", testConsole);

        // Assert
        result.Should().Be(0);
        // Both blueprint and AI Teammate plans now use the unified "Inheritable Permissions" label,
        // so use the non-DW-only "Agent Registration" step as the distinguishing marker (AI Teammate
        // plan has no Agent Registration row).
        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Agent Registration")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        // AI Teammate plan's distinctive "Azure hosting" step must be absent.
        _mockLogger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Azure hosting")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// Verifies that explicit <c>--aiteammate false</c> forces the blueprint plan even when
    /// <c>a365.config.json</c> has <c>AiTeammate = true</c>.
    /// Catches regressions where <c>FindResultFor</c> always returns null, which would cause
    /// <c>--aiteammate false</c> to be treated as "not set" and let <c>AiTeammate = true</c>
    /// route to the AI Teammate plan instead.
    /// </summary>
    [Fact]
    public async Task SetupAll_WithAiteammateFalse_ForcesBlueprintPlanRegardlessOfConfig()
    {
        // Arrange — config says AI Teammate; explicit --aiteammate false must override to blueprint.
        var config = new Agent365Config
        {
            TenantId = "tenant",
            AgentIdentityDisplayName = "agent",
            AgentBlueprintDisplayName = "TestBlueprint",
            DeploymentProjectPath = ".",
            AiTeammate = true  // config says AI Teammate
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(config));

        var command = SetupCommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockExecutor, _mockBackendConfigurator,
            _mockAuthValidator, _mockPlatformDetector,
            _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService,
            _mockFederatedCredentialService, _mockClientAppValidator, _mockConfirmationProvider);

        var parser = new CommandLineBuilder(command).Build();
        var testConsole = new TestConsole();

        // Act — explicit false overrides config's AiTeammate = true
        var result = await parser.InvokeAsync("all --aiteammate false --dry-run", testConsole);

        // Assert
        result.Should().Be(0, because: "--aiteammate false is a valid parse and dry-run exits 0");
        // Both blueprint and AI Teammate plans now use the unified "Inheritable Permissions" label,
        // so use the non-DW-only "Agent Registration" step as the distinguishing marker.
        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Agent Registration")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        _mockLogger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Azure hosting")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ── --authmode parameter validation ───────────────────────────────────────

    private Command BuildSetupCommand() => SetupCommand.CreateCommand(
        _mockLogger, _mockConfigService, _mockExecutor, _mockBackendConfigurator,
        _mockAuthValidator, _mockPlatformDetector,
        _mockGraphApiService, _mockBlueprintService, _mockBlueprintLookupService,
        _mockFederatedCredentialService, _mockClientAppValidator, _mockConfirmationProvider);

    private Agent365Config BlueprintConfig() => new()
    {
        TenantId = "tenant",
        AgentIdentityDisplayName = "agent",
        AgentBlueprintDisplayName = "TestBlueprint",
        DeploymentProjectPath = ".",
        AiTeammate = false,
        UseBlueprint = true,
    };

    /// <summary>
    /// An unrecognised --authmode value must be rejected before any Azure calls are made.
    /// </summary>
    [Fact]
    public async Task SetupAll_AuthMode_InvalidValue_ExitsWithCode1()
    {
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(BlueprintConfig()));
        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync("all --aiteammate false --authmode invalid", new TestConsole());

        result.Should().Be(1, because: "an unrecognised --authmode value must abort before touching Azure");
        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Invalid --authmode value")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// --authmode obo with --aiteammate is redundant (obo is the AI Teammate default) but not
    /// conflicting. It must emit a warning and continue — not exit with an error before setup runs.
    /// --dry-run is used to avoid hitting real Azure auth in the test environment.
    /// </summary>
    [Fact]
    public async Task SetupAll_AuthMode_Obo_WithAiteammateTrue_WarnsAndContinues()
    {
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(BlueprintConfig()));
        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync("all --aiteammate true --authmode obo --dry-run", new TestConsole());

        result.Should().Be(0, because: "--authmode obo with --aiteammate must warn and continue, not exit with error");
        _mockLogger.DidNotReceive().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("not supported with --aiteammate")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("redundant with --aiteammate")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// --authmode s2s and --authmode both are incompatible with --aiteammate because AI Teammate
    /// agents always use OBO via agent user identity. These combinations must exit with code 1.
    /// </summary>
    [Theory]
    [InlineData("s2s")]
    [InlineData("both")]
    public async Task SetupAll_AuthMode_NonObo_WithAiteammateTrue_ExitsWithCode1(string authMode)
    {
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(BlueprintConfig()));
        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync($"all --aiteammate true --authmode {authMode}", new TestConsole());

        result.Should().Be(1,
            because: $"--authmode {authMode} is incompatible with --aiteammate — AI Teammate agents always use OBO");
        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("not supported with --aiteammate")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>All three valid authMode values must be accepted without error.</summary>
    [Theory]
    [InlineData("obo")]
    [InlineData("s2s")]
    [InlineData("both")]
    [InlineData("OBO")]   // case-insensitive
    [InlineData("S2S")]
    [InlineData("BOTH")]
    public async Task SetupAll_AuthMode_ValidValues_ExitWithCode0(string authModeValue)
    {
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(BlueprintConfig()));
        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync($"all --aiteammate false --authmode {authModeValue} --dry-run", new TestConsole());

        result.Should().Be(0, because: $"'{authModeValue}' is a valid --authmode value and --dry-run exits 0");
    }

    /// <summary>
    /// Omitting --authmode defaults to OBO behaviour — the dry-run plan must show
    /// delegated grants (step 4 'Blueprint Permission Grants') and must not show S2S app role grants.
    /// </summary>
    [Fact]
    public async Task SetupAll_NoAuthMode_DefaultsToOboBehaviour()
    {
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(BlueprintConfig()));
        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync("all --aiteammate false --dry-run", new TestConsole());

        result.Should().Be(0, because: "omitting --authmode is valid and dry-run exits 0");
        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Blueprint Permission Grants") && o.ToString()!.Contains("delegated")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        _mockLogger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("S2S")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ── --blueprint-id / --select-blueprint (setup all) ────────────────────────

    /// <summary>
    /// --blueprint-id and --select-blueprint are mutually exclusive. Must fail before any mutation
    /// (no tenant/Graph calls needed to detect this — pure option validation).
    /// </summary>
    [Fact]
    public async Task SetupAll_BlueprintIdAndSelectBlueprint_MutuallyExclusive_ExitsWithCode1()
    {
        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync(
            "all --agent-name TestAgent --blueprint-id 11111111-1111-1111-1111-111111111111 --select-blueprint --dry-run",
            new TestConsole());

        result.Should().Be(1, because: "--blueprint-id and --select-blueprint are mutually exclusive and must fail before any mutation");
        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("cannot be used together")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        await _mockExecutor.DidNotReceive().ExecuteAsync(
            Arg.Is<string>(s => s == "az"), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A malformed --blueprint-id value must be rejected before any tenant/Graph call.</summary>
    [Fact]
    public async Task SetupAll_BlueprintIdInvalidGuid_ExitsWithCode1()
    {
        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync("all --agent-name TestAgent --blueprint-id not-a-guid --dry-run", new TestConsole());

        result.Should().Be(1, because: "--blueprint-id must be a valid GUID");
        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Invalid --blueprint-id value")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task SetupAll_BlueprintIdWhitespace_ExitsWithCode1()
    {
        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync("all --agent-name TestAgent --blueprint-id \" \" --dry-run", new TestConsole());

        result.Should().Be(1,
            because: "an explicitly supplied whitespace blueprint ID must not silently use the default create flow");
        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("--blueprint-id cannot be empty")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>Explicit blueprint selection is unsupported for AI Teammate agents — must fail fast.</summary>
    [Theory]
    [InlineData("--blueprint-id 11111111-1111-1111-1111-111111111111")]
    [InlineData("--select-blueprint")]
    public async Task SetupAll_ExplicitBlueprintSelection_WithAiteammateTrue_ExitsWithCode1(string blueprintOption)
    {
        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync($"all --agent-name TestAgent --aiteammate true {blueprintOption} --dry-run", new TestConsole());

        result.Should().Be(1, because: "explicit blueprint selection applies to blueprint agents only, not AI Teammate agents");
        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("not supported with --aiteammate")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetupAll_BlueprintId_WithExistingAiTeammateConfig_ExitsWithCode1(bool dryRun)
    {
        var config = new Agent365Config
        {
            TenantId = "tenant",
            AgentIdentityDisplayName = "agent",
            AgentBlueprintDisplayName = "TestBlueprint",
            DeploymentProjectPath = ".",
            AiTeammate = true,
            UseBlueprint = false,
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);
        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();
        var dryRunOption = dryRun ? " --dry-run" : string.Empty;

        var result = await parser.InvokeAsync(
            $"all --blueprint-id 11111111-1111-1111-1111-111111111111{dryRunOption}",
            new TestConsole());

        result.Should().Be(1,
            because: "explicit blueprint selection must be rejected when the loaded config belongs to an AI Teammate");
        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("applies only to blueprint agents")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        await _mockConfigService.DidNotReceiveWithAnyArgs().SaveStateAsync(
            Arg.Any<Agent365Config>(),
            Arg.Any<string>());
    }

    /// <summary>
    /// A syntactically valid --blueprint-id that does not resolve to an existing Agent Identity
    /// Blueprint in the active tenant (not found, or belongs to a different tenant) must fail before
    /// any mutation — no a365.config.json / a365.generated.config.json is written.
    /// </summary>
    [Fact]
    public async Task SetupAll_BlueprintId_NotFoundInTenant_ExitsWithCode1_AndWritesNoState()
    {
        _mockExecutor.ExecuteAsync(
                Arg.Is<string>(s => s == "az"),
                Arg.Is<string>(s => s.StartsWith("account show", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult
            {
                ExitCode = 0,
                StandardOutput = "{\"tenantId\":\"blueprint-lookup-tenant\"}",
                StandardError = string.Empty
            }));
        _mockGraphApiService.FindApplicationByDisplayNameAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("99999999-9999-9999-9999-999999999999");
        _mockBlueprintLookupService.GetBlueprintByAppIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new BlueprintLookupResult { Found = false });

        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync(
            "all --agent-name TestAgent --blueprint-id 22222222-2222-2222-2222-222222222222",
            new TestConsole());

        result.Should().Be(1,
            because: "an unresolvable --blueprint-id must fail — verifying tenant membership happens before any mutation");
        await _mockConfigService.DidNotReceiveWithAnyArgs().SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SetupAll_BlueprintIdResolvesSuccessfully_PersistsBootstrapSelection()
    {
        const string tenantId = "11111111-1111-1111-1111-111111111111";
        const string clientAppId = "22222222-2222-2222-2222-222222222222";
        const string blueprintAppId = "33333333-3333-3333-3333-333333333333";
        const string blueprintObjectId = "44444444-4444-4444-4444-444444444444";
        const string blueprintDisplayName = "Tenant Blueprint";
        var tempDir = Path.Combine(Path.GetTempPath(), $"a365-setupall-blueprintid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDir = Environment.CurrentDirectory;

        try
        {
            Environment.CurrentDirectory = tempDir;
            _mockExecutor.ExecuteAsync(
                    Arg.Is<string>(command => command == "az"),
                    Arg.Is<string>(arguments => arguments.StartsWith("account show", StringComparison.OrdinalIgnoreCase)),
                    Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult
                {
                    ExitCode = 0,
                    StandardOutput = tenantId,
                    StandardError = string.Empty
                }));
            _mockGraphApiService.FindApplicationByDisplayNameAsync(
                    tenantId,
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(clientAppId);
            _mockBlueprintLookupService.GetBlueprintByAppIdAsync(
                    tenantId,
                    blueprintAppId,
                    Arg.Any<CancellationToken>())
                .Returns(new BlueprintLookupResult
                {
                    Found = true,
                    AppId = blueprintAppId,
                    ObjectId = blueprintObjectId,
                    DisplayName = blueprintDisplayName
                });
            _mockBlueprintService.FindExistingAgentIdentityAsync(
                    tenantId,
                    blueprintAppId,
                    "TestAgent Identity",
                    Arg.Any<CancellationToken>())
                .Returns("agentic-app-id");
            _mockGraphApiService.RegisterAgentInstanceAsyncV2(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(("registration-id", false));
            _mockClientAppValidator.GetUnconsentedRequiredPermissionsAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns([]);

            var parser = new CommandLineBuilder(BuildSetupCommand()).Build();
            var result = await parser.InvokeAsync(
                $"all --agent-name TestAgent --blueprint-id {blueprintAppId} --agent-registration-only",
                new TestConsole());

            result.Should().Be(0,
                because: "a tenant-verified blueprint must be usable for a new agent identity");
            using var staticConfig = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(tempDir, "a365.config.json")));
            staticConfig.RootElement.GetProperty("agentBlueprintDisplayName").GetString().Should().Be(
                blueprintDisplayName,
                because: "the tenant-verified display name must become the static source of truth");
            await _mockConfigService.Received().SaveStateAsync(
                Arg.Is<Agent365Config>(candidate =>
                    candidate.AgentBlueprintId == blueprintAppId &&
                    candidate.AgentBlueprintObjectId == blueprintObjectId),
                Arg.Is<string>(path => path == Path.Combine(tempDir, "a365.generated.config.json")));
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SetupAll_ExistingConfig_BlueprintId_PersistsRefreshedBlueprintMetadata()
    {
        const string tenantId = "11111111-1111-1111-1111-111111111111";
        const string clientAppId = "22222222-2222-2222-2222-222222222222";
        const string blueprintAppId = "33333333-3333-3333-3333-333333333333";
        const string blueprintObjectId = "44444444-4444-4444-4444-444444444444";
        const string blueprintDisplayName = "Tenant Blueprint";
        const string agenticAppId = "66666666-6666-6666-6666-666666666666";
        const string registrationId = "77777777-7777-7777-7777-777777777777";
        var tempDir = Path.Combine(Path.GetTempPath(), $"a365-setupall-existing-blueprint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "a365.config.json");
        var generatedPath = Path.Combine(tempDir, "a365.generated.config.json");
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "tenantId": "{{tenantId}}",
              "clientAppId": "{{clientAppId}}",
              "agentIdentityDisplayName": "TestAgent Identity",
              "agentBlueprintDisplayName": "Stale Blueprint Name",
              "agentDescription": "TestAgent",
              "aiTeammate": false,
              "useBlueprint": true
            }
            """);
        await File.WriteAllTextAsync(generatedPath, $$"""
            {
              "agentBlueprintId": "{{blueprintAppId}}",
              "agentBlueprintObjectId": "55555555-5555-5555-5555-555555555555",
              "agenticAppId": "{{agenticAppId}}",
              "agentRegistrationId": "{{registrationId}}"
            }
            """);
        var originalDir = Environment.CurrentDirectory;

        try
        {
            Environment.CurrentDirectory = tempDir;
            _mockBlueprintLookupService.GetBlueprintByAppIdAsync(
                    tenantId,
                    blueprintAppId,
                    Arg.Any<CancellationToken>())
                .Returns(new BlueprintLookupResult
                {
                    Found = true,
                    AppId = blueprintAppId,
                    ObjectId = blueprintObjectId,
                    DisplayName = blueprintDisplayName
                });
            _mockGraphApiService.AgentRegistrationExistsAsync(
                    tenantId,
                    registrationId,
                    Arg.Any<CancellationToken>())
                .Returns(true);
            _mockClientAppValidator.GetUnconsentedRequiredPermissionsAsync(
                    clientAppId,
                    tenantId,
                    Arg.Any<CancellationToken>())
                .Returns([]);
            var configService = new ConfigService();
            var command = SetupCommand.CreateCommand(
                _mockLogger, configService, _mockExecutor, _mockBackendConfigurator,
                _mockAuthValidator, _mockPlatformDetector, _mockGraphApiService, _mockBlueprintService,
                _mockBlueprintLookupService, _mockFederatedCredentialService, _mockClientAppValidator,
                _mockConfirmationProvider);
            var parser = new CommandLineBuilder(command).Build();

            var result = await parser.InvokeAsync(
                $"all --blueprint-id {blueprintAppId} --agent-registration-only",
                new TestConsole());

            result.Should().Be(0,
                because: "reselecting the same blueprint must be an idempotent successful rerun");
            using var staticConfig = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            staticConfig.RootElement.GetProperty("agentBlueprintDisplayName").GetString().Should().Be(
                blueprintDisplayName,
                because: "the static config must reflect the tenant-verified blueprint name");
            using var generatedConfig = JsonDocument.Parse(await File.ReadAllTextAsync(generatedPath));
            generatedConfig.RootElement.GetProperty("agentBlueprintObjectId").GetString().Should().Be(
                blueprintObjectId,
                because: "the stable tenant-verified object ID must replace stale cached metadata");
            generatedConfig.RootElement.GetProperty("agenticAppId").GetString().Should().Be(
                agenticAppId,
                because: "reselecting the same blueprint for the same identity must preserve identity state");
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// A working directory configured for another identity must be rejected before mutation.
    /// </summary>
    [Fact]
    public async Task SetupAll_AgentNameDiffersFromExistingDirectoryIdentity_ExitsWithCode1_AndLeavesDirectoryUntouched()
    {
        const string tenantId = "matching-tenant-id";
        _mockExecutor.ExecuteAsync(
                Arg.Is<string>(s => s == "az"),
                Arg.Is<string>(s => s.StartsWith("account show", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult
            {
                ExitCode = 0,
                StandardOutput = tenantId,
                StandardError = string.Empty
            }));
        _mockGraphApiService.FindApplicationByDisplayNameAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("11111111-1111-1111-1111-111111111111");

        var tempDir = Path.Combine(Path.GetTempPath(), $"a365-setupall-identity-mismatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "a365.config.json");
        var generatedPath = Path.Combine(tempDir, "a365.generated.config.json");
        await File.WriteAllTextAsync(configPath,
            $"{{\"tenantId\":\"{tenantId}\",\"agentIdentityDisplayName\":\"AgentA Identity\",\"agentBlueprintDisplayName\":\"AgentA Blueprint\"}}");
        await File.WriteAllTextAsync(generatedPath,
            "{\"agenticAppId\":\"agent-a-agentic-app-id\",\"agentRegistrationId\":\"agent-a-registration-id\"}");

        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = tempDir;

            var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

            var result = await parser.InvokeAsync("all --agent-name AgentB", new TestConsole());

            result.Should().Be(1,
                because: "the current config format supports only one agent identity per working directory");
            _mockLogger.Received().Log(
                LogLevel.Error,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("AgentA Identity") && o.ToString()!.Contains("only one agent identity per working directory")),
                Arg.Any<Exception?>(),
                Arg.Any<Func<object, Exception?, string>>());
            (await File.ReadAllTextAsync(configPath)).Should().Contain("AgentA Identity",
                because: "the prior agent's static config must be left completely untouched, not silently overwritten or merged");
            (await File.ReadAllTextAsync(generatedPath)).Should().Contain("agent-a-agentic-app-id",
                because: "the prior agent's generated config/state must be left completely untouched — refusing must happen before any mutation");
            await _mockConfigService.DidNotReceiveWithAnyArgs().SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>());
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── setup blueprint list ────────────────────────────────────────────────────

    private static void MockAzAccountShow(CommandExecutor executor, string tenantId) =>
        executor.ExecuteAsync(
                Arg.Is<string>(s => s == "az"),
                Arg.Is<string>(s => s.StartsWith("account show", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult
            {
                ExitCode = 0,
                StandardOutput = $"{{\"tenantId\":\"{tenantId}\"}}",
                StandardError = string.Empty
            }));

    [Fact]
    public async Task BlueprintList_WhenBlueprintsExist_ExitsWithCode0AndListsThem()
    {
        MockAzAccountShow(_mockExecutor, "list-tenant");
        _mockBlueprintLookupService.ListBlueprintsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<BlueprintLookupResult>
            {
                new() { Found = true, AppId = "11111111-1111-1111-1111-111111111111", DisplayName = "Contoso Blueprint" }
            });

        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync("blueprint list", new TestConsole());

        result.Should().Be(0, because: "listing blueprints is read-only and must succeed when the query succeeds");
        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Contoso Blueprint") && o.ToString()!.Contains("11111111-1111-1111-1111-111111111111")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task BlueprintList_WhenTenantHasNoBlueprints_ExitsWithCode0AndShowsClearMessage()
    {
        MockAzAccountShow(_mockExecutor, "empty-tenant");
        _mockBlueprintLookupService.ListBlueprintsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<BlueprintLookupResult>());

        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync("blueprint list", new TestConsole());

        result.Should().Be(0, because: "an empty list is a successful outcome, not an error");
        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("No Agent Identity Blueprints found")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task BlueprintList_WhenGraphQueryFails_ExitsWithCode1()
    {
        MockAzAccountShow(_mockExecutor, "failing-tenant");
        _mockBlueprintLookupService.ListBlueprintsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<BlueprintLookupResult>>(new InvalidOperationException("Graph auth failed")));

        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync("blueprint list", new TestConsole());

        result.Should().Be(1, because: "an auth/query failure must exit non-zero rather than silently reporting an empty list as success");
    }

    [Fact]
    public async Task BlueprintList_WhenTenantIdIsWhitespace_ExitsWithCode1WithoutQueryingAzure()
    {
        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync("blueprint list --tenant-id \" \"", new TestConsole());

        result.Should().Be(1,
            because: "an explicitly supplied whitespace tenant ID must produce a targeted validation error");
        await _mockExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(
            default!, default!, default, default, default, default);
        await _mockBlueprintLookupService.DidNotReceiveWithAnyArgs().ListBlueprintsAsync(default!, default);
    }

    [Fact]
    public async Task BlueprintList_WhenTenantCannotBeAutoDetected_ExitsWithCode1()
    {
        _mockExecutor.ExecuteAsync(
                Arg.Is<string>(command => command == "az"),
                Arg.Is<string>(arguments => arguments.StartsWith("account show", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult
            {
                ExitCode = 1,
                StandardOutput = string.Empty,
                StandardError = "not logged in"
            }));
        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync("blueprint list", new TestConsole());

        result.Should().Be(1,
            because: "listing cannot query a tenant until the user signs in or supplies --tenant-id");
        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("Could not detect tenant ID")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        await _mockBlueprintLookupService.DidNotReceiveWithAnyArgs().ListBlueprintsAsync(default!, default);
    }
}
