// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Net;
using System.Net.Http.Headers;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class Agent365ToolingServiceEnableVnetTests
{
    [Fact]
    public async Task EnableVnetAsync_Success_SendsExpectedRequest()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK);
        var (service, authService) = CreateService(handler, "access-token");

        var result = await service.EnableVnetAsync();

        result.Should().BeTrue(
            because: "a successful MCP Platform response must be reported as enabled");
        handler.Method.Should().Be(
            HttpMethod.Post,
            because: "the MCP Platform VNet enable contract is a POST operation");
        handler.RequestUri.Should().Be(
            "https://agent365.svc.cloud.microsoft/agents/vnet/enable",
            because: "the command must target the MCP Platform VNet enable route without route parameters");
        handler.Body.Should().BeNull(
            because: "the VNet enable endpoint does not define a request payload");
        handler.Authorization.Should().Be(
            new AuthenticationHeaderValue("Bearer", "access-token"),
            because: "MCP Platform management calls require the acquired bearer token");
        await authService.Received(1).GetAccessTokenAsync(
            Arg.Any<string>(),
            null,
            false,
            null,
            null,
            true,
            "developer@contoso.com",
            Arg.Any<CancellationToken>());
        authService.ReceivedCalls()
            .Single()
            .GetArguments()[0]
            .Should().Be(
                McpConstants.WorkIQToolsProdAppId,
                because: "MCP Platform management calls require the Agent365 tooling OAuth audience");
    }

    [Fact]
    public async Task EnableVnetAsync_AuthorizationError_ReturnsFalse()
    {
        using var handler = new RecordingHandler(
            HttpStatusCode.Forbidden,
            """{"error":{"message":"Insufficient privileges"}}""");
        var (service, _) = CreateService(handler, "access-token");

        var result = await service.EnableVnetAsync();

        result.Should().BeFalse(
            because: "authorization failures from MCP Platform must produce a failed CLI operation");
        handler.SendCount.Should().Be(
            1,
            because: "the configured authorization error response must be exercised");
    }

    [Fact]
    public async Task EnableVnetAsync_ServiceError_ReturnsFalse()
    {
        using var handler = new RecordingHandler(
            HttpStatusCode.InternalServerError,
            """{"error":{"message":"Service unavailable"}}""");
        var (service, _) = CreateService(handler, "access-token");

        var result = await service.EnableVnetAsync();

        result.Should().BeFalse(
            because: "non-success service responses must not be reported as an enabled VNet");
        handler.SendCount.Should().Be(
            1,
            because: "the configured service error response must be exercised");
    }

    [Fact]
    public async Task EnableVnetAsync_AuthenticationFails_DoesNotSendRequest()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK);
        var (service, _) = CreateService(handler, string.Empty);

        var result = await service.EnableVnetAsync();

        result.Should().BeFalse(
            because: "authentication failure must prevent the operation from succeeding");
        handler.SendCount.Should().Be(
            0,
            because: "the MCP Platform request must not be sent without an access token");
    }

    [Fact]
    public async Task EnableVnetAsync_ReusedInjectedHandler_RemainsUsable()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK);
        var (service, _) = CreateService(handler, "access-token");

        var firstResult = await service.EnableVnetAsync();
        var secondResult = await service.EnableVnetAsync();

        firstResult.Should().BeTrue(
            because: "the initial request should succeed before handler reuse is verified");
        secondResult.Should().BeTrue(
            because: "the service must not dispose a caller-owned HTTP message handler");
        handler.SendCount.Should().Be(
            2,
            because: "the injected handler should remain available for subsequent operations");
    }

    [Fact]
    public async Task EnableVnetAsync_LoginHintResolutionStalls_PropagatesCancellation()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK);
        using var cancellationSource = new CancellationTokenSource();
        var loginHintCompletion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var (service, _) = CreateService(
            handler,
            "access-token",
            () => loginHintCompletion.Task);

        var enableVnetTask = service.EnableVnetAsync(cancellationSource.Token);
        await cancellationSource.CancelAsync();
        var action = () => enableVnetTask;

        await action.Should().ThrowAsync<OperationCanceledException>(
            because: "caller cancellation must interrupt stalled login-hint resolution");
        handler.SendCount.Should().Be(
            0,
            because: "a canceled operation must not send the MCP Platform request");
    }

    [Fact]
    public async Task EnableVnetAsync_HttpRequestIsCanceled_ForwardsTokenAndPropagatesCancellation()
    {
        using var handler = new BlockingHandler();
        using var cancellationSource = new CancellationTokenSource();
        var (service, authService) = CreateService(handler, "access-token");

        var enableVnetTask = service.EnableVnetAsync(cancellationSource.Token);
        await handler.RequestStarted.Task;
        await cancellationSource.CancelAsync();
        var action = () => enableVnetTask;

        await action.Should().ThrowAsync<OperationCanceledException>(
            because: "caller cancellation during HTTP transmission must not be converted to a service failure");
        handler.CancellationToken.CanBeCanceled.Should().BeTrue(
            because: "the HTTP request must remain cancelable by its caller");
        handler.CancellationToken.IsCancellationRequested.Should().BeTrue(
            because: "canceling the caller token must cancel the in-flight HTTP request");
        await authService.Received(1).GetAccessTokenAsync(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            cancellationSource.Token);
    }

    [Fact]
    public async Task EnableVnetAsync_AuthenticationThrows_ReturnsFalseWithoutSendingRequest()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK);
        var authService = Substitute.For<IAuthenticationService>();
        var logger = Substitute.For<ILogger<Agent365ToolingService>>();
        authService.GetAccessTokenAsync(
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<string?>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<bool>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(
                new InvalidOperationException("Authentication unavailable")));
        var service = new Agent365ToolingService(
            Substitute.For<IConfigService>(),
            authService,
            logger,
            "vnet-wire-test",
            handler,
            () => Task.FromResult<string?>("developer@contoso.com"));

        var result = await service.EnableVnetAsync();

        result.Should().BeFalse(
            because: "authentication exceptions must produce a failed CLI operation");
        handler.SendCount.Should().Be(
            0,
            because: "the MCP Platform request must not be sent when authentication throws");
        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state =>
                state != null &&
                state.ToString()!.Contains("Failed to enable virtual network support")),
            Arg.Is<Exception?>(exception => exception is InvalidOperationException),
            Arg.Any<Func<object, Exception?, string>>());
    }

    private static (Agent365ToolingService Service, IAuthenticationService AuthService) CreateService(
        HttpMessageHandler handler,
        string authToken,
        Func<Task<string?>>? loginHintResolver = null)
    {
        var authService = Substitute.For<IAuthenticationService>();
        authService.GetAccessTokenAsync(
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<string?>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<bool>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(authToken);

        var service = new Agent365ToolingService(
            Substitute.For<IConfigService>(),
            authService,
            Substitute.For<ILogger<Agent365ToolingService>>(),
            "vnet-wire-test",
            handler,
            loginHintResolver ?? (() => Task.FromResult<string?>("developer@contoso.com")));

        return (service, authService);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;
        private bool _disposed;

        public RecordingHandler(HttpStatusCode statusCode, string responseBody = "")
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        public HttpMethod? Method { get; private set; }

        public string? RequestUri { get; private set; }

        public string? Body { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        public int SendCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            SendCount++;
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization;

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody),
            };
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = disposing;
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken CancellationToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            RequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
