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

        var service = new GraphApiService(logger, executor, handler);

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

        var service = new GraphApiService(logger, executor, handler);

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
            Arg.Any<CancellationToken>())
            .Returns("token-from-provider\r\nwith-embedded-newlines\n");

        var service = new GraphApiService(logger, executor, handler, tokenProvider);

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

    [Fact]
    public async Task CheckServicePrincipalCreationPrivilegesAsync_SanitizesTokenWithNewlines()
    {
        // This test verifies that CheckServicePrincipalCreationPrivilegesAsync also
        // sanitizes tokens with newlines. This method has its own token handling code
        // separate from EnsureGraphHeadersAsync.

        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHttpMessageHandler((req) => capturedRequest = req);
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        // Mock az CLI to return a token WITH newline characters
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
                    // Return token WITH embedded newlines
                    return Task.FromResult(new CommandResult
                    {
                        ExitCode = 0,
                        StandardOutput = "privileges-check-token\r\n\n",
                        StandardError = string.Empty
                    });
                }

                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var service = new GraphApiService(logger, executor, handler);

        // Queue a successful response for the directory roles query
        using var queuedResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"value\":[{\"displayName\":\"Application Administrator\"}]}")
        };
        handler.QueueResponse(queuedResponse);

        // Act - This should NOT throw FormatException
        var (hasPrivileges, roles) = await service.CheckServicePrincipalCreationPrivilegesAsync("tenant-123");

        // Assert
        capturedRequest.Should().NotBeNull("HTTP request should have been sent");
        capturedRequest!.Headers.Authorization.Should().NotBeNull("Authorization header should be set");

        var actualToken = capturedRequest.Headers.Authorization!.Parameter;
        actualToken.Should().NotBeNull();
        actualToken.Should().NotContain("\r", "Token should not contain carriage return characters");
        actualToken.Should().NotContain("\n", "Token should not contain newline characters");
        actualToken.Should().Be("privileges-check-token", "Token should be sanitized to just the token value");

        // Also verify the method returns correct results
        hasPrivileges.Should().BeTrue("User has Application Administrator role");
        roles.Should().Contain("Application Administrator");
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

