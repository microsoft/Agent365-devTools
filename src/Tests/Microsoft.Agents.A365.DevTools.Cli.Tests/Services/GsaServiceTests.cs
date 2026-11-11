// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
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

        mock.GetAccessTokenAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(),
            Arg.Any<string?>())
            .Returns(Task.FromResult(token));
        return mock;
    }

    private static AzureAccountInfo Account(
        string tenantId = "11111111-1111-1111-1111-111111111111",
        string upn = "admin@contoso.onmicrosoft.com") =>
        new()
        {
            TenantId = tenantId,
            User = new AzureUser { Name = upn },
        };

    private static GsaService CreateService(
        HttpMessageHandler handler,
        IAuthenticationService? auth = null) =>
        new(NullLogger<GsaService>.Instance, auth ?? FakeAuth(), "prod", handler);

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

        var result = await svc.SetAsync(Account(), enabled);

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

        await svc.SetAsync(Account(), enabled: true);

        body.Should().BeEmpty();
        uri!.Query.Should().BeEmpty();
    }

    [Fact]
    public async Task SetAsync_WhenAccepted_SurfacesThePendingFlagWithTheOldStatus()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.Accepted, "Disabled", pending: true));
        var svc = CreateService(handler);

        var result = await svc.SetAsync(Account(), enabled: true);

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

        var result = await svc.SetAsync(Account(), enabled: true);

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

        var result = await svc.SetAsync(Account(), enabled: false);

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

        var result = await svc.SetAsync(Account(), enabled: true);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_WhenNoTokenIsAvailable_ReturnsNullWithoutCallingThePlatform()
    {
        using var handler = new TestHttpMessageHandler();
        var svc = CreateService(handler, FakeAuth(token: string.Empty));

        var result = await svc.SetAsync(Account(), enabled: true);

        result.Should().BeNull();
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task SetAsync_WhenTheTransportThrows_ReturnsNull()
    {
        using var handler = new ExceptionThrowingHttpMessageHandler(
            () => new HttpRequestException("connection reset"));
        var svc = CreateService(handler);

        var result = await svc.SetAsync(Account(), enabled: true);

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
        var svc = CreateService(handler, auth);

        await svc.SetAsync(Account(tenantId, upn), enabled: true);

        await auth.Received(1).GetAccessTokenAsync(
            Arg.Any<string>(),
            tenantId,
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<bool>(),
            upn,
            Arg.Any<CancellationToken>(),
            ConfigConstants.GetAuthorityHost("prod"));
    }

    [Fact]
    public async Task GetStatusAsync_AuthenticatesAgainstTheTenantAndUserOfTheCurrentAzLogin()
    {
        const string tenantId = "33333333-3333-3333-3333-333333333333";
        const string upn = "reader@fabrikam.onmicrosoft.com";
        var auth = FakeAuth();
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Disabled"));
        var svc = CreateService(handler, auth);

        await svc.GetStatusAsync(Account(tenantId, upn));

        await auth.Received(1).GetAccessTokenAsync(
            Arg.Any<string>(),
            tenantId,
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<bool>(),
            upn,
            Arg.Any<CancellationToken>(),
            ConfigConstants.GetAuthorityHost("prod"));
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

        var result = await svc.GetStatusAsync(Account());

        result.Should().NotBeNull();
        result!.Status.Should().Be("NotConfigured");
        result.Pending.Should().BeFalse();
        result.Reason.Should().BeNull();

        method.Should().Be(HttpMethod.Get);
        uri!.AbsolutePath.Should().Be("/agents/gsa/status");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("{}")]
    [InlineData("{\"status\":null}")]
    [InlineData("{\"status\":\" \"}")]
    public async Task GetStatusAsync_WhenTheBodyHasNoStatus_ReturnsFailure(string body)
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        });
        var svc = CreateService(handler);

        var result = await svc.GetStatusAsync(Account());

        result.Should().BeNull(
            because: "a successful HTTP code without a readable status must not make the CLI report success");
    }

    [Theory]
    [InlineData("gcc")]
    [InlineData("gcc-moderate")]
    [InlineData("gcc-high")]
    [InlineData("dod")]
    [InlineData("AzureUSGovernment")]
    public async Task GsaOperations_OutsidePublicCloud_FailBeforeAuthentication(string environment)
    {
        var auth = FakeAuth();
        using var handler = new TestHttpMessageHandler();
        var svc = new GsaService(NullLogger<GsaService>.Instance, auth, environment, handler);

        var status = await svc.GetStatusAsync(Account());
        var change = await svc.SetAsync(Account(), enabled: true);

        status.Should().BeNull(because: "GSA is supported only in public cloud");
        change.Should().BeNull(because: "unsupported clouds must not send tenant-wide changes");
        auth.ReceivedCalls().Should().BeEmpty(
            because: "unsupported clouds must be rejected before acquiring a commercial-cloud token");
        handler.RequestCount.Should().Be(0);
    }

    [Theory]
    [InlineData("AgentTools.Gsa.Read.All")]
    [InlineData("AgentTools.Gsa.Manage.All")]
    public async Task GetStatusAsync_WhenScopeIsMissing_ReportsRequiredScopeWithoutAdminConsentAdvice(string scope)
    {
        var logger = Substitute.For<ILogger<GsaService>>();
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                error = "insufficient_scope",
                required_scope = scope,
            })),
        });
        var svc = new GsaService(logger, FakeAuth(), "prod", handler);

        var result = await svc.GetStatusAsync(Account());

        result.Should().BeNull();
        handler.RequestCount.Should().Be(1, because: "a missing scope cannot be repaired by polling");
        var output = string.Join("\n", logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "Log").Select(c => c.GetArguments()[2]?.ToString()));
        output.Should().Contain(scope, because: "scope errors must identify the platform's required permission");
        output.Should().NotContain("requires the Global Administrator")
            .And.NotContain("client application consented",
                because: "a missing preauthorized scope is not a role failure or something an admin can grant on Microsoft's client");
    }

    [Fact]
    public async Task GetStatusAsync_SurfacesTheReason()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(
            HttpStatusCode.OK, "NotConfigured", reason: "This tenant has no Agent 365 environment yet."));
        var svc = CreateService(handler);

        var result = await svc.GetStatusAsync(Account());

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

        var act = () => svc.WaitForStatusAsync(Account(), " ", TimeSpan.FromMinutes(1));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WaitForStatusAsync_WhenTheFirstReadAlreadyMatches_StopsImmediately()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Enabled"));
        var svc = CreateService(handler);

        var result = await svc.WaitForStatusAsync(Account(), "Enabled", TimeSpan.FromMinutes(1));

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

        var result = await svc.WaitForStatusAsync(Account(), "Enabled", TimeSpan.FromMinutes(1));

        result.Should().NotBeNull();
        result!.Status.Should().Be("enabled");
        result.Pending.Should().BeFalse();
        result.Reason.Should().BeNull();
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitForStatusAsync_WhenReadsKeepFailing_StopsAfterBoundedRetries()
    {
        using var handler = new TestHttpMessageHandler();
        for (var i = 0; i < 3; i++)
        {
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { error = "upstream" })),
            });
        }
        var svc = CreateService(handler);

        var result = await svc.WaitForStatusAsync(Account(), "Enabled", TimeSpan.FromMinutes(1));

        result.Should().BeNull();
        handler.RequestCount.Should().Be(3,
            because: "transient status failures must be retried but cannot spin indefinitely");
    }

    [Fact]
    public async Task WaitForStatusAsync_WhenAReadFailsTransiently_ContinuesToTheRequestedState()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("{\"error\":\"upstream\"}"),
        });
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Enabled"));
        var svc = CreateService(handler);

        var result = await svc.WaitForStatusAsync(Account(), "Enabled", TimeSpan.FromMinutes(1));

        result.Should().NotBeNull(because: "a transient gateway error must not abort verification of an accepted change");
        result!.Status.Should().Be("Enabled");
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task WaitForStatusAsync_WhenTheBudgetCannotCoverAnotherPoll_ReturnsTheLastRead()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Disabled"));
        var svc = CreateService(handler);

        // Shorter than the poll interval, so the first non-matching read is also the last.
        var result = await svc.WaitForStatusAsync(Account(), "Enabled", TimeSpan.FromSeconds(1));

        result.Should().NotBeNull();
        result!.Status.Should().Be("Disabled");
        result.Pending.Should().BeFalse(because: "status reads never carry the mutation response's pending flag");
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
        var result = await svc.WaitForStatusAsync(Account(), "Enabled", TimeSpan.FromMilliseconds(200));
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

        var act = async () => await svc.WaitForStatusAsync(Account(), "Enabled", TimeSpan.FromMinutes(5), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ───────────────── The account is supplied, never read from the CLI ─────────
    //
    // az account show reflects mutable local state. Resolving it once for the confirmation prompt
    // and again here would let the command confirm one tenant and change another, so the caller
    // resolves it once and every method is told which account to act as.

    [Fact]
    public async Task SetAsync_WithoutAnAccount_Throws()
    {
        using var handler = new TestHttpMessageHandler();
        var svc = CreateService(handler);

        var act = async () => await svc.SetAsync(null!, enabled: true);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("account");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task GetStatusAsync_WithoutAnAccount_Throws()
    {
        using var handler = new TestHttpMessageHandler();
        var svc = CreateService(handler);

        var act = async () => await svc.GetStatusAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("account");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitForStatusAsync_WithoutAnAccount_Throws()
    {
        using var handler = new TestHttpMessageHandler();
        var svc = CreateService(handler);

        var act = async () => await svc.WaitForStatusAsync(null!, "Enabled", TimeSpan.FromMinutes(1));

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("account");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task GetStatusAsync_WhenTheTransportTimesOutWithNoCancellation_ReturnsNullRatherThanThrowing()
    {
        // HttpClient's own timeout surfaces as an OperationCanceledException with no token
        // cancelled. That is an ordinary request failure, and callers of a bare status, enable or
        // disable expect the documented null, not an exception thrown at them.
        using var handler = new ThrowingHttpMessageHandler(new TaskCanceledException("The request timed out."));
        var svc = CreateService(handler);

        var result = await svc.GetStatusAsync(Account());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_WhenTheCallerCancels_PropagatesRatherThanReturningNull()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var handler = new ThrowingHttpMessageHandler(new TaskCanceledException("Cancelled."));
        var svc = CreateService(handler);

        var act = async () => await svc.GetStatusAsync(Account(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetStatusAsync_CalledTwice_DoesNotDisposeTheInjectedHandlerOnTheFirstCall()
    {
        // A client is built per request, but the handler is a field and outlives all of them.
        // HttpClient's default ownership would have the first client's disposal take the handler
        // down with it, so every later request -- including every poll after the first in
        // WaitForStatusAsync -- would fail with ObjectDisposedException.
        using var handler = new DisposalAwareHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Disabled"));
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Enabled"));
        var svc = CreateService(handler);

        var first = await svc.GetStatusAsync(Account());
        var second = await svc.GetStatusAsync(Account());

        first.Should().NotBeNull();
        first!.Status.Should().Be("Disabled");
        first.Pending.Should().BeFalse();
        first.Reason.Should().BeNull();

        second.Should().NotBeNull();
        second!.Status.Should().Be("Enabled");
        second.Pending.Should().BeFalse();
        second.Reason.Should().BeNull();

        handler.DisposeCount.Should().Be(0);
        handler.RequestCount.Should().Be(2);
    }

    /// <summary>
    /// Fails every request with a supplied exception, so a test can pin how the service classifies
    /// it without racing a real timeout.
    /// </summary>
    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw exception;
        }
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

    /// <summary>
    /// Mimics a real handler's reaction to being disposed: it counts disposals and refuses to
    /// serve afterwards, so a test can prove the service never disposes a handler it does not own.
    /// A handler that ignores Dispose would let the defect pass unnoticed.
    /// </summary>
    private sealed class DisposalAwareHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public int DisposeCount { get; private set; }

        public int RequestCount { get; private set; }

        public void QueueResponse(HttpResponseMessage response) => _responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
            RequestCount++;
            return Task.FromResult(_responses.Dequeue());
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }
}
