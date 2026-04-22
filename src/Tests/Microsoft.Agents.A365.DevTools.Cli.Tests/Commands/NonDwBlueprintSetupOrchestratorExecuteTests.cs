// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
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

    private static SetupContext BuildContext(Agent365Config? config = null, bool skipRequirements = true)
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
            botConfigurator: Substitute.For<IBotConfigurator>(),
            authValidator: authValidator,
            platformDetector: Substitute.ForPartsOf<PlatformDetector>(
                Substitute.For<ILogger<PlatformDetector>>()),
            graphApiService: graphApiService,
            blueprintService: blueprintService,
            blueprintLookupService: blueprintLookupService,
            federatedCredentialService: federatedCredentialService,
            clientAppValidator: Substitute.For<IClientAppValidator>(),
            loginHintResolver: () => Task.FromResult<string?>(null));
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
    // GrantAgentIdentityPermissionsAsync tests
    // -------------------------------------------------------------------------

    private static (SetupContext ctx, GraphApiService graph) BuildGrantTestContext()
    {
        var graph = Substitute.ForPartsOf<GraphApiService>();

        // Prevent real HTTP calls: return null for existing-grant lookup in CreateOrUpdateOauth2PermissionGrantAsync.
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
        };

        var mockExecutor = BuildMockExecutor();
        var configService = Substitute.For<IConfigService>();
        configService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

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
            botConfigurator: Substitute.For<IBotConfigurator>(),
            authValidator: Substitute.For<AzureAuthValidator>(
                NullLogger<AzureAuthValidator>.Instance, mockExecutor),
            platformDetector: Substitute.ForPartsOf<PlatformDetector>(
                Substitute.For<ILogger<PlatformDetector>>()),
            graphApiService: graph,
            blueprintService: Substitute.ForPartsOf<AgentBlueprintService>(
                Substitute.For<ILogger<AgentBlueprintService>>(), graph),
            blueprintLookupService: Substitute.ForPartsOf<BlueprintLookupService>(
                Substitute.For<ILogger<BlueprintLookupService>>(), graph),
            federatedCredentialService: Substitute.ForPartsOf<FederatedCredentialService>(
                Substitute.For<ILogger<FederatedCredentialService>>(), graph),
            clientAppValidator: Substitute.For<IClientAppValidator>(),
            loginHintResolver: () => Task.FromResult<string?>(null));

        return (ctx, graph);
    }

    private static List<ResourcePermissionSpec> OneSpec() =>
        [new ResourcePermissionSpec("resource-app-id", "Test Resource", ["user_impersonation"], false)];

    /// <summary>
    /// When all SP lookups and grant POSTs succeed, no warnings are added to SetupResults.
    /// </summary>
    [Fact]
    public async Task GrantAgentIdentityPermissions_HappyPath_NoWarningsAdded()
    {
        var (ctx, graph) = BuildGrantTestContext();

        graph.GetCurrentUserObjectIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("user-object-id");

        graph.EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>())
            .Returns("sp-object-id");

        graph.GraphPostWithResponseAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(new GraphApiService.GraphResponse { IsSuccess = true, Body = "{}" });

        await NonDwBlueprintSetupOrchestrator.GrantAgentIdentityPermissionsAsync(ctx, OneSpec());

        ctx.Results.HasWarnings.Should().BeFalse(because: "all grants succeeded — no warnings expected");
    }

    /// <summary>
    /// When the agent identity SP cannot be resolved, no grant POSTs are made and the method
    /// returns early without adding a warning to SetupResults (SP lookup failure is logged, not a Results entry).
    /// </summary>
    [Fact]
    public async Task GrantAgentIdentityPermissions_AgentIdentitySpNotFound_NoGrantCallsMade()
    {
        var (ctx, graph) = BuildGrantTestContext();

        graph.EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>())
            .Returns((string?)null);

        await NonDwBlueprintSetupOrchestrator.GrantAgentIdentityPermissionsAsync(ctx, OneSpec());

        await graph.DidNotReceive().GraphPostWithResponseAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }

    /// <summary>
    /// When a permission grant POST fails, anyFailed is set and a warning is added to
    /// ctx.Results.Warnings so the setup summary reflects the partial failure.
    /// </summary>
    [Fact]
    public async Task GrantAgentIdentityPermissions_GrantFails_AddsWarningToResults()
    {
        var (ctx, graph) = BuildGrantTestContext();

        graph.EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>())
            .Returns("sp-object-id");

        graph.GraphPostWithResponseAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(new GraphApiService.GraphResponse { IsSuccess = false, Body = "Unauthorized" });

        await NonDwBlueprintSetupOrchestrator.GrantAgentIdentityPermissionsAsync(ctx, OneSpec());

        ctx.Results.HasWarnings.Should().BeTrue(because: "a grant failure must surface in setup results");
        ctx.Results.Warnings.Should().ContainSingle()
            .Which.Should().Contain("Entra portal",
                because: "the warning must tell the user where to manually grant permissions");
    }

    /// <summary>
    /// When specs is empty the method returns immediately — no SP lookups or grant calls made.
    /// </summary>
    [Fact]
    public async Task GrantAgentIdentityPermissions_EmptySpecs_NoCallsAndNoSideEffects()
    {
        var (ctx, graph) = BuildGrantTestContext();

        await NonDwBlueprintSetupOrchestrator.GrantAgentIdentityPermissionsAsync(ctx, []);

        ctx.Results.HasWarnings.Should().BeFalse();
        await graph.DidNotReceive().EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>());
    }

    // -------------------------------------------------------------------------
    // Idempotency tests — Steps 5 & 6
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a SetupContext suited for testing the agent identity + registration steps
    /// (Steps 5–6) via the AgentInstanceOnly path.
    /// Returns the context, graph service mock, and blueprint service mock so tests can
    /// configure stub return values.
    /// </summary>
    private static (SetupContext ctx, GraphApiService graph, AgentBlueprintService blueprintService)
        BuildIdempotencyTestContext(Agent365Config? config = null)
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
            logger: Substitute.For<ILogger>(),
            configFile: new FileInfo("a365.config.json"),
            generatedConfigPath: "a365.generated.config.json",
            correlationId: "test-correlation-id",
            skipInfrastructure: true,
            skipRequirements: true,
            cancellationToken: CancellationToken.None,
            configService: configService,
            executor: mockExecutor,
            botConfigurator: Substitute.For<IBotConfigurator>(),
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
            agentInstanceOnly: true,
            loginHintResolver: () => Task.FromResult<string?>(null));

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
            .Returns("new-reg-id");

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        ctx.Results.AgentIdentityId.Should().Be("existing-sp-id",
            because: "the existing agent identity must be reused");
        await graph.DidNotReceive().CreateAgentIdentityDelegatedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await ctx.ConfigService.Received().SaveStateAsync(
            Arg.Is<Agent365Config>(c => c.AgenticAppId == "existing-sp-id"),
            Arg.Any<string>());
    }

    /// <summary>
    /// Step 5: When the lookup returns null, CreateAgentIdentityDelegatedAsync must be called.
    /// </summary>
    [Fact]
    public async Task Step5_CreatesNewIdentity_WhenNotFoundByApiLookup()
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
            .Returns("new-reg-id");

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        ctx.Results.AgentIdentityId.Should().Be("new-sp-id",
            because: "a new agent identity must be created when no existing one is found");
        await graph.Received(1).CreateAgentIdentityDelegatedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
            .Returns("new-reg-id");

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
            .Returns("new-reg-id");

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
            .Returns("new-reg-id");

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
            .Returns("new-reg-id");

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
            .Returns("new-reg-id");

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
            .Returns("new-reg-id");

        await NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx);

        ctx.Results.AgentRegistrationAlreadyExisted.Should().BeFalse(
            because: "the registration was freshly created, not reused");
        await graph.DidNotReceive().AgentRegistrationExistsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
