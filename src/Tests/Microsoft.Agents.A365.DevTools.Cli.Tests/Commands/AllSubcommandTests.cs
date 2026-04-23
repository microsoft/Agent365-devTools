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

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Unit tests for AllSubcommand helpers.
/// </summary>
public class AllSubcommandTests : IDisposable
{
    private readonly string _tempDir;

    public AllSubcommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AllSubcommandTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // -----------------------------------------------------------------------
    // BackupAndClearStaleConfigAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task BackupAndClearStaleConfig_WhenTenantMatches_LeavesFilesUntouched()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        File.WriteAllText(configPath, """{"tenantId": "same-tenant"}""");

        await AllSubcommand.BackupAndClearStaleConfigAsync(configPath, "same-tenant", NullLogger.Instance);

        File.Exists(configPath).Should().BeTrue(
            because: "files must not be touched when the tenant matches");
        Directory.GetFiles(_tempDir, "*.bak.*").Should().BeEmpty(
            because: "no backup should be created when the tenant is the same");
    }

    [Fact]
    public async Task BackupAndClearStaleConfig_WhenConfigFileAbsent_DoesNothing()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        // deliberately not created

        await AllSubcommand.BackupAndClearStaleConfigAsync(configPath, "new-tenant", NullLogger.Instance);

        Directory.GetFiles(_tempDir).Should().BeEmpty(
            because: "nothing should be written when no config file exists");
    }

    [Fact]
    public async Task BackupAndClearStaleConfig_WhenTenantDiffers_BacksUpConfigAndRemovesOriginal()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        File.WriteAllText(configPath, """{"tenantId": "old-tenant"}""");

        await AllSubcommand.BackupAndClearStaleConfigAsync(configPath, "new-tenant", NullLogger.Instance);

        File.Exists(configPath).Should().BeFalse(
            because: "the original config file must be removed when the tenant differs");
        Directory.GetFiles(_tempDir, "a365.config.json.bak.*").Should().HaveCount(1,
            because: "the old config must be backed up with a timestamp suffix");
    }

    [Fact]
    public async Task BackupAndClearStaleConfig_WhenTenantDiffers_AlsoBacksUpGeneratedConfig()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        var generatedPath = Path.Combine(_tempDir, "a365.generated.config.json");
        File.WriteAllText(configPath, """{"tenantId": "old-tenant"}""");
        File.WriteAllText(generatedPath, """{"agentBlueprintId": "bp-from-old-tenant"}""");

        await AllSubcommand.BackupAndClearStaleConfigAsync(configPath, "new-tenant", NullLogger.Instance);

        File.Exists(generatedPath).Should().BeFalse(
            because: "the generated config must also be removed when the tenant differs");
        Directory.GetFiles(_tempDir, "a365.generated.config.json.bak.*").Should().HaveCount(1,
            because: "the generated config must be backed up alongside the static config");
    }

    [Fact]
    public async Task BackupAndClearStaleConfig_WhenTenantDiffersButNoGeneratedConfig_OnlyBacksUpStaticConfig()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        File.WriteAllText(configPath, """{"tenantId": "old-tenant"}""");
        // deliberately no a365.generated.config.json

        await AllSubcommand.BackupAndClearStaleConfigAsync(configPath, "new-tenant", NullLogger.Instance);

        Directory.GetFiles(_tempDir, "a365.config.json.bak.*").Should().HaveCount(1,
            because: "the static config must be backed up");
        Directory.GetFiles(_tempDir, "a365.generated.config.json.bak.*").Should().BeEmpty(
            because: "no generated config backup should be created when the file did not exist");
    }

    [Fact]
    public async Task BackupAndClearStaleConfig_WhenConfigIsMalformedJson_BacksUpAsIfMismatch()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        File.WriteAllText(configPath, "this is not valid json");

        await AllSubcommand.BackupAndClearStaleConfigAsync(configPath, "new-tenant", NullLogger.Instance);

        File.Exists(configPath).Should().BeFalse(
            because: "a malformed config file cannot be trusted and must be backed up");
        Directory.GetFiles(_tempDir, "a365.config.json.bak.*").Should().HaveCount(1);
    }

    [Fact]
    public async Task BackupAndClearStaleConfig_TenantComparisonIsCaseInsensitive()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        File.WriteAllText(configPath, """{"tenantId": "TENANT-ABC"}""");

        await AllSubcommand.BackupAndClearStaleConfigAsync(configPath, "tenant-abc", NullLogger.Instance);

        File.Exists(configPath).Should().BeTrue(
            because: "tenant ID comparison must be case-insensitive");
        Directory.GetFiles(_tempDir, "*.bak.*").Should().BeEmpty(
            because: "no backup should be created when tenants match case-insensitively");
    }

    // -----------------------------------------------------------------------
    // ExecuteMessagingEndpointStepAsync
    // -----------------------------------------------------------------------

    private static SetupContext BuildMessagingEndpointContext(
        ITeamsGraphBackendConfigurator backendConfigurator,
        bool isM365,
        string? blueprintId = "blueprint-id")
    {
        var config = new Agent365Config
        {
            AiTeammate = false,
            TenantId = "tenant-id",
            AgentBlueprintId = blueprintId,
            AgentIdentityDisplayName = "Test Agent",
            ClientAppId = "client-app-id",
            MessagingEndpoint = "https://example.com/api/messages",
        };

        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        Func<Task<string?>> noOpLoginHint = () => Task.FromResult<string?>(null);

        var graph = Substitute.ForPartsOf<GraphApiService>(
            Substitute.For<ILogger<GraphApiService>>(),
            executor,
            Substitute.For<IAuthenticationService>(),
            (System.Net.Http.HttpMessageHandler?)null,
            (IMicrosoftGraphTokenProvider?)null,
            noOpLoginHint,
            (string?)null,
            (RetryHelper?)null,
            (TimeSpan?)TimeSpan.Zero);

        return new SetupContext(
            config: config,
            results: new SetupResults(),
            logger: NullLogger.Instance,
            configFile: new FileInfo("a365.config.json"),
            generatedConfigPath: "a365.generated.config.json",
            correlationId: "test-correlation-id",
            skipInfrastructure: true,
            skipRequirements: true,
            cancellationToken: CancellationToken.None,
            configService: Substitute.For<IConfigService>(),
            executor: executor,
            backendConfigurator: backendConfigurator,
            authValidator: Substitute.For<AzureAuthValidator>(
                NullLogger<AzureAuthValidator>.Instance, executor),
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
            isM365: isM365,
            loginHintResolver: noOpLoginHint);
    }

    [Fact]
    public async Task ExecuteMessagingEndpointStepAsync_WhenNotM365_DoesNotCallConfigurator()
    {
        var backend = Substitute.For<ITeamsGraphBackendConfigurator>();
        var ctx = BuildMessagingEndpointContext(backend, isM365: false);

        await AllSubcommand.ExecuteMessagingEndpointStepAsync(ctx);

        await backend.DidNotReceiveWithAnyArgs().SetBackendConfigurationAsync(
            default!, default!, default);
        ctx.Results.MessagingEndpointResult.Should().BeNull(
            because: "a non-M365 agent must leave the result unset to drive the 'skipped' summary row");
        ctx.Results.MessagingEndpointRegistered.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteMessagingEndpointStepAsync_WhenM365ButBlueprintIdMissing_DoesNotCallConfigurator()
    {
        var backend = Substitute.For<ITeamsGraphBackendConfigurator>();
        var ctx = BuildMessagingEndpointContext(backend, isM365: true, blueprintId: null);

        await AllSubcommand.ExecuteMessagingEndpointStepAsync(ctx);

        await backend.DidNotReceiveWithAnyArgs().SetBackendConfigurationAsync(
            default!, default!, default);
        ctx.Results.MessagingEndpointResult.Should().BeNull(
            because: "without a blueprint ID there is nothing to register");
    }

    [Fact]
    public async Task ExecuteMessagingEndpointStepAsync_WhenConfiguratorReturnsCreated_SetsRegisteredTrue()
    {
        var backend = Substitute.For<ITeamsGraphBackendConfigurator>();
        backend.SetBackendConfigurationAsync(
            "blueprint-id",
            "https://example.com/api/messages",
            "test-correlation-id")
            .Returns((EndpointRegistrationResult.Created, (string?)null));

        var ctx = BuildMessagingEndpointContext(backend, isM365: true);

        await AllSubcommand.ExecuteMessagingEndpointStepAsync(ctx);

        ctx.Results.MessagingEndpointResult.Should().Be(EndpointRegistrationResult.Created);
        ctx.Results.MessagingEndpointRegistered.Should().BeTrue();
        ctx.Results.EndpointAlreadyExisted.Should().BeFalse();
        ctx.Results.MessagingEndpoint.Should().Be("https://example.com/api/messages");
        ctx.Results.MessagingEndpointFailureReason.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteMessagingEndpointStepAsync_WhenConfiguratorReturnsSkippedContractMismatch_SetsResultButNotRegistered()
    {
        var backend = Substitute.For<ITeamsGraphBackendConfigurator>();
        backend.SetBackendConfigurationAsync(
            "blueprint-id",
            "https://example.com/api/messages",
            "test-correlation-id")
            .Returns((EndpointRegistrationResult.SkippedContractMismatch, (string?)null));

        var ctx = BuildMessagingEndpointContext(backend, isM365: true);

        await AllSubcommand.ExecuteMessagingEndpointStepAsync(ctx);

        ctx.Results.MessagingEndpointResult.Should().Be(EndpointRegistrationResult.SkippedContractMismatch);
        ctx.Results.MessagingEndpointRegistered.Should().BeFalse(
            because: "SkippedContractMismatch means the server still runs the pre-migration contract — the user must register manually");
        ctx.Results.MessagingEndpointFailureReason.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteMessagingEndpointStepAsync_NotOwnerFailure_SetsFailureReasonNotOwner()
    {
        var backend = Substitute.For<ITeamsGraphBackendConfigurator>();
        backend.SetBackendConfigurationAsync(
            "blueprint-id",
            "https://example.com/api/messages",
            "test-correlation-id")
            .Returns((EndpointRegistrationResult.Failed, (string?)"NotOwner"));

        var ctx = BuildMessagingEndpointContext(backend, isM365: true);

        await AllSubcommand.ExecuteMessagingEndpointStepAsync(ctx);

        ctx.Results.MessagingEndpointResult.Should().Be(EndpointRegistrationResult.Failed);
        ctx.Results.MessagingEndpointFailureReason.Should().Be("NotOwner");
        ctx.Results.MessagingEndpointRegistered.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteMessagingEndpointStepAsync_GenericFailure_SetsFailureReasonOther()
    {
        var backend = Substitute.For<ITeamsGraphBackendConfigurator>();
        backend.SetBackendConfigurationAsync(
            "blueprint-id",
            "https://example.com/api/messages",
            "test-correlation-id")
            .Returns((EndpointRegistrationResult.Failed, (string?)"Other"));

        var ctx = BuildMessagingEndpointContext(backend, isM365: true);

        await AllSubcommand.ExecuteMessagingEndpointStepAsync(ctx);

        ctx.Results.MessagingEndpointResult.Should().Be(EndpointRegistrationResult.Failed);
        ctx.Results.MessagingEndpointFailureReason.Should().Be("Other");
        ctx.Results.MessagingEndpointRegistered.Should().BeFalse();
    }
}
