// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Helpers;

/// <summary>
/// Tests for <see cref="ScopeAvailabilityValidator"/>.
///
/// <para>
/// The validator's contract (issue #429): given a list of permission specs and a map of
/// resolved resource SP object IDs, query each SP's published delegated scopes and drop
/// any requested scope the SP does not expose. The caller (BatchPermissionsOrchestrator)
/// uses the filtered list to build a <c>/v2.0/adminconsent</c> URL that Entra will accept,
/// and uses the dropped-scope list to emit a per-resource warning and offer a PowerShell
/// fallback for users who want to stamp those scopes via the lenient programmatic
/// <c>oauth2PermissionGrants</c> path.
/// </para>
/// </summary>
public class ScopeAvailabilityValidatorTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string GraphAppId = "00000003-0000-0000-c000-000000000000";
    private const string GraphSpId = "22222222-2222-2222-2222-222222222222";
    private const string BotAppId = "5a807f24-c9de-44ee-a3a7-329e88a00ffc";
    private const string BotSpId = "33333333-3333-3333-3333-333333333333";

    private readonly GraphApiService _graph = Substitute.For<GraphApiService>();
    private readonly ILogger _logger = Substitute.For<ILogger>();

    [Fact]
    public async Task ValidScopes_PassThroughUnchangedAndDropNothing()
    {
        // Arrange — Graph SP exposes the exact scope set the spec requests.
        _graph
            .GetAvailableScopeNamesAsync(Tenant, GraphSpId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Mail.Send", "User.Read" });

        var specs = new[]
        {
            new ResourcePermissionSpec(GraphAppId, "Microsoft Graph", new[] { "Mail.Send", "User.Read" }, SetInheritable: true)
        };
        var spMap = new Dictionary<string, string> { [GraphAppId] = GraphSpId };

        // Act
        var result = await ScopeAvailabilityValidator.ValidateAsync(_graph, Tenant, specs, spMap, _logger, CancellationToken.None);

        // Assert
        result.DroppedScopes.Should().BeEmpty(
            because: "every requested scope is published on the resource SP — nothing should be filtered");
        result.EffectiveSpecs.Should().ContainSingle()
            .Which.Scopes.Should().BeEquivalentTo(new[] { "Mail.Send", "User.Read" },
                because: "the spec passes through unchanged when all scopes are valid");
    }

    [Fact]
    public async Task UnavailableScope_DroppedFromSpecAndRecordedInResult()
    {
        // Arrange — SP exposes one of the two requested scopes.
        _graph
            .GetAvailableScopeNamesAsync(Tenant, BotSpId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AgentData.ReadWrite" });

        var specs = new[]
        {
            // This is the exact pre-fix Messaging Bot spec from issue #429:
            // "Authorization.ReadWrite" and "user_impersonation" do not exist on the
            // Messaging Bot SP, which makes the entire /v2.0/adminconsent URL fail with
            // AADSTS650053. The validator must keep AgentData.ReadWrite and drop the others.
            new ResourcePermissionSpec(BotAppId, "Messaging Bot API",
                new[] { "Authorization.ReadWrite", "AgentData.ReadWrite", "user_impersonation" },
                SetInheritable: true)
        };
        var spMap = new Dictionary<string, string> { [BotAppId] = BotSpId };

        // Act
        var result = await ScopeAvailabilityValidator.ValidateAsync(_graph, Tenant, specs, spMap, _logger, CancellationToken.None);

        // Assert
        result.EffectiveSpecs.Should().ContainSingle()
            .Which.Scopes.Should().BeEquivalentTo(new[] { "AgentData.ReadWrite" },
                because: "scopes the resource SP does not publish must be filtered out so the unified /v2.0/adminconsent URL does not blow up with AADSTS650053");

        result.DroppedScopes.Should().BeEquivalentTo(new[]
        {
            new ScopeAvailabilityValidator.DroppedScope("Messaging Bot API", BotAppId, "Authorization.ReadWrite"),
            new ScopeAvailabilityValidator.DroppedScope("Messaging Bot API", BotAppId, "user_impersonation"),
        }, because: "the caller surfaces a warning per dropped (resource, scope) pair and offers a PowerShell fallback for users who want to stamp them anyway via the lenient programmatic OAuth2 grant path");
    }

    [Fact]
    public async Task MissingSpObjectId_PassesSpecThroughUnchanged()
    {
        // Arrange — no SP id was resolved for the resource in Phase 1.
        var specs = new[]
        {
            new ResourcePermissionSpec(GraphAppId, "Microsoft Graph", new[] { "Mail.Send" }, SetInheritable: true)
        };
        var emptyMap = new Dictionary<string, string>();

        // Act
        var result = await ScopeAvailabilityValidator.ValidateAsync(_graph, Tenant, specs, emptyMap, _logger, CancellationToken.None);

        // Assert
        result.EffectiveSpecs.Should().ContainSingle()
            .Which.Scopes.Should().BeEquivalentTo(new[] { "Mail.Send" },
                because: "specs whose SP could not be resolved in Phase 1 must pass through unchanged — dropping every scope on an unresolvable SP would silently empty the consent URL, which is worse than letting AADSTS650053 surface if it gets that far");
        result.DroppedScopes.Should().BeEmpty();

        await _graph.DidNotReceive().GetAvailableScopeNamesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GraphReturnsEmptySet_PassesSpecThroughUnchanged()
    {
        // Arrange — Graph call swallows errors and returns an empty set when the SP
        // cannot be read. Treat that as "we don't know," not "the SP exposes nothing."
        _graph
            .GetAvailableScopeNamesAsync(Tenant, GraphSpId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var specs = new[]
        {
            new ResourcePermissionSpec(GraphAppId, "Microsoft Graph", new[] { "Mail.Send" }, SetInheritable: true)
        };
        var spMap = new Dictionary<string, string> { [GraphAppId] = GraphSpId };

        // Act
        var result = await ScopeAvailabilityValidator.ValidateAsync(_graph, Tenant, specs, spMap, _logger, CancellationToken.None);

        // Assert
        result.EffectiveSpecs.Should().ContainSingle()
            .Which.Scopes.Should().BeEquivalentTo(new[] { "Mail.Send" },
                because: "an empty published-scopes set means the Graph call could not read the SP; dropping every requested scope on that basis would block setup even when the scopes are actually valid");
        result.DroppedScopes.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptySpecScopes_PassThroughWithoutGraphCall()
    {
        // Arrange — spec with no delegated scopes (e.g. an app-role-only spec). The
        // validator must not waste a Graph round-trip on a no-op.
        var specs = new[]
        {
            new ResourcePermissionSpec(GraphAppId, "Microsoft Graph", Array.Empty<string>(), SetInheritable: false)
        };
        var spMap = new Dictionary<string, string> { [GraphAppId] = GraphSpId };

        // Act
        var result = await ScopeAvailabilityValidator.ValidateAsync(_graph, Tenant, specs, spMap, _logger, CancellationToken.None);

        // Assert
        result.EffectiveSpecs.Should().ContainSingle()
            .Which.Scopes.Should().BeEmpty();
        result.DroppedScopes.Should().BeEmpty();
        await _graph.DidNotReceive().GetAvailableScopeNamesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GraphCallThrows_PassesSpecThroughUnchangedAndDoesNotPropagate()
    {
        // Arrange — simulate a transient Graph failure (or, in practice, a stubbed test
        // where the mocked JsonDocument is disposed across calls — this is exactly what
        // broke existing BatchPermissionsOrchestratorTests when the validator was first
        // wired in). The validator is a safety net; an internal failure must never block
        // setup or surface as an unhandled exception.
        _graph
            .GetAvailableScopeNamesAsync(Tenant, GraphSpId, Arg.Any<CancellationToken>())
            .Returns<HashSet<string>>(_ => throw new ObjectDisposedException("JsonDocument"));

        var specs = new[]
        {
            new ResourcePermissionSpec(GraphAppId, "Microsoft Graph", new[] { "Mail.Send" }, SetInheritable: true)
        };
        var spMap = new Dictionary<string, string> { [GraphAppId] = GraphSpId };

        // Act
        var result = await ScopeAvailabilityValidator.ValidateAsync(_graph, Tenant, specs, spMap, _logger, CancellationToken.None);

        // Assert
        result.EffectiveSpecs.Should().ContainSingle()
            .Which.Scopes.Should().BeEquivalentTo(new[] { "Mail.Send" },
                because: "a Graph error while validating must not drop scopes — the validator is a defensive safety net, not a gatekeeper, and missing a filter opportunity is better than blocking setup on an internal validator failure");
        result.DroppedScopes.Should().BeEmpty();
    }

    [Fact]
    public async Task CancellationDuringGraphCall_PropagatesOperationCanceled()
    {
        // Arrange — distinguish "Graph failed" (swallow) from "operator hit Ctrl+C"
        // (must propagate so the rest of setup terminates promptly).
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _graph
            .GetAvailableScopeNamesAsync(Tenant, GraphSpId, Arg.Any<CancellationToken>())
            .Returns<HashSet<string>>(_ => throw new OperationCanceledException(cts.Token));

        var specs = new[]
        {
            new ResourcePermissionSpec(GraphAppId, "Microsoft Graph", new[] { "Mail.Send" }, SetInheritable: true)
        };
        var spMap = new Dictionary<string, string> { [GraphAppId] = GraphSpId };

        // Act
        Func<Task> act = () => ScopeAvailabilityValidator.ValidateAsync(_graph, Tenant, specs, spMap, _logger, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>(
            because: "Ctrl+C during validation must abort setup, not be silently swallowed like a transient Graph error");
    }

    [Fact]
    public async Task CaseInsensitiveScopeMatch_KeepsDifferentCasing()
    {
        // Arrange — SP publishes "AgentData.ReadWrite", spec asks for "agentdata.readwrite".
        // Case mismatch must not cause a false drop — Entra is case-insensitive on scope names.
        _graph
            .GetAvailableScopeNamesAsync(Tenant, BotSpId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AgentData.ReadWrite" });

        var specs = new[]
        {
            new ResourcePermissionSpec(BotAppId, "Messaging Bot API", new[] { "agentdata.readwrite" }, SetInheritable: true)
        };
        var spMap = new Dictionary<string, string> { [BotAppId] = BotSpId };

        // Act
        var result = await ScopeAvailabilityValidator.ValidateAsync(_graph, Tenant, specs, spMap, _logger, CancellationToken.None);

        // Assert
        result.EffectiveSpecs.Single().Scopes.Should().BeEquivalentTo(new[] { "agentdata.readwrite" },
            because: "scope matching must be case-insensitive to mirror Entra's behavior — a casing-only difference is not a real mismatch");
        result.DroppedScopes.Should().BeEmpty();
    }
}
