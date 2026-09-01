// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class GraphApiServiceTests
{
    private readonly ILogger<GraphApiService> _mockLogger;
    private readonly CommandExecutor _mockExecutor;
    private readonly IMicrosoftGraphTokenProvider _mockTokenProvider;

    public GraphApiServiceTests()
    {
        _mockLogger = Substitute.For<ILogger<GraphApiService>>();
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);
        _mockTokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
    }


    [Fact]
    public async Task GetClientAppAccessTokenAsync_PassesExplicitClientIdAndScopesToTokenProvider()
    {
        const string tenantId = "12345678-1234-1234-1234-123456789012";
        const string clientAppId = "f54280f4-395e-4ea8-9e48-bf2d4952aa14";
        var expectedScopes = new[] { "User.Read", "Application.Read.All" };
        string? capturedClientAppId = null;
        string[]? capturedScopes = null;

        _mockTokenProvider.GetMgGraphAccessTokenAsync(
                tenantId,
                Arg.Do<IEnumerable<string>>(scopes => capturedScopes = scopes.ToArray()),
                useDeviceCode: false,
                clientAppId: Arg.Do<string?>(id => capturedClientAppId = id),
                ct: Arg.Any<CancellationToken>(),
                loginHint: null,
                forceRefresh: false)
            .Returns("first-party-token");
        var service = new GraphApiService(
            _mockLogger,
            _mockExecutor,
            FakeAuth(),
            tokenProvider: _mockTokenProvider,
            loginHintResolver: () => Task.FromResult<string?>(null));

        var token = await service.GetClientAppAccessTokenAsync(
            tenantId, clientAppId, expectedScopes);

        token.Should().Be("first-party-token");
        capturedClientAppId.Should().Be(clientAppId,
            because: "the token scp claim is authoritative only when the token was issued to the first-party client being validated");
        capturedScopes.Should().BeEquivalentTo(expectedScopes,
            because: "authorization validation must request the exact delegated scopes that will be checked in the token scp claim");
    }

    [Fact]
    public async Task GraphPostWithResponseAsync_Returns_Success_And_ParsesJson()
    {
        // Arrange
        var handler = new TestHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);
                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var service = new GraphApiService(logger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));

        // Queue successful POST with JSON body
        var bodyObj = new { result = "ok" };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(bodyObj))
        });

        // Act
        var resp = await service.GraphPostWithResponseAsync("tid", "/v1.0/some/path", new { a = 1 });

        // Assert
        resp.IsSuccess.Should().BeTrue();
        resp.StatusCode.Should().Be((int)HttpStatusCode.OK);
        resp.Body.Should().NotBeNullOrWhiteSpace();
        resp.Json.Should().NotBeNull();
        resp.Json!.RootElement.GetProperty("result").GetString().Should().Be("ok");
    }


    [Fact]
    public async Task GraphPostWithResponseAsync_Returns_Failure_With_Body()
    {
        // Arrange
        var handler = new TestHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);
                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var service = new GraphApiService(logger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));

        // Queue failing POST with JSON error body
        var errorBody = new { error = new { code = "Authorization_RequestDenied", message = "Insufficient privileges" } };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(JsonSerializer.Serialize(errorBody))
        });

        // Act
        var resp = await service.GraphPostWithResponseAsync("tid", "/v1.0/some/path", new { a = 1 });

        // Assert
        resp.IsSuccess.Should().BeFalse();
        resp.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
        resp.Body.Should().Contain("Insufficient privileges");
        resp.Json.Should().NotBeNull();
        resp.Json!.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("Authorization_RequestDenied");
    }

    [Fact]
    public async Task LookupServicePrincipalAsync_DoesNotIncludeConsistencyLevelHeader()
    {
        // This test verifies that the ConsistencyLevel header is NOT sent during service principal lookup.
        // Per Graph docs, servicePrincipal $filter=appId eq is "Default+Advanced" — it works without
        // ConsistencyLevel. Adding it caused HTTP 400 "One or more headers are invalid" in some scenarios.
        // Regression test for issue discovered on 2025-12-19.

        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHttpMessageHandler((req) => capturedRequest = req);
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        // Mock az CLI token acquisition to return a valid token
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);
                
                // Simulate az account show - logged in
                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new CommandResult 
                    { 
                        ExitCode = 0, 
                        StandardOutput = JsonSerializer.Serialize(new { tenantId = "tenant-123" }), 
                        StandardError = string.Empty 
                    });
                }
                
                // Simulate az account get-access-token -> return token
                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new CommandResult 
                    { 
                        ExitCode = 0, 
                        StandardOutput = "fake-graph-token-12345", 
                        StandardError = string.Empty 
                    });
                }
                
                // Default: success
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        // Create GraphApiService with our capturing handler
        var service = new GraphApiService(logger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));

        // Queue response for service principal lookup
        const string appId = "11111111-1111-1111-1111-111111111111";
        var spResponse = new { value = new[] { new { id = "sp-object-id-123", appId } } };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(spResponse))
        });

        // Act - Call a public method that internally uses LookupServicePrincipalAsync
        var result = await service.LookupServicePrincipalByAppIdAsync("tenant-123", appId);

        // Assert
        result.Should().NotBeNull("service principal lookup should succeed");
        capturedRequest.Should().NotBeNull("should have captured the HTTP request");
        
        // Verify this is indeed a service principal lookup request
        capturedRequest!.Method.Should().Be(HttpMethod.Get);
        capturedRequest.RequestUri.Should().NotBeNull();
        capturedRequest.RequestUri!.AbsolutePath.Should().Contain("servicePrincipals");
        capturedRequest.RequestUri.Query.Should().Contain("$filter");
        
        // Verify the ConsistencyLevel header is NOT present on the service principal lookup request
        capturedRequest.Headers.Contains("ConsistencyLevel").Should().BeFalse(
            "ConsistencyLevel header should NOT be present for simple service principal lookup queries. " +
            "Per Graph docs, appId eq is Default+Advanced and does not require this header.");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task LookupServicePrincipalByAppIdWithResponseAsync_WhenGraphFails_PreservesOperationalFailure(
        HttpStatusCode statusCode)
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                """{"error":{"code":"RequestFailed","message":"lookup failed"}}""")
        });

        string[]? requestedScopes = null;
        string? requestedClientAppId = null;
        var tokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
        tokenProvider.GetMgGraphAccessTokenAsync(
                Arg.Any<string>(),
                Arg.Do<IEnumerable<string>>(scopes => requestedScopes = scopes.ToArray()),
                Arg.Any<bool>(),
                Arg.Do<string?>(clientAppId => requestedClientAppId = clientAppId),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<bool>())
            .Returns("fake-token");
        var service = new GraphApiService(
            _mockLogger,
            _mockExecutor,
            FakeAuth(),
            handler,
            tokenProvider,
            loginHintResolver: () => Task.FromResult<string?>(null),
            retryHelper: new RetryHelper(NullLogger.Instance, maxRetries: 1, baseDelaySeconds: 0))
        {
            CustomClientAppId = AuthenticationConstants.WellKnownClientAppId
        };

        var result = await service.LookupServicePrincipalByAppIdWithResponseAsync(
            "tenant-123", AuthenticationConstants.WellKnownClientAppId);

        result.IsSuccess.Should().BeFalse(
            because: "an HTTP failure must not be represented as a successful empty service-principal query");
        result.StatusCode.Should().Be((int)statusCode,
            because: "the original Graph status must be preserved for authorization and retry diagnostics");
        result.FailureReason.Should().Contain($"HTTP {(int)statusCode}",
            because: "operators need the Graph status to distinguish authorization failures from transient failures");
        requestedScopes.Should().BeNull(
            because: "presence discovery must not request a scoped token from the app being probed — that app may not exist, so its token could never be acquired");
        requestedClientAppId.Should().BeNull(
            because: "presence discovery must authenticate ambiently, never as the client app whose existence is in question");
    }

    [Fact]
    public async Task LookupServicePrincipalByAppIdWithResponseAsync_UsesAmbientAuthEvenWhenProbingTheResolvedClientApp()
    {
        // Regression guard: authenticating as the app being probed makes an absent app report
        // NoAuth instead of a conclusive "not present", blocking the custom-app fallback.
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[]}""")
        });
        var tokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
        var authService = FakeAuth();
        var service = new GraphApiService(
            _mockLogger,
            _mockExecutor,
            authService,
            handler,
            tokenProvider,
            loginHintResolver: () => Task.FromResult<string?>(null),
            retryHelper: new RetryHelper(NullLogger.Instance, maxRetries: 1, baseDelaySeconds: 0))
        {
            CustomClientAppId = AuthenticationConstants.WellKnownClientAppId
        };

        var result = await service.LookupServicePrincipalByAppIdWithResponseAsync(
            "tenant-123", AuthenticationConstants.WellKnownClientAppId);

        result.IsSuccess.Should().BeTrue(
            because: "the ambient identity can complete the query and report the app as conclusively absent");
        result.ServicePrincipalId.Should().BeNull();
        await tokenProvider.DidNotReceiveWithAnyArgs().GetMgGraphAccessTokenAsync(
            default!, default!, default, default, default, default, default);
        await authService.ReceivedWithAnyArgs(1).GetAccessTokenAsync(
            default!, default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task LookupServicePrincipalByAppIdWithResponseAsync_WhenValidatingResolvedApp_UsesItsScopedToken()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[{"id":"first-party-sp-object-id"}]}""")
        });
        string[]? requestedScopes = null;
        string? requestedClientAppId = null;
        var tokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
        tokenProvider.GetMgGraphAccessTokenAsync(
                Arg.Any<string>(),
                Arg.Do<IEnumerable<string>>(scopes => requestedScopes = scopes.ToArray()),
                Arg.Any<bool>(),
                Arg.Do<string?>(clientAppId => requestedClientAppId = clientAppId),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<bool>())
            .Returns("first-party-token");
        var authService = FakeAuth();
        var service = new GraphApiService(
            _mockLogger,
            _mockExecutor,
            authService,
            handler,
            tokenProvider,
            loginHintResolver: () => Task.FromResult<string?>(null),
            retryHelper: new RetryHelper(NullLogger.Instance, maxRetries: 1, baseDelaySeconds: 0))
        {
            CustomClientAppId = AuthenticationConstants.WellKnownClientAppId
        };

        var result = await service.LookupServicePrincipalByAppIdWithResponseAsync(
            "tenant-123",
            AuthenticationConstants.WellKnownClientAppId,
            authenticationMode: GraphAuthenticationMode.ResolvedClientApp);

        result.ServicePrincipalId.Should().Be(
            "first-party-sp-object-id",
            because: "validation must still query the resolved first-party service principal");
        requestedClientAppId.Should().Be(
            AuthenticationConstants.WellKnownClientAppId,
            because: "post-resolution validation must not depend on the ambient Graph PowerShell application");
        requestedScopes.Should().ContainSingle(
            because: "service-principal validation requires one delegated scope")
            .Which.Should().Be(
                AuthenticationConstants.ApplicationReadAllScope,
                because: "the resolved client token needs Application.Read.All for the service-principal query");
        await authService.DidNotReceiveWithAnyArgs().GetAccessTokenAsync(
            default!, default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task LookupServicePrincipalByAppIdWithResponseAsync_WhenAmbientTokenUnavailable_ReportsNoAuth()
    {
        using var handler = new TestHttpMessageHandler();
        var service = new GraphApiService(
            _mockLogger,
            _mockExecutor,
            FakeAuthReturning(null),
            handler,
            loginHintResolver: () => Task.FromResult<string?>(null),
            retryHelper: new RetryHelper(NullLogger.Instance, maxRetries: 1, baseDelaySeconds: 0));

        var result = await service.LookupServicePrincipalByAppIdWithResponseAsync(
            "tenant-123", AuthenticationConstants.WellKnownClientAppId);

        result.IsSuccess.Should().BeFalse(
            because: "without an ambient token the presence query never ran and cannot prove absence");
        result.StatusCode.Should().Be(
            0,
            because: "no HTTP response exists when token acquisition fails before the request");
        result.FailureReason.Should().Contain(
            "NoAuth",
            because: "callers need to distinguish authentication failure from a successful empty lookup");
        handler.RequestCount.Should().Be(
            0,
            because: "Graph must not be called without an access token");
    }

    [Fact]
    public async Task LookupServicePrincipalByAppIdWithResponseAsync_WhenGraphReturnsEmptyArray_ReportsSuccessfulAbsence()
    {
        HttpRequestMessage? capturedRequest = null;
        using var handler = new CapturingHttpMessageHandler(request => capturedRequest = request);
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[]}""")
        });
        var service = CreateServiceWithTokenProvider(handler);

        var result = await service.LookupServicePrincipalByAppIdWithResponseAsync(
            "tenant-123", AuthenticationConstants.WellKnownClientAppId);

        result.IsSuccess.Should().BeTrue(
            because: "only a successful Graph response with an empty value array proves that the service principal is absent");
        result.ServicePrincipalId.Should().BeNull(
            because: "an empty successful Graph result proves that no matching service principal exists");
        result.StatusCode.Should().Be((int)HttpStatusCode.OK,
            because: "absence is conclusive only when Graph successfully processed the lookup");
        capturedRequest.Should().NotBeNull(
            because: "the test must inspect the actual Graph request used for first-party resolution");
        capturedRequest!.RequestUri!.AbsolutePath.Should().EndWith(
            "/v1.0/servicePrincipals",
            because: "customer tenants may contain only the manager-created service principal and no tenant-local application object");
        capturedRequest.RequestUri.AbsolutePath.Should().NotContain(
            "/applications",
            because: "the first-party identity must never be resolved through tenant-local application registrations");
        Uri.UnescapeDataString(capturedRequest.RequestUri.Query).Should().Be(
            $"?$filter=appId eq '{AuthenticationConstants.WellKnownClientAppId}'&$select=id",
            because: "the service-principal query must target the exact well-known application ID and request only its object ID");
    }

    [Fact]
    public async Task LookupServicePrincipalByAppIdWithResponseAsync_WhenServicePrincipalExists_ReturnsObjectId()
    {
        HttpRequestMessage? capturedRequest = null;
        using var handler = new CapturingHttpMessageHandler(request => capturedRequest = request);
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[{"id":"first-party-sp-object-id"}]}""")
        });
        var service = CreateServiceWithTokenProvider(handler);

        var result = await service.LookupServicePrincipalByAppIdWithResponseAsync(
            "tenant-123", AuthenticationConstants.WellKnownClientAppId);

        result.IsSuccess.Should().BeTrue(
            because: "a valid Graph response containing the service principal proves the first-party enterprise application is present");
        result.ServicePrincipalId.Should().Be("first-party-sp-object-id",
            because: "callers need the tenant service-principal object ID rather than a tenant-local application object");
        Uri.UnescapeDataString(capturedRequest!.RequestUri!.Query).Should().Be(
            $"?$filter=appId eq '{AuthenticationConstants.WellKnownClientAppId}'&$select=id",
            because: "the lookup must query the exact first-party appId through servicePrincipals");
    }

    [Fact]
    public async Task LookupServicePrincipalByAppIdWithResponseAsync_WhenResponseIsMalformed_ReportsFailure()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"unexpected":[]}""")
        });
        var service = CreateServiceWithTokenProvider(handler);

        var result = await service.LookupServicePrincipalByAppIdWithResponseAsync(
            "tenant-123", AuthenticationConstants.WellKnownClientAppId);

        result.IsSuccess.Should().BeFalse(
            because: "an HTTP 200 without a value array cannot prove either service-principal presence or absence");
        result.FailureReason.Should().Contain("invalid service-principal lookup response",
            because: "malformed Graph data must remain distinguishable from a successful empty lookup");
    }

    [Fact]
    public async Task LookupServicePrincipalByAppIdWithResponseAsync_WhenAppIdIsInvalid_DoesNotSendRequest()
    {
        var requestSent = false;
        using var handler = new CapturingHttpMessageHandler(_ => requestSent = true);
        var tokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
        var service = new GraphApiService(
            _mockLogger,
            _mockExecutor,
            FakeAuth(),
            handler,
            tokenProvider,
            loginHintResolver: () => Task.FromResult<string?>(null));

        var result = await service.LookupServicePrincipalByAppIdWithResponseAsync(
            "tenant-123", "not-a-guid");

        result.IsSuccess.Should().BeFalse(
            because: "user-controlled app IDs must be rejected before OData query construction");
        requestSent.Should().BeFalse(
            because: "invalid app IDs must never reach Microsoft Graph");
        await tokenProvider.DidNotReceiveWithAnyArgs().GetMgGraphAccessTokenAsync(
            default!, default!, default, default, default, default, default);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task LookupApplicationByAppIdWithResponseAsync_WhenGraphFails_PreservesOperationalFailure(
        HttpStatusCode statusCode)
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                """{"error":{"code":"RequestFailed","message":"lookup failed"}}""")
        });
        var service = CreateServiceWithTokenProvider(handler);

        var result = await service.LookupApplicationByAppIdWithResponseAsync(
            "tenant-123", "11111111-1111-1111-1111-111111111111");

        result.IsSuccess.Should().BeFalse(
            because: "a failed Graph request cannot prove that a configured custom application is absent");
        result.StatusCode.Should().Be((int)statusCode,
            because: "configuration resolution needs the original status to preserve custom IDs on inconclusive lookups");
        result.FailureReason.Should().Contain($"HTTP {(int)statusCode}",
            because: "operators need the Graph status to diagnose authorization and transient failures");
    }

    [Fact]
    public async Task LookupApplicationByAppIdWithResponseAsync_WhenGraphReturnsEmptyArray_ReportsSuccessfulAbsence()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[]}""")
        });
        var service = CreateServiceWithTokenProvider(handler);

        var result = await service.LookupApplicationByAppIdWithResponseAsync(
            "tenant-123", "11111111-1111-1111-1111-111111111111");

        result.IsSuccess.Should().BeTrue(
            because: "only a successful empty applications response proves that the configured custom app is absent");
        result.ApplicationId.Should().BeNull(
            because: "an empty applications result contains no matching custom application");
    }

    [Fact]
    public async Task GraphGetWithResponseAsync_AmbientMode_DoesNotRequestATokenForTheResolvedClientApp()
    {
        // Issue #489: a token issued for the app being probed cannot prove that app's own
        // absence, and it carries only User.Read — Graph rejects /applications with 403.
        using var handler = new TestHttpMessageHandler();
        var tokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
        tokenProvider.GetMgGraphAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>("custom-app-token"));
        var service = new GraphApiService(
            Substitute.For<ILogger<GraphApiService>>(),
            Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()),
            FakeAuth(), handler, tokenProvider,
            loginHintResolver: () => Task.FromResult<string?>(null))
        {
            CustomClientAppId = "11111111-1111-1111-1111-111111111111"
        };

        await service.GraphGetWithResponseAsync(
            "tenant-123", "/v1.0/applications", ct: default,
            authenticationMode: GraphAuthenticationMode.Ambient);

        await tokenProvider.DidNotReceive().GetMgGraphAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task GraphGetWithResponseAsync_ResolvedClientAppMode_RequestsATokenForTheResolvedClientApp()
    {
        // Counter-test to the ambient case: without it, a regression that stops using the token
        // provider entirely would still satisfy the DidNotReceive assertion above.
        const string customAppId = "11111111-1111-1111-1111-111111111111";
        using var handler = new TestHttpMessageHandler();
        var tokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
        tokenProvider.GetMgGraphAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>("custom-app-token"));
        var service = new GraphApiService(
            Substitute.For<ILogger<GraphApiService>>(),
            Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()),
            FakeAuth(), handler, tokenProvider,
            loginHintResolver: () => Task.FromResult<string?>(null))
        {
            CustomClientAppId = customAppId
        };

        await service.GraphGetWithResponseAsync("tenant-123", "/v1.0/applications");

        await tokenProvider.Received().GetMgGraphAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(),
            customAppId, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task GraphGetWithResponseAsync_WhenHttpClientTimesOut_ReturnsFailureInsteadOfThrowing()
    {
        // HttpClient surfaces its own timeout as TaskCanceledException with no cancellation
        // requested; treating it as a cancel would abort the command instead of reporting a
        // recoverable lookup failure.
        using var handler = new ExceptionThrowingHttpMessageHandler(
            () => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        var service = CreateServiceWithTokenProvider(handler);

        var result = await service.GraphGetWithResponseAsync("tenant-123", "/v1.0/servicePrincipals");

        result.IsSuccess.Should().BeFalse(
            because: "a timed-out lookup is a failure result, not a caller cancellation");
        result.StatusCode.Should().Be(0,
            because: "no HTTP response was received, so no status can be reported");
    }

    [Fact]
    public async Task GraphGetWithResponseAsync_WhenCallerCancels_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new ExceptionThrowingHttpMessageHandler(() =>
        {
            cts.Cancel();
            return new TaskCanceledException("cancelled by caller", null, cts.Token);
        });
        var authService = Substitute.For<IAuthenticationService>();
        authService.GetAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
                Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("fake-token"));
        var service = new GraphApiService(
            _mockLogger,
            _mockExecutor,
            authService,
            handler,
            loginHintResolver: () => Task.FromResult<string?>(null),
            retryHelper: new RetryHelper(NullLogger.Instance, maxRetries: 1, baseDelaySeconds: 0));

        var act = async () => await service.GraphGetWithResponseAsync(
            "tenant-123", "/v1.0/servicePrincipals", ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            because: "Ctrl+C must abort the command rather than being converted into a misleading lookup failure");
    }

    [Theory]
    [InlineData("token-with-trailing-newline\n")]
    [InlineData("token-with-trailing-crlf\r\n")]
    [InlineData("token\nwith\nembedded\nnewlines")]
    [InlineData("token\r\nwith\r\nembedded\r\ncrlf")]
    [InlineData("token\rwith\rcarriage\rreturns")]
    [InlineData("\nleading-newline-token")]
    [InlineData("\r\nleading-crlf-token")]
    [InlineData("  token-with-whitespace  \n")]
    public async Task GraphGetAsync_SanitizesTokenWithNewlineCharacters(string tokenWithNewlines)
    {
        // This test verifies that tokens containing newline characters (\r, \n, \r\n)
        // are properly sanitized before being used in HTTP Authorization headers.
        // Without this fix, System.FormatException is thrown:
        // "New-line characters are not allowed in header values."
        // Regression test for newline character issue in token handling.

        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHttpMessageHandler((req) => capturedRequest = req);
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        // Mock az CLI to return a token WITH newline characters (simulating real-world issue)
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);

                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new CommandResult
                    {
                        ExitCode = 0,
                        StandardOutput = "{}",
                        StandardError = string.Empty
                    });
                }

                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                {
                    // Return token WITH newline characters - this simulates the real-world issue
                    return Task.FromResult(new CommandResult
                    {
                        ExitCode = 0,
                        StandardOutput = tokenWithNewlines,
                        StandardError = string.Empty
                    });
                }

                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var service = new GraphApiService(logger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));

        // Queue a successful response
        using var queuedResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"value\":[]}")
        };
        handler.QueueResponse(queuedResponse);

        // Act - This should NOT throw FormatException even with newlines in token
        var result = await service.GraphGetAsync("tenant-123", "/v1.0/me");

        // Assert
        capturedRequest.Should().NotBeNull("HTTP request should have been sent");
        capturedRequest!.Headers.Authorization.Should().NotBeNull("Authorization header should be set");
        capturedRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");

        // The token in the header should NOT contain any newline characters
        var actualToken = capturedRequest.Headers.Authorization.Parameter;
        actualToken.Should().NotBeNull();
        actualToken.Should().NotContain("\r", "Token should not contain carriage return characters");
        actualToken.Should().NotContain("\n", "Token should not contain newline characters");
        actualToken.Should().NotStartWith(" ", "Token should not have leading whitespace");
        actualToken.Should().NotEndWith(" ", "Token should not have trailing whitespace");
    }

    [Fact]
    public async Task GraphGetAsync_TokenFromTokenProvider_SanitizesNewlines()
    {
        // This test verifies that tokens from IMicrosoftGraphTokenProvider are also sanitized.
        // The token provider path uses a different code branch in EnsureGraphHeadersAsync.

        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHttpMessageHandler((req) => capturedRequest = req);
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        var tokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();

        // Mock token provider to return a token WITH embedded newlines
        tokenProvider.GetMgGraphAccessTokenAsync(
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>())
            .Returns("token-from-provider\r\nwith-embedded-newlines\n");

        var service = new GraphApiService(logger, executor, FakeAuth(), handler, tokenProvider, loginHintResolver: () => Task.FromResult<string?>(null));

        // Queue a successful response
        using var queuedResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"value\":[]}")
        };
        handler.QueueResponse(queuedResponse);

        // Act - Call with scopes to trigger token provider path
        var result = await service.GraphGetAsync("tenant-123", "/v1.0/me", default, new[] { "User.Read" });

        // Assert
        capturedRequest.Should().NotBeNull("HTTP request should have been sent");
        capturedRequest!.Headers.Authorization.Should().NotBeNull("Authorization header should be set");

        var actualToken = capturedRequest.Headers.Authorization!.Parameter;
        actualToken.Should().NotBeNull();
        actualToken.Should().NotContain("\r", "Token should not contain carriage return characters");
        actualToken.Should().NotContain("\n", "Token should not contain newline characters");
    }


    #region GetServicePrincipalDisplayNameAsync Tests

    [Fact]
    public async Task GetServicePrincipalDisplayNameAsync_SuccessfulLookup_ReturnsDisplayName()
    {
        // Arrange
        using var handler = new TestHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        // Mock az CLI token acquisition
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);
                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var service = new GraphApiService(logger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));

        // Queue successful response with Microsoft Graph service principal
        var spResponse = new { value = new[] { new { displayName = "Microsoft Graph" } } };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(spResponse))
        });

        // Act
        var displayName = await service.GetServicePrincipalDisplayNameAsync("tenant-123", "00000003-0000-0000-c000-000000000000");

        // Assert
        displayName.Should().Be("Microsoft Graph");
    }

    [Fact]
    public async Task GetServicePrincipalDisplayNameAsync_ServicePrincipalNotFound_ReturnsNull()
    {
        // Arrange
        using var handler = new TestHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);
                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var service = new GraphApiService(logger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));

        // Queue response with empty array (service principal not found)
        var spResponse = new { value = Array.Empty<object>() };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(spResponse))
        });

        // Act
        var displayName = await service.GetServicePrincipalDisplayNameAsync("tenant-123", "12345678-1234-1234-1234-123456789012");

        // Assert
        displayName.Should().BeNull("service principal with unknown appId should not be found");
    }

    [Fact]
    public async Task GetServicePrincipalDisplayNameAsync_NullResponse_ReturnsNull()
    {
        // Arrange
        using var handler = new TestHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);
                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var service = new GraphApiService(logger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));

        // Queue error response (simulating network error or Graph API error)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Internal Server Error")
        });

        // Act
        var displayName = await service.GetServicePrincipalDisplayNameAsync("tenant-123", "00000003-0000-0000-c000-000000000000");

        // Assert
        displayName.Should().BeNull("failed Graph API call should return null");
    }

    [Fact]
    public async Task GetServicePrincipalDisplayNameAsync_MissingDisplayNameProperty_ReturnsNull()
    {
        // Arrange
        using var handler = new TestHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);
                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var service = new GraphApiService(logger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));

        // Queue response with malformed object (missing displayName)
        var spResponse = new { value = new[] { new { id = "sp-id-123", appId = "00000003-0000-0000-c000-000000000000" } } };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(spResponse))
        });

        // Act
        var displayName = await service.GetServicePrincipalDisplayNameAsync("tenant-123", "00000003-0000-0000-c000-000000000000");

        // Assert
        displayName.Should().BeNull("malformed response missing displayName should return null");
    }

    #endregion

    #region IsCurrentUserAdminAsync

    // Role checks now decode the wids claim from the MSAL access token instead of calling Graph.
    // Tests provide a fake JWT with the appropriate wids array via FakeAuthReturning().

    private static string BuildJwtWithWids(params string[] wids)
    {
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(new { wids });
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payloadJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"eyJhbGciOiJub25lIn0.{b64}.";
    }

    private static IAuthenticationService FakeAuthReturning(string? token)
    {
        var mock = Substitute.For<IAuthenticationService>();
        mock.GetAccessTokenAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(Task.FromResult<string>(token!));
        return mock;
    }

    private static GraphApiService CreateServiceWithWids(TestHttpMessageHandler handler, params string[] roleWids)
    {
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        var tokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
        var jwt = BuildJwtWithWids(roleWids);
        // CheckDirectoryRoleAsync now requires the token to come from _tokenProvider (the path
        // that uses the custom client app's clientId — the only app with the `wids` optional
        // claim configured). The previous AuthenticationService-based mock would not be hit by
        // the production code path.
        tokenProvider.GetMgGraphAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(jwt);
        return new GraphApiService(logger, executor, FakeAuthReturning(jwt), handler, tokenProvider,
            loginHintResolver: () => Task.FromResult<string?>(null),
            retryHelper: new RetryHelper(NullLogger.Instance, maxRetries: 1, baseDelaySeconds: 0));
    }

    private static GraphApiService CreateServiceWithNullAuth(TestHttpMessageHandler handler)
    {
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        var tokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
        // Simulate token acquisition failure on the token-provider path (the path
        // CheckDirectoryRoleAsync now uses).
        tokenProvider.GetMgGraphAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns((string?)null);
        return new GraphApiService(logger, executor, FakeAuthReturning(null), handler, tokenProvider,
            loginHintResolver: () => Task.FromResult<string?>(null),
            retryHelper: new RetryHelper(NullLogger.Instance, maxRetries: 1, baseDelaySeconds: 0));
    }

    [Fact]
    public async Task IsCurrentUserAdminAsync_UserWithGlobalAdminRole_ReturnsHasRole()
    {
        // Arrange — MSAL token contains wids claim with Global Administrator template ID
        using var handler = new TestHttpMessageHandler();
        var service = CreateServiceWithWids(handler, "62e90394-69f5-4237-9190-012177145e10");

        // Act
        var result = await service.IsCurrentUserAdminAsync("tenant-123");

        // Assert
        result.Should().Be(RoleCheckResult.HasRole, "a user holding the Global Administrator role should pass the admin check");
    }

    [Fact]
    public async Task IsCurrentUserAdminAsync_UserWithNoAdminRole_ReturnsDoesNotHaveRole()
    {
        // Arrange — MSAL token contains wids claim with no matching role GUIDs
        using var handler = new TestHttpMessageHandler();
        var service = CreateServiceWithWids(handler);  // empty wids

        // Act
        var result = await service.IsCurrentUserAdminAsync("tenant-123");

        // Assert
        result.Should().Be(RoleCheckResult.DoesNotHaveRole, "a user with no admin role should not pass the Global Administrator check");
    }

    [Fact]
    public async Task IsCurrentUserAdminAsync_TokenAcquisitionFails_ReturnsUnknown()
    {
        // Arrange — token acquisition returns null (auth failure). The role check now decodes
        // wids from the access token, so a failed acquisition (rather than a Graph 500) is the
        // realistic failure mode.
        using var handler = new TestHttpMessageHandler();
        var service = CreateServiceWithNullAuth(handler);

        // Act
        var result = await service.IsCurrentUserAdminAsync("tenant-123");

        // Assert
        result.Should().Be(RoleCheckResult.Unknown, "a failed token acquisition should return Unknown, not DoesNotHaveRole");
    }

    #endregion

    #region FindApplicationByDisplayNameAsync

    [Fact]
    public async Task FindApplicationByDisplayNameAsync_WhenAppFound_ReturnsAppId()
    {
        using var handler = new TestHttpMessageHandler();
        var service = CreateServiceWithHandler(handler);

        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"value\":[{\"appId\":\"f2d098d5-09d2-40e1-a7b0-d9fff1ace230\"}]}")
        });

        var result = await service.FindApplicationByDisplayNameAsync("tenant-id", "Agent 365 CLI");

        result.Should().Be("f2d098d5-09d2-40e1-a7b0-d9fff1ace230",
            because: "the first matching app's appId should be returned when Graph returns a non-empty value array");
    }

    [Fact]
    public async Task FindApplicationByDisplayNameAsync_WhenNotFound_ReturnsNull()
    {
        using var handler = new TestHttpMessageHandler();
        var service = CreateServiceWithHandler(handler);

        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"value\":[]}")
        });

        var result = await service.FindApplicationByDisplayNameAsync("tenant-id", "Unknown App");

        result.Should().BeNull(
            because: "an empty value array means no app with that display name exists in the tenant");
    }

    [Fact]
    public async Task FindApplicationByDisplayNameAsync_WhenApiFails_ReturnsNull()
    {
        using var handler = new TestHttpMessageHandler();
        var service = CreateServiceWithHandler(handler);

        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"error\":{\"code\":\"Authorization_RequestDenied\"}}")
        });

        var result = await service.FindApplicationByDisplayNameAsync("tenant-id", "Agent 365 CLI");

        result.Should().BeNull(
            because: "a non-success HTTP response should be treated as app-not-found to allow the caller to surface a clear error");
    }

    [Fact]
    public async Task FindApplicationByDisplayNameAsync_SendsConsistencyLevelHeader()
    {
        // Graph requires 'ConsistencyLevel: eventual' for advanced filter queries (displayName eq).
        // Missing this header causes HTTP 400 in some tenants.
        HttpRequestMessage? capturedRequest = null;
        using var handler = new CapturingHttpMessageHandler(req => capturedRequest = req);
        var service = CreateServiceWithHandler(handler);

        var result = await service.FindApplicationByDisplayNameAsync("tenant-id", "Agent 365 CLI");

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Contains("ConsistencyLevel").Should().BeTrue(
            because: "Graph advanced query filters (displayName eq) require 'ConsistencyLevel: eventual'");
        capturedRequest.Headers.GetValues("ConsistencyLevel").Should().Contain("eventual",
            because: "the exact value 'eventual' is required by the Graph API spec for advanced queries");
    }

    [Fact]
    public async Task FindApplicationByDisplayNameAsync_EscapesSingleQuotesInDisplayName()
    {
        // OData string literal escaping: ' must be doubled to ''
        // Without this, a name like "O'Brien" would break the filter URL.
        HttpRequestMessage? capturedRequest = null;
        using var handler = new CapturingHttpMessageHandler(req => capturedRequest = req);
        var service = CreateServiceWithHandler(handler);

        await service.FindApplicationByDisplayNameAsync("tenant-id", "O'Brien's App");

        capturedRequest.Should().NotBeNull();
        // The URI may URL-encode spaces (%20) but must preserve '' as the OData escape for '
        var decodedQuery = Uri.UnescapeDataString(capturedRequest!.RequestUri!.Query);
        decodedQuery.Should().Contain("O''Brien''s App",
            because: "OData requires single quotes in string literals to be escaped by doubling: ' → ''");
    }

    private static GraphApiService CreateServiceWithHandler(HttpMessageHandler handler)
    {
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var args = callInfo.ArgAt<string>(1);
                if (args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                if (args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });
        return new GraphApiService(logger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));
    }

    #endregion

    #region IsCurrentUserAgentIdAdminAsync

    private static IAuthenticationService FakeAuth()
    {
        var mock = Substitute.For<IAuthenticationService>();
        mock.GetAccessTokenAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(Task.FromResult("fake-token"));
        return mock;
    }

    private static GraphApiService CreateServiceWithTokenProvider(HttpMessageHandler handler)
    {
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        var tokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
        tokenProvider.GetMgGraphAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("fake-token");
        return new GraphApiService(logger, executor, FakeAuth(), handler, tokenProvider, loginHintResolver: () => Task.FromResult<string?>(null), retryHelper: new RetryHelper(NullLogger.Instance, maxRetries: 1, baseDelaySeconds: 0));
    }

    [Fact]
    public async Task IsCurrentUserAgentIdAdminAsync_UserWithNoRelevantRole_ReturnsDoesNotHaveRole()
    {
        // Arrange — MSAL token contains wids with no Agent ID Administrator GUID
        using var handler = new TestHttpMessageHandler();
        var service = CreateServiceWithWids(handler);  // empty wids

        // Act
        var result = await service.IsCurrentUserAgentIdAdminAsync("tenant-123");

        // Assert
        result.Should().Be(RoleCheckResult.DoesNotHaveRole, "a developer with no admin roles should not pass the Agent ID Administrator check");
    }

    [Fact]
    public async Task IsCurrentUserAgentIdAdminAsync_UserWithAgentIdAdminRole_ReturnsHasRole()
    {
        // Arrange — MSAL token contains wids with Agent ID Administrator template ID
        using var handler = new TestHttpMessageHandler();
        var service = CreateServiceWithWids(handler, "db506228-d27e-4b7d-95e5-295956d6615f");

        // Act
        var result = await service.IsCurrentUserAgentIdAdminAsync("tenant-123");

        // Assert
        result.Should().Be(RoleCheckResult.HasRole, "a user holding the Agent ID Administrator role should pass the check");
    }

    [Fact]
    public async Task IsCurrentUserAgentIdAdminAsync_UserWithGlobalAdminRoleOnly_ReturnsDoesNotHaveRole()
    {
        // Arrange — MSAL token has Global Administrator GUID but not Agent ID Administrator GUID
        using var handler = new TestHttpMessageHandler();
        var service = CreateServiceWithWids(handler, "62e90394-69f5-4237-9190-012177145e10");

        // Act
        var result = await service.IsCurrentUserAgentIdAdminAsync("tenant-123");

        // Assert
        result.Should().Be(RoleCheckResult.DoesNotHaveRole, "Global Administrator alone does not satisfy the Agent ID Administrator role requirement");
    }

    [Fact]
    public async Task IsCurrentUserAgentIdAdminAsync_TokenAcquisitionFails_ReturnsUnknown()
    {
        // Arrange — token acquisition returns null (auth failure). The role check now decodes
        // wids from the access token, so a failed acquisition (rather than a Graph 500) is the
        // realistic failure mode.
        using var handler = new TestHttpMessageHandler();
        var service = CreateServiceWithNullAuth(handler);

        // Act
        var result = await service.IsCurrentUserAgentIdAdminAsync("tenant-123");

        // Assert
        result.Should().Be(RoleCheckResult.Unknown, "a failed token acquisition should return Unknown, not DoesNotHaveRole");
    }

    #endregion

    #region GetCurrentUserObjectIdAsync

    [Fact]
    public async Task GetCurrentUserObjectIdAsync_WhenGraphReturnsId_ReturnsObjectId()
    {
        using var handler = new TestHttpMessageHandler();
        var service = CreateServiceWithTokenProvider(handler);
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"user-obj-id-123\"}")
        });

        var result = await service.GetCurrentUserObjectIdAsync("tenant-123");

        result.Should().Be("user-obj-id-123",
            because: "the object ID is read from the 'id' property of the /me response");
    }

    [Fact]
    public async Task GetCurrentUserObjectIdAsync_WhenGraphFails_ReturnsNull()
    {
        using var handler = new TestHttpMessageHandler();
        var service = CreateServiceWithTokenProvider(handler);
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(string.Empty)
        });

        var result = await service.GetCurrentUserObjectIdAsync("tenant-123");

        result.Should().BeNull(because: "a failed Graph call should return null so the caller can fall back to az CLI");
    }

    #endregion

    #region ServicePrincipalExistsAsync

    [Fact]
    public async Task ServicePrincipalExistsAsync_WhenSpFound_ReturnsTrue()
    {
        using var handler = new TestHttpMessageHandler();
        var service = CreateServiceWithTokenProvider(handler);
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"sp-obj-id\"}")
        });

        var result = await service.ServicePrincipalExistsAsync("tenant-123", "sp-obj-id");

        result.Should().BeTrue(because: "a 200 response means the service principal is visible in the tenant");
    }

    [Fact]
    public async Task ServicePrincipalExistsAsync_WhenSpNotFound_ReturnsFalse()
    {
        // MSI propagation polling: SP is not yet visible immediately after creation.
        using var handler = new TestHttpMessageHandler();
        var service = CreateServiceWithTokenProvider(handler);
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(string.Empty)
        });

        var result = await service.ServicePrincipalExistsAsync("tenant-123", "sp-obj-id");

        result.Should().BeFalse(
            because: "a 404 means the service principal has not yet propagated — the retry loop should keep polling");
    }

    #endregion

    #region CreateCliClientAppAsync

    [Fact]
    public async Task CreateCliClientAppAsync_OnSuccess_ReturnsAppIdAndSpId()
    {
        // Arrange — substitute the virtual GraphPostAsync and EnsureServicePrincipalForAppIdAsync
        // so we don't need to wire up an HTTP handler. ForPartsOf returns a real instance with
        // virtual members substitutable.
        var graph = Substitute.ForPartsOf<GraphApiService>();

        const string expectedAppId = "11111111-2222-3333-4444-555555555555";
        const string expectedSpId = "sp-object-id-123";

        var appResponseJson = JsonDocument.Parse($"{{\"id\":\"app-object-id\",\"appId\":\"{expectedAppId}\"}}");

        graph.GraphPostAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p == "/v1.0/applications"),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<bool>())
            .Returns(Task.FromResult<JsonDocument?>(appResponseJson));

        // Stub GraphPatchAsync — called to add the WAM broker redirect URI after creation.
        // Without this stub the real implementation spawns 'az account get-access-token'.
        graph.GraphPatchAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("/v1.0/applications/")),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult(true));

        graph.EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(),
            Arg.Is<string>(id => id == expectedAppId),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<bool>())
            .Returns(Task.FromResult<string?>(expectedSpId));

        // Act
        var (appId, spId) = await graph.CreateCliClientAppAsync(
            "tenant-123", "Test CLI App", CancellationToken.None);

        // Assert
        appId.Should().Be(expectedAppId,
            because: "the appId returned by Graph POST /v1.0/applications must be surfaced to the caller");
        spId.Should().Be(expectedSpId,
            because: "EnsureServicePrincipalForAppIdAsync result must be returned as the spId");
    }

    [Fact]
    public async Task CreateCliClientAppAsync_WhenPostFails_ReturnsNullNull()
    {
        // Arrange — POST /v1.0/applications returns null (e.g. 4xx/5xx with logged failure)
        var graph = Substitute.ForPartsOf<GraphApiService>();

        graph.GraphPostAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p == "/v1.0/applications"),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<bool>())
            .Returns(Task.FromResult<JsonDocument?>(null));

        // Act
        var (appId, spId) = await graph.CreateCliClientAppAsync(
            "tenant-123", "Test CLI App", CancellationToken.None);

        // Assert
        appId.Should().BeNull(
            because: "a failed app creation must surface (null, null) so the caller does not proceed");
        spId.Should().BeNull(
            because: "spId is meaningless when the app itself was not created");

        // SP creation must NOT be attempted when the app POST failed.
        await graph.DidNotReceive().EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>());
    }

    #endregion
}

// Handler that throws the supplied exception instead of sending a request.
internal class ExceptionThrowingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<Exception> _exceptionFactory;

    public ExceptionThrowingHttpMessageHandler(Func<Exception> exceptionFactory) => _exceptionFactory = exceptionFactory;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw _exceptionFactory();
}

// Simple test handler that returns queued responses sequentially
internal class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public int RequestCount { get; private set; }

    public void QueueResponse(HttpResponseMessage resp) => _responses.Enqueue(resp);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        if (_responses.Count == 0)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });

        var resp = _responses.Dequeue();
        return Task.FromResult(resp);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            while (_responses.Count > 0)
            {
                _responses.Dequeue().Dispose();
            }
        }
        base.Dispose(disposing);
    }
}

// Capturing handler that captures requests AFTER headers are applied
internal class CapturingHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly Action<HttpRequestMessage> _captureAction;

    public CapturingHttpMessageHandler(Action<HttpRequestMessage> captureAction)
    {
        _captureAction = captureAction;
    }

    public void QueueResponse(HttpResponseMessage resp) => _responses.Enqueue(resp);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Important: Capture AFTER HttpClient has applied DefaultRequestHeaders
        // At this point, request.Headers contains both request-specific and default headers
        _captureAction(request);

        if (_responses.Count == 0)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });

        var resp = _responses.Dequeue();
        return Task.FromResult(resp);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            while (_responses.Count > 0)
            {
                _responses.Dequeue().Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
