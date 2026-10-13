// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Guards the token the grant-agents-access flow authenticates with. A token without the Graph
/// scopes below fails the calls with 403 Authorization_RequestDenied.
/// </summary>
public class GraphApiServiceAgentAccessAuthTests
{
    private const string TenantId = "12345678-1234-1234-1234-123456789012";

    private static (GraphApiService Service, IMicrosoftGraphTokenProvider Provider, IAuthenticationService Auth) CreateService(
        TestHttpMessageHandler handler)
    {
        var provider = Substitute.For<IMicrosoftGraphTokenProvider>();
        provider.GetMgGraphAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("agent-access-token");

        // The Ambient and legacy paths acquire through AuthenticationService, not the provider.
        var auth = Substitute.For<IAuthenticationService>();
        auth.GetAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
                Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("ambient-token");

        var service = new GraphApiService(
            NullLogger<GraphApiService>.Instance,
            new CommandExecutor(NullLogger<CommandExecutor>.Instance),
            auth,
            handler,
            provider,
            loginHintResolver: () => Task.FromResult<string?>(null));
        return (service, provider, auth);
    }

    /// <summary>
    /// Every acquisition in the operation must use the agent-access scopes and client app, and
    /// there must be exactly <paramref name="expectedCalls"/> of them.
    /// </summary>
    private static async Task AssertEveryAcquisitionUsedAgentAccessAsync(
        IMicrosoftGraphTokenProvider provider, int expectedCalls)
    {
        await provider.Received(expectedCalls).GetMgGraphAccessTokenAsync(
            TenantId,
            Arg.Is<IEnumerable<string>>(s => s.SequenceEqual(AuthenticationConstants.McpAgentAccessScopes)),
            Arg.Any<bool>(),
            AuthenticationConstants.GraphPowershellClientId,
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>());

        await provider.DidNotReceive().GetMgGraphAccessTokenAsync(
            Arg.Any<string>(),
            Arg.Is<IEnumerable<string>>(s => !s.SequenceEqual(AuthenticationConstants.McpAgentAccessScopes)),
            Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>());

        await provider.DidNotReceive().GetMgGraphAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(),
            Arg.Is<string?>(c => c != AuthenticationConstants.GraphPowershellClientId),
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    [Fact]
    public async Task TryFindApplicationAppIdsByDisplayNameForAgentAccessAsync_UsesTheAgentAccessTokenAndScopes()
    {
        using var handler = new TestHttpMessageHandler();
        var (service, provider, _) = CreateService(handler);
        handler.QueueResponse(Ok("""{"value":[{"appId":"app-1"}]}"""));

        await service.TryFindApplicationAppIdsByDisplayNameForAgentAccessAsync(TenantId, "Contoso MCP");

        await AssertEveryAcquisitionUsedAgentAccessAsync(provider, expectedCalls: 1);
    }

    [Fact]
    public async Task LookupServicePrincipalByAppIdForAgentAccessAsync_UsesTheAgentAccessTokenAndScopes()
    {
        using var handler = new TestHttpMessageHandler();
        var (service, provider, _) = CreateService(handler);
        handler.QueueResponse(Ok("""{"value":[{"id":"sp-1"}]}"""));

        await service.LookupServicePrincipalByAppIdForAgentAccessAsync(TenantId, "app-1");

        await AssertEveryAcquisitionUsedAgentAccessAsync(provider, expectedCalls: 1);
    }

    [Fact]
    public async Task TryGetOauth2PermissionGrantsForAgentAccessAsync_UsesTheAgentAccessTokenAndScopes()
    {
        using var handler = new TestHttpMessageHandler();
        var (service, provider, _) = CreateService(handler);
        handler.QueueResponse(Ok("""{"value":[]}"""));

        await service.TryGetOauth2PermissionGrantsForAgentAccessAsync(TenantId, "client-sp");

        await AssertEveryAcquisitionUsedAgentAccessAsync(provider, expectedCalls: 1);
    }

    [Fact]
    public async Task CreateOrUpdateOauth2PermissionGrantForAgentAccessAsync_UsesTheAgentAccessTokenAndScopes()
    {
        using var handler = new TestHttpMessageHandler();
        var (service, provider, _) = CreateService(handler);
        handler.QueueResponse(Ok("""{"value":[]}"""));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":"grant-1"}""")
        });

        var granted = await service.CreateOrUpdateOauth2PermissionGrantForAgentAccessAsync(
            TenantId, "client-sp", "resource-sp", ["Server.Read"]);

        granted.Should().BeTrue();
        handler.RequestCount.Should().Be(2, because: "the create path is a preflight read followed by a POST");
        await AssertEveryAcquisitionUsedAgentAccessAsync(provider, expectedCalls: 2);
    }

    /// <summary>
    /// The merge path reads then PATCHes. Both must run under the same identity: a read under one
    /// identity and a write under another 403s on the read alone, which is the original bug.
    /// </summary>
    [Fact]
    public async Task CreateOrUpdateOauth2PermissionGrantForAgentAccessAsync_UsesTheAgentAccessTokenWhenPatchingAnExistingGrant()
    {
        using var handler = new TestHttpMessageHandler();
        var (service, provider, _) = CreateService(handler);
        handler.QueueResponse(Ok("""{"value":[{"id":"grant-1","consentType":"AllPrincipals","resourceId":"resource-sp","scope":"Other.Read"}]}"""));
        handler.QueueResponse(Ok("""{"id":"grant-1"}"""));

        var granted = await service.CreateOrUpdateOauth2PermissionGrantForAgentAccessAsync(
            TenantId, "client-sp", "resource-sp", ["Server.Read"]);

        granted.Should().BeTrue();
        handler.RequestCount.Should().Be(2, because: "an existing grant missing the scope is read then PATCHed, not re-created");
        handler.RequestUris[1].ToString().Should().Contain("/oauth2PermissionGrants/grant-1",
            because: "the second request must target the existing grant by ID, which distinguishes the " +
                     "PATCH merge path from the POST create path this test is not covering");
        await AssertEveryAcquisitionUsedAgentAccessAsync(provider, expectedCalls: 2);
    }

    [Fact]
    public async Task TryGetOauth2PermissionGrantsAsync_StillUsesTheAmbientIdentityForOtherCommands()
    {
        using var handler = new TestHttpMessageHandler();
        var (service, provider, auth) = CreateService(handler);
        handler.QueueResponse(Ok("""{"value":[]}"""));

        await service.TryGetOauth2PermissionGrantsAsync(TenantId, "client-sp");

        await provider.DidNotReceive().GetMgGraphAccessTokenAsync(
            Arg.Any<string>(),
            Arg.Is<IEnumerable<string>>(s => s.SequenceEqual(AuthenticationConstants.McpAgentAccessScopes)),
            Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>());
        await auth.Received().GetAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    /// <summary>
    /// The flow must authenticate as Graph Command Line Tools even inside a project with a
    /// configured client app. #409 removed DelegatedPermissionGrant.ReadWrite.All from the custom
    /// app, so preferring it would prompt an admin to consent that scope back onto it.
    /// </summary>
    [Fact]
    public async Task AgentAccessFlow_IgnoresTheConfiguredClientApp()
    {
        using var handler = new TestHttpMessageHandler();
        var (service, provider, _) = CreateService(handler);
        const string configuredAppId = "99999999-9999-9999-9999-999999999999";
        service.CustomClientAppId = configuredAppId;
        handler.QueueResponse(Ok("""{"value":[{"id":"sp-1"}]}"""));

        var result = await service.LookupServicePrincipalByAppIdForAgentAccessAsync(TenantId, "app-1");

        result.Should().Be("sp-1");
        await AssertEveryAcquisitionUsedAgentAccessAsync(provider, expectedCalls: 1);
    }

    [Fact]
    public void McpAgentAccessScopes_CoverEveryGraphCallTheFlowMakes()
    {
        AuthenticationConstants.McpAgentAccessScopes.Should().BeEquivalentTo(
            new[]
            {
                AuthenticationConstants.ApplicationReadAllScope,
                AuthenticationConstants.AgentIdentityReadAllScope,
                AuthenticationConstants.DelegatedPermissionGrantReadWriteAllScope
            },
            because: "the flow reads applications and agent identities and writes an AllPrincipals delegated grant; " +
                     "a missing scope is silently dropped from the token and surfaces only as a 403 at the call site");
    }
}
