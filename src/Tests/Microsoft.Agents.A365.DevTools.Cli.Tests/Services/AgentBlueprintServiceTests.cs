// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class AgentBlueprintServiceTests
{
    private readonly ILogger<AgentBlueprintService> _mockLogger;
    private readonly ILogger<GraphApiService> _mockGraphLogger;
    private readonly CommandExecutor _mockExecutor;
    private readonly IMicrosoftGraphTokenProvider _mockTokenProvider;

    public AgentBlueprintServiceTests()
    {
        _mockLogger = Substitute.For<ILogger<AgentBlueprintService>>();
        _mockGraphLogger = Substitute.For<ILogger<GraphApiService>>();
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);
        _mockTokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
    }

    [Fact]
    public async Task SetInheritablePermissionsAsync_Creates_WhenMissing()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
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

        var graphService = new GraphApiService(_mockGraphLogger, executor, handler);
        var service = new AgentBlueprintService(_mockLogger, graphService);

        // ResolveBlueprintObjectIdAsync: First GET to check if blueprintAppId is objectId (returns 404 NotFound)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));

        // ResolveBlueprintObjectIdAsync: Second GET to resolve appId -> objectId
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = "resolved-object-id" } } }))
        });

        // SetInheritablePermissionsAsync: GET existing permissions (returns empty list = not found)
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

        var graphService = new GraphApiService(_mockGraphLogger, executor, handler);
        var service = new AgentBlueprintService(_mockLogger, graphService);

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

        // ResolveBlueprintObjectIdAsync: Check if bpAppId is an objectId (returns 404 NotFound)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));

        // ResolveBlueprintObjectIdAsync: Resolve appId to objectId
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = "resolved-object-id" } } }))
        });

        // SetInheritablePermissionsAsync: GET existing permissions
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
    public async Task DeleteAgentIdentityAsync_WithValidIdentity_ReturnsTrue()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            const string tenantId = "12345678-1234-1234-1234-123456789012";
            const string identityId = "identity-sp-id-123";

            // Override with specific scope assertion
            _mockTokenProvider.GetMgGraphAccessTokenAsync(
                tenantId,
                Arg.Is<IEnumerable<string>>(scopes => scopes.Contains("AgentIdentityBlueprint.ReadWrite.All")),
                false,
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
                .Returns("fake-delegated-token");

            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));

            // Act
            var result = await service.DeleteAgentIdentityAsync(tenantId, identityId);

            // Assert
            result.Should().BeTrue();

            await _mockTokenProvider.Received(1).GetMgGraphAccessTokenAsync(
                tenantId,
                Arg.Is<IEnumerable<string>>(scopes => scopes.Contains("AgentIdentityBlueprint.ReadWrite.All")),
                false,
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task DeleteAgentIdentityAsync_WhenResourceNotFound_ReturnsTrueIdempotent()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            const string tenantId = "12345678-1234-1234-1234-123456789012";
            const string identityId = "non-existent-identity";

            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\": {\"code\": \"Request_ResourceNotFound\"}}")
            });

            // Act
            var result = await service.DeleteAgentIdentityAsync(tenantId, identityId);

            // Assert
            result.Should().BeTrue("404 should be treated as success for idempotent deletion");
        }
    }

    [Fact]
    public async Task DeleteAgentIdentityAsync_WhenTokenProviderIsNull_ReturnsFalse()
    {
        // Arrange
        using var handler = new FakeHttpMessageHandler();
        var graphService = new GraphApiService(_mockGraphLogger, _mockExecutor, handler, tokenProvider: null);
        var service = new AgentBlueprintService(_mockLogger, graphService);

        const string tenantId = "12345678-1234-1234-1234-123456789012";
        const string identityId = "identity-123";

        // Act
        var result = await service.DeleteAgentIdentityAsync(tenantId, identityId);

        // Assert
        result.Should().BeFalse();

        _mockGraphLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Token provider is not configured")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task DeleteAgentIdentityAsync_WhenDeletionFails_ReturnsFalse()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            const string tenantId = "12345678-1234-1234-1234-123456789012";
            const string identityId = "identity-123";

            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"error\": {\"code\": \"Authorization_RequestDenied\"}}")
            });

            // Act
            var result = await service.DeleteAgentIdentityAsync(tenantId, identityId);

            // Assert
            result.Should().BeFalse();

            _mockGraphLogger.Received().Log(
                LogLevel.Error,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Graph DELETE") && o.ToString()!.Contains("403")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
    }

    [Fact]
    public async Task DeleteAgentIdentityAsync_WhenExceptionThrown_ReturnsFalse()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            const string tenantId = "12345678-1234-1234-1234-123456789012";
            const string identityId = "identity-123";

            // Override token provider to throw
            _mockTokenProvider.GetMgGraphAccessTokenAsync(
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<bool>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
                .Returns(Task.FromException<string?>(new HttpRequestException("Connection timeout")));

            // Act
            var result = await service.DeleteAgentIdentityAsync(tenantId, identityId);

            // Assert
            result.Should().BeFalse();

            _mockLogger.Received().Log(
                LogLevel.Error,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Exception deleting agent identity")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
    }

    [Fact]
    public async Task GetAgentInstancesForBlueprintAsync_ReturnsFilteredInstances()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            const string blueprintId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

            // Response 1: GET /beta/servicePrincipals/microsoft.graph.agentIdentity?$filter=agentIdentityBlueprintId eq '...'
            // Server-side filtered response returns only matching SPs
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    value = new[]
                    {
                        new { id = "sp-obj-1", displayName = "Instance A", agentIdentityBlueprintId = blueprintId }
                    }
                }))
            });

            // Response 2: GET /beta/users/microsoft.graph.agentUser?$filter=agentIdentityBlueprintId eq '...'
            // Bulk query returns all agent users for the blueprint; correlated via identityParentId
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    value = new[] { new { id = "user-obj-1", identityParentId = "sp-obj-1" } }
                }))
            });

            // Act
            var instances = await service.GetAgentInstancesForBlueprintAsync("tenant-id", blueprintId);

            // Assert
            instances.Should().HaveCount(1);
            instances[0].IdentitySpId.Should().Be("sp-obj-1");
            instances[0].DisplayName.Should().Be("Instance A");
            instances[0].AgentUserId.Should().Be("user-obj-1");
        }
    }

    [Fact]
    public async Task GetAgentInstancesForBlueprintAsync_ReturnsEmpty_WhenNoneFound()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            // Response 1: SPs query returns empty
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { value = Array.Empty<object>() }))
            });

            // Response 2: Users query returns empty (both run in parallel)
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { value = Array.Empty<object>() }))
            });

            // Act
            var instances = await service.GetAgentInstancesForBlueprintAsync("tenant-id", "b2c3d4e5-f6a7-8901-bcde-f12345678901");

            // Assert
            instances.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task GetAgentInstancesForBlueprintAsync_Throws_WhenGraphQueryFails()
    {
        // Arrange
        var (service, _) = CreateServiceWithFakeHandler();

        // Override token provider to throw so the Graph call fails
        _mockTokenProvider.GetMgGraphAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new HttpRequestException("Connection timeout")));

        // Act & Assert - exception must propagate so callers can abort rather than proceeding with 0 instances
        await service.Invoking(s => s.GetAgentInstancesForBlueprintAsync("tenant-id", "blueprint-id"))
            .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task DeleteAgentUserAsync_ReturnsTrue_OnSuccess()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            // Queue HTTP response for DELETE /beta/agentUsers/{userId}
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));

            // Act
            var result = await service.DeleteAgentUserAsync("tenant-id", "user-obj-1");

            // Assert
            result.Should().BeTrue();
        }
    }

    [Fact]
    public async Task DeleteAgentUserAsync_ReturnsFalse_OnGraphError()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            // Override token provider to throw
            _mockTokenProvider.GetMgGraphAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<string?>(new HttpRequestException("Connection timeout")));

            // Act
            var result = await service.DeleteAgentUserAsync("tenant-id", "user-obj-1");

            // Assert
            result.Should().BeFalse();
        }
    }

    private (AgentBlueprintService service, FakeHttpMessageHandler handler) CreateServiceWithFakeHandler()
    {
        var handler = new FakeHttpMessageHandler();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult
                { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty }));
        _mockTokenProvider.GetMgGraphAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("test-token");
        var graphService = new GraphApiService(_mockGraphLogger, executor, handler, _mockTokenProvider);
        return (new AgentBlueprintService(_mockLogger, graphService), handler);
    }
}

// Simple fake handler that returns queued responses sequentially
internal class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly List<HttpResponseMessage> _sentResponses = new();

    public void QueueResponse(HttpResponseMessage resp) => _responses.Enqueue(resp);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_responses.Count == 0)
        {
            var fallback = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
            _sentResponses.Add(fallback);
            return Task.FromResult(fallback);
        }

        var resp = _responses.Dequeue();
        _sentResponses.Add(resp);
        return Task.FromResult(resp);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var resp in _sentResponses)
                resp.Dispose();
            _sentResponses.Clear();

            while (_responses.Count > 0)
                _responses.Dequeue().Dispose();
        }
        base.Dispose(disposing);
    }
}
