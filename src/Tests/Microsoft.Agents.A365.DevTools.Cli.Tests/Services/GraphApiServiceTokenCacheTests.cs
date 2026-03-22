// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Tests to validate that Azure CLI Graph tokens are cached at the process level
/// (via AzCliHelper) so a single CLI invocation only spawns one 'az' subprocess
/// per (resource, tenantId) pair, regardless of how many GraphApiService instances
/// or callers request the same token.
/// </summary>
[Collection("GraphApiServiceTokenCacheTests")]
public class GraphApiServiceTokenCacheTests : IDisposable
{
    public GraphApiServiceTokenCacheTests()
    {
        AzCliHelper.AzCliTokenAcquirerOverride = null;
        AzCliHelper.ResetAzCliTokenCacheForTesting();
    }

    public void Dispose()
    {
        AzCliHelper.AzCliTokenAcquirerOverride = null;
        AzCliHelper.ResetAzCliTokenCacheForTesting();
    }

    /// <summary>
    /// Sets the process-level token acquirer override and returns a counter reference.
    /// The override is invoked inside GetOrAdd, so the cache still applies — only one
    /// invocation per (resource, tenantId) key within a test.
    /// </summary>
    private static int[] SetupTokenAcquirerWithCounter(string token = "cached-token")
    {
        var callCount = new int[1];
        AzCliHelper.AzCliTokenAcquirerOverride = (resource, tenantId) =>
        {
            callCount[0]++;
            return Task.FromResult<string?>(token);
        };
        return callCount;
    }

    private static (GraphApiService service, TestHttpMessageHandler handler) CreateService()
    {
        var handler = new TestHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        // 'az account show' is still used in GetGraphAccessTokenAsync for the auth-check
        // fallback path; stub it to succeed so tests that hit the fallback don't hang.
        executor.ExecuteAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var args = callInfo.ArgAt<string>(1);
                if (args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var service = new GraphApiService(logger, executor, handler);
        return (service, handler);
    }

    [Fact]
    public async Task MultipleGraphGetAsync_SameTenant_AcquiresTokenOnlyOnce()
    {
        var callCount = SetupTokenAcquirerWithCounter();
        var (service, handler) = CreateService();

        try
        {
            for (int i = 0; i < 3; i++)
                handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{\"value\":[]}") });

            await service.GraphGetAsync("tenant-1", "/v1.0/path1");
            await service.GraphGetAsync("tenant-1", "/v1.0/path2");
            await service.GraphGetAsync("tenant-1", "/v1.0/path3");

            callCount[0].Should().Be(1,
                because: "the process-level cache must serve the same (resource, tenant) token " +
                         "from the first acquisition — re-running az account get-access-token on every " +
                         "Graph call within a single command costs 20-40s per call");
        }
        finally { handler.Dispose(); }
    }

    [Fact]
    public async Task GraphGetAsync_DifferentTenants_AcquiresTokenForEach()
    {
        var callCount = SetupTokenAcquirerWithCounter();
        var (service, handler) = CreateService();

        try
        {
            for (int i = 0; i < 2; i++)
                handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{\"value\":[]}") });

            await service.GraphGetAsync("tenant-1", "/v1.0/path1");
            await service.GraphGetAsync("tenant-2", "/v1.0/path2");

            callCount[0].Should().Be(2,
                because: "different tenant IDs are different cache keys — each tenant requires " +
                         "its own 'az account get-access-token --tenant' call");
        }
        finally { handler.Dispose(); }
    }

    [Fact]
    public async Task MixedGraphOperations_SameTenant_AcquiresTokenOnlyOnce()
    {
        var callCount = SetupTokenAcquirerWithCounter();
        var (service, handler) = CreateService();

        try
        {
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"value\":[]}") });
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"id\":\"123\"}") });
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"value\":[]}") });

            await service.GraphGetAsync("tenant-1", "/v1.0/path1");
            await service.GraphPostAsync("tenant-1", "/v1.0/path2", new { name = "test" });
            await service.GraphGetAsync("tenant-1", "/v1.0/path3");

            callCount[0].Should().Be(1,
                because: "GET and POST operations share the same process-level token cache — " +
                         "mixed Graph operations within a command must not each re-acquire a token");
        }
        finally { handler.Dispose(); }
    }

    [Fact]
    public async Task MultipleGraphApiServiceInstances_SameTenant_AcquireTokenOnlyOnce()
    {
        // This is the key regression scenario: previously, each GraphApiService instance had
        // its own instance-level cache, so a new instance in each setup phase would re-run
        // 'az account get-access-token'. With a process-level cache, all instances share one token.
        var callCount = SetupTokenAcquirerWithCounter();

        var handler1 = new TestHttpMessageHandler();
        var handler2 = new TestHttpMessageHandler();

        try
        {
            handler1.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"value\":[]}") });
            handler2.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"value\":[]}") });

            var logger = Substitute.For<ILogger<GraphApiService>>();
            var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
            executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty }));

            var service1 = new GraphApiService(logger, executor, handler1);
            var service2 = new GraphApiService(logger, executor, handler2);

            await service1.GraphGetAsync("tenant-1", "/v1.0/path1");
            await service2.GraphGetAsync("tenant-1", "/v1.0/path1");

            callCount[0].Should().Be(1,
                because: "the process-level cache is shared across all GraphApiService instances — " +
                         "a second instance must not re-run 'az account get-access-token' for the same tenant");
        }
        finally
        {
            handler1.Dispose();
            handler2.Dispose();
        }
    }

    [Fact]
    public async Task GraphGetAsync_AfterCacheInvalidation_AcquiresNewToken()
    {
        // Validates that InvalidateAzCliTokenCache() forces fresh token acquisition —
        // used by ClientAppValidator and DelegatedConsentService after az login/CAE events.
        var callCount = SetupTokenAcquirerWithCounter();
        var (service, handler) = CreateService();

        try
        {
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"value\":[]}") });
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"value\":[]}") });

            await service.GraphGetAsync("tenant-1", "/v1.0/path1");

            // Simulate a CAE event or forced re-auth that invalidates all cached tokens
            AzCliHelper.InvalidateAzCliTokenCache();

            await service.GraphGetAsync("tenant-1", "/v1.0/path2");

            callCount[0].Should().Be(2,
                because: "InvalidateAzCliTokenCache clears the process-level cache — " +
                         "the next call must re-acquire a fresh token (e.g., after CAE revocation or az login)");
        }
        finally { handler.Dispose(); }
    }
}

[CollectionDefinition("GraphApiServiceTokenCacheTests", DisableParallelization = true)]
public class GraphApiServiceTokenCacheTestCollection { }
