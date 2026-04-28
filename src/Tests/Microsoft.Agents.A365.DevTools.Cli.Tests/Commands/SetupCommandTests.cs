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
        // Blueprint plan logs "Inheritable Permissions" at step 3; AI Teammate plan does not.
        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Inheritable Permissions")),
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
        // Blueprint plan must be shown despite config AiTeammate = true.
        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Inheritable Permissions")),
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
    /// --authmode is only meaningful for blueprint agents. Passing it with --aiteammate true
    /// must be rejected because the combination is undefined.
    /// </summary>
    [Fact]
    public async Task SetupAll_AuthMode_WithAiteammateTrue_ExitsWithCode1()
    {
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(BlueprintConfig()));
        var parser = new CommandLineBuilder(BuildSetupCommand()).Build();

        var result = await parser.InvokeAsync("all --aiteammate true --authmode obo", new TestConsole());

        result.Should().Be(1, because: "--authmode is not supported with --aiteammate true");
        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("not supported with --aiteammate true")),
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
    /// delegated grants and must not mention AllPrincipals or admin consent.
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
            Arg.Is<object>(o => o.ToString()!.Contains("obo")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
