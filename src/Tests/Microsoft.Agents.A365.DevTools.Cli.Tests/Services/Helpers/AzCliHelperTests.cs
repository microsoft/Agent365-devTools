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
        // Start each test with a clean slate — both static caches
        AzCliHelper.LoginHintResolverOverride = null;
        AzCliHelper.ResetLoginHintCacheForTesting();
        AzCliHelper.AzCliTokenAcquirerOverride = null;
        AzCliHelper.ResetAzCliTokenCacheForTesting();
    }

    public void Dispose()
    {
        // Restore static state so other tests are not affected
        AzCliHelper.LoginHintResolverOverride = null;
        AzCliHelper.ResetLoginHintCacheForTesting();
        AzCliHelper.AzCliTokenAcquirerOverride = null;
        AzCliHelper.ResetAzCliTokenCacheForTesting();
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

    // -------------------------------------------------------------------------
    // AcquireAzCliTokenAsync — process-level token cache
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AcquireAzCliTokenAsync_WhenOverrideSet_ReturnsOverrideValue()
    {
        AzCliHelper.AzCliTokenAcquirerOverride = (_, __) => Task.FromResult<string?>("test-token");

        var result = await AzCliHelper.AcquireAzCliTokenAsync("https://graph.microsoft.com/", "tenant-1");

        result.Should().Be("test-token",
            because: "the override replaces the real az subprocess — used in tests to inject known tokens");
    }

    [Fact]
    public async Task AcquireAzCliTokenAsync_CalledTwiceSameKey_InvokesAcquirerOnce()
    {
        var callCount = 0;
        AzCliHelper.AzCliTokenAcquirerOverride = (_, __) =>
        {
            callCount++;
            return Task.FromResult<string?>("shared-token");
        };

        await AzCliHelper.AcquireAzCliTokenAsync("https://graph.microsoft.com/", "tenant-1");
        await AzCliHelper.AcquireAzCliTokenAsync("https://graph.microsoft.com/", "tenant-1");

        callCount.Should().Be(1,
            because: "the process-level cache must serve the same (resource, tenant) token " +
                     "after the first acquisition — calling az account get-access-token on every " +
                     "request costs 20-40s per call");
    }

    [Fact]
    public async Task AcquireAzCliTokenAsync_DifferentTenants_InvokesAcquirerForEach()
    {
        var callCount = 0;
        AzCliHelper.AzCliTokenAcquirerOverride = (_, __) =>
        {
            callCount++;
            return Task.FromResult<string?>("token");
        };

        await AzCliHelper.AcquireAzCliTokenAsync("https://graph.microsoft.com/", "tenant-1");
        await AzCliHelper.AcquireAzCliTokenAsync("https://graph.microsoft.com/", "tenant-2");

        callCount.Should().Be(2,
            because: "different tenant IDs are different cache keys — each tenant requires its own token");
    }

    [Fact]
    public async Task AcquireAzCliTokenAsync_AfterInvalidation_InvokesAcquirerAgain()
    {
        var callCount = 0;
        AzCliHelper.AzCliTokenAcquirerOverride = (_, __) =>
        {
            callCount++;
            return Task.FromResult<string?>("token");
        };

        await AzCliHelper.AcquireAzCliTokenAsync("https://graph.microsoft.com/", "tenant-1");
        AzCliHelper.InvalidateAzCliTokenCache();
        await AzCliHelper.AcquireAzCliTokenAsync("https://graph.microsoft.com/", "tenant-1");

        callCount.Should().Be(2,
            because: "InvalidateAzCliTokenCache clears the cache — the next call must re-acquire " +
                     "a fresh token; this is required after 'az login' or a CAE token revocation event");
    }

    [Fact]
    public async Task WarmAzCliTokenCache_InjectedToken_ReturnedOnNextCall()
    {
        // Override that always fails — should NOT be called after warming the cache
        AzCliHelper.AzCliTokenAcquirerOverride = (_, __) =>
            Task.FromResult<string?>(null);

        AzCliHelper.WarmAzCliTokenCache("https://graph.microsoft.com/", "tenant-1", "warmed-token");

        // The warmup bypasses the GetOrAdd — the cache entry is set directly.
        // Reset override so we can verify the warmed value is returned, not re-acquired.
        AzCliHelper.AzCliTokenAcquirerOverride = null;
        var result = await AzCliHelper.AcquireAzCliTokenAsync("https://graph.microsoft.com/", "tenant-1");

        result.Should().Be("warmed-token",
            because: "WarmAzCliTokenCache injects a token acquired via auth recovery into the " +
                     "process-level cache — subsequent callers must receive the injected token " +
                     "without re-running az account get-access-token");
    }
}

[CollectionDefinition("AzCliHelperTests", DisableParallelization = true)]
public class AzCliHelperTestCollection { }
