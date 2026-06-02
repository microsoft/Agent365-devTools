// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Tests for <see cref="PublishCommandExecutor"/> dry-run output. The dry-run log must mirror the
/// real Entra app naming scheme (derived from <c>ServerName</c>) so users can predict what will be
/// created. Earlier the log used <c>Alias</c>, which diverged from CreateEntraAppsAsync's
/// <c>{ServerName}-A365Proxy</c> / <c>{ServerName}-PublicClients</c> naming.
/// </summary>
public class PublishCommandExecutorDryRunTests
{
    [Fact(Skip = "A365 Proxy Entra app creation is TEMPORARILY DISABLED in PublishCommandExecutor while the platform custom connector flow is commented out. Dry-run log no longer mentions the A365Proxy app or the redirect-URI back-fill. Re-enable together with CreateEntraAppsAsync's proxy creation.")]
    public async Task ExecuteAsync_DryRun_LogsEntraAppNamesDerivedFromServerName()
    {
        var logger = Substitute.For<ILogger>();
        var toolingService = Substitute.For<IAgent365ToolingService>();

        // ServerName and Alias are intentionally distinct so a regression that reverts to Alias
        // would surface in the negative assertion below.
        const string serverName = "mcp_TestServer";
        const string alias = "myAlias";
        const string expectedA365AppName = "mcp_TestServer-A365Proxy";
        const string expectedPublicClientsAppName = "mcp_TestServer-PublicClients";

        var args = new RawPublishArgs(
            EnvironmentId: "00000000-0000-0000-0000-000000000000",
            ServerName: serverName,
            Alias: alias,
            DisplayName: "Test Display",
            PublisherName: null,
            Yes: false,
            DryRun: true);

        var executor = new PublishCommandExecutor(logger, toolingService, graphApiService: null);

        await executor.ExecuteAsync(args, CancellationToken.None);

        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o =>
                o.ToString()!.Contains("[DRY RUN] Would create Entra apps") &&
                o.ToString()!.Contains(expectedA365AppName) &&
                o.ToString()!.Contains(expectedPublicClientsAppName) &&
                !o.ToString()!.Contains($"{alias}-A365Proxy") &&
                !o.ToString()!.Contains($"{alias}-PublicClients")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Would call publish endpoint and back-fill")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        await toolingService.DidNotReceiveWithAnyArgs().PublishServerAsync(default!, default!, default!, default);
    }

    /// <summary>
    /// While the A365 Proxy flow is disabled, the dry-run log must (a) mention only the Public
    /// Clients app — not the A365 Proxy app — derived from <c>ServerName</c>, (b) replace the
    /// redirect-URI back-fill line with a PPMI-scope-only back-fill line, and (c) still skip the
    /// platform publish call. Acts as the canary: if the proxy line silently comes back without
    /// the corresponding logic being re-enabled, this fails.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DryRun_OmitsA365ProxyApp_WhileCustomConnectorFlowDisabled()
    {
        var logger = Substitute.For<ILogger>();
        var toolingService = Substitute.For<IAgent365ToolingService>();

        const string serverName = "mcp_TestServer";
        const string alias = "myAlias";
        const string disabledA365AppName = "mcp_TestServer-A365Proxy";
        const string expectedPublicClientsAppName = "mcp_TestServer-PublicClients";

        var args = new RawPublishArgs(
            EnvironmentId: "00000000-0000-0000-0000-000000000000",
            ServerName: serverName,
            Alias: alias,
            DisplayName: "Test Display",
            PublisherName: null,
            Yes: false,
            DryRun: true);

        var executor = new PublishCommandExecutor(logger, toolingService, graphApiService: null);

        await executor.ExecuteAsync(args, CancellationToken.None);

        // The active dry-run line names only Public Clients and does NOT name A365 Proxy.
        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o =>
                o.ToString()!.Contains("[DRY RUN] Would create Entra app") &&
                o.ToString()!.Contains(expectedPublicClientsAppName) &&
                !o.ToString()!.Contains(disabledA365AppName)),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // The back-fill line now mentions only PPMI scope, not redirect URI.
        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o =>
                o.ToString()!.Contains("back-fill PPMI scope") &&
                !o.ToString()!.Contains("redirect URI")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        await toolingService.DidNotReceiveWithAnyArgs().PublishServerAsync(default!, default!, default!, default);
    }

    /// <summary>
    /// Sanity check that <c>--publisher-name</c> threads through the executor without crashing the
    /// dry-run flow. The platform's v2 validator rejects an empty publisher only for Custom servers;
    /// at this CLI layer we don't classify, we just forward what was provided. This test pins that
    /// the field is accepted on <see cref="RawPublishArgs"/>, no interactive prompt is invoked when
    /// a value is supplied (the prompt would hang in xUnit without stdin), and the dry-run still
    /// short-circuits before any platform call.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DryRun_AcceptsPublisherName_WithoutCallingPlatform()
    {
        var logger = Substitute.For<ILogger>();
        var toolingService = Substitute.For<IAgent365ToolingService>();

        var args = new RawPublishArgs(
            EnvironmentId: "00000000-0000-0000-0000-000000000000",
            ServerName: "mcp_TestServer",
            Alias: "myAlias",
            DisplayName: "Test Display",
            PublisherName: "Contoso",
            Yes: false,
            DryRun: true);

        var executor = new PublishCommandExecutor(logger, toolingService, graphApiService: null);

        await executor.ExecuteAsync(args, CancellationToken.None);

        // Dry-run short-circuits before the platform publish call regardless of publisher.
        await toolingService.DidNotReceiveWithAnyArgs().PublishServerAsync(default!, default!, default!, default);
    }
}
