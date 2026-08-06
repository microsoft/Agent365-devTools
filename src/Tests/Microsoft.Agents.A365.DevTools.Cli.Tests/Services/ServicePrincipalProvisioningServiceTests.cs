// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Tests must run sequentially because the service reads process environment variables.
/// </summary>
[CollectionDefinition("ServicePrincipalProvisioningTests", DisableParallelization = true)]
public class ServicePrincipalProvisioningTestCollection { }

/// <summary>
/// Unit tests for <see cref="ServicePrincipalProvisioningService"/>.
/// Uses TestHttpMessageHandler and CapturingHttpMessageHandler (defined in
/// GraphApiServiceTests.cs, same assembly) to inject fake HTTP responses.
/// </summary>
[Collection("ServicePrincipalProvisioningTests")]
public class ServicePrincipalProvisioningServiceTests
{
    private const string TenantId = "01eed126-1111-2222-3333-444455556666";

    private static IAuthenticationService FakeAuth()
    {
        var mock = Substitute.For<IAuthenticationService>();
        mock.GetAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
                Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("fake-pp-token"));
        return mock;
    }

    private static ServicePrincipalProvisioningService CreateService(
        HttpMessageHandler handler,
        IAuthenticationService? auth = null) =>
        new(
            NullLogger<ServicePrincipalProvisioningService>.Instance,
            auth ?? FakeAuth(),
            configService: null,
            handler: handler,
            retryHelper: new RetryHelper(NullLogger.Instance, maxRetries: 1, baseDelaySeconds: 0));

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task EnsureProvisionedAsync_WhenServiceReportsProvisioned_ReturnsProvisioned()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(JsonResponse(
            HttpStatusCode.OK,
            """{"status":"Provisioned","servicePrincipalObjectId":"sp-obj-1"}"""));

        var result = await CreateService(handler).EnsureProvisionedAsync(TenantId);

        result.Status.Should().Be(
            ServicePrincipalProvisioningStatus.Provisioned,
            because: "the CLI must surface the provisioning outcome reported by the service");
        result.ServicePrincipalObjectId.Should().Be(
            "sp-obj-1",
            because: "the returned object ID identifies the service principal that was created");
    }

    [Fact]
    public async Task EnsureProvisionedAsync_WhenServiceReportsAlreadyProvisioned_ReturnsAlreadyProvisioned()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(JsonResponse(
            HttpStatusCode.OK,
            """{"status":"AlreadyProvisioned","servicePrincipalObjectId":"sp-obj-1"}"""));

        var result = await CreateService(handler).EnsureProvisionedAsync(TenantId);

        result.Status.Should().Be(
            ServicePrincipalProvisioningStatus.AlreadyProvisioned,
            because: "an existing service principal is not an error and must be distinguishable from a fresh provision");
    }

    [Fact]
    public async Task EnsureProvisionedAsync_PostsToTenantScopedProvisioningRoute()
    {
        HttpRequestMessage? captured = null;
        using var handler = new CapturingHttpMessageHandler(r => captured = r);
        handler.QueueResponse(JsonResponse(HttpStatusCode.OK, """{"status":"Provisioned"}"""));

        await CreateService(handler).EnsureProvisionedAsync(TenantId);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(
            HttpMethod.Post,
            because: "the provisioning route is defined as an HTTP POST");
        captured.RequestUri!.AbsoluteUri.Should().Contain(
            $"/maven/tenants/{TenantId}/agent365/servicePrincipals/agent365Cli/provision",
            because: "the gateway routes on the service namespace and the tenant path segment");
        captured.RequestUri.Query.Should().Contain(
            "api-version=1",
            because: "the Power Platform API gateway rejects requests without an api-version");
        captured.Headers.Authorization!.Scheme.Should().Be(
            "Bearer",
            because: "the service authenticates the caller with a delegated bearer token");
    }

    [Fact]
    public async Task EnsureProvisionedAsync_CalledTwiceForSameTenant_IssuesOneRequest()
    {
        var requestCount = 0;
        using var handler = new CapturingHttpMessageHandler(_ => requestCount++);
        handler.QueueResponse(JsonResponse(HttpStatusCode.OK, """{"status":"Provisioned"}"""));
        handler.QueueResponse(JsonResponse(HttpStatusCode.OK, """{"status":"Provisioned"}"""));

        var svc = CreateService(handler);
        await svc.EnsureProvisionedAsync(TenantId);
        await svc.EnsureProvisionedAsync(TenantId);

        requestCount.Should().Be(
            1,
            because: "provisioning must run at most once per tenant per process so setup is not slowed by repeat calls");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task EnsureProvisionedAsync_WithInvalidTenantId_SkipsWithoutHttpCall(string? tenantId)
    {
        var requestCount = 0;
        using var handler = new CapturingHttpMessageHandler(_ => requestCount++);

        var result = await CreateService(handler).EnsureProvisionedAsync(tenantId);

        result.Status.Should().Be(
            ServicePrincipalProvisioningStatus.Skipped,
            because: "a tenant ID that is not a usable GUID must never be interpolated into a request URL");
        requestCount.Should().Be(0, because: "no request may be sent without a valid tenant ID");
    }

    [Fact]
    public async Task EnsureProvisionedAsync_WhenServiceReturnsForbidden_FailsWithoutThrowing()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(JsonResponse(HttpStatusCode.Forbidden, """{"error":"denied"}"""));

        var result = await CreateService(handler).EnsureProvisionedAsync(TenantId);

        result.Status.Should().Be(
            ServicePrincipalProvisioningStatus.Failed,
            because: "provisioning is best-effort and a rejection must not abort the caller's command");
    }

    [Fact]
    public async Task EnsureProvisionedAsync_WhenTransportThrows_FailsWithoutThrowing()
    {
        using var handler = new ThrowingHttpMessageHandler();

        var result = await CreateService(handler).EnsureProvisionedAsync(TenantId);

        result.Status.Should().Be(
            ServicePrincipalProvisioningStatus.Failed,
            because: "a network failure must not surface as an exception to setup commands");
    }

    [Fact]
    public async Task EnsureProvisionedAsync_WhenTokenAcquisitionThrows_FailsWithoutThrowing()
    {
        var auth = Substitute.For<IAuthenticationService>();
        auth.GetAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
                Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("no token"));

        using var handler = new TestHttpMessageHandler();

        var result = await CreateService(handler, auth).EnsureProvisionedAsync(TenantId);

        result.Status.Should().Be(
            ServicePrincipalProvisioningStatus.Failed,
            because: "a user who cannot obtain a Power Platform token must still be able to run setup");
    }

    [Fact]
    public async Task EnsureProvisionedAsync_WhenDisabledByEnvironmentVariable_SkipsWithoutHttpCall()
    {
        var requestCount = 0;
        using var handler = new CapturingHttpMessageHandler(_ => requestCount++);

        Environment.SetEnvironmentVariable(
            ServicePrincipalProvisioningService.DisableEnvironmentVariable, "true");
        try
        {
            var result = await CreateService(handler).EnsureProvisionedAsync(TenantId);

            result.Status.Should().Be(
                ServicePrincipalProvisioningStatus.Skipped,
                because: "operators must have a documented way to turn the extra call off");
            requestCount.Should().Be(0, because: "the disable switch must prevent the request entirely");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                ServicePrincipalProvisioningService.DisableEnvironmentVariable, null);
        }
    }
}
