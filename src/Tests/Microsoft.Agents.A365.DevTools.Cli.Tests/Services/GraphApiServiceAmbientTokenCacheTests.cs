// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Inside an ambient token reuse scope the Graph token must be acquired once. Flows that read
/// per-record state (for example one permission-grant lookup per agent instance) otherwise
/// re-run MSAL once per record, which re-prompts whenever the persistent cache is cold.
/// Outside a scope the behavior must be unchanged, so unrelated commands are unaffected.
/// </summary>
public class GraphApiServiceAmbientTokenCacheTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task GetGraphAccessTokenAsync_WithoutAReuseScope_AcquiresOnEveryCall()
    {
        // Opt-in by design: caching ambient tokens for every caller would change the token
        // lifetime of commands that never asked for it.
        var auth = CreateAuthService(MakeJwt(DateTimeOffset.UtcNow.AddHours(1)));
        var service = CreateService(auth);

        await service.GetGraphAccessTokenAsync(TenantId);
        await service.GetGraphAccessTokenAsync(TenantId);

        await auth.Received(2).GetAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GetGraphAccessTokenAsync_InsideAReuseScope_AcquiresOnce()
    {
        var auth = CreateAuthService(MakeJwt(DateTimeOffset.UtcNow.AddHours(1)));
        var service = CreateService(auth);

        using (service.BeginAmbientTokenReuseScope())
        {
            var first = await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: false, CancellationToken.None, useDeviceCode: false);
            var second = await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: false, CancellationToken.None, useDeviceCode: false);
            second.Should().Be(first);
        }

        await auth.Received(1).GetAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GetGraphAccessTokenAsync_AfterTheScopeEnds_AcquiresAgain()
    {
        // A token must not outlive the flow that opted in, or a later unrelated call would
        // silently inherit that flow's cached identity.
        var auth = CreateAuthService(MakeJwt(DateTimeOffset.UtcNow.AddHours(1)));
        var service = CreateService(auth);

        using (service.BeginAmbientTokenReuseScope())
        {
            await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: false, CancellationToken.None, useDeviceCode: false);
        }

        await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: false, CancellationToken.None, useDeviceCode: false);

        await auth.Received(2).GetAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GetGraphAccessTokenAsync_WithNestedScopes_KeepsReusingUntilTheOutermostEnds()
    {
        var auth = CreateAuthService(MakeJwt(DateTimeOffset.UtcNow.AddHours(1)));
        var service = CreateService(auth);

        using (service.BeginAmbientTokenReuseScope())
        {
            using (service.BeginAmbientTokenReuseScope())
            {
                await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: false, CancellationToken.None, useDeviceCode: false);
            }

            await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: false, CancellationToken.None, useDeviceCode: false);
        }

        await auth.Received(1).GetAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GetGraphAccessTokenAsync_WithForceRefresh_AcquiresAgain()
    {
        var auth = CreateAuthService(MakeJwt(DateTimeOffset.UtcNow.AddHours(1)));
        var service = CreateService(auth);

        using var scope = service.BeginAmbientTokenReuseScope();
        await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: false, CancellationToken.None, useDeviceCode: false);
        await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: true, CancellationToken.None, useDeviceCode: false);

        await auth.Received(2).GetAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GetGraphAccessTokenAsync_WithExpiredToken_AcquiresAgain()
    {
        // A cached token inside the expiration buffer must not be served, or the next Graph call
        // fails with a 401 the caller has no way to distinguish from a permission problem.
        var auth = CreateAuthService(MakeJwt(DateTimeOffset.UtcNow.AddMinutes(1)));
        var service = CreateService(auth);

        using var scope = service.BeginAmbientTokenReuseScope();
        await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: false, CancellationToken.None, useDeviceCode: false);
        await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: false, CancellationToken.None, useDeviceCode: false);

        await auth.Received(2).GetAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GetGraphAccessTokenAsync_WithUndecodableExpiry_IsNotCached()
    {
        // An unknown lifetime must not be cached: serving it past its real expiry would turn a
        // one-off sign-in into a hard failure mid-command.
        var auth = CreateAuthService("not-a-jwt");
        var service = CreateService(auth);

        using var scope = service.BeginAmbientTokenReuseScope();
        await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: false, CancellationToken.None, useDeviceCode: false);
        await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: false, CancellationToken.None, useDeviceCode: false);

        await auth.Received(2).GetAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GetGraphAccessTokenAsync_WithoutAReuseScope_DoesNotInspectTheToken()
    {
        // Nothing cache-related may run for commands that did not opt in. Decoding the token
        // outside a scope is wasted work whose failure modes would reach unrelated commands.
        var auth = CreateAuthService("not-a-jwt");
        var service = CreateService(auth);

        var token = await service.GetGraphAccessTokenAsync(TenantId);

        token.Should().Be("not-a-jwt",
            because: "a token that cannot be decoded is still a valid token to return");
    }

    [Fact]
    public async Task GetGraphAccessTokenAsync_WithOutOfRangeExpiry_StillReturnsTheToken()
    {
        // An unparseable expiry must not be reported as an acquisition failure: the caller
        // holds a working token and would otherwise fall through to a sign-in prompt.
        var auth = CreateAuthService(MakeJwtWithRawExp(long.MaxValue));
        var service = CreateService(auth);

        using var scope = service.BeginAmbientTokenReuseScope();
        var token = await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: false, CancellationToken.None, useDeviceCode: false);

        token.Should().NotBeNull();
        await auth.Received(1).GetAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ClearTokenCacheAsync_DiscardsCachedAmbientToken()
    {
        // Callers clear the cache after an operation that invalidates tokens (for example adding
        // the wids optional claim); an in-process copy must not survive that.
        var auth = CreateAuthService(MakeJwt(DateTimeOffset.UtcNow.AddHours(1)));
        var service = CreateService(auth);

        using var scope = service.BeginAmbientTokenReuseScope();
        await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: false, CancellationToken.None, useDeviceCode: false);
        await service.ClearTokenCacheAsync();
        await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: false, CancellationToken.None, useDeviceCode: false);

        await auth.Received(2).GetAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GetGraphAccessTokenAsync_ForDifferentTenant_AcquiresSeparateToken()
    {
        var auth = CreateAuthService(MakeJwt(DateTimeOffset.UtcNow.AddHours(1)));
        var service = CreateService(auth);

        using var scope = service.BeginAmbientTokenReuseScope();
        await service.GetReusableGraphAccessTokenAsync(TenantId, forceRefresh: false, CancellationToken.None, useDeviceCode: false);
        await service.GetReusableGraphAccessTokenAsync("22222222-2222-2222-2222-222222222222", forceRefresh: false, CancellationToken.None, useDeviceCode: false);

        await auth.Received(2).GetAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    private static IAuthenticationService CreateAuthService(string token)
    {
        var auth = Substitute.For<IAuthenticationService>();
        auth.GetAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
                Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(Task.FromResult(token));
        return auth;
    }

    private static GraphApiService CreateService(IAuthenticationService auth) =>
        new(
            Substitute.For<ILogger<GraphApiService>>(),
            Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()),
            auth,
            new TestHttpMessageHandler(),
            loginHintResolver: () => Task.FromResult<string?>(null));

    private static string MakeJwt(DateTimeOffset expiresOn)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { exp = expiresOn.ToUnixTimeSeconds() });
        var encoded = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Base64Url("{\"alg\":\"none\"}")}.{encoded}.signature";
    }

    private static string MakeJwtWithRawExp(long exp)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { exp });
        var encoded = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Base64Url("{\"alg\":\"none\"}")}.{encoded}.signature";
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
