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
/// The ambient Graph token must be reused for the lifetime of the process. Commands that read
/// per-record state (for example one permission-grant lookup per agent instance) otherwise
/// re-run MSAL once per record, which re-prompts the user whenever the persistent cache is cold.
/// </summary>
public class GraphApiServiceAmbientTokenCacheTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task GetGraphAccessTokenAsync_SecondCall_ReusesTokenWithoutReauthenticating()
    {
        var auth = CreateAuthService(MakeJwt(DateTimeOffset.UtcNow.AddHours(1)));
        var service = CreateService(auth);

        var first = await service.GetGraphAccessTokenAsync(TenantId);
        var second = await service.GetGraphAccessTokenAsync(TenantId);

        second.Should().Be(first);
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

        await service.GetGraphAccessTokenAsync(TenantId);
        await service.GetGraphAccessTokenAsync(TenantId, forceRefresh: true);

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

        await service.GetGraphAccessTokenAsync(TenantId);
        await service.GetGraphAccessTokenAsync(TenantId);

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

        await service.GetGraphAccessTokenAsync(TenantId);
        await service.GetGraphAccessTokenAsync(TenantId);

        await auth.Received(2).GetAccessTokenAsync(
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

        await service.GetGraphAccessTokenAsync(TenantId);
        await service.ClearTokenCacheAsync();
        await service.GetGraphAccessTokenAsync(TenantId);

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

        await service.GetGraphAccessTokenAsync(TenantId);
        await service.GetGraphAccessTokenAsync("22222222-2222-2222-2222-222222222222");

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

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
