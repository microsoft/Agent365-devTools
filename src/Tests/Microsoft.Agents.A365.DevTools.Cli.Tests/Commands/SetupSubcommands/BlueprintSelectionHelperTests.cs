// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands.SetupSubcommands;

/// <summary>
/// Tests blueprint selection validation, lookup, and state isolation.
/// </summary>
public class BlueprintSelectionHelperTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string BlueprintAppId = "22222222-2222-2222-2222-222222222222";
    private const string OtherBlueprintAppId = "33333333-3333-3333-3333-333333333333";
    private const string BlueprintObjectId = "44444444-4444-4444-4444-444444444444";
    private const string BlueprintDisplayName = "Contoso Support Blueprint";

    private static BlueprintLookupService CreateBlueprintLookupService(GraphApiService? graphApiService = null) =>
        new(NullLogger<BlueprintLookupService>.Instance, graphApiService ?? Substitute.For<GraphApiService>());

    // -----------------------------------------------------------------------
    // ValidateOptions
    // -----------------------------------------------------------------------

    [Fact]
    public void ValidateOptions_NeitherOptionSupplied_ReturnsTrue()
    {
        var result = BlueprintSelectionHelper.ValidateOptions(
            blueprintId: null, blueprintIdSpecified: false, selectBlueprint: false, aiTeammateFlag: null,
            dryRun: false, nonInteractive: false, NullLogger.Instance);

        result.Should().BeTrue(because: "default derive/create behavior must be unaffected when neither option is used");
    }

    [Fact]
    public void ValidateOptions_BothBlueprintIdAndSelectBlueprint_ReturnsFalse()
    {
        var result = BlueprintSelectionHelper.ValidateOptions(
            blueprintId: BlueprintAppId, blueprintIdSpecified: true, selectBlueprint: true, aiTeammateFlag: null,
            dryRun: false, nonInteractive: false, NullLogger.Instance);

        result.Should().BeFalse(because: "--blueprint-id and --select-blueprint are mutually exclusive");
    }

    [Fact]
    public void ValidateOptions_InvalidGuidBlueprintId_ReturnsFalse()
    {
        var result = BlueprintSelectionHelper.ValidateOptions(
            blueprintId: "not-a-guid", blueprintIdSpecified: true, selectBlueprint: false, aiTeammateFlag: null,
            dryRun: false, nonInteractive: false, NullLogger.Instance);

        result.Should().BeFalse(because: "--blueprint-id must be a valid GUID");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ValidateOptions_WithAiTeammateTrue_ReturnsFalse(bool useBlueprintId, bool useSelectBlueprint)
    {
        var result = BlueprintSelectionHelper.ValidateOptions(
            blueprintId: useBlueprintId ? BlueprintAppId : null,
            blueprintIdSpecified: useBlueprintId,
            selectBlueprint: useSelectBlueprint,
            aiTeammateFlag: true,
            dryRun: false, nonInteractive: false, NullLogger.Instance);

        result.Should().BeFalse(because: "explicit blueprint selection applies to blueprint agents only, not AI Teammate agents");
    }

    [Fact]
    public void ValidateOptions_SelectBlueprintWithRedirectedStdin_ReturnsFalse()
    {
        var result = BlueprintSelectionHelper.ValidateOptions(
            blueprintId: null, blueprintIdSpecified: false, selectBlueprint: true, aiTeammateFlag: null,
            dryRun: false, nonInteractive: true, NullLogger.Instance);

        result.Should().BeFalse(because: "--select-blueprint requires an interactive terminal and must fail clearly when stdin is redirected");
    }

    [Fact]
    public void ValidateOptions_SelectBlueprintWithDryRun_ReturnsFalse()
    {
        var result = BlueprintSelectionHelper.ValidateOptions(
            blueprintId: null, blueprintIdSpecified: false, selectBlueprint: true, aiTeammateFlag: null,
            dryRun: true, nonInteractive: false, NullLogger.Instance);

        result.Should().BeFalse(because: "--select-blueprint requires a real run to query the tenant and cannot combine with --dry-run");
    }

    [Fact]
    public void ValidateOptions_BlueprintIdWithDryRun_ReturnsTrue()
    {
        var result = BlueprintSelectionHelper.ValidateOptions(
            blueprintId: BlueprintAppId, blueprintIdSpecified: true, selectBlueprint: false, aiTeammateFlag: null,
            dryRun: true, nonInteractive: false, NullLogger.Instance);

        result.Should().BeTrue(because: "--blueprint-id does not require a Graph call and may be previewed in --dry-run");
    }

    [Fact]
    public void ValidateOptions_ValidBlueprintId_ReturnsTrue()
    {
        var result = BlueprintSelectionHelper.ValidateOptions(
            blueprintId: BlueprintAppId, blueprintIdSpecified: true, selectBlueprint: false, aiTeammateFlag: false,
            dryRun: false, nonInteractive: false, NullLogger.Instance);

        result.Should().BeTrue();
    }

    [Fact]
    public void ValidateOptions_WhitespaceBlueprintId_ReturnsFalse()
    {
        var result = BlueprintSelectionHelper.ValidateOptions(
            blueprintId: " ", blueprintIdSpecified: true, selectBlueprint: false, aiTeammateFlag: null,
            dryRun: false, nonInteractive: false, NullLogger.Instance);

        result.Should().BeFalse(
            because: "an explicitly supplied empty option must not fall back to creating a new blueprint");
    }

    // -----------------------------------------------------------------------
    // ResolveAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_BlueprintIdNotFoundInTenant_ReturnsNull()
    {
        var graphApiService = Substitute.For<GraphApiService>();
        graphApiService.GraphGetAsync(TenantId, Arg.Any<string>(), Arg.Any<CancellationToken>(), null)
            .Returns((System.Text.Json.JsonDocument?)null);
        var lookupService = CreateBlueprintLookupService(graphApiService);

        var result = await BlueprintSelectionHelper.ResolveAsync(
            lookupService, TenantId, OtherBlueprintAppId, selectBlueprint: false, NullLogger.Instance, CancellationToken.None);

        result.Should().BeNull(
            because: "a blueprint ID that does not resolve in the active tenant (not found, or belongs to a different tenant) must fail before any mutation");
    }

    [Fact]
    public async Task ResolveAsync_BlueprintIdFound_ReturnsLookupResult()
    {
        var graphApiService = Substitute.For<GraphApiService>();
        var json = $@"{{ ""value"": [ {{ ""id"": ""{BlueprintObjectId}"", ""appId"": ""{BlueprintAppId}"", ""displayName"": ""{BlueprintDisplayName}"" }} ] }}";
        graphApiService.GraphGetAsync(TenantId, Arg.Any<string>(), Arg.Any<CancellationToken>(), null)
            .Returns(System.Text.Json.JsonDocument.Parse(json));
        var lookupService = CreateBlueprintLookupService(graphApiService);

        var result = await BlueprintSelectionHelper.ResolveAsync(
            lookupService, TenantId, BlueprintAppId, selectBlueprint: false, NullLogger.Instance, CancellationToken.None);

        result.Should().NotBeNull();
        result!.AppId.Should().Be(BlueprintAppId);
        result.DisplayName.Should().Be(BlueprintDisplayName);
        result.ObjectId.Should().Be(BlueprintObjectId);
    }

    [Fact]
    public async Task ResolveAsync_BlueprintLookupReturnsMismatchedAppId_ReturnsNull()
    {
        var lookupService = Substitute.ForPartsOf<BlueprintLookupService>(
            NullLogger<BlueprintLookupService>.Instance,
            Substitute.For<GraphApiService>());
        lookupService.GetBlueprintByAppIdAsync(
                TenantId,
                BlueprintAppId,
                Arg.Any<CancellationToken>())
            .Returns(new BlueprintLookupResult
            {
                Found = true,
                AppId = OtherBlueprintAppId,
                ObjectId = BlueprintObjectId,
                DisplayName = BlueprintDisplayName
            });

        var result = await BlueprintSelectionHelper.ResolveAsync(
            lookupService, TenantId, BlueprintAppId, selectBlueprint: false, NullLogger.Instance, CancellationToken.None);

        result.Should().BeNull(
            because: "the selection boundary must reject inconsistent identifiers even if the lookup layer regresses");
    }

    [Fact]
    public async Task ResolveAsync_SelectBlueprint_ValidNumberedChoice_ResolvesChosenBlueprint()
    {
        var graphApiService = Substitute.For<GraphApiService>();
        var listJson = $@"{{ ""value"": [
            {{ ""id"": ""aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"", ""appId"": ""bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"", ""displayName"": ""First Blueprint"" }},
            {{ ""id"": ""{BlueprintObjectId}"", ""appId"": ""{BlueprintAppId}"", ""displayName"": ""{BlueprintDisplayName}"" }}
        ] }}";
        graphApiService.GraphGetAsync(TenantId,
                Arg.Is<string>(s => s.Contains("$top=100")),
                Arg.Any<CancellationToken>(), null)
            .Returns(System.Text.Json.JsonDocument.Parse(listJson));
        graphApiService.GraphGetAsync(TenantId,
                Arg.Is<string>(s => s.Contains("$filter=appId")),
                Arg.Any<CancellationToken>(), null)
            .Returns(ci => System.Text.Json.JsonDocument.Parse(
                $@"{{ ""value"": [ {{ ""id"": ""{BlueprintObjectId}"", ""appId"": ""{BlueprintAppId}"", ""displayName"": ""{BlueprintDisplayName}"" }} ] }}"));
        var lookupService = CreateBlueprintLookupService(graphApiService);

        ConsoleHelper.ReadLineOverrideForTests.Value = () => "2";
        try
        {
            var result = await BlueprintSelectionHelper.ResolveAsync(
                lookupService, TenantId, blueprintId: null, selectBlueprint: true, NullLogger.Instance, CancellationToken.None);

            result.Should().NotBeNull();
            result!.AppId.Should().Be(BlueprintAppId, because: "choosing '2' must select the second listed blueprint");
        }
        finally
        {
            ConsoleHelper.ReadLineOverrideForTests.Value = null;
        }
    }

    [Fact]
    public async Task ResolveAsync_SelectBlueprint_OutOfRangeChoice_ReturnsNull()
    {
        var graphApiService = Substitute.For<GraphApiService>();
        var listJson = $@"{{ ""value"": [ {{ ""id"": ""{BlueprintObjectId}"", ""appId"": ""{BlueprintAppId}"", ""displayName"": ""{BlueprintDisplayName}"" }} ] }}";
        graphApiService.GraphGetAsync(TenantId, Arg.Any<string>(), Arg.Any<CancellationToken>(), null)
            .Returns(System.Text.Json.JsonDocument.Parse(listJson));
        var lookupService = CreateBlueprintLookupService(graphApiService);

        ConsoleHelper.ReadLineOverrideForTests.Value = () => "99";
        try
        {
            var result = await BlueprintSelectionHelper.ResolveAsync(
                lookupService, TenantId, blueprintId: null, selectBlueprint: true, NullLogger.Instance, CancellationToken.None);

            result.Should().BeNull(because: "a selection outside the printed numbered range must be rejected");
        }
        finally
        {
            ConsoleHelper.ReadLineOverrideForTests.Value = null;
        }
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ResolveAsync_SelectBlueprint_NonNumericOrMissingChoice_ReturnsNull(string? input)
    {
        var graphApiService = Substitute.For<GraphApiService>();
        var listJson = $@"{{ ""value"": [ {{ ""id"": ""{BlueprintObjectId}"", ""appId"": ""{BlueprintAppId}"", ""displayName"": ""{BlueprintDisplayName}"" }} ] }}";
        graphApiService.GraphGetAsync(TenantId, Arg.Any<string>(), Arg.Any<CancellationToken>(), null)
            .Returns(System.Text.Json.JsonDocument.Parse(listJson));
        var lookupService = CreateBlueprintLookupService(graphApiService);

        ConsoleHelper.ReadLineOverrideForTests.Value = () => input;
        try
        {
            var result = await BlueprintSelectionHelper.ResolveAsync(
                lookupService, TenantId, blueprintId: null, selectBlueprint: true, NullLogger.Instance, CancellationToken.None);

            result.Should().BeNull(
                because: "interactive selection must reject input that is not one of the printed numbers");
        }
        finally
        {
            ConsoleHelper.ReadLineOverrideForTests.Value = null;
        }
    }

    [Fact]
    public async Task ResolveAsync_SelectBlueprint_EmptyTenant_ReturnsNull()
    {
        var graphApiService = Substitute.For<GraphApiService>();
        graphApiService.GraphGetAsync(TenantId, Arg.Any<string>(), Arg.Any<CancellationToken>(), null)
            .Returns(System.Text.Json.JsonDocument.Parse(@"{""value"": []}"));
        var lookupService = CreateBlueprintLookupService(graphApiService);

        var result = await BlueprintSelectionHelper.ResolveAsync(
            lookupService, TenantId, blueprintId: null, selectBlueprint: true, NullLogger.Instance, CancellationToken.None);

        result.Should().BeNull(because: "there is nothing to choose from when the tenant has no blueprints");
    }

    // ApplyExplicitBlueprintSelection

    [Fact]
    public void ApplyExplicitBlueprintSelection_RerunOfSameBlueprint_PreservesCachedIdentityAndSecret()
    {
        // Arrange: config already has cached state from a prior run against this exact blueprint,
        // for the exact same agent identity being provisioned again.
        var config = new Agent365Config
        {
            TenantId = TenantId,
            AgentIdentityDisplayName = "Support Europe Identity",
            AgentBlueprintDisplayName = "Support Europe Blueprint", // stale derived name from bootstrap
            AgentBlueprintId = BlueprintAppId,
            AgentBlueprintObjectId = BlueprintObjectId,
            AgentBlueprintServicePrincipalObjectId = "sp-object-id",
            AgentBlueprintClientSecret = "cached-secret",
            AgentBlueprintClientSecretProtected = true,
            AgenticAppId = "agentic-app-id",
            AgentRegistrationId = "registration-id",
            ResourceConsents = [new ResourceConsent { ResourceAppId = "resource-app-id" }],
        };
        var blueprint = new BlueprintLookupResult { Found = true, AppId = BlueprintAppId, ObjectId = BlueprintObjectId, DisplayName = BlueprintDisplayName };

        var updated = BlueprintSelectionHelper.ApplyExplicitBlueprintSelection(
            config, blueprint, cachedAgentIdentityDisplayName: "Support Europe Identity");

        updated.AgenticAppId.Should().Be("agentic-app-id",
            because: "a rerun that selects the exact same blueprint for the exact same agent identity must reuse the existing agent identity, not recreate it");
        updated.AgentRegistrationId.Should().Be("registration-id",
            because: "a rerun that selects the exact same blueprint for the exact same agent identity must reuse the existing agent registration");
        updated.AgentBlueprintClientSecret.Should().Be("cached-secret",
            because: "the cached blueprint client secret must be preserved on a same-blueprint rerun");
        updated.AgentBlueprintId.Should().Be(BlueprintAppId,
            because: "the verified blueprint app ID must be applied on every rerun");
        updated.AgentBlueprintObjectId.Should().Be(BlueprintObjectId,
            because: "the verified blueprint object ID must be applied on every rerun");
        updated.AgentBlueprintDisplayName.Should().Be(BlueprintDisplayName,
            because: "discovery is display-name based, so it must always reflect the blueprint's real name");
        config.AgentBlueprintDisplayName.Should().Be("Support Europe Blueprint",
            because: "the original config instance passed in must not be mutated");
        updated.ResourceConsents.Should().NotBeSameAs(config.ResourceConsents,
            because: "the selected config must not share a mutable consent list with its source");
        updated.ResourceConsents.Should().ContainSingle(
            because: "a same-blueprint rerun must preserve the blueprint's resource consent state");
    }

    [Fact]
    public void ApplyExplicitBlueprintSelection_SameBlueprint_RefreshesAuthoritativeObjectId()
    {
        var config = new Agent365Config
        {
            AgentIdentityDisplayName = "Support Europe Identity",
            AgentBlueprintDisplayName = "Support Europe Blueprint",
            AgentBlueprintId = BlueprintAppId,
            AgentBlueprintObjectId = "55555555-5555-5555-5555-555555555555",
            AgenticAppId = "agentic-app-id",
        };
        var blueprint = new BlueprintLookupResult
        {
            Found = true,
            AppId = BlueprintAppId,
            ObjectId = BlueprintObjectId,
            DisplayName = BlueprintDisplayName
        };

        var updated = BlueprintSelectionHelper.ApplyExplicitBlueprintSelection(
            config, blueprint, cachedAgentIdentityDisplayName: "Support Europe Identity");

        updated.AgentBlueprintObjectId.Should().Be(BlueprintObjectId,
            because: "tenant-verified identifiers must replace stale cached metadata");
        updated.AgenticAppId.Should().Be("agentic-app-id",
            because: "refreshing blueprint metadata must not recreate the same agent identity");
    }

    [Fact]
    public void ApplyExplicitBlueprintSelection_SameBlueprintDifferentAgentIdentity_ResetsIdentityRegistrationButKeepsBlueprintState()
    {
        var config = new Agent365Config
        {
            TenantId = TenantId,
            AgentIdentityDisplayName = "Agent B Identity", // the identity being provisioned NOW
            AgentBlueprintDisplayName = "Agent B Blueprint", // stale derived name from bootstrap
            AgentBlueprintId = BlueprintAppId, // merged in from Agent A's generated config
            AgentBlueprintObjectId = BlueprintObjectId,
            AgentBlueprintServicePrincipalObjectId = "sp-object-id",
            AgentBlueprintClientSecret = "cached-secret",
            AgentBlueprintClientSecretProtected = true,
            AgenticAppId = "agent-a-agentic-app-id",
            AgenticUserId = "agent-a-user-id",
            AgentInstanceId = "agent-a-instance-id",
            AgentRegistrationId = "agent-a-registration-id",
            BotId = "agent-a-bot-id",
            BotMsaAppId = "agent-a-bot-msa-app-id",
            BotMessagingEndpoint = "https://agent-a.example.com/api/messages",
            Completed = true,
            CompletedAt = DateTime.UtcNow,
        };
        var blueprint = new BlueprintLookupResult { Found = true, AppId = BlueprintAppId, ObjectId = BlueprintObjectId, DisplayName = BlueprintDisplayName };

        var updated = BlueprintSelectionHelper.ApplyExplicitBlueprintSelection(
            config, blueprint, cachedAgentIdentityDisplayName: "Agent A Identity");

        updated.AgenticAppId.Should().BeNull(
            because: "a shared blueprint does not make another agent identity reusable");
        updated.AgenticUserId.Should().BeNull();
        updated.AgentInstanceId.Should().BeNull();
        updated.AgentRegistrationId.Should().BeNull(
            because: "Agent A's registration must not be handed to Agent B");
        updated.BotId.Should().BeNull(because: "bot registration is agent-identity-scoped");
        updated.BotMsaAppId.Should().BeNull(because: "bot registration is agent-identity-scoped");
        updated.BotMessagingEndpoint.Should().BeNull(because: "bot registration is agent-identity-scoped");
        updated.Completed.Should().BeFalse();
        updated.CompletedAt.Should().BeNull();

        updated.AgentBlueprintId.Should().Be(BlueprintAppId,
            because: "the blueprint itself is genuinely shared by both agent identities");
        updated.AgentBlueprintObjectId.Should().Be(BlueprintObjectId);
        updated.AgentBlueprintServicePrincipalObjectId.Should().Be("sp-object-id");
        updated.AgentBlueprintClientSecret.Should().Be("cached-secret",
            because: "the blueprint's own client secret is shared by every agent identity hosted under it");
        updated.AgentBlueprintDisplayName.Should().Be(BlueprintDisplayName);

        config.AgenticAppId.Should().Be("agent-a-agentic-app-id",
            because: "the original config instance passed in must not be mutated");
    }

    [Fact]
    public void ApplyExplicitBlueprintSelection_DifferentBlueprintThanCached_ResetsIdentityRegistrationAndSecret()
    {
        var config = new Agent365Config
        {
            TenantId = TenantId,
            AgentIdentityDisplayName = "Support Europe Identity",
            AgentBlueprintDisplayName = "Support Europe Blueprint",
            AgentBlueprintId = OtherBlueprintAppId,
            AgentBlueprintObjectId = "other-object-id",
            AgentBlueprintServicePrincipalObjectId = "other-sp-object-id",
            AgentBlueprintClientSecret = "other-cached-secret",
            AgentBlueprintClientSecretProtected = true,
            AgenticAppId = "other-agentic-app-id",
            AgentRegistrationId = "other-registration-id",
        };
        config.ResourceConsents.Add(new ResourceConsent { ResourceName = "Microsoft Graph", ResourceAppId = "other-resource-app-id" });
        var blueprint = new BlueprintLookupResult { Found = true, AppId = BlueprintAppId, ObjectId = BlueprintObjectId, DisplayName = BlueprintDisplayName };

        var updated = BlueprintSelectionHelper.ApplyExplicitBlueprintSelection(
            config, blueprint, cachedAgentIdentityDisplayName: "Support Europe Identity");

        updated.AgenticAppId.Should().BeNull(
            because: "an agent identity created under a different blueprint must never be reused for the newly selected blueprint");
        updated.AgentRegistrationId.Should().BeNull(
            because: "an agent registration from a different blueprint/agent must never be reused for the newly selected blueprint");
        updated.AgentBlueprintClientSecret.Should().BeNull(
            because: "a client secret cached for a different blueprint cannot authenticate the selected blueprint");
        updated.AgentBlueprintId.Should().Be(BlueprintAppId,
            because: "the exact tenant-verified blueprint ID must be applied directly");
        updated.AgentBlueprintObjectId.Should().Be(BlueprintObjectId,
            because: "the exact tenant-verified blueprint object ID must be applied directly");
        updated.AgentBlueprintServicePrincipalObjectId.Should().BeNull();
        updated.ResourceConsents.Should().BeEmpty(
            because: "resource consent records from a different blueprint/agent must not be carried over to the newly selected blueprint");
        updated.AgentBlueprintDisplayName.Should().Be(BlueprintDisplayName,
            because: "setup must discover/create resources under the explicitly selected blueprint's real display name");
        config.AgenticAppId.Should().Be("other-agentic-app-id",
            because: "the original config instance passed in must not be mutated");
        config.ResourceConsents.Should().HaveCount(1,
            because: "resetting the clone's ResourceConsents to a new list must not affect the original config's list (MemberwiseClone shallow-copies list references)");
    }

    [Fact]
    public void ApplyExplicitBlueprintSelection_NoCachedBlueprint_ResetsToEmptyAndSetsDisplayName()
    {
        var config = new Agent365Config
        {
            TenantId = TenantId,
            AgentIdentityDisplayName = "Support Europe Identity",
            AgentBlueprintDisplayName = "Support Europe Blueprint",
        };
        var blueprint = new BlueprintLookupResult { Found = true, AppId = BlueprintAppId, ObjectId = BlueprintObjectId, DisplayName = BlueprintDisplayName };

        var updated = BlueprintSelectionHelper.ApplyExplicitBlueprintSelection(config, blueprint);

        updated.AgenticAppId.Should().BeNull();
        updated.AgentRegistrationId.Should().BeNull();
        updated.AgentBlueprintId.Should().Be(BlueprintAppId,
            because: "with nothing cached, the exact resolved blueprint ID must still be applied directly");
        updated.AgentBlueprintObjectId.Should().Be(BlueprintObjectId);
        updated.AgentBlueprintDisplayName.Should().Be(BlueprintDisplayName);
    }
}
