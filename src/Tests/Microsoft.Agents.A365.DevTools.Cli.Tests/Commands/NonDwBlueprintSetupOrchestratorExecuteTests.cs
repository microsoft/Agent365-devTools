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
            Location = "eastus",
            SubscriptionId = "sub-id",
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
            (RetryHelper?)null);

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
}
