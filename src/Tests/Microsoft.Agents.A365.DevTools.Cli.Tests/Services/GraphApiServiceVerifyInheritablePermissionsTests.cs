// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class AgentBlueprintServiceVerifyInheritablePermissionsTests
{
    [Fact]
    public async Task VerifyInheritablePermissionsAsync_PermissionsExist_ReturnsScopes()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var graphLogger = Substitute.For<ILogger<GraphApiService>>();
        var blueprintLogger = Substitute.For<ILogger<AgentBlueprintService>>();
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

        var graphService = new GraphApiService(graphLogger, executor, handler);
        var service = new AgentBlueprintService(blueprintLogger, graphService);

        var response = new
        {
            value = new[]
            {
                new
                {
                    resourceAppId = "resource-123",
                    inheritableScopes = new
                    {
                        scopes = new[] { "scope1 scope2", "scope3" }
                    }
                }
            }
        };

        // ResolveBlueprintObjectIdAsync: Check if bpAppId is an objectId (returns 404 NotFound)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));

        // ResolveBlueprintObjectIdAsync: Resolve appId to objectId
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = "resolved-object-id" } } }))
        });

        // VerifyInheritablePermissionsAsync: GET existing permissions
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response))
        });

        // Act
        var (exists, scopes, error) = await service.VerifyInheritablePermissionsAsync("tid", "bpAppId", "resource-123");

        // Assert
        exists.Should().BeTrue();
        scopes.Should().BeEquivalentTo(new[] { "scope1", "scope2", "scope3" });
        error.Should().BeNull();
    }

    [Fact]
    public async Task VerifyInheritablePermissionsAsync_PermissionsNotFound_ReturnsFalse()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var graphLogger = Substitute.For<ILogger<GraphApiService>>();
        var blueprintLogger = Substitute.For<ILogger<AgentBlueprintService>>();
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

        var graphService = new GraphApiService(graphLogger, executor, handler);
        var service = new AgentBlueprintService(blueprintLogger, graphService);

        var response = new
        {
            value = new[]
            {
                new
                {
                    resourceAppId = "different-resource",
                    inheritableScopes = new { scopes = new[] { "scope1" } }
                }
            }
        };

        // ResolveBlueprintObjectIdAsync: Check if bpAppId is an objectId (returns 404 NotFound)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));

        // ResolveBlueprintObjectIdAsync: Resolve appId to objectId
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = "resolved-object-id" } } }))
        });

        // VerifyInheritablePermissionsAsync: GET existing permissions
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response))
        });

        // Act
        var (exists, scopes, error) = await service.VerifyInheritablePermissionsAsync("tid", "bpAppId", "resource-123");

        // Assert
        exists.Should().BeFalse();
        scopes.Should().BeEmpty();
        error.Should().BeNull();
    }

    [Fact]
    public async Task VerifyInheritablePermissionsAsync_ApiFailure_ReturnsError()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var graphLogger = Substitute.For<ILogger<GraphApiService>>();
        var blueprintLogger = Substitute.For<ILogger<AgentBlueprintService>>();
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

        var graphService = new GraphApiService(graphLogger, executor, handler);
        var service = new AgentBlueprintService(blueprintLogger, graphService);

        // Simulate 404 Not Found to trigger API failure path
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));

        // Act
        var (exists, scopes, error) = await service.VerifyInheritablePermissionsAsync("tid", "bpAppId", "resource-123");

        // Assert
        exists.Should().BeFalse();
        scopes.Should().BeEmpty();
        error.Should().Be("Failed to retrieve inheritable permissions");
    }
}
