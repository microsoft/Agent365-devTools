// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
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

        var service = new GraphApiService(logger, executor, handler);

        // Queue successful POST with JSON body
        var bodyObj = new { result = "ok" };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(bodyObj))
        });

        // Act
        var resp = await service.GraphPostWithResponseAsync(
            "tid",
            "/v1.0/some/path",
            new { a = 1 },
            CancellationToken.None,
            scopes: null);

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

        var service = new GraphApiService(logger, executor, handler);

        // Queue failing POST with JSON error body
        var errorBody = new { error = new { code = "Authorization_RequestDenied", message = "Insufficient privileges" } };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(JsonSerializer.Serialize(errorBody))
        });

        // Act
        var resp = await service.GraphPostWithResponseAsync(
            "tid",
            "/v1.0/some/path",
            new { a = 1 },
            CancellationToken.None,
            scopes: null);

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
        // The ConsistencyLevel: eventual header is only required for advanced Graph queries and causes
        // HTTP 400 "One or more headers are invalid" errors for simple $filter queries.
        // Regression test for issue discovered on 2025-12-19.
        //
        // NOTE: This test covers BOTH bug locations:
        // 1. ExecutePublishGraphStepsAsync (line 211) - where header was incorrectly set after token acquisition
        // 2. EnsureGraphHeadersAsync (lines 745-746) - where header was incorrectly set before all Graph API calls
        //
        // The bug in ExecutePublishGraphStepsAsync was "defensive" - it set the header on the HttpClient, but
        // EnsureGraphHeadersAsync would have overwritten it anyway. By testing EnsureGraphHeadersAsync (which is
        // called by ALL Graph API operations), we ensure the header is never sent regardless of whether
        // ExecutePublishGraphStepsAsync tries to set it.

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
        var service = new GraphApiService(logger, executor, handler);

        // Queue response for service principal lookup
        var spResponse = new { value = new[] { new { id = "sp-object-id-123", appId = "blueprint-456" } } };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(spResponse))
        });

        // Act - Call a public method that internally uses LookupServicePrincipalAsync
        var result = await service.LookupServicePrincipalByAppIdAsync("tenant-123", "blueprint-456");

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
            "This header is only needed for advanced Graph query capabilities and causes HTTP 400 errors otherwise.");
    }
}

// Simple test handler that returns queued responses sequentially
internal class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public void QueueResponse(HttpResponseMessage resp) => _responses.Enqueue(resp);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_responses.Count == 0)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });

        var resp = _responses.Dequeue();
        return Task.FromResult(resp);
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
}

public class GraphApiServiceScopeTests
{
    [Fact]
    public async Task GraphGetAsync_WhenScopesProvidedButTokenProviderNull_FallsBackToAzureCli()
    {
        // Arrange
        var handler = new TestHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        // Mock Azure CLI to return token
        executor.ExecuteAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);

                if (cmd == "az" && args?.StartsWith("account show", StringComparison.OrdinalIgnoreCase) == true)
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });

                if (cmd == "az" && args?.Contains("get-access-token", StringComparison.OrdinalIgnoreCase) == true)
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "azure-cli-token", StandardError = string.Empty });

                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        // Create GraphApiService WITHOUT token provider (null)
        var service = new GraphApiService(logger, executor, handler, tokenProvider: null);

        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = Array.Empty<object>() }))
        });

        // Act - Call with scopes even though token provider is null
        var scopes = new[] { "User.Read", "Mail.Read" };
        var result = await service.GraphGetAsync("tenant-123", "/v1.0/users", CancellationToken.None, scopes);

        // Assert
        result.Should().NotBeNull("should successfully fall back to Azure CLI authentication");

        // Verify warning was logged about falling back to Azure CLI
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Token provider is not configured")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Verify Azure CLI was called to get token
        await executor.Received().ExecuteAsync(
            "az",
            Arg.Is<string>(args => args.Contains("get-access-token")),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GraphGetAsync_WhenScopesAndTokenProviderProvided_UsesDeviceCodeFlow()
    {
        // Arrange
        var handler = new TestHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        var tokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();

        var scopes = new[] { "User.Read", "Mail.Read" };

        // Mock token provider to return delegated token with device code
        tokenProvider.GetMgGraphAccessTokenAsync(
            "tenant-123",
            scopes,
            true,  // Must use device code = true
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns("delegated-token-with-device-code");

        var service = new GraphApiService(logger, executor, handler, tokenProvider);

        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = Array.Empty<object>() }))
        });

        // Act
        var result = await service.GraphGetAsync("tenant-123", "/v1.0/users", CancellationToken.None, scopes);

        // Assert
        result.Should().NotBeNull();

        // Verify device code flow was used (useDeviceCode: true, not false)
        await tokenProvider.Received(1).GetMgGraphAccessTokenAsync(
            "tenant-123",
            scopes,
            true,  // CRITICAL: Must be true for device code enforcement
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        // Verify NO warning was logged (token provider is available)
        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Token provider is not configured")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}

