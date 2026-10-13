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
    /// Every agent-access call must request the same (scopes, clientId) pair, or the token
    /// provider partitions its cache and the run costs more than one sign-in.
    /// </summary>
    private static async Task AssertAcquiredAgentAccessTokenAsync(IMicrosoftGraphTokenProvider provider)
    {
        await provider.Received().GetMgGraphAccessTokenAsync(
            TenantId,
            Arg.Is<IEnumerable<string>>(s => s.SequenceEqual(AuthenticationConstants.McpAgentAccessScopes)),
            Arg.Any<bool>(),
            AuthenticationConstants.GraphPowershellClientId,
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

        await AssertAcquiredAgentAccessTokenAsync(provider);
    }

    [Fact]
    public async Task LookupServicePrincipalByAppIdForAgentAccessAsync_UsesTheAgentAccessTokenAndScopes()
    {
        using var handler = new TestHttpMessageHandler();
        var (service, provider, _) = CreateService(handler);
        handler.QueueResponse(Ok("""{"value":[{"id":"sp-1"}]}"""));

        await service.LookupServicePrincipalByAppIdForAgentAccessAsync(TenantId, "app-1");

        await AssertAcquiredAgentAccessTokenAsync(provider);
    }

    [Fact]
    public async Task TryGetOauth2PermissionGrantsForAgentAccessAsync_UsesTheAgentAccessTokenAndScopes()
    {
        using var handler = new TestHttpMessageHandler();
        var (service, provider, _) = CreateService(handler);
        handler.QueueResponse(Ok("""{"value":[]}"""));

        await service.TryGetOauth2PermissionGrantsForAgentAccessAsync(TenantId, "client-sp");

        await AssertAcquiredAgentAccessTokenAsync(provider);
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

        await service.CreateOrUpdateOauth2PermissionGrantForAgentAccessAsync(
            TenantId, "client-sp", "resource-sp", ["Server.Read"]);

        await AssertAcquiredAgentAccessTokenAsync(provider);
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

    [Fact]
    public async Task AgentAccessFlow_PrefersTheConfiguredClientApp()
    {
        using var handler = new TestHttpMessageHandler();
        var (service, provider, _) = CreateService(handler);
        const string configuredAppId = "99999999-9999-9999-9999-999999999999";
        service.CustomClientAppId = configuredAppId;
        handler.QueueResponse(Ok("""{"value":[{"id":"sp-1"}]}"""));

        await service.LookupServicePrincipalByAppIdForAgentAccessAsync(TenantId, "app-1");

        await provider.Received().GetMgGraphAccessTokenAsync(
            TenantId,
            Arg.Is<IEnumerable<string>>(s => s.SequenceEqual(AuthenticationConstants.McpAgentAccessScopes)),
            Arg.Any<bool>(),
            configuredAppId,
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task AgentAccessFlow_FallsBackToGraphCommandLineToolsWhenTheConfiguredAppCannotGetAToken()
    {
        using var handler = new TestHttpMessageHandler();
        var (service, provider, _) = CreateService(handler);
        const string configuredAppId = "99999999-9999-9999-9999-999999999999";
        service.CustomClientAppId = configuredAppId;

        // A registration that predates this command has no DelegatedPermissionGrant.ReadWrite.All,
        // so its acquisition yields no token.
        provider.GetMgGraphAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(), configuredAppId,
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns((string?)null);
        handler.QueueResponse(Ok("""{"value":[{"id":"sp-1"}]}"""));

        var result = await service.LookupServicePrincipalByAppIdForAgentAccessAsync(TenantId, "app-1");

        result.Should().Be("sp-1",
            because: "the command worked with no config before the configured app was preferred, so a " +
                     "registration that lacks the scopes must not turn a working flow into a failure");
        await AssertAcquiredAgentAccessTokenAsync(provider);
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
