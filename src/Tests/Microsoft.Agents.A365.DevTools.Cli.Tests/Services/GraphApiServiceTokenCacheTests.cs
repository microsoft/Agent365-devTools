// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Tests to validate that Azure CLI Graph tokens are cached across consecutive
/// Graph API calls, avoiding redundant 'az' subprocess spawns.
/// </summary>
public class GraphApiServiceTokenCacheTests
{
    /// <summary>
    /// Helper: create a GraphApiService with a mock executor that counts calls
    /// and returns a predictable token.
    /// </summary>
    private static (GraphApiService service, TestHttpMessageHandler handler, CommandExecutor executor) CreateService(string token = "cached-token")
    {
        var handler = new TestHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        executor.ExecuteAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);
                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = token, StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var service = new GraphApiService(logger, executor, handler);
        return (service, handler, executor);
    }

    [Fact]
    public async Task MultipleGraphGetAsync_SameTenant_AcquiresTokenOnlyOnce()
    {
        // Arrange
        var (service, handler, executor) = CreateService();

        try
        {
            // Queue 3 successful GET responses
            for (int i = 0; i < 3; i++)
            {
                handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"value\":[]}")
                });
            }

            // Act - make 3 consecutive Graph GET calls to the same tenant
            var r1 = await service.GraphGetAsync("tenant-1", "/v1.0/path1");
            var r2 = await service.GraphGetAsync("tenant-1", "/v1.0/path2");
            var r3 = await service.GraphGetAsync("tenant-1", "/v1.0/path3");

            // Assert - all calls should succeed
            r1.Should().NotBeNull();
            r2.Should().NotBeNull();
            r3.Should().NotBeNull();

            // The token should be acquired only ONCE (1 account show + 1 get-access-token = 2 az calls)
            await executor.Received(1).ExecuteAsync(
                "az",
                Arg.Is<string>(s => s.Contains("get-access-token")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

            await executor.Received(1).ExecuteAsync(
                "az",
                Arg.Is<string>(s => s.Contains("account show")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            handler.Dispose();
        }
    }

    [Fact]
    public async Task GraphGetAsync_DifferentTenants_AcquiresTokenForEach()
    {
        // Arrange
        var (service, handler, executor) = CreateService();

        try
        {
            // Queue 2 responses
            for (int i = 0; i < 2; i++)
            {
                handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"value\":[]}")
                });
            }

            // Act - make calls to different tenants
            var r1 = await service.GraphGetAsync("tenant-1", "/v1.0/path1");
            var r2 = await service.GraphGetAsync("tenant-2", "/v1.0/path2");

            // Assert
            r1.Should().NotBeNull();
            r2.Should().NotBeNull();

            // Token should be acquired twice (once per tenant)
            await executor.Received(2).ExecuteAsync(
                "az",
                Arg.Is<string>(s => s.Contains("get-access-token")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            handler.Dispose();
        }
    }

    [Fact]
    public async Task MixedGraphOperations_SameTenant_AcquiresTokenOnlyOnce()
    {
        // Arrange
        var (service, handler, executor) = CreateService();

        try
        {
            // Queue responses for GET, POST, GET sequence
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[]}")
            });
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"123\"}")
            });
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[]}")
            });

            // Act - interleave GET and POST calls
            var r1 = await service.GraphGetAsync("tenant-1", "/v1.0/path1");
            var r2 = await service.GraphPostAsync("tenant-1", "/v1.0/path2", new { name = "test" });
            var r3 = await service.GraphGetAsync("tenant-1", "/v1.0/path3");

            // Assert
            r1.Should().NotBeNull();
            r2.Should().NotBeNull();
            r3.Should().NotBeNull();

            // Only one token acquisition across all operations
            await executor.Received(1).ExecuteAsync(
                "az",
                Arg.Is<string>(s => s.Contains("get-access-token")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            handler.Dispose();
        }
    }

    [Fact]
    public void AzCliTokenCacheDuration_IsFiveMinutes()
    {
        // The cache duration should be a reasonable window to avoid stale tokens
        // while eliminating redundant subprocess spawns within a single command.
        GraphApiService.AzCliTokenCacheDuration.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task GraphGetAsync_ExpiredCache_AcquiresNewToken()
    {
        // Arrange
        var (service, handler, executor) = CreateService();

        try
        {
            // Queue 2 successful GET responses
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[]}")
            });
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[]}")
            });

            // Act - First call should acquire token and cache it
            await service.GraphGetAsync("tenant-1", "/v1.0/path1");

            // Simulate cache expiry by setting expiry to past
            service.CachedAzCliTokenExpiry = DateTimeOffset.UtcNow.AddMinutes(-1);

            // Second call should acquire new token because cache expired
            await service.GraphGetAsync("tenant-1", "/v1.0/path2");

            // Assert - Token should be acquired twice (once for each call since cache expired)
            await executor.Received(2).ExecuteAsync(
                "az",
                Arg.Is<string>(s => s.Contains("get-access-token")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            handler.Dispose();
        }
    }
}
