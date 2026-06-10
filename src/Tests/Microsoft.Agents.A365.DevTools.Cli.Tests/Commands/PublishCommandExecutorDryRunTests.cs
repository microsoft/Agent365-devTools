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
/// created — the <c>{ServerName}-PublicClients</c> app.
/// </summary>
public class PublishCommandExecutorDryRunTests
{
    /// <summary>
    /// The dry-run log must (a) name only the Public Clients app — derived from <c>ServerName</c>,
    /// not <c>Alias</c> — (b) describe a PPMI-scope-only back-fill (no redirect-URI back-fill), and
    /// (c) skip the platform publish call entirely.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DryRun_NamesPublicClientsApp_AndBackfillsPpmiScopeOnly()
    {
        var logger = Substitute.For<ILogger>();
        var toolingService = Substitute.For<IAgent365ToolingService>();

        // ServerName and Alias are intentionally distinct so a regression that reverts to Alias
        // would surface in the negative assertion below.
        const string serverName = "mcp_TestServer";
        const string alias = "myAlias";
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

        // The dry-run line names the Public Clients app (derived from ServerName, not Alias).
        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o =>
                o.ToString()!.Contains("[DRY RUN] Would create Entra app") &&
                o.ToString()!.Contains(expectedPublicClientsAppName) &&
                !o.ToString()!.Contains($"{alias}-PublicClients")),
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
