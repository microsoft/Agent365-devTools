// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Covers the Entra-app orchestration <see cref="PublishCommandExecutor"/> performs for custom
/// (non-Dataverse) MCP servers: the confidential A365 proxy app must be created and its credentials
/// forwarded to the platform so the Power Platform connector is created at publish time, and both
/// created apps must be rolled back on failure. These invariants are what let the platform stop
/// logging <c>A365ProxyConnectorCreation=SkippedNoCredentials</c>.
///
/// Tests substitute the concrete <see cref="GraphApiService"/> (all Entra calls are virtual) and let
/// the real <see cref="EntraAppProvisioner"/> run against it, mirroring the production wiring.
/// </summary>
public class PublishCommandExecutorEntraAppTests
{
    private const string TenantId = "00000000-0000-0000-0000-000000000001";
    private const string EnvironmentId = "00000000-0000-0000-0000-000000000000";
    private const string ServerName = "mcp_TestServer";

    private static RawPublishArgs MakeArgs() => new(
        EnvironmentId: EnvironmentId,
        ServerName: ServerName,
        Alias: "myAlias",
        DisplayName: "Test Display",
        PublisherName: "Contoso",
        Yes: true,
        DryRun: false);

    private static PublishCommandExecutor MakeExecutor(
        ILogger logger, IAgent365ToolingService tooling, GraphApiService graph)
    {
        var retry = new RetryHelper(logger, maxRetries: 1, baseDelaySeconds: 0);
        return new TestablePublishCommandExecutor(logger, tooling, graph, retry, TenantId);
    }

    /// <summary>
    /// Stubs a successful two-app creation: the A365 proxy app (with a secret) and the Public
    /// Clients app. Returns the proxy client id / secret / object id the tests assert on.
    /// </summary>
    private static (string ProxyClientId, string ProxySecret, string ProxyObjectId) ArrangeSuccessfulAppCreation(GraphApiService graph)
    {
        const string proxyObjectId = "proxy-object-id";
        const string proxyClientId = "proxy-client-id";
        const string proxySecret = "proxy-secret";

        graph.CreateEntraAppAsync(Arg.Any<string>(), Arg.Is<string>(n => n.EndsWith("-A365Proxy")), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((proxyObjectId, proxyClientId));
        graph.CreateEntraAppAsync(Arg.Any<string>(), Arg.Is<string>(n => n.EndsWith("-PublicClients")), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(("pc-object-id", "pc-client-id"));
        graph.AddAppPasswordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(proxySecret);
        graph.UpdateAppPublicClientRedirectUrisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        graph.UpdateAppRedirectUrisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        graph.DeleteEntraAppAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        return (proxyClientId, proxySecret, proxyObjectId);
    }

    [Fact]
    public async Task ExecuteAsync_WhenProxyAppCreationFails_AbortsPublish_WithoutCallingPlatform()
    {
        var logger = Substitute.For<ILogger>();
        var tooling = Substitute.For<IAgent365ToolingService>();
        var graph = Substitute.For<GraphApiService>();

        // Proxy app creation fails; public-clients creation is never reached because the proxy app
        // is mandatory for custom servers.
        graph.CreateEntraAppAsync(Arg.Any<string>(), Arg.Is<string>(n => n.EndsWith("-A365Proxy")), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(((string, string)?)null);

        var executor = MakeExecutor(logger, tooling, graph);

        var result = await executor.ExecuteAsync(MakeArgs(), CancellationToken.None);

        result.Should().BeFalse("proxy app creation failure must fail the publish for custom servers");
        await tooling.DidNotReceiveWithAnyArgs().PublishServerAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsProxyCredentials_ToPlatformPublishRequest()
    {
        var logger = Substitute.For<ILogger>();
        var tooling = Substitute.For<IAgent365ToolingService>();
        var graph = Substitute.For<GraphApiService>();

        var (proxyClientId, proxySecret, _) = ArrangeSuccessfulAppCreation(graph);

        PublishMcpServerRequest? capturedRequest = null;
        tooling.PublishServerAsync(EnvironmentId, ServerName, Arg.Do<PublishMcpServerRequest>(r => capturedRequest = r), Arg.Any<CancellationToken>())
            .Returns(new PublishMcpServerResponse { Status = "Success" });

        var executor = MakeExecutor(logger, tooling, graph);

        var result = await executor.ExecuteAsync(MakeArgs(), CancellationToken.None);

        result.Should().BeTrue();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.A365ProxyClientId.Should().Be(proxyClientId,
            because: "the platform creates the Power Platform connector only when the proxy app's client id is supplied");
        capturedRequest.A365ProxyClientSecret.Should().Be(proxySecret,
            because: "the platform needs the proxy app's secret to create the connector; without it it logs SkippedNoCredentials");
    }

    [Fact]
    public async Task ExecuteAsync_WhenProxyRedirectUriReturned_UpdatesProxyAppRedirectUris()
    {
        var logger = Substitute.For<ILogger>();
        var tooling = Substitute.For<IAgent365ToolingService>();
        var graph = Substitute.For<GraphApiService>();

        var (_, _, proxyObjectId) = ArrangeSuccessfulAppCreation(graph);

        tooling.PublishServerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PublishMcpServerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishMcpServerResponse
            {
                Status = "Success",
                A365ProxyRedirectUri = "https://global.consent.azure-apim.net/redirect",
            });

        var executor = MakeExecutor(logger, tooling, graph);

        var result = await executor.ExecuteAsync(MakeArgs(), CancellationToken.None);

        result.Should().BeTrue();
        await graph.Received(1).UpdateAppRedirectUrisAsync(
            TenantId, proxyObjectId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenProxyRedirectUriMissing_WarnsAndSkipsRedirectUpdate()
    {
        var logger = Substitute.For<ILogger>();
        var tooling = Substitute.For<IAgent365ToolingService>();
        var graph = Substitute.For<GraphApiService>();

        ArrangeSuccessfulAppCreation(graph);

        tooling.PublishServerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PublishMcpServerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishMcpServerResponse { Status = "Success" });

        var executor = MakeExecutor(logger, tooling, graph);

        var result = await executor.ExecuteAsync(MakeArgs(), CancellationToken.None);

        result.Should().BeTrue();
        await graph.DidNotReceive().UpdateAppRedirectUrisAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("A365 Proxy redirect URI was not returned")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task RollbackEntraAppsAsync_DeletesBothPublicClientsAndProxyApps()
    {
        var logger = Substitute.For<ILogger>();
        var tooling = Substitute.For<IAgent365ToolingService>();
        var graph = Substitute.For<GraphApiService>();
        graph.DeleteEntraAppAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var executor = MakeExecutor(logger, tooling, graph);

        var apps = new PublishCommandExecutor.EntraAppSet(
            PublicClientsClientId: "pc-client-id",
            PublicClientsObjectId: "pc-object-id",
            PublicClientsAppName: $"{ServerName}-PublicClients",
            A365AppClientId: "proxy-client-id",
            A365AppSecret: "proxy-secret",
            A365AppObjectId: "proxy-object-id",
            A365AppName: $"{ServerName}-A365Proxy");

        await executor.RollbackEntraAppsAsync(apps, TenantId, CancellationToken.None);

        await graph.Received(1).DeleteEntraAppAsync(TenantId, "pc-object-id", Arg.Any<CancellationToken>());
        await graph.Received(1).DeleteEntraAppAsync(TenantId, "proxy-object-id", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Overrides only the tenant-detection seam (which shells out to Azure CLI) so the rest of the
    /// executor runs unchanged against the substituted Graph and tooling services.
    /// </summary>
    private sealed class TestablePublishCommandExecutor : PublishCommandExecutor
    {
        private readonly string _tenantId;

        public TestablePublishCommandExecutor(
            ILogger logger, IAgent365ToolingService toolingService, GraphApiService graphApiService,
            RetryHelper retryHelper, string tenantId)
            : base(logger, toolingService, graphApiService, retryHelper)
        {
            _tenantId = tenantId;
        }

        protected override Task<string?> DetectTenantIdAsync() => Task.FromResult<string?>(_tenantId);
    }
}
