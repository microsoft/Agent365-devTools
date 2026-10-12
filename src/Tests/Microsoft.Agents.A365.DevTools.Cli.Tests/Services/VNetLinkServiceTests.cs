// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Unit tests for VNetLinkService.
/// Uses TestHttpMessageHandler / CapturingHttpMessageHandler (defined in GraphApiServiceTests.cs,
/// same assembly) to inject fake platform responses.
/// </summary>
public class VNetLinkServiceTests
{
    private const string TenantId = "tid";

    private const string PolicyArmId =
        "/subscriptions/sub-123/resourceGroups/rg-test/providers/Microsoft.PowerPlatform/enterprisePolicies/policy-1";

    private const string PolicySystemId =
        "/regions/unitedstates/providers/Microsoft.PowerPlatform/enterprisePolicies/1b2c8a4e-0000-0000-0000-000000000000";

    private const string OperationId = "op-abc";

    private static IAuthenticationService FakeAuth(string token = "fake-a365-token")
    {
        var mock = Substitute.For<IAuthenticationService>();

        // Every optional parameter needs a matcher. Omitting one pins the setup to that
        // parameter's default, so a call supplying a real value silently misses and returns null,
        // which the service reports as a failed token acquisition. The CancellationToken and
        // authorityHost have both caused that already.
        mock.GetAccessTokenAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(),
            Arg.Any<string?>())
            .Returns(Task.FromResult(token));
        return mock;
    }

    private static ArmApiService FakeArm(string? systemId = PolicySystemId)
    {
        // Pass constructor arguments: the parameterless overload builds a real
        // AuthenticationService, which creates the CLI's app-data directory and deletes any legacy
        // token file on the machine running the tests.
        var arm = Substitute.For<ArmApiService>(
            NullLogger<ArmApiService>.Instance,
            Substitute.For<IAuthenticationService>(),
            null,
            null);
        arm.GetEnterprisePolicySystemIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(systemId));
        return arm;
    }

    private static VNetLinkService CreateService(
        HttpMessageHandler handler,
        ArmApiService? arm = null,
        IAuthenticationService? auth = null,
        ILogger<VNetLinkService>? logger = null,
        TimeSpan? pollInterval = null) =>
        new(
            logger ?? NullLogger<VNetLinkService>.Instance,
            auth ?? FakeAuth(),
            arm ?? FakeArm(),
            "prod",
            handler,
            NoLoginHint,
            pollInterval);

    /// <summary>
    /// Stands in for the real resolver so the tests never shell out to `az account show`.
    /// The production default caches in a static field shared with AzCliHelperTests.
    /// </summary>
    private static Task<string?> NoLoginHint() => Task.FromResult<string?>(null);

    private static HttpResponseMessage StatusResponse(
        HttpStatusCode code,
        string? status = null,
        string? operationId = null,
        string? policyArmId = null,
        string? reason = null) =>
        new(code)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                status,
                policyArmId,
                operationId,
                reason,
            })),
        };

    // ──────────────────────────────── IsRunning ────────────────────────────────

    [Theory]
    [InlineData("Running", true)]
    [InlineData("running", true)]
    [InlineData("RUNNING", true)]
    [InlineData("NotStarted", true)]
    [InlineData("notstarted", true)]
    [InlineData("Linked", false)]
    [InlineData("NotLinked", false)]
    [InlineData("Failed", false)]
    [InlineData("Unknown", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsRunning_ClassifiesStatus(string? status, bool expected)
    {
        VNetLinkService.IsRunning(status).Should().Be(expected);
    }

    // ──────────────────────────────── LinkAsync ────────────────────────────────

    [Fact]
    public async Task LinkAsync_SendsResolvedSystemIdNotTheArmId()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        using var handler = new CapturingHttpMessageHandler(r =>
        {
            captured = r;
            body = r.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        });
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Linked"));
        var svc = CreateService(handler);

        var result = await svc.LinkAsync(PolicyArmId, swap: true, TenantId);

        result.Should().NotBeNull();
        result!.Status.Should().Be("Linked");
        result.PolicyArmId.Should().BeNull();
        result.OperationId.Should().BeNull();
        result.Reason.Should().BeNull();

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/agents/vnet/link");

        body.Should().NotBeNull();
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("policySystemId").GetString().Should().Be(PolicySystemId);
        doc.RootElement.GetProperty("policyArmId").GetString().Should().Be(PolicyArmId);
        doc.RootElement.GetProperty("swap").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task LinkAsync_WhenSwapNotRequested_SendsSwapFalse()
    {
        string? body = null;
        using var handler = new CapturingHttpMessageHandler(r =>
            body = r.Content?.ReadAsStringAsync().GetAwaiter().GetResult());
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Linked"));
        var svc = CreateService(handler);

        await svc.LinkAsync(PolicyArmId, swap: false, TenantId);

        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("swap").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task LinkAsync_When202_ReturnsRunningWithOperationId()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.Accepted, "Running", OperationId));
        var svc = CreateService(handler);

        var result = await svc.LinkAsync(PolicyArmId, swap: false, TenantId);

        result.Should().NotBeNull();
        result!.Status.Should().Be("Running");
        result.OperationId.Should().Be(OperationId);
        result.PolicyArmId.Should().BeNull();
        result.Reason.Should().BeNull();
    }

    [Fact]
    public async Task LinkAsync_WhenSystemIdCannotBeResolved_DoesNotCallThePlatform()
    {
        using var handler = new TestHttpMessageHandler();
        var svc = CreateService(handler, FakeArm(systemId: null));

        var result = await svc.LinkAsync(PolicyArmId, swap: false, TenantId);

        result.Should().BeNull();
        handler.RequestCount.Should().Be(0, because: "there is no systemId to send");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task LinkAsync_WhenPlatformFails_ReturnsNull(HttpStatusCode status)
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = "nope" })),
        });
        var svc = CreateService(handler);

        var result = await svc.LinkAsync(PolicyArmId, swap: false, TenantId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task LinkAsync_WhenErrorBodyIsNotJson_StillReturnsNull()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>gateway</html>"),
        });
        var svc = CreateService(handler);

        var result = await svc.LinkAsync(PolicyArmId, swap: false, TenantId);

        result.Should().BeNull(because: "a non-JSON body means something upstream of the platform answered");
    }

    [Fact]
    public async Task LinkAsync_WhenTokenUnavailable_ReturnsNullWithoutCallingThePlatform()
    {
        using var handler = new TestHttpMessageHandler();
        var svc = CreateService(handler, auth: FakeAuth(string.Empty));

        var result = await svc.LinkAsync(PolicyArmId, swap: false, TenantId);

        result.Should().BeNull();
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task LinkAsync_WhenHttpThrows_ReturnsNull()
    {
        using var handler = new ThrowingHttpMessageHandler();
        var svc = CreateService(handler);

        var result = await svc.LinkAsync(PolicyArmId, swap: false, TenantId);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LinkAsync_WhenPolicyArmIdBlank_Throws(string? policyArmId)
    {
        using var handler = new TestHttpMessageHandler();
        var svc = CreateService(handler);

        var act = async () => await svc.LinkAsync(policyArmId!, swap: false, TenantId);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ─────────────────────────────── UnlinkAsync ───────────────────────────────

    [Fact]
    public async Task UnlinkAsync_ReadsStatusThenResolvesSystemIdAndSendsIt()
    {
        var captured = new List<HttpRequestMessage>();
        var bodies = new List<string?>();
        using var handler = new CapturingHttpMessageHandler(r =>
        {
            captured.Add(r);

            // Read here, not after the call: HttpClient disposes request content once the request
            // completes, so the assertion below would otherwise fault on a disposed StringContent.
            bodies.Add(r.Content?.ReadAsStringAsync().GetAwaiter().GetResult());
        });
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Linked", policyArmId: PolicyArmId));
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "NotLinked"));
        var arm = FakeArm();
        var svc = CreateService(handler, arm);

        var result = await svc.UnlinkAsync(TenantId);

        result.Should().NotBeNull();
        result!.Status.Should().Be("NotLinked");
        result.PolicyArmId.Should().BeNull();
        result.OperationId.Should().BeNull();
        result.Reason.Should().BeNull();

        captured.Should().HaveCount(2);
        captured[0].Method.Should().Be(HttpMethod.Get);
        captured[0].RequestUri!.AbsolutePath.Should().Be("/agents/vnet/status");
        bodies[0].Should().BeNull();

        captured[1].Method.Should().Be(HttpMethod.Post);
        captured[1].RequestUri!.AbsolutePath.Should().Be("/agents/vnet/unlink");
        bodies[1].Should().Contain("policySystemId").And.Contain(PolicySystemId);

        // The ARM read must use the ARM id the platform reported, not anything the admin typed:
        // unlink takes no policy argument at all.
        await arm.Received(1).GetEnterprisePolicySystemIdAsync(PolicyArmId, TenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnlinkAsync_WhenNothingLinked_ReturnsStatusWithoutCallingArmOrUnlink()
    {
        var captured = new List<HttpRequestMessage>();
        using var handler = new CapturingHttpMessageHandler(r => captured.Add(r));
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "NotLinked"));
        var arm = FakeArm();
        var svc = CreateService(handler, arm);

        var result = await svc.UnlinkAsync(TenantId);

        result.Should().NotBeNull();
        result!.Status.Should().Be("NotLinked");
        result.PolicyArmId.Should().BeNull();
        result.OperationId.Should().BeNull();
        result.Reason.Should().BeNull();

        captured.Should().HaveCount(1);
        captured[0].Method.Should().Be(HttpMethod.Get);
        await arm.DidNotReceive().GetEnterprisePolicySystemIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnlinkAsync_WhenStatusFails_ReturnsNullWithoutUnlinking()
    {
        var captured = new List<HttpRequestMessage>();
        using var handler = new CapturingHttpMessageHandler(r => captured.Add(r));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = "forbidden" })),
        });
        var svc = CreateService(handler);

        var result = await svc.UnlinkAsync(TenantId);

        result.Should().BeNull();
        captured.Should().HaveCount(1);
    }

    /// <summary>
    /// The policy was deleted from Azure while still linked, or the caller's Azure session cannot
    /// read it. There is no stored copy on the platform to fall back to, so this has to be a clear
    /// client-side failure rather than a bodyless request the platform would reject anyway.
    /// </summary>
    [Fact]
    public async Task UnlinkAsync_WhenSystemIdCannotBeResolved_ReturnsNullWithoutUnlinking()
    {
        var captured = new List<HttpRequestMessage>();
        using var handler = new CapturingHttpMessageHandler(r => captured.Add(r));
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Linked", policyArmId: PolicyArmId));
        var svc = CreateService(handler, FakeArm(systemId: null));

        var result = await svc.UnlinkAsync(TenantId);

        result.Should().BeNull();
        captured.Should().HaveCount(1, because: "the unlink must not be sent without a systemId");
        captured[0].Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task UnlinkAsync_WhenPlatformFails_ReturnsNull()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Linked", policyArmId: PolicyArmId));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = "policy mismatch" })),
        });
        var svc = CreateService(handler);

        var result = await svc.UnlinkAsync(TenantId);

        result.Should().BeNull();
    }

    // ────────────────────────────── GetStatusAsync ─────────────────────────────

    [Fact]
    public async Task GetStatusAsync_WithoutOperationId_OmitsTheQueryString()
    {
        HttpRequestMessage? captured = null;
        using var handler = new CapturingHttpMessageHandler(r => captured = r);
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Linked", policyArmId: PolicyArmId));
        var svc = CreateService(handler);

        var result = await svc.GetStatusAsync(TenantId);

        result.Should().NotBeNull();
        result!.Status.Should().Be("Linked");
        result.PolicyArmId.Should().Be(PolicyArmId);
        result.OperationId.Should().BeNull();
        result.Reason.Should().BeNull();

        captured!.Method.Should().Be(HttpMethod.Get);
        captured.RequestUri!.AbsolutePath.Should().Be("/agents/vnet/status");
        captured.RequestUri.Query.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatusAsync_WithOperationId_EscapesItIntoTheQueryString()
    {
        HttpRequestMessage? captured = null;
        using var handler = new CapturingHttpMessageHandler(r => captured = r);
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Running", "a b/c"));
        var svc = CreateService(handler);

        await svc.GetStatusAsync(TenantId, "a b/c");

        captured!.RequestUri!.Query.Should().Be("?operationId=a%20b%2Fc");
    }

    [Fact]
    public async Task GetStatusAsync_WhenBodyEmpty_ReturnsEmptyStatus()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) });
        var svc = CreateService(handler);

        var result = await svc.GetStatusAsync(TenantId);

        result.Should().NotBeNull();
        result!.Status.Should().BeNull();
        result.PolicyArmId.Should().BeNull();
        result.OperationId.Should().BeNull();
        result.Reason.Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_SurfacesTheFailureReason()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Failed", OperationId, reason: "Region mismatch."));
        var svc = CreateService(handler);

        var result = await svc.GetStatusAsync(TenantId, OperationId);

        result.Should().NotBeNull();
        result!.Status.Should().Be("Failed");
        result.Reason.Should().Be("Region mismatch.");
        result.OperationId.Should().Be(OperationId);
        result.PolicyArmId.Should().BeNull();
    }

    // ───────────────────────── WaitForCompletionAsync ──────────────────────────

    [Fact]
    public async Task WaitForCompletionAsync_ReturnsAsSoonAsTheOperationSettles()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Linked", OperationId));
        var svc = CreateService(handler);

        var result = await svc.WaitForCompletionAsync(TenantId, OperationId, TimeSpan.FromMinutes(5));

        result.Should().NotBeNull();
        result!.Status.Should().Be("Linked");
        handler.RequestCount.Should().Be(1, because: "a settled operation needs no second poll");
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenStillRunningAndBudgetExhausted_ReturnsRunning()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Running", OperationId));
        var svc = CreateService(handler);

        // Shorter than the poll interval, so the first read is also the last -- without arming
        // the ceiling at zero, which races the first poll and cancels it before it can return.
        var result = await svc.WaitForCompletionAsync(TenantId, OperationId, TimeSpan.FromSeconds(5));

        result.Should().NotBeNull();
        result!.Status.Should().Be("Running");
        result.OperationId.Should().Be(OperationId);
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenStatusCannotBeRead_ReturnsNull()
    {
        using var handler = new TestHttpMessageHandler();
        for (var i = 0; i < 4; i++)
        {
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { error = "upstream" })),
            });
        }

        var svc = CreateService(handler, pollInterval: TimeSpan.FromMilliseconds(1));

        var result = await svc.WaitForCompletionAsync(TenantId, OperationId, TimeSpan.FromMinutes(5));

        result.Should().BeNull();
        handler.RequestCount.Should().Be(
            4,
            because: "a status read is retried a few times before the wait gives up on it");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WaitForCompletionAsync_WhenOperationIdBlank_Throws(string? operationId)
    {
        using var handler = new TestHttpMessageHandler();
        var svc = CreateService(handler);

        var act = async () => await svc.WaitForCompletionAsync(TenantId, operationId!, TimeSpan.FromMinutes(5));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenCeilingElapsesDuringAPoll_StopsWaitingOnTheInFlightCall()
    {
        // The pre-sleep stopwatch check only bounds the gap between completed polls. Without the
        // ceiling armed on the request's own token, a poll that starts inside the budget runs to
        // the HttpClient's timeout -- minutes past what the caller asked for.
        using var handler = new SlowHttpMessageHandler(
            TimeSpan.FromSeconds(30),
            () => StatusResponse(HttpStatusCode.OK, "Running", OperationId));
        var svc = CreateService(handler);

        var stopwatch = Stopwatch.StartNew();
        var result = await svc.WaitForCompletionAsync(TenantId, OperationId, TimeSpan.FromMilliseconds(200));
        stopwatch.Stop();

        result.Should().BeNull(because: "the ceiling elapsed before any status was read");
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(10),
            because: "the wait must abandon the in-flight request rather than block on it");
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenCallerCancels_PropagatesRatherThanReportingATimeout()
    {
        // The timeout and a Ctrl+C both surface as OperationCanceledException. Only the timeout is
        // swallowed into "still running"; a caller cancel has to reach the caller.
        using var handler = new SlowHttpMessageHandler(
            TimeSpan.FromSeconds(30),
            () => StatusResponse(HttpStatusCode.OK, "Running", OperationId));
        var svc = CreateService(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = async () => await svc.WaitForCompletionAsync(
            TenantId, OperationId, TimeSpan.FromMinutes(5), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetStatusAsync_WhenTheTransportTimesOutWithNoCancellation_ReturnsNullRatherThanThrowing()
    {
        // HttpClient's own timeout surfaces as an OperationCanceledException with no token
        // cancelled. That is an ordinary request failure, and callers of a bare link, unlink or
        // status expect the documented null, not an exception thrown at them.
        using var handler = new CancelThrowingHttpMessageHandler();
        var svc = CreateService(handler);

        var result = await svc.GetStatusAsync(TenantId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_WhenTheCallerCancels_PropagatesRatherThanReturningNull()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var handler = new CancelThrowingHttpMessageHandler();
        var svc = CreateService(handler);

        var act = async () => await svc.GetStatusAsync(TenantId, operationId: null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetStatusAsync_CalledTwice_DoesNotDisposeTheInjectedHandlerOnTheFirstCall()
    {
        // A client is built per request, but the handler is a field and outlives all of them.
        // HttpClient's default ownership would have the first client's disposal take the handler
        // down with it, so every later request -- including every poll after the first in
        // WaitForCompletionAsync -- would fail with ObjectDisposedException.
        using var handler = new DisposalAwareHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Running", OperationId));
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Linked", OperationId));
        var svc = CreateService(handler);

        var first = await svc.GetStatusAsync(TenantId);
        var second = await svc.GetStatusAsync(TenantId);

        first.Should().NotBeNull();
        first!.Status.Should().Be("Running");
        first.OperationId.Should().Be(OperationId);
        first.PolicyArmId.Should().BeNull();
        first.Reason.Should().BeNull();

        second.Should().NotBeNull();
        second!.Status.Should().Be("Linked");
        second.OperationId.Should().Be(OperationId);
        second.PolicyArmId.Should().BeNull();
        second.Reason.Should().BeNull();

        handler.DisposeCount.Should().Be(0);
        handler.RequestCount.Should().Be(2);
    }

    /// <summary>
    /// Fails every request the way HttpClient's own timeout does — an OperationCanceledException
    /// with no token cancelled — so a test can pin how the service classifies it.
    /// </summary>
    private sealed class CancelThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TaskCanceledException("The request timed out.");
        }
    }

    /// <summary>
    /// Holds each request open for <paramref name="delay"/> unless the request's own token is
    /// cancelled first, so a test can tell "abandoned the call" from "waited for the response".
    /// </summary>
    private sealed class SlowHttpMessageHandler(TimeSpan delay, Func<HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return responseFactory();
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

    // ────────────────────────── Token acquisition ──────────────────────────────

    [Fact]
    public async Task LinkAsync_AcquiresTheAgent365TokenForTheRequestedTenant()
    {
        var auth = FakeAuth();
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Linked"));
        var svc = CreateService(handler, auth: auth);

        await svc.LinkAsync(PolicyArmId, swap: false, "contoso-tenant");

        await auth.Received(1).GetAccessTokenAsync(
            Arg.Any<string>(),
            "contoso-tenant",
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task UnlinkAsync_AcquiresTheAgent365TokenForTheRequestedTenant()
    {
        var auth = FakeAuth();
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "NotLinked"));
        var svc = CreateService(handler, auth: auth);

        await svc.UnlinkAsync("contoso-tenant");

        await auth.Received(1).GetAccessTokenAsync(
            Arg.Any<string>(),
            "contoso-tenant",
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task GetStatusAsync_AcquiresTheAgent365TokenForTheRequestedTenant()
    {
        var auth = FakeAuth();
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Linked"));
        var svc = CreateService(handler, auth: auth);

        await svc.GetStatusAsync("contoso-tenant");

        await auth.Received(1).GetAccessTokenAsync(
            Arg.Any<string>(),
            "contoso-tenant",
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>());
    }

    // ───────────────────────────── Constructor guards ──────────────────────────

    [Fact]
    public void Constructor_WhenLoggerNull_Throws()
    {
        var act = () => new VNetLinkService(null!, FakeAuth(), FakeArm());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WhenAuthServiceNull_Throws()
    {
        var act = () => new VNetLinkService(NullLogger<VNetLinkService>.Instance, null!, FakeArm());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WhenArmApiServiceNull_Throws()
    {
        var act = () => new VNetLinkService(NullLogger<VNetLinkService>.Instance, FakeAuth(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ───────────────────── Authority host and endpoint origin ──────────────────

    [Fact]
    public async Task SendAsync_AuthenticatesAgainstTheEnvironmentAuthorityHost()
    {
        var auth = FakeAuth();
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Linked"));
        var svc = new VNetLinkService(
            NullLogger<VNetLinkService>.Instance, auth, FakeArm(), "dogfood", handler, NoLoginHint);

        await svc.GetStatusAsync(TenantId);

        await auth.Received(1).GetAccessTokenAsync(
            Arg.Any<string>(),
            TenantId,
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>(),
            ConfigConstants.GetAuthorityHost("dogfood"));
    }

    [Fact]
    public async Task SendAsync_TargetsTheSharedAgent365ToolsOriginForTheEnvironment()
    {
        Uri? requestUri = null;
        using var handler = new CapturingHttpMessageHandler(r => requestUri = r.RequestUri);
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Linked"));
        var svc = new VNetLinkService(
            NullLogger<VNetLinkService>.Instance, FakeAuth(), FakeArm(), "dogfood", handler, NoLoginHint);

        await svc.GetStatusAsync(TenantId);

        requestUri.Should().NotBeNull();
        requestUri!.ToString().Should().StartWith(
            ConfigConstants.BuildAgent365ToolsEndpointUrl("dogfood", "/agents/vnet/status"),
            because: "the origin must come from the shared helper, not a second copy of the table");
    }

    // ──────────────────────────── 403 disambiguation ───────────────────────────

    [Fact]
    public async Task SendAsync_WhenForbiddenWithRequiredScope_NamesTheMissingScope()
    {
        var logger = new CapturingLogger<VNetLinkService>();
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                error = "insufficient_scope",
                error_description = "The token is missing a required scope.",
                required_scope = "AgentTools.VNet.Manage.All",
            })),
        });
        var svc = CreateService(handler, logger: logger);

        var result = await svc.GetStatusAsync(TenantId);

        result.Should().BeNull();
        logger.Messages.Should().Contain(m => m.Contains("The token is missing a required scope."));
        logger.Messages.Should().Contain(m => m.Contains("AgentTools.VNet.Manage.All") && m.Contains("consent"));
        logger.Messages.Should().NotContain(m => m.Contains("Global Administrator"));
    }

    [Fact]
    public async Task SendAsync_WhenForbiddenWithoutRequiredScope_NamesTheMissingRole()
    {
        var logger = new CapturingLogger<VNetLinkService>();
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                error = "The signed-in user is not a Power Platform administrator.",
            })),
        });
        var svc = CreateService(handler, logger: logger);

        var result = await svc.GetStatusAsync(TenantId);

        result.Should().BeNull();
        logger.Messages.Should().Contain(m => m.Contains("not a Power Platform administrator"));
        logger.Messages.Should().Contain(m =>
            m.Contains("Global Administrator") && m.Contains("Power Platform Administrator role"));
        logger.Messages.Should().NotContain(m => m.Contains("consent"));
    }

    // ───────────────────── Transient poll failure tolerance ────────────────────

    [Fact]
    public async Task WaitForCompletionAsync_WhenAPollFailsTransiently_KeepsWaiting()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Running", OperationId));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{}"),
        });
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Linked", OperationId));
        var svc = CreateService(handler, pollInterval: TimeSpan.FromMilliseconds(1));

        var result = await svc.WaitForCompletionAsync(TenantId, OperationId, TimeSpan.FromMinutes(5));

        result.Should().NotBeNull();
        result!.Status.Should().Be("Linked");
        result.OperationId.Should().Be(OperationId);
        result.PolicyArmId.Should().BeNull();
        result.Reason.Should().BeNull();
        handler.RequestCount.Should().Be(3, because: "the failed read is retried rather than ending the wait");
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenPollsFailConsecutivelyPastTheLimit_ReturnsNull()
    {
        using var handler = new TestHttpMessageHandler();
        for (var i = 0; i < 4; i++)
        {
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{}"),
            });
        }

        var logger = new CapturingLogger<VNetLinkService>();
        var svc = CreateService(handler, logger: logger, pollInterval: TimeSpan.FromMilliseconds(1));

        var result = await svc.WaitForCompletionAsync(TenantId, OperationId, TimeSpan.FromMinutes(5));

        result.Should().BeNull();
        handler.RequestCount.Should().Be(4, because: "three failures are tolerated and the fourth gives up");
        logger.Messages.Should().Contain(m =>
            m.Contains($"--operation-id {OperationId}") && m.Contains($"--tenant-id {TenantId}"));
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenBudgetExhausted_PrintsAResumeCommandNamingTheTenant()
    {
        var logger = new CapturingLogger<VNetLinkService>();
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "Running", OperationId));
        var svc = CreateService(handler, logger: logger);

        await svc.WaitForCompletionAsync(TenantId, OperationId, TimeSpan.FromSeconds(5));

        logger.Messages.Should().Contain(m =>
            m.Contains($"--operation-id {OperationId}") && m.Contains($"--tenant-id {TenantId}"));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
