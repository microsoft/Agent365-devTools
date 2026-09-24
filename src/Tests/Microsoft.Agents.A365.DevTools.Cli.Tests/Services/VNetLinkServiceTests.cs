// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
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
        mock.GetAccessTokenAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(Task.FromResult(token));
        return mock;
    }

    private static ArmApiService FakeArm(string? systemId = PolicySystemId)
    {
        var arm = Substitute.For<ArmApiService>();
        arm.GetEnterprisePolicySystemIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(systemId));
        return arm;
    }

    private static VNetLinkService CreateService(
        HttpMessageHandler handler,
        ArmApiService? arm = null,
        IAuthenticationService? auth = null) =>
        new(
            NullLogger<VNetLinkService>.Instance,
            auth ?? FakeAuth(),
            arm ?? FakeArm(),
            "prod",
            handler,
            NoLoginHint);

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
    public async Task UnlinkAsync_PostsToUnlinkWithNoBody()
    {
        HttpRequestMessage? captured = null;
        using var handler = new CapturingHttpMessageHandler(r => captured = r);
        handler.QueueResponse(StatusResponse(HttpStatusCode.OK, "NotLinked"));
        var svc = CreateService(handler);

        var result = await svc.UnlinkAsync(TenantId);

        result.Should().NotBeNull();
        result!.Status.Should().Be("NotLinked");
        result.PolicyArmId.Should().BeNull();
        result.OperationId.Should().BeNull();
        result.Reason.Should().BeNull();

        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/agents/vnet/unlink");
        captured.Content.Should().BeNull(because: "the platform supplies the stored policy itself");
    }

    [Fact]
    public async Task UnlinkAsync_WhenPlatformFails_ReturnsNull()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = "no stored policy" })),
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

        // A zero budget cannot fit another poll interval, so the first read is also the last.
        var result = await svc.WaitForCompletionAsync(TenantId, OperationId, TimeSpan.Zero);

        result.Should().NotBeNull();
        result!.Status.Should().Be("Running");
        result.OperationId.Should().Be(OperationId);
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenStatusCannotBeRead_ReturnsNull()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = "upstream" })),
        });
        var svc = CreateService(handler);

        var result = await svc.WaitForCompletionAsync(TenantId, OperationId, TimeSpan.FromMinutes(5));

        result.Should().BeNull();
        handler.RequestCount.Should().Be(1, because: "an unreadable status is terminal for the wait");
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
}
