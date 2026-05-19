// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class AgentBlueprintServiceVerifyInheritablePermissionsTests
{
    private static IAuthenticationService FakeAuth()
    {
        var mock = Substitute.For<IAuthenticationService>();
        mock.GetAccessTokenAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(Task.FromResult("fake-token"));
        return mock;
    }

    [Fact]
    public async Task VerifyInheritablePermissionsAsync_PermissionsAllAllowed_ReturnsBothKindsTrue()
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

        var graphService = new GraphApiService(graphLogger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));
        var service = new AgentBlueprintService(blueprintLogger, graphService);

        var response = new
        {
            value = new[]
            {
                new
                {
                    resourceAppId = "resource-123",
                    inheritableScopes = new { kind = "allAllowed" },
                    inheritableRoles = new { kind = "allAllowed" }
                }
            }
        };

        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = "resolved-object-id" } } }))
        });
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response))
        });

        // Act
        var (exists, scopesAllAllowed, rolesAllAllowed, error) = await service.VerifyInheritablePermissionsAsync("tid", "bpAppId", "resource-123");

        // Assert
        exists.Should().BeTrue();
        scopesAllAllowed.Should().BeTrue(because: "the response declares scopes.kind = allAllowed");
        rolesAllAllowed.Should().BeTrue(because: "the response declares roles.kind = allAllowed");
        error.Should().BeNull();
    }

    [Fact]
    public async Task VerifyInheritablePermissionsAsync_LegacyEnumeratedEntry_ReturnsExistsTrueButKindsFalse()
    {
        // Arrange — an entry written by an older CLI version (enumerated scopes, no roles).
        // Verification must report the entry exists but neither kind is allAllowed yet, so the
        // caller knows a reconciliation PATCH is needed.
        var handler = new FakeHttpMessageHandler();
        var graphLogger = Substitute.For<ILogger<GraphApiService>>();
        var blueprintLogger = Substitute.For<ILogger<AgentBlueprintService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var args = callInfo.ArgAt<string>(1);
                if (args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
            });

        var graphService = new GraphApiService(graphLogger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));
        var service = new AgentBlueprintService(blueprintLogger, graphService);

        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = "resolved-object-id" } } }))
        });
        var legacy = new
        {
            value = new[]
            {
                new
                {
                    resourceAppId = "resource-123",
                    inheritableScopes = new { scopes = new[] { "scope1" } }
                }
            }
        };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(legacy))
        });

        // Act
        var (exists, scopesAllAllowed, rolesAllAllowed, error) = await service.VerifyInheritablePermissionsAsync("tid", "bpAppId", "resource-123");

        // Assert
        exists.Should().BeTrue();
        scopesAllAllowed.Should().BeFalse(because: "legacy enumerated entries lack kind=allAllowed");
        rolesAllAllowed.Should().BeFalse(because: "legacy entries don't have inheritableRoles at all");
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

        var graphService = new GraphApiService(graphLogger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));
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
        var (exists, scopesAllAllowed, rolesAllAllowed, error) = await service.VerifyInheritablePermissionsAsync("tid", "bpAppId", "resource-123");

        // Assert
        exists.Should().BeFalse();
        scopesAllAllowed.Should().BeFalse();
        rolesAllAllowed.Should().BeFalse();
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

        var graphService = new GraphApiService(graphLogger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));
        var service = new AgentBlueprintService(blueprintLogger, graphService);

        // Simulate 404 Not Found to trigger API failure path
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));

        // Act
        var (exists, scopesAllAllowed, rolesAllAllowed, error) = await service.VerifyInheritablePermissionsAsync("tid", "bpAppId", "resource-123");

        // Assert
        exists.Should().BeFalse();
        scopesAllAllowed.Should().BeFalse();
        rolesAllAllowed.Should().BeFalse();
        error.Should().Be("Failed to retrieve inheritable permissions");
    }
}
