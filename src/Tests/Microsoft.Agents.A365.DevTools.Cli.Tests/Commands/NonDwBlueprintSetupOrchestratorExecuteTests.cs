// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Tests for NonDwBlueprintSetupOrchestrator.ExecuteAsync — Phase B setup execution.
///
/// Behavioral coverage:
///   - Blueprint failure results in exit code 1 and populated errors
///   - Agent instance ID is recorded on success
///
/// Note: The full success path (blueprint created → batch permissions → agent instance registered)
/// requires an integration test harness because BlueprintSubcommand.CreateBlueprintImplementationAsync
/// is a static method with many Graph API calls. Those tests are tracked separately.
/// </summary>
public class NonDwBlueprintSetupOrchestratorExecuteTests
{
    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _messages = [];

        public string AllOutput => string.Join("\n", _messages);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _messages.Add(formatter(state, exception));
    }

    // -------------------------------------------------------------------------
    // ExecuteAsync behavioral tests — error paths
    // -------------------------------------------------------------------------

    private static CommandExecutor BuildMockExecutor()
    {
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        executor.ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult
            {
                ExitCode = 0,
                StandardOutput = string.Empty,
                StandardError = string.Empty
            }));
        return executor;
    }

    private static SetupContext BuildContext(Agent365Config? config = null, bool skipRequirements = true, bool skipObservabilityPermissions = false)
    {
        var cfg = config ?? new Agent365Config
        {
            AiTeammate = false,
            TenantId = "tenant-id",
            AgentIdentityDisplayName = "Test Agent",
            ClientAppId = "client-app-id",
        };

        var mockExecutor = BuildMockExecutor();

        // Use ForPartsOf so virtual methods return null/default without triggering real logic
        Func<Task<string?>> noOpLoginHint = () => Task.FromResult<string?>(null);
        var graphApiService = Substitute.ForPartsOf<GraphApiService>(
            Substitute.For<ILogger<GraphApiService>>(),
            mockExecutor,
            Substitute.For<IAuthenticationService>(),
            (System.Net.Http.HttpMessageHandler?)null,
            (IMicrosoftGraphTokenProvider?)null,
            noOpLoginHint,
            (string?)null,
            (RetryHelper?)null,
            (TimeSpan?)TimeSpan.Zero);

        var blueprintService = Substitute.ForPartsOf<AgentBlueprintService>(
            Substitute.For<ILogger<AgentBlueprintService>>(),
            graphApiService);

        var blueprintLookupService = Substitute.ForPartsOf<BlueprintLookupService>(
            Substitute.For<ILogger<BlueprintLookupService>>(),
            graphApiService);

        var federatedCredentialService = Substitute.ForPartsOf<FederatedCredentialService>(
            Substitute.For<ILogger<FederatedCredentialService>>(),
            graphApiService);

        var authValidator = Substitute.For<AzureAuthValidator>(
            NullLogger<AzureAuthValidator>.Instance, mockExecutor);

        var configService = Substitute.For<IConfigService>();
        // LoadAsync returns a config with blueprint ID so the reload after blueprint step
        // does not throw a SetupValidationException about missing AgentBlueprintId.
        configService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new Agent365Config
            {
                AiTeammate = false,
                TenantId = cfg.TenantId,
                AgentIdentityDisplayName = cfg.AgentIdentityDisplayName,
                AgentBlueprintId = "test-blueprint-id",
                ClientAppId = cfg.ClientAppId,
            });
        configService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        return new SetupContext(
            config: cfg,
            results: new SetupResults(),
            logger: Substitute.For<ILogger>(),
            configFile: new FileInfo("a365.config.json"),
            generatedConfigPath: "a365.generated.config.json",
            correlationId: "test-correlation-id",
            skipInfrastructure: true,
            skipRequirements: skipRequirements,
            cancellationToken: CancellationToken.None,
            configService: configService,
            executor: mockExecutor,
            backendConfigurator: Substitute.For<ITeamsGraphBackendConfigurator>(),
            authValidator: authValidator,
            platformDetector: Substitute.ForPartsOf<PlatformDetector>(
                Substitute.For<ILogger<PlatformDetector>>()),
            graphApiService: graphApiService,
            blueprintService: blueprintService,
            blueprintLookupService: blueprintLookupService,
            federatedCredentialService: federatedCredentialService,
            clientAppValidator: Substitute.For<IClientAppValidator>(),
            loginHintResolver: () => Task.FromResult<string?>(null),
            skipObservabilityPermissions: skipObservabilityPermissions);
    }

    /// <summary>
    /// When blueprint creation fails (which it will with mocked services returning null),
    /// ExecuteAsync must return exit code 1 — never throw.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReturnsExitCode1_WhenBlueprintFails()
    {
        var ctx = BuildContext();

        var exitCode = await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_CustomObservabilityPermission_DoesNotMarkObservabilitySkipped()
    {
        var ctx = BuildContext(
            new Agent365Config
            {
                AiTeammate = false,
                TenantId = "tenant-id",
                AgentIdentityDisplayName = "Test Agent",
                ClientAppId = "client-app-id",
                CustomBlueprintPermissions =
                [
                    new CustomResourcePermission
                    {
                        ResourceAppId = ConfigConstants.ObservabilityApiAppId,
                        ResourceName = "Observability API",
                        Scopes = [ConfigConstants.ObservabilityApiOtelWriteScope],
                    }
                ],
            },
            skipObservabilityPermissions: true);

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        ctx.Results.ObservabilityPermissionsSkipped.Should().BeFalse(
            because: "a valid custom Observability permission is an explicit opt-back-in even though setup omits the default fixed OtelWrite spec");
    }

    /// <summary>
    /// When blueprint creation fails, errors must be added to SetupResults
    /// so the summary display can show what went wrong.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AddsErrors_WhenBlueprintFails()
    {
        var ctx = BuildContext();

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        ctx.Results.HasErrors.Should().BeTrue();
    }

    /// <summary>
    /// When SkipRequirements is true, the requirements check step must be skipped entirely.
    /// The setup will still fail at blueprint creation (mocked services), but it must not
    /// fail on requirements validation.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DoesNotRunRequirementsCheck_WhenSkipRequirementsIsTrue()
    {
        var ctx = BuildContext(skipRequirements: true);

        // This must not throw a requirements-related exception even with partial mocks
        var exitCode = await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        // Blueprint fails → exit 1, but NOT due to requirements check
        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_FirstPartyClientApp_DoesNotReadOrMutateTenantConsentGrants()
    {
        var ctx = BuildContext(new Agent365Config
        {
            AiTeammate = false,
            TenantId = "tenant-id",
            AgentIdentityDisplayName = "Test Agent",
            ClientAppId = AuthenticationConstants.WellKnownClientAppId,
        });

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        var consentCalls = ctx.ClientAppValidator.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name is
                nameof(IClientAppValidator.GetUnconsentedRequiredPermissionsAsync) or
                nameof(IClientAppValidator.GrantConsentForPermissionsAsync))
            .ToList();
        consentCalls.Should().BeEmpty(
            because: "setup must trust validated first-party token preauthorization instead of reading or mutating a tenant-local oauth2PermissionGrant");
    }

    /// <summary>
    /// AgentInstanceRegistered must be false when the blueprint step fails
    /// (agent instance registration is not attempted if blueprint creation fails).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AgentInstanceNotRegistered_WhenBlueprintFails()
    {
        var ctx = BuildContext();

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        ctx.Results.AgentInstanceRegistered.Should().BeFalse();
        ctx.Results.AgentInstanceId.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // SetupResults field tests
    // -------------------------------------------------------------------------

    [Fact]
    public void SetupResults_AgentInstanceRegistered_DefaultsFalse()
    {
        var results = new SetupResults();
        results.AgentInstanceRegistered.Should().BeFalse();
    }

    [Fact]
    public void SetupResults_AgentInstanceId_DefaultsNull()
    {
        var results = new SetupResults();
        results.AgentInstanceId.Should().BeNull();
    }

    [Fact]
    public void SetupResults_CanSetAgentInstanceRegisteredAndId()
    {
        var results = new SetupResults();
        results.AgentInstanceRegistered = true;
        results.AgentInstanceId = "test-instance-id-123";

        results.AgentInstanceRegistered.Should().BeTrue();
        results.AgentInstanceId.Should().Be("test-instance-id-123");
    }

    // -------------------------------------------------------------------------
    // Idempotency tests — Steps 5 & 6
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a SetupContext suited for testing the agent identity + registration steps
    /// (Steps 5–6), by default via the AgentInstanceOnly path.
    /// Returns the context, graph service mock, and blueprint service mock so tests can
    /// configure stub return values.
    /// </summary>
    private static (SetupContext ctx, GraphApiService graph, AgentBlueprintService blueprintService)
        BuildIdempotencyTestContext(
            Agent365Config? config = null,
            ILogger? logger = null,
            bool agentInstanceOnly = true,
            bool skipObservabilityPermissions = false,
            string? authMode = null)
    {
        var graph = Substitute.ForPartsOf<GraphApiService>();

        // Prevent real HTTP for consent lookup and oauth2 grant lookups.
        graph.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns((System.Text.Json.JsonDocument?)null);

        var cfg = config ?? new Agent365Config
        {
            AiTeammate = false,
            TenantId = "tenant-id",
            AgentBlueprintId = "blueprint-id",
            AgentIdentityDisplayName = "sellakapri211 Identity",
            ClientAppId = "client-app-id",
        };

        var mockExecutor = BuildMockExecutor();
        var configService = Substitute.For<IConfigService>();
        configService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        var blueprintService = Substitute.ForPartsOf<AgentBlueprintService>(
            Substitute.For<ILogger<AgentBlueprintService>>(), graph);

        var ctx = new SetupContext(
            config: cfg,
            results: new SetupResults(),
            logger: logger ?? Substitute.For<ILogger>(),
            configFile: new FileInfo("a365.config.json"),
            generatedConfigPath: "a365.generated.config.json",
            correlationId: "test-correlation-id",
            skipInfrastructure: true,
            skipRequirements: true,
            cancellationToken: CancellationToken.None,
            configService: configService,
            executor: mockExecutor,
            backendConfigurator: Substitute.For<ITeamsGraphBackendConfigurator>(),
            authValidator: Substitute.For<AzureAuthValidator>(
                NullLogger<AzureAuthValidator>.Instance, mockExecutor),
            platformDetector: Substitute.ForPartsOf<PlatformDetector>(
                Substitute.For<ILogger<PlatformDetector>>()),
            graphApiService: graph,
            blueprintService: blueprintService,
            blueprintLookupService: Substitute.ForPartsOf<BlueprintLookupService>(
                Substitute.For<ILogger<BlueprintLookupService>>(), graph),
            federatedCredentialService: Substitute.ForPartsOf<FederatedCredentialService>(
                Substitute.For<ILogger<FederatedCredentialService>>(), graph),
            clientAppValidator: Substitute.For<IClientAppValidator>(),
            agentInstanceOnly: agentInstanceOnly,
            loginHintResolver: () => Task.FromResult<string?>(null),
            skipObservabilityPermissions: skipObservabilityPermissions,
            authMode: authMode);

        return (ctx, graph, blueprintService);
    }

    /// <summary>
    /// Step 5: When the API lookup finds an existing identity by display name,
    /// the existing ID must be reused and CreateAgentIdentityDelegatedAsync must NOT be called.
    /// </summary>
    [Fact]
    public async Task Step5_ReuseExistingIdentity_WhenFoundByApiLookup()
    {
        var (ctx, graph, blueprintService) = BuildIdempotencyTestContext();

        blueprintService.FindExistingAgentIdentityAsync(
            "tenant-id", "blueprint-id", "sellakapri211 Identity", Arg.Any<CancellationToken>())
            .Returns("existing-sp-id");

        // Step 6 must also complete cleanly; stub RegisterAgentInstanceAsyncV2
        graph.RegisterAgentInstanceAsyncV2(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(("new-reg-id", false));

        // Capture field values at each SaveStateAsync call so we can assert the Step 5 save
        // happened before Step 6 mutated AgentRegistrationId on the same config object.
        var savedStates = new List<(string? AgenticAppId, string? AgentRegistrationId)>();
        ctx.ConfigService
            .When(s => s.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>()))
            .Do(callInfo =>
            {
                var c = callInfo.Arg<Agent365Config>();
                savedStates.Add((c.AgenticAppId, c.AgentRegistrationId));
            });

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        ctx.Results.AgentIdentityId.Should().Be("existing-sp-id",
            because: "the existing agent identity must be reused");
        await graph.DidNotReceive().CreateAgentIdentityDelegatedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        savedStates.Should().Contain(
            s => s.AgenticAppId == "existing-sp-id" && s.AgentRegistrationId == null,
            because: "Step 5 must persist the reused identity ID before Step 6 sets AgentRegistrationId on the same config instance");
    }

    /// <summary>
    /// Step 5 (--agent-registration-only): When the API lookup returns null, the path must error out
    /// rather than create a new identity — identity creation is not the responsibility of this flag.
    /// </summary>
    [Fact]
    public async Task Step5_FailsWithError_WhenIdentityNotFoundByApiLookup()
    {
        var (ctx, graph, blueprintService) = BuildIdempotencyTestContext();

        blueprintService.FindExistingAgentIdentityAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var exitCode = await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        exitCode.Should().Be(1,
            because: "missing agent identity is a fatal error for --agent-registration-only");
        ctx.Results.AgentIdentityFailed.Should().BeTrue(
            because: "identity not found via API lookup must surface as an identity failure");
        ctx.Results.AgentIdentityFailureIsError.Should().BeTrue(
            because: "registration-only cannot continue without an identity, so the identity row must point to Errors");
        ctx.Results.AgentRegistrationFailed.Should().BeTrue(
            because: "registration cannot proceed without an agent identity");
        ctx.Results.AgentRegistrationFailureIsError.Should().BeTrue(
            because: "registration-only cannot complete without an identity, so the registration row must point to Errors");
        await graph.DidNotReceive().CreateAgentIdentityDelegatedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Step 6 (--agent-registration-only): A registration API failure must be fatal for the focused
    /// command, returning exit code 1 and emitting an error summary instead of a success-with-warnings banner.
    /// </summary>
    [Fact]
    public async Task Step6_RegistrationOnly_ReturnsExitCode1AndAvoidsSuccessfulSummary_WhenRegistrationFails()
    {
        var logger = new CapturingLogger();
        var config = new Agent365Config
        {
            AiTeammate = false,
            TenantId = "tenant-id",
            AgentBlueprintId = "blueprint-id",
            AgentIdentityDisplayName = "sellakapri211 Identity",
            ClientAppId = "client-app-id",
            AgenticAppId = "agentic-app-id",
        };
        var (ctx, graph, _) = BuildIdempotencyTestContext(config, logger);

        graph.RegisterAgentInstanceAsyncV2(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(((string?)null, false));

        var exitCode = await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        exitCode.Should().Be(1,
            because: "registration-only mode requested only agent registration, so that failure must be fatal");
        ctx.Results.Errors.Should().ContainSingle(error => error.StartsWith("Agent registration failed via Graph copilot/agentRegistrations API."),
            because: "registration-only failures are fatal and must be listed under Errors for the summary and exit-code path");
        ctx.Results.Warnings.Should().NotContain(warning => warning.StartsWith("Agent registration failed"),
            because: "registration-only failures must not be downgraded to warnings");
        logger.AllOutput.Should().Contain("Setup completed with errors",
            because: "the summary must not present a registration-only failure as successful");
        logger.AllOutput.Should().NotContain("Setup completed successfully",
            because: "registration-only failure must not emit either success status line");
    }

    /// <summary>
    /// Step 6: When AgentRegistrationId is not in config, RegisterAgentInstanceAsyncV2 must be called.
    /// </summary>
    [Fact]
    public async Task Step6_RegistersNewAgent_WhenNotInConfig()
    {
        var config = new Agent365Config
        {
            AiTeammate = false,
            TenantId = "tenant-id",
            AgentBlueprintId = "blueprint-id",
            AgentIdentityDisplayName = "sellakapri211 Identity",
            ClientAppId = "client-app-id",
            AgenticAppId = "agentic-app-id",
        };
        var (ctx, graph, _) = BuildIdempotencyTestContext(config);

        graph.RegisterAgentInstanceAsyncV2(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(("new-reg-id", false));

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        ctx.Results.AgentInstanceId.Should().Be("new-reg-id",
            because: "RegisterAgentInstanceAsyncV2 must be called when no registration ID is in config");
        await graph.Received(1).RegisterAgentInstanceAsyncV2(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await graph.DidNotReceive().AgentRegistrationExistsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Step 5: When an existing identity is found by API lookup, AgentIdentityAlreadyExisted
    /// must be true so the summary shows "reused" rather than "created".
    /// </summary>
    [Fact]
    public async Task Step5_SetsAlreadyExistedFlag_WhenFoundByApiLookup()
    {
        var (ctx, graph, blueprintService) = BuildIdempotencyTestContext();

        blueprintService.FindExistingAgentIdentityAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("existing-sp-id");

        graph.RegisterAgentInstanceAsyncV2(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(("new-reg-id", false));

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        ctx.Results.AgentIdentityAlreadyExisted.Should().BeTrue(
            because: "the identity was found by API lookup, not freshly created");
    }

    /// <summary>
    /// Step 5: When AgenticAppId is already in config, AgentIdentityAlreadyExisted must be true
    /// so the summary shows "reused" rather than "created".
    /// </summary>
    [Fact]
    public async Task Step5_SetsAlreadyExistedFlag_WhenFoundInConfig()
    {
        var config = new Agent365Config
        {
            AiTeammate = false,
            TenantId = "tenant-id",
            AgentBlueprintId = "blueprint-id",
            AgentIdentityDisplayName = "sellakapri211 Identity",
            ClientAppId = "client-app-id",
            AgenticAppId = "agentic-app-id-from-config",
        };
        var (ctx, graph, _) = BuildIdempotencyTestContext(config);

        graph.RegisterAgentInstanceAsyncV2(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(("new-reg-id", false));

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        ctx.Results.AgentIdentityAlreadyExisted.Should().BeTrue(
            because: "the identity was already recorded in config, not freshly created");
    }

    /// <summary>
    /// Step 5: When a new identity is created (lookup returns null), AgentIdentityAlreadyExisted
    /// must remain false so the summary shows "created".
    /// </summary>
    [Fact]
    public async Task Step5_AlreadyExistedFlag_IsFalse_WhenNewlyCreated()
    {
        var (ctx, graph, blueprintService) = BuildIdempotencyTestContext();

        blueprintService.FindExistingAgentIdentityAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        graph.CreateAgentIdentityDelegatedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("new-sp-id");
        graph.RegisterAgentInstanceAsyncV2(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(("new-reg-id", false));

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        ctx.Results.AgentIdentityAlreadyExisted.Should().BeFalse(
            because: "the identity was freshly created, not reused");
    }

    /// <summary>
    /// Step 6: When AgentRegistrationId is in config and the GET confirms it still exists,
    /// AgentRegistrationAlreadyExisted must be true and RegisterAgentInstanceAsyncV2 must NOT be called.
    /// </summary>
    [Fact]
    public async Task Step6_SetsAlreadyExistedFlag_WhenFoundInConfigAndVerified()
    {
        var config = new Agent365Config
        {
            AiTeammate = false,
            TenantId = "tenant-id",
            AgentBlueprintId = "blueprint-id",
            AgentIdentityDisplayName = "sellakapri211 Identity",
            ClientAppId = "client-app-id",
            AgenticAppId = "agentic-app-id",
            AgentRegistrationId = "reg-id-from-config",
        };
        var (ctx, graph, _) = BuildIdempotencyTestContext(config);

        // Verification GET returns true — registration still exists
        graph.AgentRegistrationExistsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        ctx.Results.AgentRegistrationAlreadyExisted.Should().BeTrue(
            because: "the registration was verified in the registry, not freshly registered");
        await graph.DidNotReceive().RegisterAgentInstanceAsyncV2(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Step 6: When AgentRegistrationId is in config but the GET returns false (stale ID),
    /// a new registration must be created and AgentRegistrationAlreadyExisted must be false.
    /// </summary>
    [Fact]
    public async Task Step6_CreatesNewRegistration_WhenStoredIdIsStale()
    {
        var config = new Agent365Config
        {
            AiTeammate = false,
            TenantId = "tenant-id",
            AgentBlueprintId = "blueprint-id",
            AgentIdentityDisplayName = "sellakapri211 Identity",
            ClientAppId = "client-app-id",
            AgenticAppId = "agentic-app-id",
            AgentRegistrationId = "stale-reg-id",
        };
        var (ctx, graph, _) = BuildIdempotencyTestContext(config);

        // Verification GET returns false — stored registration no longer exists
        graph.AgentRegistrationExistsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        graph.RegisterAgentInstanceAsyncV2(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(("new-reg-id", false));

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        ctx.Results.AgentInstanceId.Should().Be("new-reg-id",
            because: "stale registration must be replaced by a new one");
        ctx.Results.AgentRegistrationAlreadyExisted.Should().BeFalse(
            because: "the registration was freshly created after the stored ID was found stale");
        await graph.Received(1).RegisterAgentInstanceAsyncV2(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Step 6: When a new registration is created (no ID in config),
    /// AgentRegistrationAlreadyExisted must remain false so the summary shows "registered".
    /// </summary>
    [Fact]
    public async Task Step6_AlreadyExistedFlag_IsFalse_WhenNewlyRegistered()
    {
        var config = new Agent365Config
        {
            AiTeammate = false,
            TenantId = "tenant-id",
            AgentBlueprintId = "blueprint-id",
            AgentIdentityDisplayName = "sellakapri211 Identity",
            ClientAppId = "client-app-id",
            AgenticAppId = "agentic-app-id",
        };
        var (ctx, graph, _) = BuildIdempotencyTestContext(config);

        graph.RegisterAgentInstanceAsyncV2(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(("new-reg-id", false));

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        ctx.Results.AgentRegistrationAlreadyExisted.Should().BeFalse(
            because: "the registration was freshly created, not reused");
        await graph.DidNotReceive().AgentRegistrationExistsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Step 6: When RegisterAgentInstanceAsyncV2 returns an existing ID via 409 Conflict,
    /// AgentRegistrationAlreadyExisted must be true so the summary shows "reused" not "registered".
    /// </summary>
    [Fact]
    public async Task Step6_SetsAlreadyExistedFlag_When409ConflictReturnedByRegisterApi()
    {
        var config = new Agent365Config
        {
            AiTeammate = false,
            TenantId = "tenant-id",
            AgentBlueprintId = "blueprint-id",
            AgentIdentityDisplayName = "sellakapri211 Identity",
            ClientAppId = "client-app-id",
            AgenticAppId = "agentic-app-id",
            // No AgentRegistrationId — simulates a first-time run that hits a 409
        };
        var (ctx, graph, _) = BuildIdempotencyTestContext(config);

        graph.RegisterAgentInstanceAsyncV2(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(("conflict-reg-id", true));

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        ctx.Results.AgentRegistrationAlreadyExisted.Should().BeTrue(
            because: "a 409 Conflict means the agent was already registered; the flag must be true so the summary shows 'reused' rather than 'registered'");
        ctx.Results.AgentInstanceId.Should().Be("conflict-reg-id",
            because: "the existing registration ID returned via 409 must be recorded");
        ctx.Results.AgentInstanceRegistered.Should().BeTrue(
            because: "registration is considered successful even when retrieved via 409");
    }

    /// <summary>
    /// Step 6: When AgentRegistrationExistsAsync returns null (auth or transient error) and registration is
    /// optional (Observability permissions requested, not --agent-registration-only), the stored registration
    /// ID must be preserved and re-registration must not be attempted.
    /// </summary>
    [Fact]
    public async Task Step6_PreservesStoredRegistrationId_WhenVerificationIsInconclusive()
    {
        // Empty project directory: the project settings step finds no project and writes nothing.
        var projectDir = Path.Combine(Path.GetTempPath(), "NonDwRegistrationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        try
        {
            var config = new Agent365Config
            {
                AiTeammate = false,
                TenantId = "tenant-id",
                AgentBlueprintId = "blueprint-id",
                AgentIdentityDisplayName = "sellakapri211 Identity",
                ClientAppId = "client-app-id",
                AgenticAppId = "agentic-app-id",
                AgentRegistrationId = "stored-reg-id",
                DeploymentProjectPath = projectDir,
            };
            var (ctx, graph, _) = BuildIdempotencyTestContext(config, agentInstanceOnly: false);

            graph.AgentRegistrationExistsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((bool?)null);

            await NonDwBlueprintSetupOrchestrator.ExecuteAgentIdentityAndRegistrationAsync(ctx, specs: []);

            ctx.Results.AgentInstanceId.Should().Be("stored-reg-id",
                because: "when verification is inconclusive the stored ID must be preserved to avoid unintended re-registration");
            ctx.Results.AgentRegistrationAlreadyExisted.Should().BeTrue(
                because: "an inconclusive verification is treated as 'assume still exists' to prevent data loss");
            await graph.DidNotReceive().RegisterAgentInstanceAsyncV2(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }

    /// <summary>
    /// Step 6: when registration is required (--agent-registration-only, or Observability permissions not
    /// requested so registration is the agent's only authorization), an inconclusive verification must fail
    /// setup instead of passing — while still keeping the stored ID and not creating a duplicate registration.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Step6_RegistrationRequired_FailsWithoutReRegistering_WhenVerificationIsInconclusive(bool agentInstanceOnly, bool skipObservabilityPermissions)
    {
        var projectDir = Path.Combine(Path.GetTempPath(), "NonDwRegistrationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        try
        {
            var config = new Agent365Config
            {
                AiTeammate = false,
                TenantId = "tenant-id",
                AgentBlueprintId = "blueprint-id",
                AgentIdentityDisplayName = "Test Agent Identity",
                ClientAppId = "client-app-id",
                AgenticAppId = "agentic-app-id",
                AgentRegistrationId = "stored-reg-id",
                DeploymentProjectPath = projectDir,
            };
            var (ctx, graph, _) = BuildIdempotencyTestContext(
                config, agentInstanceOnly: agentInstanceOnly, skipObservabilityPermissions: skipObservabilityPermissions);
            graph.AgentRegistrationExistsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((bool?)null);

            await NonDwBlueprintSetupOrchestrator.ExecuteAgentIdentityAndRegistrationAsync(
                ctx, specs: [], skipIdentityAndPermissions: agentInstanceOnly);

            ctx.Results.Errors.Should().ContainSingle(e => e.Contains("Could not verify agent registration"),
                because: "an unverifiable registration cannot be relied on as the agent's only authorization, so setup must exit 1");
            ctx.Results.AgentInstanceRegistered.Should().BeFalse(
                because: "the summary must not report a registration that could not be confirmed");
            ctx.Config.AgentRegistrationId.Should().Be("stored-reg-id",
                because: "an auth or transient failure is not proof the registration is gone, so the stored ID is kept for the retry");
            await graph.DidNotReceive().RegisterAgentInstanceAsyncV2(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }

    private static Agent365Config RegistrationReadyConfig(string deploymentProjectPath = "") => new()
    {
        AiTeammate = false,
        TenantId = "tenant-id",
        AgentBlueprintId = "blueprint-id",
        AgentIdentityDisplayName = "Test Agent Identity",
        ClientAppId = "client-app-id",
        AgenticAppId = "agentic-app-id",
        DeploymentProjectPath = deploymentProjectPath,
    };

    private static void StubRegistrationFailure(GraphApiService graph) =>
        graph.RegisterAgentInstanceAsyncV2(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(((string?)null, false));

    /// <summary>
    /// Step 5: when the blueprint secret is missing, setup cannot create the identity or register the agent.
    /// </summary>
    [Fact]
    public async Task Step5_MissingBlueprintClientSecret_RecordsErrorForExitCode1()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), "NonDwMissingSecretTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        try
        {
            var config = new Agent365Config
            {
                AiTeammate = false,
                TenantId = "tenant-id",
                AgentBlueprintId = "blueprint-id",
                AgentIdentityDisplayName = "Test Agent Identity",
                ClientAppId = "client-app-id",
                DeploymentProjectPath = projectDir,
                AgentBlueprintClientSecret = null,
            };
            var (ctx, _, blueprintService) = BuildIdempotencyTestContext(
                config, agentInstanceOnly: false, skipObservabilityPermissions: true);
            blueprintService.FindExistingAgentIdentityAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((string?)null);

            await NonDwBlueprintSetupOrchestrator.ExecuteAgentIdentityAndRegistrationAsync(ctx, specs: []);

            ctx.Results.AgentIdentityFailed.Should().BeTrue(
                because: "without the blueprint secret setup cannot create the agent identity");
            ctx.Results.AgentIdentityFailureIsError.Should().BeTrue(
                because: "the missing secret path records an error, so the identity summary row must say see errors");
            ctx.Results.AgentRegistrationFailed.Should().BeTrue(
                because: "registration is mandatory when Observability permissions were skipped");
            ctx.Results.AgentRegistrationFailureIsError.Should().BeTrue(
                because: "the missing secret path records an error before registration can run");
            ctx.Results.Errors.Should().ContainSingle(e => e.Contains("blueprint client secret is not available"),
                because: "ExecuteAsync returns exit code 1 whenever Results.HasErrors is true");
            ctx.Results.Errors.Single().Should().Contain("re-run 'a365 setup all'")
                .And.NotContain("--agent-registration-only",
                    because: "--agent-registration-only skips identity creation, so it cannot recover a run that never created the identity");
            ctx.Results.HasErrors.Should().BeTrue(
                because: "the full setup command maps recorded errors to exit code 1");
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }

    /// <summary>
    /// Step 5: an s2s run whose identity step fails still records its auth mode and that no S2S app role
    /// is requested, so the summary does not fall back to delegated-consent wording.
    /// </summary>
    [Fact]
    public async Task Step5_IdentityStepFails_S2sMode_SummaryStillReportsNoS2SAppRolesToGrant()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), "NonDwS2sIdentityFailureTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        try
        {
            var config = new Agent365Config
            {
                AiTeammate = false,
                TenantId = "tenant-id",
                AgentBlueprintId = "blueprint-id",
                AgentIdentityDisplayName = "Test Agent Identity",
                ClientAppId = "client-app-id",
                DeploymentProjectPath = projectDir,
                AgentBlueprintClientSecret = null,
            };
            var (ctx, _, blueprintService) = BuildIdempotencyTestContext(
                config, agentInstanceOnly: false, skipObservabilityPermissions: true, authMode: "s2s");
            blueprintService.FindExistingAgentIdentityAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((string?)null);
            // A non-admin run: the blueprint grants completed but tenant-wide consent was not granted.
            ctx.Results.IsNonDwBlueprintFlow = true;
            ctx.Results.BatchPermissionsPhase1Completed = true;
            ctx.Results.BatchPermissionsPhase2Completed = true;
            ctx.Results.TenantWideConsentOutcome = GrantOutcome.Failed;

            await NonDwBlueprintSetupOrchestrator.ExecuteAgentIdentityAndRegistrationAsync(ctx, specs: []);

            ctx.Results.AgentIdentityFailed.Should().BeTrue(because: "precondition: the missing secret stops identity creation");
            ctx.Results.EffectiveAuthMode.Should().Be(AuthMode.S2s,
                because: "the auth mode is known before identity creation, and the summary derives delegated-consent applicability from it");
            ctx.Results.NoS2SAppRolesToGrant.Should().BeTrue(
                because: "no requested spec carries an app role, whether or not the identity step succeeds");

            var logger = new CapturingLogger();
            SetupHelpers.DisplaySetupSummary(ctx.Results, logger);
            logger.AllOutput.Split('\n').Should().ContainSingle(l => l.Contains("Blueprint Permission Grants"))
                .Which.Should().Contain("not required  (no S2S app roles to grant)",
                    because: "an s2s run with no app roles to grant needs no blueprint grant, even when the identity step failed");
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public async Task Step5_IdentityCreationNull_RecordsIdentityWarningSeverity()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), "NonDwIdentityNullTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        try
        {
            var config = new Agent365Config
            {
                AiTeammate = false,
                TenantId = "tenant-id",
                AgentBlueprintId = "blueprint-id",
                AgentIdentityDisplayName = "Test Agent Identity",
                ClientAppId = "client-app-id",
                DeploymentProjectPath = projectDir,
                AgentBlueprintClientSecret = "secret",
            };
            var (ctx, graph, blueprintService) = BuildIdempotencyTestContext(
                config, agentInstanceOnly: false, skipObservabilityPermissions: false);
            blueprintService.FindExistingAgentIdentityAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((string?)null);
            graph.CreateAgentIdentityAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((string?)null);

            await NonDwBlueprintSetupOrchestrator.ExecuteAgentIdentityAndRegistrationAsync(ctx, specs: []);

            ctx.Results.AgentIdentityFailed.Should().BeTrue(
                because: "a null identity creation result is still surfaced to the summary");
            ctx.Results.AgentIdentityFailureIsError.Should().BeFalse(
                because: "identity creation returning null is recorded as a warning path, not an Errors path");
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }

    /// <summary>
    /// Step 6: when Observability permissions are not requested, registration is the agent's only Observability
    /// authorization, so its failure is an error; when they are requested (AI Teammate) it stays a warning.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Step6_RegistrationFailureIsError_OnlyWhenObservabilityPermissionsSkipped(bool skipObservabilityPermissions)
    {
        // Empty project directory: the project settings step finds no project and writes nothing.
        var projectDir = Path.Combine(Path.GetTempPath(), "NonDwRegistrationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        try
        {
            var (ctx, graph, _) = BuildIdempotencyTestContext(
                RegistrationReadyConfig(projectDir), agentInstanceOnly: false, skipObservabilityPermissions: skipObservabilityPermissions);
            StubRegistrationFailure(graph);

            await NonDwBlueprintSetupOrchestrator.ExecuteAgentIdentityAndRegistrationAsync(ctx, specs: []);

            ctx.Results.AgentRegistrationFailed.Should().BeTrue(because: "precondition: the stubbed registration API returned no ID");
            ctx.Results.Errors.Any(e => e.Contains("Agent registration failed")).Should().Be(skipObservabilityPermissions,
                because: "without OtelWrite an unregistered agent cannot export telemetry, so setup must fail (exit 1)");
            if (skipObservabilityPermissions)
                ctx.Results.Errors.Should().Contain(e => e.Contains(AuthenticationConstants.AgentRegistrationReadWriteAllScope),
                    because: "the failure guidance should name the Graph permission the registration API requires");
            ctx.Results.Warnings.Any(w => w.Contains("Agent registration failed")).Should().Be(!skipObservabilityPermissions,
                because: "when Observability permissions are requested the agent keeps OtelWrite, and a failed registration remains a non-fatal warning");
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public async Task Step6_RegistrationFailureWithCustomObservabilityPermission_RemainsWarning()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), "NonDwRegistrationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        try
        {
            var config = new Agent365Config
            {
                AiTeammate = false,
                TenantId = "tenant-id",
                AgentBlueprintId = "blueprint-id",
                AgentIdentityDisplayName = "Test Agent Identity",
                ClientAppId = "client-app-id",
                AgenticAppId = "agentic-app-id",
                DeploymentProjectPath = projectDir,
                CustomBlueprintPermissions =
                [
                    new CustomResourcePermission
                    {
                        ResourceAppId = ConfigConstants.ObservabilityApiAppId,
                        ResourceName = "Observability API",
                        Scopes = [ConfigConstants.ObservabilityApiOtelWriteScope],
                    }
                ],
            };
            var (ctx, graph, _) = BuildIdempotencyTestContext(
                config, agentInstanceOnly: false, skipObservabilityPermissions: true);
            StubRegistrationFailure(graph);

            await NonDwBlueprintSetupOrchestrator.ExecuteAgentIdentityAndRegistrationAsync(ctx, specs: []);

            ctx.Results.Errors.Should().NotContain(e => e.Contains("Agent registration failed"),
                because: "custom Observability permissions mean registration is not the agent's only Observability authorization");
            ctx.Results.Warnings.Should().Contain(w => w.Contains("Agent registration failed"),
                because: "registration failure remains non-fatal when Observability was explicitly requested through custom permissions");
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // GrantOrInstructAgentIdentityAppPermissionsAsync tests
    // -------------------------------------------------------------------------

    private static (SetupContext ctx, GraphApiService graph, AgentBlueprintService blueprintService)
        BuildS2SGrantTestContext()
    {
        var graph = Substitute.ForPartsOf<GraphApiService>();

        graph.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns((System.Text.Json.JsonDocument?)null);

        var config = new Agent365Config
        {
            AiTeammate = false,
            TenantId = "tenant-id",
            AgentIdentityDisplayName = "Test Agent",
            ClientAppId = "client-app-id",
            AgenticAppId = "agentic-app-id",
            AuthMode = "s2s",
        };

        var mockExecutor = BuildMockExecutor();
        var configService = Substitute.For<IConfigService>();
        configService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        var blueprintService = Substitute.ForPartsOf<AgentBlueprintService>(
            Substitute.For<ILogger<AgentBlueprintService>>(), graph);

        var ctx = new SetupContext(
            config: config,
            results: new SetupResults(),
            logger: Substitute.For<ILogger>(),
            configFile: new FileInfo("a365.config.json"),
            generatedConfigPath: "a365.generated.config.json",
            correlationId: "test-correlation-id",
            skipInfrastructure: true,
            skipRequirements: true,
            cancellationToken: CancellationToken.None,
            configService: configService,
            executor: mockExecutor,
            backendConfigurator: Substitute.For<ITeamsGraphBackendConfigurator>(),
            authValidator: Substitute.For<AzureAuthValidator>(
                NullLogger<AzureAuthValidator>.Instance, mockExecutor),
            platformDetector: Substitute.ForPartsOf<PlatformDetector>(
                Substitute.For<ILogger<PlatformDetector>>()),
            graphApiService: graph,
            blueprintService: blueprintService,
            blueprintLookupService: Substitute.ForPartsOf<BlueprintLookupService>(
                Substitute.For<ILogger<BlueprintLookupService>>(), graph),
            federatedCredentialService: Substitute.ForPartsOf<FederatedCredentialService>(
                Substitute.For<ILogger<FederatedCredentialService>>(), graph),
            clientAppValidator: Substitute.For<IClientAppValidator>(),
            authMode: "s2s",
            loginHintResolver: () => Task.FromResult<string?>(null));

        return (ctx, graph, blueprintService);
    }

    private static List<ResourcePermissionSpec> OneS2SSpec() =>
        [new ResourcePermissionSpec("resource-app-id", "Test Resource", [], false, AppRoleScopes: ["TestRole.ReadWrite"])];

    /// <summary>
    /// When no spec has AppRoleScopes, the method exits immediately — no SP lookup or grant calls made.
    /// </summary>
    [Fact]
    public async Task GrantOrInstructAgentIdentityAppPermissions_NoS2SSpecs_NoCallsMade()
    {
        var (ctx, graph, blueprintService) = BuildS2SGrantTestContext();
        var specsWithNoAppRoles = new List<ResourcePermissionSpec>
        {
            new("resource-app-id", "Test Resource", ["user_impersonation"], false)
        };

        await NonDwBlueprintSetupOrchestrator.GrantOrInstructAgentIdentityAppPermissionsAsync(ctx, specsWithNoAppRoles);

        await graph.DidNotReceive().EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>());
        await blueprintService.DidNotReceive().GrantAppRoleAssignmentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When AgenticAppId is missing (the SP object ID was never stored), AgentIdentityS2SOutcome
    /// is set to Failed and no grant calls are made — the SP ID is required as the grant target.
    /// AgenticAppId holds the SP object ID directly (not an app ID); no lookup is performed.
    /// </summary>
    [Fact]
    public async Task GrantOrInstructAgentIdentityAppPermissions_AgentSpIdMissing_SetsGrantedFalse_NoGrantCalls()
    {
        var (ctx, _, blueprintService) = BuildS2SGrantTestContext();
        ctx.Config.AgenticAppId = null;

        await NonDwBlueprintSetupOrchestrator.GrantOrInstructAgentIdentityAppPermissionsAsync(ctx, OneS2SSpec());

        ctx.Results.AgentIdentityS2SOutcome.Should().Be(Cli.Models.GrantOutcome.Failed,
            because: "when AgenticAppId (the SP object ID) is absent, grants cannot proceed and the outcome must be Failed");
        ctx.Results.PendingAgentIdentityAppRoleSpecs.Should().ContainSingle(s => s.ResourceName == "Test Resource",
            because: "the summary's hand-off must list the app role that could not be assigned");
        await blueprintService.DidNotReceive().GrantAppRoleAssignmentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When all GrantAppRoleAssignmentAsync calls return true, AgentIdentityS2SOutcome is Granted
    /// and no warning is added — this is the Global Admin success path.
    /// </summary>
    [Fact]
    public async Task GrantOrInstructAgentIdentityAppPermissions_AllGrantsSucceed_SetsGrantedTrue_NoWarnings()
    {
        var (ctx, graph, blueprintService) = BuildS2SGrantTestContext();

        graph.EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>())
            .Returns("agent-sp-object-id");

        blueprintService.GrantAppRoleAssignmentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Cli.Models.AppRoleGrantResult(AllSucceeded: true, AllAlreadyAssigned: false)));

        await NonDwBlueprintSetupOrchestrator.GrantOrInstructAgentIdentityAppPermissionsAsync(ctx, OneS2SSpec());

        ctx.Results.AgentIdentityS2SOutcome.Should().Be(Cli.Models.GrantOutcome.Granted,
            because: "when all app role assignments succeed, AgentIdentityS2SOutcome must be Granted");
        ctx.Results.HasWarnings.Should().BeFalse(
            because: "a successful S2S grant must not add any warnings to setup results");
    }

    /// <summary>
    /// Graph grant and az rest fallback both fail -> outcome Failed, warning added, PowerShell instructions logged (issue #460).
    /// </summary>
    [Fact]
    public async Task GrantOrInstructAgentIdentityAppPermissions_GraphAndAzRestBothFail_PrintsPowerShell_AddsWarning()
    {
        var (ctx, _, blueprintService) = BuildS2SGrantTestContext();

        const string agentSpId = "11111111-1111-1111-1111-111111111111";
        const string resourceAppId = "22222222-2222-2222-2222-222222222222";
        ctx.Config.AgenticAppId = agentSpId;
        var specs = new List<ResourcePermissionSpec>
        {
            new(resourceAppId, "Test Resource", [], false, AppRoleScopes: ["TestRole.ReadWrite"])
        };

        // Programmatic Graph path fails -> az rest fallback is reached.
        blueprintService.GrantAppRoleAssignmentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Cli.Models.AppRoleGrantResult(AllSucceeded: false, AllAlreadyAssigned: false)));

        // Stub each az rest call explicitly so the test does not depend on BuildMockExecutor's default.
        //   1. GET appRoleAssignments on the agent identity SP -> none existing.
        ctx.Executor.ExecuteAsync(
            "az", Arg.Is<string>(a => a.Contains("appRoleAssignments") && a.Contains("--method GET")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{\"value\":[]}", StandardError = string.Empty }));
        //   2. Resource-SP lookup returns an empty value array -> "resource SP not found" -> fallback fails.
        ctx.Executor.ExecuteAsync(
            "az", Arg.Is<string>(a => a.Contains("$filter=appId eq")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{\"value\":[]}", StandardError = string.Empty }));

        await NonDwBlueprintSetupOrchestrator.GrantOrInstructAgentIdentityAppPermissionsAsync(ctx, specs);

        // Prove the az rest fallback was actually attempted before falling through to PowerShell.
        await ctx.Executor.Received().ExecuteAsync(
            "az", Arg.Is<string>(a => a.Contains("$filter=appId eq")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        ctx.Results.AgentIdentityS2SOutcome.Should().Be(Cli.Models.GrantOutcome.Failed,
            because: "when both the Graph grant and the az rest fallback fail, AgentIdentityS2SOutcome must be Failed");
        ctx.Results.PendingAgentIdentityAppRoleSpecs.Should().ContainSingle(s => s.ResourceAppId == resourceAppId,
            because: "the summary's hand-off must list exactly the app role that failed");
        ctx.Results.HasWarnings.Should().BeTrue(
            because: "a failed S2S grant must add a warning so the setup summary shows Action Required");
        ctx.Results.Warnings.Should().ContainSingle()
            .Which.Should().Contain("PowerShell",
                because: "the warning must reference the printed PowerShell instructions so the user knows where to look");
    }

    /// <summary>
    /// Graph grant fails but az rest fallback succeeds -> outcome Granted, no warning, no PowerShell hand-off (issue #460).
    /// </summary>
    [Fact]
    public async Task GrantOrInstructAgentIdentityAppPermissions_GraphFailsAzRestSucceeds_SetsGranted_NoWarning()
    {
        var (ctx, _, blueprintService) = BuildS2SGrantTestContext();

        // AzRestS2SRunner validates both the agent identity SP id and the resource app id as
        // GUIDs before issuing any az call, so the fallback path requires real GUIDs here.
        const string agentSpId = "11111111-1111-1111-1111-111111111111";
        const string resourceAppId = "22222222-2222-2222-2222-222222222222";
        ctx.Config.AgenticAppId = agentSpId;
        var specs = new List<ResourcePermissionSpec>
        {
            new(resourceAppId, "Test Resource", [], false, AppRoleScopes: ["TestRole.ReadWrite"])
        };

        // Programmatic Graph path fails -> fallback is reached.
        blueprintService.GrantAppRoleAssignmentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Cli.Models.AppRoleGrantResult(AllSucceeded: false, AllAlreadyAssigned: false)));

        // Stub each az rest call explicitly so the test does not depend on BuildMockExecutor's default:
        //   1. GET appRoleAssignments on the agent identity SP -> no existing assignments.
        ctx.Executor.ExecuteAsync(
            "az", Arg.Is<string>(a => a.Contains("appRoleAssignments") && a.Contains("--method GET")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{\"value\":[]}", StandardError = string.Empty }));
        //   2. Resource-SP lookup -> SP id plus the role-value -> role-id map.
        ctx.Executor.ExecuteAsync(
            "az", Arg.Is<string>(a => a.Contains("$filter=appId eq")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult
            {
                ExitCode = 0,
                StandardOutput =
                    "{\"value\":[{\"id\":\"33333333-3333-3333-3333-333333333333\"," +
                    "\"appRoles\":[{\"value\":\"TestRole.ReadWrite\",\"id\":\"44444444-4444-4444-4444-444444444444\"}]}]}",
                StandardError = string.Empty
            }));
        //   3. POST of the new appRoleAssignment -> success.
        ctx.Executor.ExecuteAsync(
            "az", Arg.Is<string>(a => a.Contains("--method POST")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty }));

        await NonDwBlueprintSetupOrchestrator.GrantOrInstructAgentIdentityAppPermissionsAsync(ctx, specs);

        // The outcome is Granted because the az rest fallback actually POSTed the assignment.
        await ctx.Executor.Received(1).ExecuteAsync(
            "az", Arg.Is<string>(a => a.Contains("--method POST")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        ctx.Results.AgentIdentityS2SOutcome.Should().Be(Cli.Models.GrantOutcome.Granted,
            because: "when the az rest fallback assigns the app role, the agent identity S2S grant succeeded");
        ctx.Results.PendingAgentIdentityAppRoleSpecs.Should().BeEmpty(
            because: "a completed fallback leaves no app role for the summary's hand-off");
        ctx.Results.HasWarnings.Should().BeFalse(
            because: "a successful az rest fallback must not surface a PowerShell hand-off warning");
    }

    // -------------------------------------------------------------------------
    // GrantAgentIdentityS2SPermissionsAsync wiring (issue #460 redundancy gate)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Inherited roles (Phase 2 + blueprint grant) -> direct grant skipped, outcome Granted (issue #460).
    /// </summary>
    [Fact]
    public async Task GrantAgentIdentityS2SPermissions_SkipsDirectGrant_WhenRolesInherited()
    {
        var (ctx, _, blueprintService) = BuildS2SGrantTestContext();
        ctx.Results.BatchPermissionsPhase2Completed = true;
        ctx.Results.BlueprintS2SOutcome = Cli.Models.GrantOutcome.Granted;

        await NonDwBlueprintSetupOrchestrator.GrantAgentIdentityS2SPermissionsAsync(ctx, OneS2SSpec());

        await blueprintService.DidNotReceive().GrantAppRoleAssignmentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>());
        ctx.Results.AgentIdentityS2SOutcome.Should().Be(Cli.Models.GrantOutcome.Granted,
            because: "an inherited role is effectively granted on the agent identity, so the outcome must be Granted without a direct grant");
    }

    /// <summary>
    /// No inheritance -> the direct grant runs (developer / non-inherited path, issue #460).
    /// </summary>
    [Fact]
    public async Task GrantAgentIdentityS2SPermissions_PerformsDirectGrant_WhenNotInherited()
    {
        var (ctx, _, blueprintService) = BuildS2SGrantTestContext();
        // Defaults: BatchPermissionsPhase2Completed = false -> predicate false -> grant runs.
        blueprintService.GrantAppRoleAssignmentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Cli.Models.AppRoleGrantResult(AllSucceeded: true, AllAlreadyAssigned: false)));

        await NonDwBlueprintSetupOrchestrator.GrantAgentIdentityS2SPermissionsAsync(ctx, OneS2SSpec());

        await blueprintService.Received(1).GrantAppRoleAssignmentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>());
        ctx.Results.AgentIdentityS2SOutcome.Should().Be(Cli.Models.GrantOutcome.Granted,
            because: "without inheritance the direct grant runs and, when it succeeds, the outcome is Granted");
        ctx.Results.NoS2SAppRolesToGrant.Should().BeFalse(
            because: "an app role was requested, so the summary must report the S2S grant");
    }

    /// <summary>
    /// No S2S specs -> skip does not fire; outcome stays NotApplicable, not falsely Granted (issue #460).
    /// </summary>
    [Fact]
    public async Task GrantAgentIdentityS2SPermissions_DoesNotClaimGranted_WhenNoS2SSpecs_EvenIfInherited()
    {
        var (ctx, _, blueprintService) = BuildS2SGrantTestContext();
        ctx.Results.BatchPermissionsPhase2Completed = true;
        ctx.Results.BlueprintS2SOutcome = Cli.Models.GrantOutcome.Granted;
        var specsWithNoAppRoles = new List<ResourcePermissionSpec>
        {
            new("resource-app-id", "Test Resource", ["user_impersonation"], false)
        };

        await NonDwBlueprintSetupOrchestrator.GrantAgentIdentityS2SPermissionsAsync(ctx, specsWithNoAppRoles);

        await blueprintService.DidNotReceive().GrantAppRoleAssignmentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>());
        ctx.Results.AgentIdentityS2SOutcome.Should().Be(Cli.Models.GrantOutcome.NotApplicable,
            because: "with no S2S specs there is nothing to grant or inherit, so the outcome must remain NotApplicable");
        ctx.Results.NoS2SAppRolesToGrant.Should().BeTrue(
            because: "the summary needs to know the s2s/both grant step had no app role to grant");
    }

    // -------------------------------------------------------------------------
    // AgentIdentityInheritsBlueprintAppRoles predicate (issue #460 redundancy gate)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(true, Cli.Models.GrantOutcome.Granted, true)]    // inheritance + blueprint grant -> inherited
    [InlineData(false, Cli.Models.GrantOutcome.Granted, false)]  // no inheritance -> must grant directly
    [InlineData(true, Cli.Models.GrantOutcome.Failed, false)]    // blueprint SP lacks the role -> nothing to inherit
    [InlineData(true, Cli.Models.GrantOutcome.NotApplicable, false)] // blueprint grant never ran
    public void AgentIdentityInheritsBlueprintAppRoles_ReturnsTrue_OnlyWhenInheritanceAndBlueprintGrantBothSucceed(
        bool phase2Completed, Cli.Models.GrantOutcome blueprintS2SOutcome, bool expected)
    {
        var results = new SetupResults
        {
            BatchPermissionsPhase2Completed = phase2Completed,
            BlueprintS2SOutcome = blueprintS2SOutcome,
        };

        NonDwBlueprintSetupOrchestrator.AgentIdentityInheritsBlueprintAppRoles(results)
            .Should().Be(expected,
                because: "the agent identity inherits the blueprint's app roles only when inheritable permissions (allAllowed) were configured AND the blueprint SP was actually granted the roles");
    }
}
