// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Unit tests for GsaService.
/// Uses TestHttpMessageHandler / CapturingHttpMessageHandler (defined in GraphApiServiceTests.cs,
/// same assembly) to inject fake platform responses.
/// </summary>
public class GsaServiceTests
{
    private static IAuthenticationService FakeAuth(string token = "fake-a365-token")
    {
        var mock = Substitute.For<IAuthenticationService>();

        // The 8th parameter is the CancellationToken. Omitting a matcher for it pins the setup to
        // ct == default, so any call carrying a real token -- a caller's, or the wait ceiling's --
        // silently misses and returns null, which the service reports as a failed token acquisition.
        mock.GetAccessTokenAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(token));
        return mock;
    }

    private static IAzureCliService FakeAzureCli(
        string tenantId = "11111111-1111-1111-1111-111111111111",
        string upn = "admin@contoso.onmicrosoft.com")
    {
        var mock = Substitute.For<IAzureCliService>();
        mock.GetCurrentAccountAsync().Returns(Task.FromResult<AzureAccountInfo?>(new AzureAccountInfo
        {
            TenantId = tenantId,
            User = new AzureUser { Name = upn },
        }));
        return mock;
    }

    private static GsaService CreateService(
        HttpMessageHandler handler,
        IAuthenticationService? auth = null,
        IAzureCliService? azureCli = null) =>
        new(NullLogger<GsaService>.Instance, auth ?? FakeAuth(), azureCli ?? FakeAzureCli(), "prod", handler);

    private static HttpResponseMessage StatusResponse(
        HttpStatusCode code,
        string? status = null,
        bool pending = false,
        string? reason = null) =>
        new(code)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { status, pending, reason })),
        };

    // ───────────────────────────────── SetAsync ─────────────────────────────────

    [Theory]
    [InlineData(true, "/agents/gsa/enable")]
    [InlineData(false, "/agents/gsa/disable")]
    public async Task SetAsync_PostsToTheRouteThatCarriesTheIntent(bool enabled, string expectedPath)
    {
        HttpMethod? method = null;
        Uri? uri = null;
        using var handler = new CapturingHttpMessageHandler(r =>
        {
            method = r.Method;
            uri = r.RequestUri;
        });
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, enabled ? "Enabled" : "Disabled"));
        var svc = CreateService(handler);

        var result = await svc.SetAsync(enabled);

        result.Should().NotBeNull();
        result!.Status.Should().Be(enabled ? "Enabled" : "Disabled");
        result.Pending.Should().BeFalse();
        result.Reason.Should().BeNull();

        method.Should().Be(HttpMethod.Post);
        uri!.AbsolutePath.Should().Be(expectedPath);
    }

    [Fact]
    public async Task SetAsync_SendsNoEnvironmentIdentifierBecauseThePlatformResolvesIt()
    {
        string? body = null;
        Uri? uri = null;
        using var handler = new CapturingHttpMessageHandler(r =>
        {
            uri = r.RequestUri;
            body = r.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        });
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Enabled"));
        var svc = CreateService(handler);

        await svc.SetAsync(enabled: true);

        body.Should().BeEmpty();
        uri!.Query.Should().BeEmpty();
    }

    [Fact]
    public async Task SetAsync_WhenAccepted_SurfacesThePendingFlagWithTheOldStatus()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.Accepted, "Disabled", pending: true));
        var svc = CreateService(handler);

        var result = await svc.SetAsync(enabled: true);

        result.Should().NotBeNull();
        result!.Status.Should().Be("Disabled");
        result.Pending.Should().BeTrue();
        result.Reason.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_WhenGovernedByPolicy_ReturnsNull()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                error = "A Power Platform policy governs this setting.",
            })),
        });
        var svc = CreateService(handler);

        var result = await svc.SetAsync(enabled: true);

        result.Should().BeNull();
        handler.RequestCount.Should().Be(1, because: "a governed setting cannot be fixed by retrying");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task SetAsync_WhenTheCallFails_ReturnsNull(HttpStatusCode code)
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(code)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = "nope" })),
        });
        var svc = CreateService(handler);

        var result = await svc.SetAsync(enabled: false);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_WhenTheErrorBodyIsNotJson_StillReturnsNullWithoutThrowing()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>gateway</html>"),
        });
        var svc = CreateService(handler);

        var result = await svc.SetAsync(enabled: true);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_WhenNoTokenIsAvailable_ReturnsNullWithoutCallingThePlatform()
    {
        using var handler = new TestHttpMessageHandler();
        var svc = CreateService(handler, FakeAuth(token: string.Empty));

        var result = await svc.SetAsync(enabled: true);

        result.Should().BeNull();
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task SetAsync_WhenTheTransportThrows_ReturnsNull()
    {
        using var handler = new ExceptionThrowingHttpMessageHandler(
            () => new HttpRequestException("connection reset"));
        var svc = CreateService(handler);

        var result = await svc.SetAsync(enabled: true);

        result.Should().BeNull();
    }

    // ─────────────────────────── Tenant targeting ───────────────────────────
    //
    // The tenant of the current az login is passed explicitly to token acquisition. Without it
    // the authority is "common", and the Windows broker silently returns the Windows account even
    // when a login hint names a different one — which would apply a tenant-wide setting to the
    // wrong tenant. Passing the tenant also arms the mismatch self-heal in AuthenticationService.

    [Fact]
    public async Task SetAsync_AuthenticatesAgainstTheTenantAndUserOfTheCurrentAzLogin()
    {
        const string tenantId = "22222222-2222-2222-2222-222222222222";
        const string upn = "admin@fabrikam.onmicrosoft.com";
        var auth = FakeAuth();
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Enabled"));
        var svc = CreateService(handler, auth, FakeAzureCli(tenantId, upn));

        await svc.SetAsync(enabled: true);

        await auth.Received(1).GetAccessTokenAsync(
            Arg.Any<string>(),
            tenantId,
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<bool>(),
            upn,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStatusAsync_AuthenticatesAgainstTheTenantAndUserOfTheCurrentAzLogin()
    {
        const string tenantId = "33333333-3333-3333-3333-333333333333";
        const string upn = "reader@fabrikam.onmicrosoft.com";
        var auth = FakeAuth();
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Disabled"));
        var svc = CreateService(handler, auth, FakeAzureCli(tenantId, upn));

        await svc.GetStatusAsync();

        await auth.Received(1).GetAccessTokenAsync(
            Arg.Any<string>(),
            tenantId,
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<bool>(),
            upn,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_WhenAzLoginIsUnavailable_ReturnsNullWithoutCallingThePlatform()
    {
        var azureCli = Substitute.For<IAzureCliService>();
        azureCli.GetCurrentAccountAsync().Returns(Task.FromResult<AzureAccountInfo?>(null));
        using var handler = new TestHttpMessageHandler();
        var svc = CreateService(handler, azureCli: azureCli);

        var result = await svc.SetAsync(enabled: true);

        result.Should().BeNull();
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task SetAsync_WhenTheAzAccountCarriesNoTenant_ReturnsNullWithoutCallingThePlatform()
    {
        using var handler = new TestHttpMessageHandler();
        var svc = CreateService(handler, azureCli: FakeAzureCli(tenantId: string.Empty));

        var result = await svc.SetAsync(enabled: true);

        result.Should().BeNull();
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public void Constructor_WithoutAnAzureCliService_Throws()
    {
        using var handler = new TestHttpMessageHandler();

        var act = () => new GsaService(NullLogger<GsaService>.Instance, FakeAuth(), null!, "prod", handler);

        act.Should().Throw<ArgumentNullException>().WithParameterName("azureCliService");
    }

    // ──────────────────────────────── GetStatusAsync ────────────────────────────

    [Fact]
    public async Task GetStatusAsync_GetsTheStatusRoute()
    {
        HttpMethod? method = null;
        Uri? uri = null;
        using var handler = new CapturingHttpMessageHandler(r =>
        {
            method = r.Method;
            uri = r.RequestUri;
        });
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "NotConfigured"));
        var svc = CreateService(handler);

        var result = await svc.GetStatusAsync();

        result.Should().NotBeNull();
        result!.Status.Should().Be("NotConfigured");
        result.Pending.Should().BeFalse();
        result.Reason.Should().BeNull();

        method.Should().Be(HttpMethod.Get);
        uri!.AbsolutePath.Should().Be("/agents/gsa/status");
    }

    [Fact]
    public async Task GetStatusAsync_WhenTheBodyIsEmpty_ReturnsAnEmptyStatus()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty),
        });
        var svc = CreateService(handler);

        var result = await svc.GetStatusAsync();

        result.Should().NotBeNull();
        result!.Status.Should().BeNull();
        result.Pending.Should().BeFalse();
        result.Reason.Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_SurfacesTheReason()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(
            HttpStatusCode.OK, "NotConfigured", reason: "This tenant has no Agent 365 environment yet."));
        var svc = CreateService(handler);

        var result = await svc.GetStatusAsync();

        result.Should().NotBeNull();
        result!.Status.Should().Be("NotConfigured");
        result.Pending.Should().BeFalse();
        result.Reason.Should().Be("This tenant has no Agent 365 environment yet.");
    }

    // ─────────────────────────────── WaitForStatusAsync ─────────────────────────

    [Fact]
    public async Task WaitForStatusAsync_WithoutAnExpectedStatus_Throws()
    {
        using var handler = new TestHttpMessageHandler();
        var svc = CreateService(handler);

        var act = () => svc.WaitForStatusAsync(" ", TimeSpan.FromMinutes(1));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WaitForStatusAsync_WhenTheFirstReadAlreadyMatches_StopsImmediately()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Enabled"));
        var svc = CreateService(handler);

        var result = await svc.WaitForStatusAsync("Enabled", TimeSpan.FromMinutes(1));

        result.Should().NotBeNull();
        result!.Status.Should().Be("Enabled");
        result.Pending.Should().BeFalse();
        result.Reason.Should().BeNull();
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitForStatusAsync_MatchesStatusCaseInsensitively()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "enabled"));
        var svc = CreateService(handler);

        var result = await svc.WaitForStatusAsync("Enabled", TimeSpan.FromMinutes(1));

        result.Should().NotBeNull();
        result!.Status.Should().Be("enabled");
        result.Pending.Should().BeFalse();
        result.Reason.Should().BeNull();
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitForStatusAsync_WhenAReadFails_GivesUpRatherThanSpinning()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = "upstream" })),
        });
        var svc = CreateService(handler);

        var result = await svc.WaitForStatusAsync("Enabled", TimeSpan.FromMinutes(1));

        result.Should().BeNull();
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitForStatusAsync_WhenTheBudgetCannotCoverAnotherPoll_ReturnsTheLastRead()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Disabled", pending: true));
        var svc = CreateService(handler);

        // Shorter than the poll interval, so the first non-matching read is also the last.
        var result = await svc.WaitForStatusAsync("Enabled", TimeSpan.FromSeconds(1));

        result.Should().NotBeNull();
        result!.Status.Should().Be("Disabled");
        result.Pending.Should().BeTrue();
        result.Reason.Should().BeNull();
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitForStatusAsync_WhenCeilingElapsesDuringAPoll_StopsWaitingOnTheInFlightCall()
    {
        // The pre-sleep stopwatch check only bounds the gap between completed polls. Without the
        // ceiling armed on the request's own token, a poll that starts inside the budget runs to
        // the HttpClient's timeout -- minutes past what the caller asked for.
        using var handler = new SlowHttpMessageHandler(TimeSpan.FromSeconds(30));
        var svc = CreateService(handler);

        var stopwatch = Stopwatch.StartNew();
        var result = await svc.WaitForStatusAsync("Enabled", TimeSpan.FromMilliseconds(200));
        stopwatch.Stop();

        result.Should().BeNull(because: "the ceiling elapsed before any status was read");
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(10),
            because: "the wait must abandon the in-flight request rather than block on it");
    }

    [Fact]
    public async Task WaitForStatusAsync_WhenCallerCancels_PropagatesRatherThanReportingATimeout()
    {
        // The timeout and a Ctrl+C both surface as OperationCanceledException. Only the timeout is
        // swallowed into "still applying"; a caller cancel has to reach the caller.
        using var handler = new SlowHttpMessageHandler(TimeSpan.FromSeconds(30));
        var svc = CreateService(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = async () => await svc.WaitForStatusAsync("Enabled", TimeSpan.FromMinutes(5), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ───────────────────── az account resolution ────────────────────────────────

    [Fact]
    public async Task WaitForStatusAsync_ResolvesTheAzAccountOnceAcrossEveryPoll()
    {
        // Each resolution shells out to `az account show` and returns null on any CLI hiccup, so
        // re-resolving per poll turns a transient failure mid-wait into "could not determine your
        // Azure tenant" even though the tenant was known from the first call.
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Disabled", pending: true));
        var azureCli = FakeAzureCli();
        var svc = CreateService(handler, azureCli: azureCli);

        await svc.GetStatusAsync();
        await svc.WaitForStatusAsync("Enabled", TimeSpan.FromSeconds(1));

        await azureCli.Received(1).GetCurrentAccountAsync();
    }

    [Fact]
    public async Task GetStatusAsync_WhenTheAccountIsUnavailable_RetriesOnTheNextCall()
    {
        // The cache must not pin a failure: an admin who runs `az login` after the first attempt
        // should not have to restart the process.
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Enabled"));
        var azureCli = Substitute.For<IAzureCliService>();
        azureCli.GetCurrentAccountAsync().Returns(
            Task.FromResult<AzureAccountInfo?>(null),
            Task.FromResult<AzureAccountInfo?>(new AzureAccountInfo
            {
                TenantId = "11111111-1111-1111-1111-111111111111",
                User = new AzureUser { Name = "admin@contoso.onmicrosoft.com" },
            }));
        var svc = CreateService(handler, azureCli: azureCli);

        var first = await svc.GetStatusAsync();
        var second = await svc.GetStatusAsync();

        first.Should().BeNull(because: "no az account means no tenant to authenticate against");
        second.Should().NotBeNull();
        second!.Status.Should().Be("Enabled");
        second.Pending.Should().BeFalse();
        second.Reason.Should().BeNull();
        await azureCli.Received(2).GetCurrentAccountAsync();
    }

    /// <summary>
    /// Holds each request open until the request's own token is cancelled, so a test can tell
    /// "abandoned the call" from "waited for the response".
    /// </summary>
    private sealed class SlowHttpMessageHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { status = "Enabled", pending = false })),
            };
        }
    }
}
