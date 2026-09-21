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
        _graph.GetGraphAccessTokenAsync(TenantId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        _graph.FindApplicationByDisplayNameAsync(TenantId, ByoDisplayName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var result = await _service.ResolveServerResourceAsync(TenantId, ServerName);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveServerResourceAsync_DoesNotAcquireATokenOnTheSuccessPath()
    {
        _graph.FindApplicationByDisplayNameAsync(TenantId, ByoDisplayName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(ByoAppId));
        _graph.LookupServicePrincipalByAppIdAsync(TenantId, ByoAppId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(ByoSpObjectId));

        await _service.ResolveServerResourceAsync(TenantId, ServerName);

        await _graph.DidNotReceive().GetGraphAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveServerResourceAsync_LooksUpTheByoSuffixedApplication()
    {
        _graph.FindApplicationByDisplayNameAsync(TenantId, ByoDisplayName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(ByoAppId));
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
        _graph.FindApplicationByDisplayNameAsync(TenantId, ByoDisplayName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var result = await _service.ResolveServerResourceAsync(TenantId, ServerName);

        result.Should().BeNull();
        await _graph.DidNotReceive().LookupServicePrincipalByAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }

    [Fact]
    public async Task ResolveServerResourceAsync_ReturnsNull_WhenServicePrincipalMissing()
    {
        _graph.FindApplicationByDisplayNameAsync(TenantId, ByoDisplayName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(ByoAppId));
        _graph.LookupServicePrincipalByAppIdAsync(TenantId, ByoAppId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(null));

        var result = await _service.ResolveServerResourceAsync(TenantId, ServerName);

        result.Should().BeNull(
            because: "a grant cannot be created without a resource service principal, so the caller must fail rather than proceed");
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

        _graph.GetOauth2PermissionGrantsAsync(TenantId, "sp-granted", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<(string, string, string)>
            {
                (ByoSpObjectId, $"User.Read {McpConstants.V2ScopeValue}", "AllPrincipals"),
            }));
        _graph.GetOauth2PermissionGrantsAsync(TenantId, "sp-missing", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<(string, string, string)>
            {
                (ByoSpObjectId, "User.Read", "AllPrincipals"),
            }));

        var statuses = await _service.GetAgentInstanceStatusesAsync(TenantId, BlueprintId, ByoSpObjectId);

        statuses.Should().HaveCount(2);
        statuses.Single(s => s.ServicePrincipalObjectId == "sp-granted").HasScope.Should().BeTrue();
        statuses.Single(s => s.ServicePrincipalObjectId == "sp-missing").HasScope.Should().BeFalse();
    }

    [Fact]
    public async Task GetAgentInstanceStatusesAsync_IgnoresGrantsAgainstOtherResources()
    {
        _blueprintService.GetAgentInstancesForBlueprintAsync(TenantId, BlueprintId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentInstanceInfo>>(
            [
                new AgentInstanceInfo { IdentitySpId = AgentSpId, DisplayName = "Agent" },
            ]));

        _graph.GetOauth2PermissionGrantsAsync(TenantId, AgentSpId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<(string, string, string)>
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

        _graph.GetOauth2PermissionGrantsAsync(TenantId, AgentSpId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<(string, string, string)>
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
                Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult(true));

        var granted = await _service.GrantServerScopeAsync(TenantId, AgentSpId, ByoSpObjectId);

        granted.Should().BeTrue();
    }

    [Fact]
    public async Task GrantServerScopeAsync_ReturnsFalse_WhenGraphRejectsTheGrant()
    {
        _graph.CreateOrUpdateOauth2PermissionGrantAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
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
}
