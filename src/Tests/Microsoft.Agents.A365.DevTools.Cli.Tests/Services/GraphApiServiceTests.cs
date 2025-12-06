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
    [Fact]
    public async Task SetInheritablePermissionsAsync_Creates_WhenMissing()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        // Mock az CLI token acquisition flows used by EnsureGraphHeadersAsync
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);

                // Simulate az account show
                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                }

                // Simulate az account get-access-token -> return token
                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                }

                // Default: success
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var service = new GraphApiService(logger, executor, handler);

        // Simulate GET returning empty list
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = Array.Empty<object>() }))
        });

        // Simulate POST success
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { id = "created" }))
        });

        // Act
        var (ok, already, err) = await service.SetInheritablePermissionsAsync("tid", "bpAppId", "resAppId", new[] { "scope1", "scope2" });

        // Assert
        ok.Should().BeTrue();
        already.Should().BeFalse();
        err.Should().BeNull();
    }

    [Fact]
    public async Task SetInheritablePermissionsAsync_Patches_WhenPresent()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        // Mock az CLI token acquisition flows used by EnsureGraphHeadersAsync
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);

                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                }

                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                }

                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var service = new GraphApiService(logger, executor, handler);

        // Existing entry with one scope
        var existing = new
        {
            value = new[]
            {
                new
                {
                    resourceAppId = "resAppId",
                    inheritableScopes = new { scopes = new[] { "scope1" } }
                }
            }
        };

        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(existing))
        });

        // PATCH returns 204 NoContent
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));

        // Act
        var (ok, already, err) = await service.SetInheritablePermissionsAsync("tid", "bpAppId", "resAppId", new[] { "scope2" });

        // Assert
        ok.Should().BeTrue();
        already.Should().BeFalse();
        err.Should().BeNull();
    }

    [Fact]
    public async Task GraphPostWithResponseAsync_Returns_Success_And_ParsesJson()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
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
        var handler = new FakeHttpMessageHandler();
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
}

// Simple fake handler that returns queued responses sequentially
internal class FakeHttpMessageHandler : HttpMessageHandler
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
