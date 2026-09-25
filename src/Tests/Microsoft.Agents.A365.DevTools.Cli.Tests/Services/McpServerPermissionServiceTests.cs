// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Tests for <see cref="McpServerPermissionService"/>, which resolves the BYO Entra resource for an
/// MCP server and manages the delegated grants that let agent identities call it.
/// </summary>
public class McpServerPermissionServiceTests
{
    private const string TenantId = "00000000-0000-0000-0000-0000000000aa";
    private const string ServerName = "Foo";
    private const string ByoDisplayName = "Foo - BYO";
    private const string ByoAppId = "11111111-1111-1111-1111-111111111111";
    private const string ByoSpObjectId = "22222222-2222-2222-2222-222222222222";
    private const string BlueprintId = "33333333-3333-3333-3333-333333333333";
    private const string AgentSpId = "44444444-4444-4444-4444-444444444444";
    private const string OtherResourceSpId = "55555555-5555-5555-5555-555555555555";

    private readonly GraphApiService _graph = Substitute.For<GraphApiService>();
    private readonly AgentBlueprintService _blueprintService;
    private readonly McpServerPermissionService _service;

    public McpServerPermissionServiceTests()
    {
        _blueprintService = Substitute.For<AgentBlueprintService>(
            Substitute.For<ILogger<AgentBlueprintService>>(), _graph);
        _service = new McpServerPermissionService(
            _graph, _blueprintService, Substitute.For<ILogger<McpServerPermissionService>>());
        _graph.GetGraphAccessTokenAsync(TenantId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("fake-token"));
    }

    [Fact]
    public async Task ResolveServerResourceAsync_ReportsSignInFailure_WithoutClaimingTheAppIsMissing()
    {
        var logger = new CapturingLogger<McpServerPermissionService>();
        var service = new McpServerPermissionService(_graph, _blueprintService, logger);
        _graph.TryFindApplicationAppIdsByDisplayNameAsync(TenantId, ByoDisplayName, Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult(new GraphApiService.ApplicationDisplayNameSearchResult(null, SignInFailed: true)));

        var result = await service.ResolveServerResourceAsync(TenantId, ServerName);

        result.Should().BeNull();
        logger.Messages.Should().Contain(m => m.Contains("Could not sign in"),
            because: "a failed sign-in must be reported as such, not as a missing application");
        logger.Messages.Should().NotContain(m => m.Contains("was found"),
            because: "telling the user the application does not exist would send them off to create one that may already exist");
        await _graph.DidNotReceive().GetGraphAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task ResolveServerResourceAsync_DoesNotAcquireATokenOnTheSuccessPath()
    {
        _graph.TryFindApplicationAppIdsByDisplayNameAsync(TenantId, ByoDisplayName, Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult(new GraphApiService.ApplicationDisplayNameSearchResult([ByoAppId], SignInFailed: false)));
        _graph.LookupServicePrincipalByAppIdAsync(TenantId, ByoAppId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(ByoSpObjectId));

        await _service.ResolveServerResourceAsync(TenantId, ServerName);

        await _graph.DidNotReceive().GetGraphAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveServerResourceAsync_LooksUpTheByoSuffixedApplication()
    {
        _graph.TryFindApplicationAppIdsByDisplayNameAsync(TenantId, ByoDisplayName, Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult(new GraphApiService.ApplicationDisplayNameSearchResult([ByoAppId], SignInFailed: false)));
        _graph.LookupServicePrincipalByAppIdAsync(TenantId, ByoAppId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(ByoSpObjectId));

        var result = await _service.ResolveServerResourceAsync(TenantId, ServerName);

        result.Should().NotBeNull();
        result!.DisplayName.Should().Be(ByoDisplayName,
            because: "the BYO application for an MCP server is registered as '{name} - BYO'; any other name would resolve the wrong Entra resource");
        result.AppId.Should().Be(ByoAppId);
        result.ServicePrincipalObjectId.Should().Be(ByoSpObjectId,
            because: "the service principal object ID, not the app ID, is the resourceId of an oauth2PermissionGrant");
    }

    [Fact]
    public async Task ResolveServerResourceAsync_ReturnsNull_WhenApplicationNotFound()
    {
        _graph.TryFindApplicationAppIdsByDisplayNameAsync(TenantId, ByoDisplayName, Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult(new GraphApiService.ApplicationDisplayNameSearchResult([], SignInFailed: false)));

        var result = await _service.ResolveServerResourceAsync(TenantId, ServerName);

        result.Should().BeNull();
        await _graph.DidNotReceive().LookupServicePrincipalByAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
        await _graph.DidNotReceive().GetGraphAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveServerResourceAsync_WhenTheReadFails_DoesNotReportTheApplicationAsAbsent()
    {
        var logger = new CapturingLogger<McpServerPermissionService>();
        var service = new McpServerPermissionService(_graph, _blueprintService, logger);
        _graph.TryFindApplicationAppIdsByDisplayNameAsync(TenantId, ByoDisplayName, Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult(new GraphApiService.ApplicationDisplayNameSearchResult(null, SignInFailed: false)));

        var result = await service.ResolveServerResourceAsync(TenantId, ServerName);

        result.Should().BeNull();
        logger.Messages.Should().Contain(m => m.Contains("Could not read application registrations"),
            because: "a directory read the caller is not authorized for must be reported as a permission problem");
        logger.Messages.Should().NotContain(m => m.Contains("was found"),
            because: "an unreadable directory is not evidence the application is missing");
        await _graph.DidNotReceive().GetGraphAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
        await _graph.DidNotReceive().LookupServicePrincipalByAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task ResolveServerResourceAsync_ReturnsNull_WhenServicePrincipalMissing()
    {
        _graph.TryFindApplicationAppIdsByDisplayNameAsync(TenantId, ByoDisplayName, Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult(new GraphApiService.ApplicationDisplayNameSearchResult([ByoAppId], SignInFailed: false)));
        _graph.LookupServicePrincipalByAppIdAsync(TenantId, ByoAppId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(null));

        var result = await _service.ResolveServerResourceAsync(TenantId, ServerName);

        result.Should().BeNull(
            because: "a grant cannot be created without a resource service principal, so the caller must fail rather than proceed");
    }

    [Fact]
    public async Task ResolveServerResourceAsync_ReturnsNull_WhenMultipleApplicationsShareTheDisplayName()
    {
        _graph.TryFindApplicationAppIdsByDisplayNameAsync(TenantId, ByoDisplayName, Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult(new GraphApiService.ApplicationDisplayNameSearchResult([ByoAppId, "99999999-9999-9999-9999-999999999999"], SignInFailed: false)));

        var result = await _service.ResolveServerResourceAsync(TenantId, ServerName);

        result.Should().BeNull(
            because: "Entra display names are not unique, and picking an arbitrary match would grant " +
                     "the agent tenant-wide access to a different MCP server than the caller named");
        await _graph.DidNotReceive().LookupServicePrincipalByAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }

    [Fact]
    public async Task ResolveServerResourceAsync_OffersEveryMatch_WhenADisplayNameIsAmbiguous()
    {
        const string OtherAppId = "99999999-9999-9999-9999-999999999999";
        _graph.TryFindApplicationAppIdsByDisplayNameAsync(TenantId, ByoDisplayName, Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult(new GraphApiService.ApplicationDisplayNameSearchResult([OtherAppId, ByoAppId], SignInFailed: false)));
        _graph.LookupServicePrincipalByAppIdAsync(TenantId, ByoAppId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(ByoSpObjectId));

        IReadOnlyList<string>? offered = null;

        var result = await _service.ResolveServerResourceAsync(
            TenantId, ServerName, CancellationToken.None,
            selectAppId: candidates => { offered = candidates; return ByoAppId; });

        offered.Should().BeEquivalentTo([OtherAppId, ByoAppId],
            because: "the user can only choose correctly if every matching application is offered, not a truncated subset");
        result!.AppId.Should().Be(ByoAppId,
            because: "the resolved resource must be the application the user picked, not the first match");
    }

    [Fact]
    public async Task ResolveServerResourceAsync_ReturnsNull_WhenTheAmbiguousSelectionIsCancelled()
    {
        _graph.TryFindApplicationAppIdsByDisplayNameAsync(TenantId, ByoDisplayName, Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult(new GraphApiService.ApplicationDisplayNameSearchResult([ByoAppId, "99999999-9999-9999-9999-999999999999"], SignInFailed: false)));

        var result = await _service.ResolveServerResourceAsync(
            TenantId, ServerName, CancellationToken.None, selectAppId: _ => null);

        result.Should().BeNull(
            because: "declining to choose between duplicates must abort rather than fall back to an arbitrary match");
        await _graph.DidNotReceive().LookupServicePrincipalByAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }

    [Fact]
    public async Task GetAgentInstanceStatusesAsync_Throws_WhenTheGrantReadFails()
    {
        _blueprintService.GetAgentInstancesForBlueprintAsync(TenantId, BlueprintId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentInstanceInfo>>(
            [
                new AgentInstanceInfo { IdentitySpId = AgentSpId, DisplayName = "Agent" },
            ]));
        _graph.TryGetOauth2PermissionGrantsAsync(TenantId, AgentSpId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<List<(string, string, string)>?>(null));

        var act = async () => await _service.GetAgentInstanceStatusesAsync(TenantId, BlueprintId, ByoSpObjectId);

        await act.Should().ThrowAsync<InvalidOperationException>(
            because: "a failed grant read is indistinguishable from an empty one, so reporting it as " +
                     "'missing the permission' would let --yes grant on the strength of a lookup that never succeeded");
    }

    [Fact]
    public async Task GetAgentInstanceStatusesAsync_FlagsInstancesWithAndWithoutTheScope()
    {
        _blueprintService.GetAgentInstancesForBlueprintAsync(TenantId, BlueprintId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentInstanceInfo>>(
            [
                new AgentInstanceInfo { IdentitySpId = "sp-granted", DisplayName = "Granted" },
                new AgentInstanceInfo { IdentitySpId = "sp-missing", DisplayName = "Missing" },
            ]));

        _graph.TryGetOauth2PermissionGrantsAsync(TenantId, "sp-granted", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<List<(string, string, string)>?>(new List<(string, string, string)>
            {
                (ByoSpObjectId, $"User.Read {McpConstants.V2ScopeValue}", "AllPrincipals"),
            }));
        _graph.TryGetOauth2PermissionGrantsAsync(TenantId, "sp-missing", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<List<(string, string, string)>?>(new List<(string, string, string)>
            {
                (ByoSpObjectId, "User.Read", "AllPrincipals"),
            }));

        var statuses = await _service.GetAgentInstanceStatusesAsync(TenantId, BlueprintId, ByoSpObjectId);

        statuses.Should().HaveCount(2);
        statuses.Single(s => s.ServicePrincipalObjectId == "sp-granted").HasScope.Should().BeTrue();
        statuses.Single(s => s.ServicePrincipalObjectId == "sp-missing").HasScope.Should().BeFalse();
    }

    [Fact]
    public async Task GetAgentInstanceStatusesAsync_IgnoresPrincipalScopedGrants()
    {
        _blueprintService.GetAgentInstancesForBlueprintAsync(TenantId, BlueprintId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentInstanceInfo>>(
            [
                new AgentInstanceInfo { IdentitySpId = AgentSpId, DisplayName = "Agent" },
            ]));

        _graph.TryGetOauth2PermissionGrantsAsync(TenantId, AgentSpId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<List<(string, string, string)>?>(new List<(string, string, string)>
            {
                (ByoSpObjectId, McpConstants.V2ScopeValue, "Principal"),
            }));

        var statuses = await _service.GetAgentInstanceStatusesAsync(TenantId, BlueprintId, ByoSpObjectId);

        statuses.Single().HasScope.Should().BeFalse(
            because: "the command writes a tenant-wide AllPrincipals grant, so a grant scoped to a single user does not satisfy it and the read must agree with the write");
    }

    [Fact]
    public async Task GetAgentInstanceStatusesAsync_IgnoresGrantsAgainstOtherResources()
    {
        _blueprintService.GetAgentInstancesForBlueprintAsync(TenantId, BlueprintId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentInstanceInfo>>(
            [
                new AgentInstanceInfo { IdentitySpId = AgentSpId, DisplayName = "Agent" },
            ]));

        _graph.TryGetOauth2PermissionGrantsAsync(TenantId, AgentSpId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<List<(string, string, string)>?>(new List<(string, string, string)>
            {
                (OtherResourceSpId, McpConstants.V2ScopeValue, "AllPrincipals"),
            }));

        var statuses = await _service.GetAgentInstanceStatusesAsync(TenantId, BlueprintId, ByoSpObjectId);

        statuses.Single().HasScope.Should().BeFalse(
            because: "the same scope name is exposed by every MCP server, so a grant only counts when its resourceId is this server's service principal");
    }

    [Fact]
    public async Task GetAgentInstanceStatusesAsync_DoesNotMatchScopeByPrefix()
    {
        _blueprintService.GetAgentInstancesForBlueprintAsync(TenantId, BlueprintId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentInstanceInfo>>(
            [
                new AgentInstanceInfo { IdentitySpId = AgentSpId, DisplayName = "Agent" },
            ]));

        _graph.TryGetOauth2PermissionGrantsAsync(TenantId, AgentSpId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<List<(string, string, string)>?>(new List<(string, string, string)>
            {
                (ByoSpObjectId, $"{McpConstants.V2ScopeValue}.Extra", "AllPrincipals"),
            }));

        var statuses = await _service.GetAgentInstanceStatusesAsync(TenantId, BlueprintId, ByoSpObjectId);

        statuses.Single().HasScope.Should().BeFalse(
            because: "grant scope strings are space-delimited, so matching must be per-token; a substring match would report a scope the agent does not actually hold");
    }

    [Fact]
    public async Task GrantServerScopeAsync_RequestsTheMcpServerScope()
    {
        _graph.CreateOrUpdateOauth2PermissionGrantAsync(
                TenantId, AgentSpId, ByoSpObjectId,
                Arg.Is<IEnumerable<string>>(s => s.SequenceEqual(new[] { McpConstants.V2ScopeValue })),
                Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(Task.FromResult(true));

        var granted = await _service.GrantServerScopeAsync(TenantId, AgentSpId, ByoSpObjectId);

        granted.Should().BeTrue();

        await _graph.Received(1).CreateOrUpdateOauth2PermissionGrantAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(),
            true, Arg.Any<bool>(), true);
    }

    [Fact]
    public async Task GrantServerScopeAsync_ReturnsFalse_WhenGraphRejectsTheGrant()
    {
        _graph.CreateOrUpdateOauth2PermissionGrantAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(Task.FromResult(false));

        var granted = await _service.GrantServerScopeAsync(TenantId, AgentSpId, ByoSpObjectId);

        granted.Should().BeFalse();
    }

    [Theory]
    [InlineData("Foo", "Foo - BYO")]
    [InlineData("  Foo  ", "Foo - BYO")]
    [InlineData("ext_MyServer", "ext_MyServer - BYO")]
    public void BuildByoAppDisplayName_AppendsTheByoSuffix(string serverName, string expected)
    {
        McpConstants.BuildByoAppDisplayName(serverName).Should().Be(expected,
            because: "the CLI must derive the same display name the BYO registration flow created, or the resource lookup fails");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
