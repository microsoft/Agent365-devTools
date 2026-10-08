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

    /// <summary>
    /// ServiceTree-enrolled tenants reject app registrations without a serviceManagementReference,
    /// and strict tenants cap secret lifetimes; both the A365 proxy and Public Clients apps must
    /// therefore receive <c>--service-tree-id</c>, and the proxy secret must honor
    /// <c>--secret-lifetime-months</c>, exactly as the register flow does.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ForwardsServiceTreeIdAndSecretLifetime_ToEntraAppCreation()
    {
        var logger = Substitute.For<ILogger>();
        var tooling = Substitute.For<IAgent365ToolingService>();
        var graph = Substitute.For<GraphApiService>();

        ArrangeSuccessfulAppCreation(graph);

        tooling.PublishServerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PublishMcpServerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishMcpServerResponse { Status = "Success" });

        var args = MakeArgs() with { ServiceTreeId = "st-123", SecretLifetimeMonths = 6 };

        var executor = MakeExecutor(logger, tooling, graph);

        var result = await executor.ExecuteAsync(args, CancellationToken.None);

        result.Should().BeTrue();
        await graph.Received(1).CreateEntraAppAsync(
            TenantId, Arg.Is<string>(n => n.EndsWith("-A365Proxy")), "st-123", Arg.Any<CancellationToken>());
        await graph.Received(1).CreateEntraAppAsync(
            TenantId, Arg.Is<string>(n => n.EndsWith("-PublicClients")), "st-123", Arg.Any<CancellationToken>());
        await graph.Received(1).AddAppPasswordAsync(
            TenantId, "proxy-object-id", Arg.Any<string>(), 6, Arg.Any<CancellationToken>());
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
    public async Task ExecuteAsync_WhenConnectorCreatedButRedirectUriMissing_WarnsAndSkipsRedirectUpdate()
    {
        var logger = Substitute.For<ILogger>();
        var tooling = Substitute.For<IAgent365ToolingService>();
        var graph = Substitute.For<GraphApiService>();

        ArrangeSuccessfulAppCreation(graph);

        // Connector was created (id present) but no redirect URI came back — a real anomaly worth a
        // warning, unlike the first-party case where no connector is expected at all.
        tooling.PublishServerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PublishMcpServerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishMcpServerResponse { Status = "Success", A365ProxyConnectorId = "connector-id" });

        var executor = MakeExecutor(logger, tooling, graph);

        var result = await executor.ExecuteAsync(MakeArgs(), CancellationToken.None);

        result.Should().BeTrue();
        await graph.DidNotReceive().UpdateAppRedirectUrisAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("connector was created but publish returned no redirect URI")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// A custom server normally gets a connector, but if the platform returns a real payload that
    /// reports no connector (the CLI's name classification drifted from the platform's, or the platform
    /// treats the server as first-party), the proxy app created for it is unused and must be reconciled
    /// away so it doesn't linger in the tenant. The Public Clients app must be left in place. A "real
    /// payload" is distinguished by McpServerAppId being present - see the metadata-free placeholder
    /// case in <see cref="ExecuteAsync_WhenPublishReturnsMetadataFreeSuccess_KeepsProxyApp_AndWarns"/>.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenNoConnectorCreated_DeletesUnusedProxyApp_AndSkipsRedirectUpdate()
    {
        var logger = Substitute.For<ILogger>();
        var tooling = Substitute.For<IAgent365ToolingService>();
        var graph = Substitute.For<GraphApiService>();

        var (_, _, proxyObjectId) = ArrangeSuccessfulAppCreation(graph);

        // Real v2 payload (McpServerAppId present) with no connector id and no redirect URI => the
        // platform genuinely created no connector for this server.
        tooling.PublishServerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PublishMcpServerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishMcpServerResponse { Status = "Success", McpServerAppId = "1a2a0eb6-0000-0000-0000-000000000000" });

        var executor = MakeExecutor(logger, tooling, graph);

        var result = await executor.ExecuteAsync(MakeArgs(), CancellationToken.None);

        result.Should().BeTrue();
        await graph.Received(1).DeleteEntraAppAsync(TenantId, proxyObjectId, Arg.Any<CancellationToken>());
        await graph.DidNotReceive().DeleteEntraAppAsync(TenantId, "pc-object-id", Arg.Any<CancellationToken>());
        await graph.DidNotReceive().UpdateAppRedirectUrisAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The tooling layer returns a placeholder <c>{ Status = "Success" }</c> (every connector field and
    /// McpServerAppId null) when the platform answers 2xx with an empty or undeserializable body. By
    /// then the proxy client id and secret have already been sent, so the platform may have created the
    /// connector. Deleting the proxy app here would orphan that connector's OAuth client. The executor
    /// must therefore keep the proxy app, warn naming it and its clientId, and still exit success.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenPublishReturnsMetadataFreeSuccess_KeepsProxyApp_AndWarns()
    {
        var logger = Substitute.For<ILogger>();
        var tooling = Substitute.For<IAgent365ToolingService>();
        var graph = Substitute.For<GraphApiService>();

        var (proxyClientId, _, proxyObjectId) = ArrangeSuccessfulAppCreation(graph);

        // Metadata-free placeholder: a 2xx with no body => no McpServerAppId and no connector fields.
        // This must NOT be read as proof that no connector was created.
        tooling.PublishServerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PublishMcpServerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishMcpServerResponse { Status = "Success" });

        var executor = MakeExecutor(logger, tooling, graph);

        var result = await executor.ExecuteAsync(MakeArgs(), CancellationToken.None);

        result.Should().BeTrue("a 2xx publish still succeeds even when the body carries no metadata");
        await graph.DidNotReceive().DeleteEntraAppAsync(TenantId, proxyObjectId, Arg.Any<CancellationToken>());
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("connector creation could not be confirmed")
                && o.ToString()!.Contains(proxyClientId)),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// If Public Clients creation throws after the confidential proxy app (with its secret) is
    /// created, the proxy app is orphaned unless explicitly cleaned up — the failure predates the
    /// full <c>EntraAppSet</c> that the platform-failure rollback path deletes. The executor must
    /// delete the proxy app itself and fail the publish without calling the platform.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenPublicClientsCreationThrows_DeletesOrphanedProxyApp_AndAbortsPublish()
    {
        var logger = Substitute.For<ILogger>();
        var tooling = Substitute.For<IAgent365ToolingService>();
        var graph = Substitute.For<GraphApiService>();

        const string proxyObjectId = "proxy-object-id";
        graph.CreateEntraAppAsync(Arg.Any<string>(), Arg.Is<string>(n => n.EndsWith("-A365Proxy")), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((proxyObjectId, "proxy-client-id"));
        graph.AddAppPasswordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("proxy-secret");
        graph.CreateEntraAppAsync(Arg.Any<string>(), Arg.Is<string>(n => n.EndsWith("-PublicClients")), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<(string, string)?>(_ => throw new InvalidOperationException("graph throttled"));
        graph.DeleteEntraAppAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var executor = MakeExecutor(logger, tooling, graph);

        var result = await executor.ExecuteAsync(MakeArgs(), CancellationToken.None);

        result.Should().BeFalse("a failure creating the Public Clients app must abort the publish");
        await graph.Received(1).DeleteEntraAppAsync(TenantId, proxyObjectId, Arg.Any<CancellationToken>());
        await tooling.DidNotReceiveWithAnyArgs().PublishServerAsync(default!, default!, default!, default);
    }

    /// <summary>
    /// The A365 proxy app is the OAuth client of the platform-created connector, with McpServerAppId
    /// as its resource. Without a required-resource-access grant for that resource on the proxy app,
    /// Entra rejects the connector's token request (AADSTS650057). The grant must therefore land on
    /// BOTH the proxy app and the Public Clients app.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_GrantsMcpServerResourceAccess_OnBothProxyAndPublicClientsApps()
    {
        var logger = Substitute.For<ILogger>();
        var tooling = Substitute.For<IAgent365ToolingService>();
        var graph = Substitute.For<GraphApiService>();

        var (_, _, proxyObjectId) = ArrangeSuccessfulAppCreation(graph);

        const string mcpServerAppId = "1a2a0eb6-0000-0000-0000-000000000000";
        const string mcpServerScope = "Tools.ListInvoke.All";
        var scopeId = Guid.NewGuid();

        graph.GetOAuth2PermissionScopeIdAsync(TenantId, mcpServerAppId, mcpServerScope, Arg.Any<CancellationToken>())
            .Returns(scopeId);
        graph.AddRequiredResourceAccessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        tooling.PublishServerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PublishMcpServerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishMcpServerResponse
            {
                Status = "Success",
                McpServerAppId = mcpServerAppId,
                McpServerScope = mcpServerScope,
                A365ProxyConnectorId = "connector-id",
            });

        var executor = MakeExecutor(logger, tooling, graph);

        var result = await executor.ExecuteAsync(MakeArgs(), CancellationToken.None);

        result.Should().BeTrue();
        await graph.Received(1).AddRequiredResourceAccessAsync(
            TenantId, proxyObjectId, mcpServerAppId, scopeId, Arg.Any<CancellationToken>());
        await graph.Received(1).AddRequiredResourceAccessAsync(
            TenantId, "pc-object-id", mcpServerAppId, scopeId, Arg.Any<CancellationToken>());
        await graph.Received(2).AddRequiredResourceAccessAsync(
            Arg.Any<string>(), Arg.Any<string>(), mcpServerAppId, scopeId, Arg.Any<CancellationToken>());
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
    /// First-party (OOB) Dataverse servers are fronted by the platform's own Entra app. Publish must
    /// classify them by name up front and NOT create an A365 proxy app or secret, and must send null
    /// (not empty) proxy credentials: the platform's v2 publish binds the proxy client id as a Guid?,
    /// where null means "no connector needed" and an empty string is a 400. Only the Public Clients
    /// app is created, and there is no unused proxy app to reconcile away.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenFirstPartyDataverseServer_SkipsProxyApp_AndSendsNullProxyCredentials()
    {
        var logger = Substitute.For<ILogger>();
        var tooling = Substitute.For<IAgent365ToolingService>();
        var graph = Substitute.For<GraphApiService>();

        graph.CreateEntraAppAsync(Arg.Any<string>(), Arg.Is<string>(n => n.EndsWith("-PublicClients")), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(("pc-object-id", "pc-client-id"));
        graph.UpdateAppPublicClientRedirectUrisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        PublishMcpServerRequest? capturedRequest = null;
        tooling.PublishServerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<PublishMcpServerRequest>(r => capturedRequest = r), Arg.Any<CancellationToken>())
            .Returns(new PublishMcpServerResponse { Status = "Success" });

        var executor = MakeExecutor(logger, tooling, graph);
        var args = MakeArgs() with { ServerName = "msdyn_DataverseMCPServer" };

        var result = await executor.ExecuteAsync(args, CancellationToken.None);

        result.Should().BeTrue();
        await graph.DidNotReceive().CreateEntraAppAsync(
            Arg.Any<string>(), Arg.Is<string>(n => n.EndsWith("-A365Proxy")), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await graph.DidNotReceiveWithAnyArgs().AddAppPasswordAsync(default!, default!, default!, default, default);
        await graph.Received(1).CreateEntraAppAsync(
            Arg.Any<string>(), Arg.Is<string>(n => n.EndsWith("-PublicClients")), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        capturedRequest.Should().NotBeNull();
        capturedRequest!.A365ProxyClientId.Should().BeNull(
            because: "a first-party server has no proxy app; the platform binds proxy client id as Guid? and a null (not empty) value signals 'no connector needed', avoiding a 400");
        capturedRequest.A365ProxyClientSecret.Should().BeNull(
            because: "no proxy secret is created for first-party servers");

        await graph.DidNotReceiveWithAnyArgs().DeleteEntraAppAsync(default!, default!, default);
    }

    /// <summary>
    /// Rollback runs because a publish failed - frequently because the caller cancelled (Ctrl+C). It
    /// must therefore delete the just-created apps with a cancellation-independent token, not the
    /// caller's (already-cancelled) token; otherwise the Public Clients app and the confidential proxy
    /// app (with its live secret) are left orphaned in the tenant.
    /// </summary>
    [Fact]
    public async Task RollbackEntraAppsAsync_UsesCancellationIndependentToken_WhenCallerTokenIsCancelled()
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

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await executor.RollbackEntraAppsAsync(apps, TenantId, cts.Token);

        await graph.Received(1).DeleteEntraAppAsync(
            TenantId, "pc-object-id", Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
        await graph.Received(1).DeleteEntraAppAsync(
            TenantId, "proxy-object-id", Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    /// <summary>
    /// When the publish response shows no connector (the proxy app is unused) AND the automatic delete
    /// of that proxy app fails, the executor must surface a user-facing warning to delete it manually.
    /// A silent delete failure would leave an unused confidential app (with a live secret) in the
    /// tenant with no signal to the user.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenUnusedProxyDeleteFails_SurfacesManualCleanupWarning()
    {
        var logger = Substitute.For<ILogger>();
        var tooling = Substitute.For<IAgent365ToolingService>();
        var graph = Substitute.For<GraphApiService>();

        ArrangeSuccessfulAppCreation(graph);
        // The reconcile delete of the unused proxy app fails (overrides the arrange's success stub).
        graph.DeleteEntraAppAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        // Real v2 payload (McpServerAppId present) with no connector => proxy is unused and the
        // reconcile tries to delete it; that delete failing is what this test exercises.
        tooling.PublishServerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PublishMcpServerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishMcpServerResponse { Status = "Success", McpServerAppId = "1a2a0eb6-0000-0000-0000-000000000000" });

        var executor = MakeExecutor(logger, tooling, graph);

        var result = await executor.ExecuteAsync(MakeArgs(), CancellationToken.None);

        result.Should().BeTrue("a failed cleanup of an unused proxy app is a warning, not a publish failure");
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("could not be deleted automatically")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// Cancellation can arrive after both Entra apps are provisioned but before the platform publish
    /// call. The pre-publish cancellation check must roll back BOTH apps (including the confidential
    /// proxy app and its live secret) before propagating the cancellation; otherwise they are leaked
    /// in the tenant with no corresponding platform record. The direct RollbackEntraAppsAsync test
    /// does not exercise this ExecuteAsync path.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenCancelledAfterProvisioning_RollsBackBothApps_AndSkipsPlatformCall()
    {
        var logger = Substitute.For<ILogger>();
        var tooling = Substitute.For<IAgent365ToolingService>();
        var graph = Substitute.For<GraphApiService>();

        ArrangeSuccessfulAppCreation(graph);

        using var cts = new CancellationTokenSource();
        // Cancel on the last provisioning Graph call (public-client redirect URIs) so both apps are
        // fully created and the next cancellation check in ExecuteAsync observes the cancellation.
        graph.When(g => g.UpdateAppPublicClientRedirectUrisAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>()))
            .Do(_ => cts.Cancel());

        var executor = MakeExecutor(logger, tooling, graph);

        var act = async () => await executor.ExecuteAsync(MakeArgs(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            because: "a cancellation observed after provisioning but before the platform call must propagate, not be swallowed");

        // Both apps rolled back, with a cancellation-independent token so the already-cancelled
        // caller token does not skip the deletes.
        await graph.Received(1).DeleteEntraAppAsync(
            TenantId, "pc-object-id", Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
        await graph.Received(1).DeleteEntraAppAsync(
            TenantId, "proxy-object-id", Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
        // No platform call occurred, so there is nothing published to compensate for beyond the apps.
        await tooling.DidNotReceiveWithAnyArgs().PublishServerAsync(default!, default!, default!, default);
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
