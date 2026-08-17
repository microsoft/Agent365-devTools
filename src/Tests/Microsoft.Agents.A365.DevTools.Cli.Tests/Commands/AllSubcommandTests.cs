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

    // MergeCachedBootstrapState

    [Fact]
    public void MergeCachedBootstrapState_SameAgentIdentity_MergesGeneratedState()
    {
        var genConfig = new Agent365Config
        {
            AgentIdentityDisplayName = "Support Europe Identity",
            AgentBlueprintId = "cached-blueprint-id",
            AgentBlueprintObjectId = "cached-blueprint-object-id",
            AgentBlueprintServicePrincipalObjectId = "cached-blueprint-sp-id",
            AgentBlueprintClientSecret = "cached-secret",
            AgentBlueprintClientSecretProtected = true,
            AgentRegistrationId = "cached-registration-id",
            AgenticAppId = "cached-agentic-app-id",
            AgentInstanceId = "cached-instance-id",
            AgenticUserId = "cached-agentic-user-id",
            ManagedIdentityPrincipalId = "cached-managed-identity-id",
            BotId = "cached-bot-id",
            BotMsaAppId = "cached-bot-app-id",
            BotMessagingEndpoint = "https://example.test/api/messages",
            AzureOpenAIEndpoint = "https://openai.example.test",
            AzureOpenAIApiKey = "cached-openai-key",
            Completed = true,
            CompletedAt = DateTime.UtcNow,
        };
        genConfig.ResourceConsents.Add(new ResourceConsent { ResourceAppId = "resource-app-id" });
        var nonDwConfig = new Agent365Config { AgentIdentityDisplayName = "Support Europe Identity" };

        AllSubcommand.MergeCachedBootstrapState(nonDwConfig, genConfig);

        nonDwConfig.AgentBlueprintId.Should().Be("cached-blueprint-id",
            because: "a bootstrap rerun must reuse the selected blueprint");
        nonDwConfig.AgentBlueprintObjectId.Should().Be("cached-blueprint-object-id",
            because: "the stable object ID prevents ambiguous display-name discovery");
        nonDwConfig.AgentBlueprintServicePrincipalObjectId.Should().Be("cached-blueprint-sp-id");
        nonDwConfig.AgentBlueprintClientSecret.Should().Be("cached-secret");
        nonDwConfig.AgentBlueprintClientSecretProtected.Should().BeTrue();
        nonDwConfig.ResourceConsents.Should().ContainSingle();
        nonDwConfig.AgentRegistrationId.Should().Be("cached-registration-id",
            because: "an exact rerun for the same agent identity must reuse the existing registration, not recreate it");
        nonDwConfig.AgenticAppId.Should().Be("cached-agentic-app-id",
            because: "an exact rerun for the same agent identity must reuse the existing agent identity, not recreate it");
        nonDwConfig.AgentInstanceId.Should().Be("cached-instance-id");
        nonDwConfig.AgenticUserId.Should().Be("cached-agentic-user-id");
        nonDwConfig.ManagedIdentityPrincipalId.Should().Be("cached-managed-identity-id");
        nonDwConfig.BotId.Should().Be("cached-bot-id");
        nonDwConfig.BotMsaAppId.Should().Be("cached-bot-app-id");
        nonDwConfig.BotMessagingEndpoint.Should().Be("https://example.test/api/messages");
        nonDwConfig.AzureOpenAIEndpoint.Should().Be("https://openai.example.test");
        nonDwConfig.AzureOpenAIApiKey.Should().Be("cached-openai-key");
        nonDwConfig.Completed.Should().BeTrue();
        nonDwConfig.CompletedAt.Should().Be(genConfig.CompletedAt);
    }

    [Fact]
    public void MergeCachedBootstrapState_DifferentAgentIdentity_MergesBlueprintStateOnly()
    {
        var genConfig = new Agent365Config
        {
            AgentIdentityDisplayName = "Agent A Identity",
            AgentBlueprintId = "shared-blueprint-id",
            AgentBlueprintObjectId = "shared-blueprint-object-id",
            AgentBlueprintClientSecret = "shared-secret",
            AgentRegistrationId = "agent-a-registration-id",
            AgenticAppId = "agent-a-agentic-app-id",
            BotId = "agent-a-bot-id",
        };
        genConfig.ResourceConsents.Add(new ResourceConsent { ResourceAppId = "resource-app-id" });
        var nonDwConfig = new Agent365Config { AgentIdentityDisplayName = "Agent B Identity" };

        AllSubcommand.MergeCachedBootstrapState(nonDwConfig, genConfig);

        nonDwConfig.AgentBlueprintId.Should().Be("shared-blueprint-id",
            because: "AgentBlueprintId is blueprint-scoped and safe to share across agent identities hosted under the same blueprint");
        nonDwConfig.AgentBlueprintObjectId.Should().Be("shared-blueprint-object-id");
        nonDwConfig.AgentBlueprintClientSecret.Should().Be("shared-secret");
        nonDwConfig.ResourceConsents.Should().ContainSingle();
        nonDwConfig.AgentRegistrationId.Should().BeNull(
            because: "a shared blueprint does not make another agent's registration reusable");
        nonDwConfig.AgenticAppId.Should().BeNull(
            because: "Agent A's identity must not be handed to Agent B");
        nonDwConfig.BotId.Should().BeNull(
            because: "bot state belongs to the specific agent identity");
    }

    [Fact]
    public void MergeCachedBootstrapState_DoesNotOverwriteAlreadyResolvedIds()
    {
        var genConfig = new Agent365Config
        {
            AgentIdentityDisplayName = "Support Europe Identity",
            AgentBlueprintId = "cached-blueprint-id",
            AgenticAppId = "cached-agentic-app-id",
        };
        var nonDwConfig = new Agent365Config
        {
            AgentIdentityDisplayName = "Support Europe Identity",
            AgentBlueprintId = "already-resolved-blueprint-id",
        };

        AllSubcommand.MergeCachedBootstrapState(nonDwConfig, genConfig);

        nonDwConfig.AgentBlueprintId.Should().Be("already-resolved-blueprint-id",
            because: "an already-resolved value on the target config must not be clobbered by the cached one");
    }

    // RefuseIfDirectoryBelongsToDifferentAgentIdentityAsync

    [Fact]
    public async Task RefuseIfDirectoryBelongsToDifferentAgentIdentity_WhenConfigFileAbsent_ReturnsFalse()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        // deliberately not created — fresh directory, must always be allowed to proceed.

        var refused = await AllSubcommand.RefuseIfDirectoryBelongsToDifferentAgentIdentityAsync(
            configPath, "Agent B Identity", "Agent B", NullLogger.Instance);

        refused.Should().BeFalse(because: "a fresh directory with no existing config must always be allowed to proceed");
    }

    [Fact]
    public async Task RefuseIfDirectoryBelongsToDifferentAgentIdentity_WhenIdentityMatches_ReturnsFalse()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        File.WriteAllText(configPath, """{"tenantId": "tenant", "agentIdentityDisplayName": "Agent A Identity"}""");

        var refused = await AllSubcommand.RefuseIfDirectoryBelongsToDifferentAgentIdentityAsync(
            configPath, "Agent A Identity", "Agent A", NullLogger.Instance);

        refused.Should().BeFalse(
            because: "an exact --agent-name rerun in the same directory must remain idempotent and must not be refused");
        File.Exists(configPath).Should().BeTrue(because: "the check must not mutate the file either way");
    }

    [Fact]
    public async Task RefuseIfDirectoryBelongsToDifferentAgentIdentity_WhenIdentityDiffers_ReturnsTrueAndLeavesFileUntouched()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        File.WriteAllText(configPath, """{"tenantId": "tenant", "agentIdentityDisplayName": "Agent A Identity"}""");
        var logger = Substitute.For<ILogger>();

        var refused = await AllSubcommand.RefuseIfDirectoryBelongsToDifferentAgentIdentityAsync(
            configPath, "Agent B Identity", "Agent B", logger);

        refused.Should().BeTrue(
            because: "the current config format supports only one agent identity per working directory");
        File.ReadAllText(configPath).Should().Contain("Agent A Identity",
            because: "the prior agent's static config must be left completely untouched, not silently overwritten or merged");
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Agent A Identity") && o.ToString()!.Contains("only one agent identity per working directory")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task RefuseIfDirectoryBelongsToDifferentAgentIdentity_WhenConfigHasNoIdentityField_ReturnsFalse()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        File.WriteAllText(configPath, """{"tenantId": "tenant"}""");

        var refused = await AllSubcommand.RefuseIfDirectoryBelongsToDifferentAgentIdentityAsync(
            configPath, "Agent B Identity", "Agent B", NullLogger.Instance);

        refused.Should().BeFalse(because: "with no identity recorded on disk there is nothing to conflict with");
    }

    // -----------------------------------------------------------------------
    // ExecuteMessagingEndpointStepAsync
    // -----------------------------------------------------------------------

    private static SetupContext BuildMessagingEndpointContext(
        ITeamsGraphBackendConfigurator backendConfigurator,
        bool isM365,
        string? blueprintId = "blueprint-id",
        string? configEndpoint = "https://example.com/api/messages",
        string? messagingEndpointOverride = null)
    {
        var config = new Agent365Config
        {
            AiTeammate = false,
            TenantId = "tenant-id",
            AgentBlueprintId = blueprintId,
            AgentIdentityDisplayName = "Test Agent",
            ClientAppId = "client-app-id",
            MessagingEndpoint = configEndpoint ?? string.Empty,
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
            loginHintResolver: noOpLoginHint,
            messagingEndpointOverride: messagingEndpointOverride,
            // Tests are non-interactive: a missing endpoint must defer rather than block on a console
            // prompt. This keeps the result deterministic regardless of test runner (the VS test host
            // does not redirect stdin the way 'dotnet test' does, so Console.IsInputRedirected is unreliable).
            nonInteractive: true);
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
    public async Task ExecuteMessagingEndpointStepAsync_WhenM365ButBlueprintIdMissing_RecordsBlueprintMissingFailure()
    {
        var backend = Substitute.For<ITeamsGraphBackendConfigurator>();
        var ctx = BuildMessagingEndpointContext(backend, isM365: true, blueprintId: null);

        await AllSubcommand.ExecuteMessagingEndpointStepAsync(ctx);

        await backend.DidNotReceiveWithAnyArgs().SetBackendConfigurationAsync(
            default!, default!, default);
        ctx.Results.MessagingEndpointResult.Should().Be(
            EndpointRegistrationResult.Failed,
            because: "the step must record a distinct failure state so the summary doesn't misreport it as 'skipped (non-M365 agent)' — null is reserved for non-M365");
        ctx.Results.MessagingEndpointFailureReason.Should().Be(
            MessagingEndpointFailureReasons.BlueprintMissing,
            because: "a dedicated reason code lets the summary point the user at the blueprint step rather than generic retry guidance");
        ctx.Results.Warnings.Should().ContainSingle(
            w => w.Contains("agent blueprint ID is missing", StringComparison.OrdinalIgnoreCase),
            because: "the operator needs a warning surfaced in the summary explaining why the step was skipped");
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
            .Returns((EndpointRegistrationResult.Failed, (string?)MessagingEndpointFailureReasons.NotOwner));

        var ctx = BuildMessagingEndpointContext(backend, isM365: true);

        await AllSubcommand.ExecuteMessagingEndpointStepAsync(ctx);

        ctx.Results.MessagingEndpointResult.Should().Be(EndpointRegistrationResult.Failed);
        ctx.Results.MessagingEndpointFailureReason.Should().Be(MessagingEndpointFailureReasons.NotOwner);
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
            .Returns((EndpointRegistrationResult.Failed, (string?)MessagingEndpointFailureReasons.Other));

        var ctx = BuildMessagingEndpointContext(backend, isM365: true);

        await AllSubcommand.ExecuteMessagingEndpointStepAsync(ctx);

        ctx.Results.MessagingEndpointResult.Should().Be(EndpointRegistrationResult.Failed);
        ctx.Results.MessagingEndpointFailureReason.Should().Be(MessagingEndpointFailureReasons.Other);
        ctx.Results.MessagingEndpointRegistered.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteMessagingEndpointStepAsync_WhenM365AndEndpointMissing_DefersWithoutCallingConfigurator()
    {
        // No endpoint in config, no --messaging-endpoint override, and a redirected stdin (the test
        // host) so the interactive prompt is skipped: the step must defer, not fail.
        var backend = Substitute.For<ITeamsGraphBackendConfigurator>();
        var ctx = BuildMessagingEndpointContext(backend, isM365: true, configEndpoint: null);

        await AllSubcommand.ExecuteMessagingEndpointStepAsync(ctx);

        await backend.DidNotReceiveWithAnyArgs().SetBackendConfigurationAsync(default!, default!, default);
        ctx.Results.MessagingEndpointResult.Should().Be(EndpointRegistrationResult.Failed,
            because: "an absent endpoint is recorded as Failed+NotConfigured so the summary can render it as deferred");
        ctx.Results.MessagingEndpointFailureReason.Should().Be(MessagingEndpointFailureReasons.NotConfigured,
            because: "NotConfigured is the deferred reason the summary reframes as 'configure after deploy'");
        ctx.Results.MessagingEndpointRegistered.Should().BeFalse();
        ctx.Results.Warnings.Should().BeEmpty(
            because: "deferring an endpoint is expected and must not add a warning that downgrades the status line");
    }

    [Fact]
    public async Task ExecuteMessagingEndpointStepAsync_WhenOverrideProvidedAndConfigEmpty_RegistersUsingOverride()
    {
        const string overrideUrl = "https://override.example.com/api/messages";
        var backend = Substitute.For<ITeamsGraphBackendConfigurator>();
        backend.SetBackendConfigurationAsync("blueprint-id", overrideUrl, "test-correlation-id")
            .Returns((EndpointRegistrationResult.Created, (string?)null));

        // --messaging-endpoint supplied even though config has no endpoint — the override must win.
        var ctx = BuildMessagingEndpointContext(backend, isM365: true, configEndpoint: null, messagingEndpointOverride: overrideUrl);

        await AllSubcommand.ExecuteMessagingEndpointStepAsync(ctx);

        await backend.Received(1).SetBackendConfigurationAsync("blueprint-id", overrideUrl, "test-correlation-id");
        ctx.Results.MessagingEndpointResult.Should().Be(EndpointRegistrationResult.Created);
        ctx.Results.MessagingEndpointRegistered.Should().BeTrue();
        ctx.Results.MessagingEndpoint.Should().Be(overrideUrl,
            because: "the registered endpoint reported in the summary must be the override URL");
    }
}
