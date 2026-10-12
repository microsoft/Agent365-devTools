// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
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

    private static (GraphApiService Service, IMicrosoftGraphTokenProvider Provider, TestHttpMessageHandler Handler) CreateService()
    {
        var provider = Substitute.For<IMicrosoftGraphTokenProvider>();
        provider.GetMgGraphAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("agent-access-token");

        var handler = new TestHttpMessageHandler();
        var service = new GraphApiService(
            NullLogger<GraphApiService>.Instance,
            new CommandExecutor(NullLogger<CommandExecutor>.Instance),
            Substitute.For<IAuthenticationService>(),
            handler,
            provider,
            loginHintResolver: () => Task.FromResult<string?>(null));
        return (service, provider, handler);
    }

    [Fact]
    public async Task LookupServicePrincipalByAppIdForAgentAccessAsync_RequestsAgentAccessScopesFromThePreauthorizedApp()
    {
        var (service, provider, handler) = CreateService();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[{"id":"sp-1"}]}""")
        });

        await service.LookupServicePrincipalByAppIdForAgentAccessAsync(TenantId, "app-1");

        await provider.Received(1).GetMgGraphAccessTokenAsync(
            TenantId,
            Arg.Is<IEnumerable<string>>(s => s.SequenceEqual(AuthenticationConstants.McpAgentAccessScopes)),
            Arg.Any<bool>(),
            AuthenticationConstants.GraphPowershellClientId,
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task CreateOrUpdateOauth2PermissionGrantForAgentAccessAsync_RequestsAgentAccessScopesFromThePreauthorizedApp()
    {
        var (service, provider, handler) = CreateService();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[]}""")
        });
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":"grant-1"}""")
        });

        await service.CreateOrUpdateOauth2PermissionGrantForAgentAccessAsync(
            TenantId, "client-sp", "resource-sp", ["Server.Read"]);

        await provider.Received().GetMgGraphAccessTokenAsync(
            TenantId,
            Arg.Is<IEnumerable<string>>(s => s.SequenceEqual(AuthenticationConstants.McpAgentAccessScopes)),
            Arg.Any<bool>(),
            AuthenticationConstants.GraphPowershellClientId,
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>());
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

    [Fact]
    public void AgentAccessUsesGraphCommandLineToolsRatherThanAzurePowerShell()
    {
        AuthenticationConstants.GraphPowershellClientId.Should().NotBe(
            AuthenticationConstants.PowershellClientId,
            because: "Azure PowerShell is not preauthorized for Graph delegated scopes, so its '.default' token " +
                     "silently omits them instead of failing, which is what produced the original 403");
    }
}
