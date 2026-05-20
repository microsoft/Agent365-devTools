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
    [Fact]
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

        await toolingService.DidNotReceiveWithAnyArgs().PublishServerV2Async(default!, default!, default!, default);
    }
}
