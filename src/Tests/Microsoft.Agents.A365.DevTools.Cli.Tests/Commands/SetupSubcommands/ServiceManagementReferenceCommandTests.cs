// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using System.Net;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands.SetupSubcommands;

/// <summary>
/// Invocation tests for --service-management-reference on 'setup blueprint' and 'setup all': validation,
/// dry-run output, and forwarding of the value (or the serviceManagementReference config key) into blueprint creation.
/// Runs in the ConfigTests collection because the setup handlers probe a365 config files in the working directory.
/// </summary>
[Collection("ConfigTests")]
public class ServiceManagementReferenceCommandTests
{
    private const string Option = "--service-management-reference";
    private const string ReferenceId = "6f0e5d8a-3b1c-4c2d-9e7f-1a2b3c4d5e6f";
    private const string ConfigReferenceId = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";

    private readonly ILogger _logger = Substitute.For<ILogger>();
    private readonly IConfigService _configService = Substitute.For<IConfigService>();
    private readonly IBootstrapConfigResolver _resolver = Substitute.For<IBootstrapConfigResolver>();
    private readonly ITeamsGraphBackendConfigurator _backendConfigurator = Substitute.For<ITeamsGraphBackendConfigurator>();
    private readonly IClientAppValidator _clientAppValidator = Substitute.For<IClientAppValidator>();
    private readonly CommandExecutor _executor;
    private readonly AzureAuthValidator _authValidator;
    private readonly PlatformDetector _platformDetector;
    private readonly GraphApiService _graphApiService;
    private readonly AgentBlueprintService _blueprintService;
    private readonly BlueprintLookupService _blueprintLookupService;
    private readonly FederatedCredentialService _federatedCredentialService;

    private readonly List<BlueprintCreationOptions> _capturedOptions = new();
    private BlueprintCreationResult _creationResult = new()
    {
        BlueprintCreated = false,
        Failure = new BlueprintCreationFailure(
            BlueprintCreationFailureKind.ServiceManagementReference,
            "ServiceManagementReference field is required for Create, but is missing in the request.",
            400,
            "Request_BadRequest"),
    };

    public ServiceManagementReferenceCommandTests()
    {
        // Full mock: ForPartsOf would fall through to real CommandExecutor.ExecuteAsync and spawn real processes
        _executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        _executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty }));
        _authValidator = Substitute.For<AzureAuthValidator>(NullLogger<AzureAuthValidator>.Instance, _executor);
        _platformDetector = Substitute.ForPartsOf<PlatformDetector>(Substitute.For<ILogger<PlatformDetector>>());
        _graphApiService = Substitute.ForPartsOf<GraphApiService>(
            Substitute.For<ILogger<GraphApiService>>(), _executor, (Func<Task<string?>>)(() => Task.FromResult<string?>(null)));
        _blueprintService = Substitute.ForPartsOf<AgentBlueprintService>(Substitute.For<ILogger<AgentBlueprintService>>(), _graphApiService);
        _blueprintLookupService = Substitute.ForPartsOf<BlueprintLookupService>(Substitute.For<ILogger<BlueprintLookupService>>(), _graphApiService);
        _federatedCredentialService = Substitute.ForPartsOf<FederatedCredentialService>(Substitute.For<ILogger<FederatedCredentialService>>(), _graphApiService);
    }

    public static TheoryData<string> InvalidReferences => new() { "", "   ", "not-a-guid", "00000000-0000-0000-0000-000000000000" };

    // --- Validation ---

    [Theory]
    [MemberData(nameof(InvalidReferences))]
    public async Task SetupBlueprint_InvalidReference_ExitsOneBeforeSetupStarts(string value)
    {
        var exitCode = await BuildParser(BuildSetupBlueprintCommand()).InvokeAsync(
            new[] { Option, value }, new TestConsole());

        exitCode.Should().Be(1,
            because: "an empty or malformed serviceManagementReference must be rejected locally instead of being dropped and failing later in Graph");
        await _configService.DidNotReceiveWithAnyArgs().LoadAsync(default!, default!);
        _capturedOptions.Should().BeEmpty(because: "blueprint creation must not start with an unusable serviceManagementReference");
        AssertLogged(LogLevel.Error, Option);
    }

    [Theory]
    [MemberData(nameof(InvalidReferences))]
    public async Task SetupAll_InvalidReference_ExitsOneBeforeSetupStarts(string value)
    {
        var exitCode = await BuildParser(BuildSetupAllCommand(_resolver)).InvokeAsync(
            new[] { "--agent-name", "Contoso", Option, value }, new TestConsole());

        exitCode.Should().Be(1,
            because: "an empty or malformed serviceManagementReference must be rejected locally instead of being dropped and failing later in Graph");
        await _resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default, default, default!, default, default);
        await _configService.DidNotReceiveWithAnyArgs().LoadAsync(default!, default!);
        _capturedOptions.Should().BeEmpty(because: "blueprint creation must not start with an unusable serviceManagementReference");
        AssertLogged(LogLevel.Error, Option);
    }

    // --- Dry run ---

    [Fact]
    public async Task SetupBlueprint_DryRun_ShowsNormalizedReference()
    {
        _configService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(BlueprintAgentConfig());

        var exitCode = await BuildParser(BuildSetupBlueprintCommand()).InvokeAsync(
            new[] { "--dry-run", Option, ReferenceId.ToUpperInvariant() }, new TestConsole());

        exitCode.Should().Be(0);
        AssertLogged(LogLevel.Information, $"serviceManagementReference: {ReferenceId}");
        _capturedOptions.Should().BeEmpty(because: "a dry run must not create the blueprint");
    }

    [Fact]
    public async Task SetupBlueprint_DryRunWithExistingBlueprint_OmitsReferenceRow()
    {
        var config = BlueprintAgentConfig(ConfigReferenceId);
        config.AgentBlueprintId = "existing-blueprint-app-id";
        _configService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);

        var exitCode = await BuildParser(BuildSetupBlueprintCommand()).InvokeAsync(
            new[] { "--dry-run", Option, ReferenceId }, new TestConsole());

        exitCode.Should().Be(0);
        AssertNotLogged(LogLevel.Information, "serviceManagementReference:",
            because: "an existing blueprint is reused unchanged, so the plan must not promise to set serviceManagementReference");
    }

    [Fact]
    public async Task SetupAll_BlueprintAgentDryRun_ShowsReferenceOnBlueprintStep()
    {
        _configService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(BlueprintAgentConfig());

        var exitCode = await BuildParser(BuildSetupAllCommand()).InvokeAsync(
            new[] { "--aiteammate", "false", "--dry-run", Option, ReferenceId }, new TestConsole());

        exitCode.Should().Be(0);
        AssertLogged(LogLevel.Information, $"set serviceManagementReference: {ReferenceId}");
    }

    [Fact]
    public async Task SetupAll_AiTeammateDryRun_ShowsReferenceFromConfig()
    {
        _configService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(AiTeammateConfig(ConfigReferenceId));

        var exitCode = await BuildParser(BuildSetupAllCommand()).InvokeAsync(
            new[] { "--aiteammate", "--dry-run" }, new TestConsole());

        exitCode.Should().Be(0);
        AssertLogged(LogLevel.Information, $"set serviceManagementReference: {ConfigReferenceId}");
    }

    [Fact]
    public async Task SetupAll_AiTeammateBootstrapDryRun_IgnoresReferenceFromConfigFile()
    {
        _configService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(AiTeammateConfig(ConfigReferenceId));

        var exitCode = await BuildParser(BuildSetupAllCommand()).InvokeAsync(
            new[] { "--aiteammate", "--agent-name", "Contoso", "--dry-run" }, new TestConsole());

        exitCode.Should().Be(0);
        AssertNotLogged(LogLevel.Information, "set serviceManagementReference",
            because: "with --agent-name the real run does not read a365.config.json, so the plan must not promise its value");
    }

    [Fact]
    public async Task SetupAll_DryRunWithoutReference_PlanHasNoReferenceRow()
    {
        _configService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(BlueprintAgentConfig());

        var exitCode = await BuildParser(BuildSetupAllCommand()).InvokeAsync(
            new[] { "--aiteammate", "false", "--dry-run" }, new TestConsole());

        exitCode.Should().Be(0);
        AssertNotLogged(LogLevel.Information, "set serviceManagementReference",
            because: "without a value the plan is unchanged");
    }

    // --- setup all forwarding ---

    [Fact]
    public async Task SetupAll_BootstrapWithReference_ReachesBlueprintCreationOptionsAndGraphPayload()
    {
        _resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<FileInfo>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(BlueprintAgentConfig());

        var exitCode = await BuildParser(BuildSetupAllCommand(_resolver)).InvokeAsync(
            new[] { "--agent-name", "Contoso", Option, ReferenceId.ToUpperInvariant(), "--skip-requirements" },
            new TestConsole());

        exitCode.Should().Be(1, because: "the injected blueprint step reports a failure, which must fail setup");
        var options = _capturedOptions.Should().ContainSingle().Subject;
        options.ServiceManagementReference.Should().Be(ReferenceId,
            because: "--service-management-reference must reach blueprint creation in --agent-name mode, normalized to the canonical GUID");
        options.DeferConsent.Should().BeTrue(because: "setup all runs consent in its own batch permissions step");

        // The options that setup all produced must put serviceManagementReference on the Graph create request.
        using var handler = new BlueprintApplicationCreateTests.RecordingGraphHandler()
            .Respond(HttpStatusCode.Created, """{"id":"object-id","appId":"app-id"}""");
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        await BlueprintSubcommand.CreateBlueprintApplicationAsync(
            httpClient, "https://graph.microsoft.com", "Contoso Blueprint", sponsorUserId: null, options, NullLogger.Instance, CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.Body["serviceManagementReference"]!.GetValue<string>().Should().Be(ReferenceId,
            because: "the value passed to setup all is what Graph receives as serviceManagementReference");
    }

    [Theory]
    [InlineData(null, ConfigReferenceId)]
    [InlineData(ReferenceId, ReferenceId)]
    public async Task SetupAll_ConfigMode_ReferenceFlagOverridesConfigKey(string? flag, string expected)
    {
        _configService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(BlueprintAgentConfig(ConfigReferenceId));

        var exitCode = await BuildParser(BuildSetupAllCommand()).InvokeAsync(
            WithReference(flag, "--aiteammate", "false", "--skip-requirements"), new TestConsole());

        exitCode.Should().Be(1);
        _capturedOptions.Should().ContainSingle().Which.ServiceManagementReference.Should().Be(expected,
            because: "serviceManagementReference in a365.config.json applies when the option is omitted, and the option overrides it");
    }

    [Fact]
    public async Task SetupAll_AiTeammate_ForwardsReferenceToBlueprintStep()
    {
        _configService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(AiTeammateConfig());

        var act = () => BuildParser(BuildSetupAllCommand()).InvokeAsync(
            new[] { "--aiteammate", Option, ReferenceId, "--skip-requirements" }, new TestConsole());

        (await act.Should().ThrowAsync<CleanExitException>()).Which.ExitCode.Should().Be(1,
            because: "AI Teammate setup exits with code 1 when the blueprint step fails");
        _capturedOptions.Should().ContainSingle().Which.ServiceManagementReference.Should().Be(ReferenceId,
            because: "--service-management-reference must reach blueprint creation with --aiteammate as well");
    }

    [Fact]
    public async Task SetupAll_AiTeammateBootstrap_ForwardsReferenceToBlueprintStep()
    {
        // Full mock so the first-party client app lookup never reaches Graph.
        var graphApiService = Substitute.For<GraphApiService>();
        graphApiService.LookupServicePrincipalByAppIdWithResponseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.ServicePrincipalLookupResult { IsSuccess = true, StatusCode = 200, ServicePrincipalId = "first-party-sp-id" });

        var act = () => BuildParser(BuildSetupAllCommand(_resolver, graphApiService)).InvokeAsync(
            new[]
            {
                "--aiteammate", "--agent-name", "Contoso", "--tenant-id", "00000000-0000-0000-0000-000000000001",
                "--m365", Option, ReferenceId, "--skip-requirements"
            },
            new TestConsole());

        (await act.Should().ThrowAsync<CleanExitException>()).Which.ExitCode.Should().Be(1,
            because: "AI Teammate setup exits with code 1 when the blueprint step fails");
        _capturedOptions.Should().ContainSingle().Which.ServiceManagementReference.Should().Be(ReferenceId,
            because: "'setup all --aiteammate --agent-name <name> --m365' is the bootstrap flow where blueprint creation failed for a missing serviceManagementReference");
    }

    // --- setup blueprint forwarding ---

    [Theory]
    [InlineData(null, ConfigReferenceId)]
    [InlineData(ReferenceId, ReferenceId)]
    public async Task SetupBlueprint_ReferenceFlagOverridesConfigKey(string? flag, string expected)
    {
        _configService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(BlueprintAgentConfig(ConfigReferenceId));
        _creationResult = new BlueprintCreationResult { BlueprintCreated = true };

        var exitCode = await BuildParser(BuildSetupBlueprintCommand()).InvokeAsync(
            WithReference(flag, "--skip-requirements"), new TestConsole());

        exitCode.Should().Be(0);
        _capturedOptions.Should().ContainSingle().Which.ServiceManagementReference.Should().Be(expected,
            because: "serviceManagementReference in a365.config.json applies when the option is omitted, and the option overrides it");
    }

    [Fact]
    public async Task SetupBlueprint_BootstrapWithReference_ForwardsToBlueprintCreation()
    {
        _resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<FileInfo>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(BlueprintAgentConfig());
        _creationResult = new BlueprintCreationResult { BlueprintCreated = true };

        var exitCode = await BuildParser(BuildSetupBlueprintCommand(_resolver)).InvokeAsync(
            new[] { "--agent-name", "Contoso", Option, ReferenceId, "--skip-requirements" }, new TestConsole());

        exitCode.Should().Be(0);
        _capturedOptions.Should().ContainSingle().Which.ServiceManagementReference.Should().Be(ReferenceId,
            because: "--service-management-reference must reach blueprint creation in --agent-name mode");
    }

    [Fact]
    public async Task SetupBlueprint_WhenBlueprintCreationFails_ExitsOneAndPrintsErrorCode()
    {
        _configService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(BlueprintAgentConfig());

        // The error block is written to Console.Error; this collection runs serially, so redirecting it is safe.
        var originalError = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);
        int exitCode;
        try
        {
            exitCode = await BuildParser(BuildSetupBlueprintCommand()).InvokeAsync(
                new[] { "--skip-requirements" }, new TestConsole());
        }
        finally
        {
            Console.SetError(originalError);
        }

        exitCode.Should().Be(1,
            because: "a failed blueprint creation must not report success to scripts and CI");
        stderr.ToString().Should().Contain($"Error code: {ErrorCodes.ServiceManagementReferenceRequired}",
            because: "standalone 'setup blueprint' must print the documented error code, not only 'setup all' in its summary");
        _capturedOptions.Should().ContainSingle().Which.ServiceManagementReference.Should().BeNull(
            because: "without the option or config key, no serviceManagementReference is sent");
    }

    // --- Helpers ---

    private Task<BlueprintCreationResult> CaptureBlueprintCreation(
        Agent365Config config, BlueprintCreationOptions options, CancellationToken cancellationToken)
    {
        _capturedOptions.Add(options);
        return Task.FromResult(_creationResult);
    }

    private Command BuildSetupAllCommand(IBootstrapConfigResolver? resolver = null, GraphApiService? graphApiService = null) => AllSubcommand.CreateCommand(
        _logger, _configService, _executor, _backendConfigurator, _authValidator, _platformDetector,
        graphApiService ?? _graphApiService, _blueprintService, _clientAppValidator, _blueprintLookupService, _federatedCredentialService,
        armApiService: null,
        confirmationProvider: Substitute.For<IConfirmationProvider>(),
        resolver: resolver,
        blueprintCreatorOverride: CaptureBlueprintCreation);

    private Command BuildSetupBlueprintCommand(IBootstrapConfigResolver? resolver = null) => BlueprintSubcommand.CreateCommand(
        _logger, _configService, _executor, _authValidator, _platformDetector, _backendConfigurator,
        _graphApiService, _blueprintService, _clientAppValidator, _blueprintLookupService, _federatedCredentialService,
        resolver: resolver,
        blueprintCreatorOverride: CaptureBlueprintCreation);

    private static Parser BuildParser(Command command) => new CommandLineBuilder(command).Build();

    private static string[] WithReference(string? reference, params string[] args) =>
        reference is null ? args : args.Concat(new[] { Option, reference }).ToArray();

    private static Agent365Config BlueprintAgentConfig(string? reference = null) => new()
    {
        TenantId = "00000000-0000-0000-0000-000000000001",
        ClientAppId = AuthenticationConstants.WellKnownClientAppId,
        AgentIdentityDisplayName = "Contoso Identity",
        AgentBlueprintDisplayName = "Contoso Blueprint",
        AiTeammate = false,
        UseBlueprint = true,
        ServiceManagementReference = reference,
    };

    private static Agent365Config AiTeammateConfig(string? reference = null) => new()
    {
        TenantId = "00000000-0000-0000-0000-000000000001",
        ClientAppId = AuthenticationConstants.WellKnownClientAppId,
        AgentIdentityDisplayName = "Contoso Identity",
        AgentBlueprintDisplayName = "Contoso Blueprint",
        AiTeammate = true,
        ServiceManagementReference = reference,
    };

    private void AssertLogged(LogLevel level, string fragment) =>
        _logger.Received().Log(
            level,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains(fragment)),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

    private void AssertNotLogged(LogLevel level, string fragment, string because)
    {
        var matches = _logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log)
                        && (LogLevel)c.GetArguments()[0]! == level
                        && c.GetArguments()[2]?.ToString()?.Contains(fragment) == true)
            .ToList();
        matches.Should().BeEmpty(because);
    }
}
