// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Helpers;

/// <summary>
/// Tests for AzCliHelper.ResolveLoginHintAsync caching and override behavior.
/// Isolated from other tests because the cache and override are static state.
/// </summary>
[Collection("AzCliHelperTests")]
public class AzCliHelperTests : IDisposable
{
    public AzCliHelperTests()
    {
        // Start each test with a clean slate
        AzCliHelper.LoginHintResolverOverride = null;
        AzCliHelper.ResetLoginHintCacheForTesting();
    }

    public void Dispose()
    {
        // Restore static state so other tests are not affected
        AzCliHelper.LoginHintResolverOverride = null;
        AzCliHelper.ResetLoginHintCacheForTesting();
    }

    [Fact]
    public async Task ResolveLoginHintAsync_WhenOverrideSet_ReturnsOverrideValue()
    {
        AzCliHelper.LoginHintResolverOverride = () => Task.FromResult<string?>("admin@contoso.com");

        var result = await AzCliHelper.ResolveLoginHintAsync();

        result.Should().Be("admin@contoso.com",
            because: "the override replaces the real az subprocess — used in tests and to inject known identities");
    }

    [Fact]
    public async Task ResolveLoginHintAsync_CalledTwice_ReturnsSameTaskInstance()
    {
        // Override returns a known value so we never hit the real 'az' process
        AzCliHelper.LoginHintResolverOverride = () => Task.FromResult<string?>("user@test.com");

        // Populate the cache on the first call, then reset override to simulate production
        var firstResult = await AzCliHelper.ResolveLoginHintAsync();

        // Clear override — subsequent calls must use the cache, not the resolver
        AzCliHelper.LoginHintResolverOverride = null;

        // The cached Task should be returned directly — no new subprocess
        var cachedTask = AzCliHelper.ResolveLoginHintAsync();
        var secondResult = await cachedTask;

        secondResult.Should().Be(firstResult,
            because: "the cached result must be returned on subsequent calls — re-running az account show on every token acquire costs 20-40s per call");
    }

    [Fact]
    public async Task ResolveLoginHintAsync_OverrideInvokedOnce_WhenCalledMultipleTimes()
    {
        var callCount = 0;
        AzCliHelper.LoginHintResolverOverride = () =>
        {
            callCount++;
            return Task.FromResult<string?>("counted@test.com");
        };

        // First call populates the cache via the override
        await AzCliHelper.ResolveLoginHintAsync();

        // Reset override to null — cache should serve subsequent calls without invoking anything
        AzCliHelper.LoginHintResolverOverride = null;
        await AzCliHelper.ResolveLoginHintAsync();
        await AzCliHelper.ResolveLoginHintAsync();

        callCount.Should().Be(1,
            because: "the resolver must be invoked exactly once per process lifetime — the cache eliminates the repeated 20-40s az account show calls across setup phases");
    }

    [Fact]
    public async Task ResolveLoginHintAsync_ConcurrentCalls_InvokesResolverOnce()
    {
        const int followerCount = 8;
        var callCount = 0;
        using var resolverEntered = new ManualResetEventSlim();
        using var releaseResolver = new ManualResetEventSlim();
        using var followersStarted = new CountdownEvent(followerCount);

        AzCliHelper.LoginHintResolverOverride = () =>
        {
            Interlocked.Increment(ref callCount);
            resolverEntered.Set();
            releaseResolver.Wait();
            return Task.FromResult<string?>("concurrent@test.com");
        };

        var firstCall = StartConcurrentResolve();
        Task<string?>[] followerCalls = [];
        try
        {
            resolverEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
                because: "the first concurrent resolve must enter the injected resolver");

            followerCalls = Enumerable.Range(0, followerCount)
                .Select(_ => Task.Factory.StartNew(
                    () =>
                    {
                        followersStarted.Signal();
                        return AzCliHelper.ResolveLoginHintAsync();
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap())
                .ToArray();

            followersStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
                because: "all follower threads must reach the concurrent cache lookup");
            await Task.Delay(100);

            Volatile.Read(ref callCount).Should().Be(1,
                because: "concurrent callers must share one cached az account show subprocess");
        }
        finally
        {
            releaseResolver.Set();
        }

        var results = await Task.WhenAll(followerCalls.Append(firstCall));
        results.Should().OnlyContain(result => result == "concurrent@test.com",
            because: "all concurrent callers must observe the same cached login hint");

        static Task<string?> StartConcurrentResolve()
            => Task.Factory.StartNew(
                AzCliHelper.ResolveLoginHintAsync,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
    }

    [Fact]
    public async Task ResolveLoginHintAsync_AfterCacheReset_InvokesResolverAgain()
    {
        var callCount = 0;
        AzCliHelper.LoginHintResolverOverride = () =>
        {
            callCount++;
            return Task.FromResult<string?>("reset@test.com");
        };

        await AzCliHelper.ResolveLoginHintAsync();
        AzCliHelper.ResetLoginHintCacheForTesting();
        await AzCliHelper.ResolveLoginHintAsync();

        callCount.Should().Be(2,
            because: "ResetLoginHintCacheForTesting clears the cache, forcing a fresh resolve — required for test isolation");
    }
}

[CollectionDefinition("AzCliHelperTests", DisableParallelization = true)]
public class AzCliHelperTestCollection { }
