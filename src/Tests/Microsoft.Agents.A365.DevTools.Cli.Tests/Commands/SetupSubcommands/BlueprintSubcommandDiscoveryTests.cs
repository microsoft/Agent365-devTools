// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands.SetupSubcommands;

/// <summary>
/// Tests stable-ID blueprint discovery when display names are duplicated.
/// </summary>
public class BlueprintSubcommandDiscoveryTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private const string SharedDisplayName = "Contoso Support Blueprint";
    private const string FirstDuplicateAppId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string FirstDuplicateObjectId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string SelectedAppId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string SelectedObjectId = "dddddddd-dddd-dddd-dddd-dddddddddddd";

    private static BlueprintLookupService CreateBlueprintLookupService(GraphApiService graphApiService) =>
        new(NullLogger<BlueprintLookupService>.Instance, graphApiService);

    private static GraphApiService CreateGraphApiServiceReturningDuplicateOnDisplayNameSearch()
    {
        var graphApiService = Substitute.For<GraphApiService>();

        graphApiService.GraphGetAsync(
                TenantId,
                Arg.Is<string>(s => s.Contains("/beta/applications?$filter=")),
                Arg.Any<CancellationToken>(),
                null)
            .Returns(JsonDocument.Parse(
                $@"{{ ""value"": [
                    {{ ""id"": ""{FirstDuplicateObjectId}"", ""appId"": ""{FirstDuplicateAppId}"", ""displayName"": ""{SharedDisplayName}"" }},
                    {{ ""id"": ""some-other-object-id"", ""appId"": ""some-other-app-id"", ""displayName"": ""{SharedDisplayName}"" }}
                ] }}"));

        graphApiService.GraphGetAsync(
                TenantId,
                Arg.Is<string>(s =>
                    s.Contains($"/beta/applications/{SelectedObjectId}/microsoft.graph.agentIdentityBlueprint")),
                Arg.Any<CancellationToken>(),
                null)
            .Returns(JsonDocument.Parse(
                $@"{{ ""value"": {{ ""id"": ""{SelectedObjectId}"", ""appId"": ""{SelectedAppId}"", ""displayName"": ""{SharedDisplayName}"" }} }}"));

        return graphApiService;
    }

    [Fact]
    public async Task ResolveExistingBlueprintAsync_WithCachedObjectId_UsesItAndNeverConsultsDisplayNameSearch()
    {
        var graphApiService = CreateGraphApiServiceReturningDuplicateOnDisplayNameSearch();
        var lookupService = CreateBlueprintLookupService(graphApiService);

        var result = await BlueprintSubcommand.ResolveExistingBlueprintAsync(
            lookupService, TenantId, cachedObjectId: SelectedObjectId, SharedDisplayName, NullLogger.Instance, CancellationToken.None);

        result.Found.Should().BeTrue(because: "the object-ID lookup must resolve when the cached ID is still valid in the tenant");
        result.AppId.Should().Be(SelectedAppId,
            because: "an authoritative cached object ID must take precedence over an ambiguous display-name search");
        result.ObjectId.Should().Be(SelectedObjectId);

        await graphApiService.DidNotReceive().GraphGetAsync(
            TenantId,
            Arg.Is<string>(s => s.Contains("/beta/applications?$filter=")),
            Arg.Any<CancellationToken>(),
            null);
    }

    [Fact]
    public async Task ResolveExistingBlueprintAsync_WithDuplicateDisplayNames_SelectedBlueprintRemainsSelected()
    {
        var graphApiService = CreateGraphApiServiceReturningDuplicateOnDisplayNameSearch();
        var lookupService = CreateBlueprintLookupService(graphApiService);

        var result = await BlueprintSubcommand.ResolveExistingBlueprintAsync(
            lookupService, TenantId, cachedObjectId: SelectedObjectId, SharedDisplayName, NullLogger.Instance, CancellationToken.None);

        result.AppId.Should().Be(SelectedAppId,
            because: "the explicitly selected blueprint (#2) must remain selected");
        result.AppId.Should().NotBe(FirstDuplicateAppId,
            because: "the first duplicate by display name must never silently override an explicit selection");
    }

    [Fact]
    public async Task ResolveExistingBlueprintAsync_NoCachedObjectId_FallsBackToDisplayNameSearch()
    {
        var graphApiService = CreateGraphApiServiceReturningDuplicateOnDisplayNameSearch();
        var lookupService = CreateBlueprintLookupService(graphApiService);

        var result = await BlueprintSubcommand.ResolveExistingBlueprintAsync(
            lookupService, TenantId, cachedObjectId: null, SharedDisplayName, NullLogger.Instance, CancellationToken.None);

        result.Found.Should().BeTrue(because: "the display-name fallback must resolve when at least one match exists");
        result.AppId.Should().Be(FirstDuplicateAppId,
            because: "with no cached object ID, the default derive/create flow must still fall back to the pre-existing display-name-first discovery");
        result.RequiresPersistence.Should().BeTrue(
            because: "a display-name discovery result must be persisted so future runs use the faster/unambiguous object-ID path");
    }

    [Fact]
    public async Task ResolveExistingBlueprintAsync_CachedObjectIdNoLongerResolves_FallsBackToDisplayNameSearch()
    {
        var graphApiService = CreateGraphApiServiceReturningDuplicateOnDisplayNameSearch();
        var lookupService = CreateBlueprintLookupService(graphApiService);

        var result = await BlueprintSubcommand.ResolveExistingBlueprintAsync(
            lookupService, TenantId, cachedObjectId: "deleted-object-id", SharedDisplayName, NullLogger.Instance, CancellationToken.None);

        result.Found.Should().BeTrue(
            because: "a stale/deleted cached object ID must gracefully fall back to display-name discovery rather than reporting not-found");
        result.AppId.Should().Be(FirstDuplicateAppId);
    }
}
