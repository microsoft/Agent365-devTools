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

/// <summary>
/// Tests for GraphApiService.RegisterAgentInstanceAsync.
/// Verifies correct API call shape, success path, and error handling.
/// </summary>
public class GraphApiServiceRegisterAgentInstanceTests
{
    private static CommandExecutor BuildMockExecutor()
    {
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        executor.ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var args = callInfo.ArgAt<string>(1);
                if (args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                if (args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });
        return executor;
    }

    private static GraphApiService BuildService(HttpMessageHandler handler)
    {
        var authService = Substitute.For<IAuthenticationService>();
        authService.GetAccessTokenAsync(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<bool>(),
            Arg.Any<string?>())
            .Returns(Task.FromResult("fake-graph-token"));
        return new GraphApiService(
            Substitute.For<ILogger<GraphApiService>>(),
            BuildMockExecutor(),
            authService,
            handler,
            tokenProvider: null,
            loginHintResolver: () => Task.FromResult<string?>(null),
            agentRegistryRetryDelay: TimeSpan.Zero);
    }

    [Fact]
    public async Task RegisterAgentInstanceAsync_ReturnsInstanceId_OnSuccess()
    {
        using var handler = new TestHttpMessageHandler();

        // GET /v1.0/me response
        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "user-object-id" }))
        });

        // POST /beta/agentRegistry/agentInstances response
        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "instance-id-123" }))
        });

        var service = BuildService(handler);

        var result = await service.RegisterAgentInstanceAsync(
            tenantId: "tenant-id",
            displayName: "My Agent",
            agentBlueprintId: "blueprint-id");

        result.Should().Be("instance-id-123");
    }

    [Fact]
    public async Task RegisterAgentInstanceAsync_ReturnsNull_WhenMeCallFails()
    {
        using var handler = new TestHttpMessageHandler();

        // GET /v1.0/me fails
        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new System.Net.Http.StringContent("{\"error\":{\"message\":\"Unauthorized\"}}")
        });

        var service = BuildService(handler);

        var result = await service.RegisterAgentInstanceAsync(
            tenantId: "tenant-id",
            displayName: "My Agent",
            agentBlueprintId: null);

        result.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAgentInstanceAsync_ReturnsNull_WhenPostFails()
    {
        using var handler = new TestHttpMessageHandler();

        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "user-object-id" }))
        });

        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new System.Net.Http.StringContent("{\"error\":{\"message\":\"Forbidden\"}}")
        });

        var service = BuildService(handler);

        var result = await service.RegisterAgentInstanceAsync(
            tenantId: "tenant-id",
            displayName: "My Agent",
            agentBlueprintId: "blueprint-id");

        result.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAgentInstanceAsync_IncludesBlueprintId_InPayload()
    {
        string? capturedBody = null;

        using var handler = new CapturingHttpMessageHandler(req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Post)
                capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        });

        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "user-object-id" }))
        });

        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "instance-id-abc" }))
        });

        var service = BuildService(handler);

        await service.RegisterAgentInstanceAsync(
            tenantId: "tenant-id",
            displayName: "My Agent",
            agentBlueprintId: "bp-id-xyz");

        capturedBody.Should().Contain("bp-id-xyz");
        capturedBody.Should().Contain("agentIdentityBlueprintId");
    }

    [Fact]
    public async Task RegisterAgentInstanceAsync_OmitsBlueprintId_WhenNull()
    {
        string? capturedBody = null;

        using var handler = new CapturingHttpMessageHandler(req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Post)
                capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        });

        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "user-object-id" }))
        });

        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "instance-id-abc" }))
        });

        var service = BuildService(handler);

        await service.RegisterAgentInstanceAsync(
            tenantId: "tenant-id",
            displayName: "My Agent",
            agentBlueprintId: null);

        capturedBody.Should().NotContain("agentIdentityBlueprintId");
    }

    [Fact]
    public async Task RegisterAgentInstanceAsync_IncludesDisplayName_InPayload()
    {
        string? capturedBody = null;

        using var handler = new CapturingHttpMessageHandler(req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Post)
                capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        });

        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "user-object-id" }))
        });

        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "instance-id-abc" }))
        });

        var service = BuildService(handler);

        await service.RegisterAgentInstanceAsync(
            tenantId: "tenant-id",
            displayName: "Contoso Agent",
            agentBlueprintId: null);

        capturedBody.Should().Contain("Contoso Agent");
        capturedBody.Should().Contain("displayName");
    }

    [Fact]
    public async Task RegisterAgentInstanceAsync_PostsToCorrectEndpoint()
    {
        System.Net.Http.HttpRequestMessage? capturedRequest = null;

        using var handler = new CapturingHttpMessageHandler(req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Post)
                capturedRequest = req;
        });

        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "user-object-id" }))
        });

        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "instance-id-abc" }))
        });

        var service = BuildService(handler);

        await service.RegisterAgentInstanceAsync("tenant-id", "My Agent", null);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri!.ToString()
            .Should().Contain("/beta/agentRegistry/agentInstances");
    }

    /// <summary>
    /// 409 Conflict means sourceAgentId already exists. When the body contains an "id",
    /// RegisterAgentInstanceAsyncV2 must return that ID (idempotent re-run).
    /// </summary>
    [Fact]
    public async Task RegisterAgentInstanceAsyncV2_ReturnsExistingId_On409Conflict()
    {
        using var handler = new TestHttpMessageHandler();

        // GET /v1.0/me — needed to resolve current user ID
        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "user-id" }))
        });

        // POST returns 409 with the existing registration ID in the body
        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "existing-reg-id-409" }))
        });

        var service = BuildService(handler);
        var result = await service.RegisterAgentInstanceAsyncV2(
            tenantId: "tenant-id",
            displayName: "My Agent",
            description: null,
            blueprintId: "blueprint-id",
            agentIdentityId: "agent-identity-id",
            clientAppId: null);

        result.Id.Should().Be("existing-reg-id-409",
            because: "409 Conflict means the agent is already registered; the existing ID in the body must be returned");
        result.AlreadyExisted.Should().BeTrue(
            because: "a 409 Conflict signals the registration pre-existed; the orchestrator uses this to show 'reused' in the summary");
    }

    /// <summary>
    /// When 409 response body has no "id", RegisterAgentInstanceAsyncV2 returns null
    /// (caller cannot infer the existing registration ID).
    /// </summary>
    [Fact]
    public async Task RegisterAgentInstanceAsyncV2_ReturnsNull_On409WithoutId()
    {
        using var handler = new TestHttpMessageHandler();

        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "user-id" }))
        });

        // 409 but no "id" in body
        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new System.Net.Http.StringContent("{\"error\":{\"message\":\"Agent with same sourceAgentId already exists\"}}")
        });

        var service = BuildService(handler);
        var result = await service.RegisterAgentInstanceAsyncV2(
            tenantId: "tenant-id",
            displayName: "My Agent",
            description: null,
            blueprintId: "blueprint-id",
            agentIdentityId: "agent-identity-id",
            clientAppId: null);

        result.Id.Should().BeNull(
            because: "when 409 body contains no id, the existing registration ID cannot be determined");
        result.AlreadyExisted.Should().BeFalse(
            because: "without an ID in the 409 response body the registration cannot be confirmed as pre-existing");
    }

    [Fact]
    public async Task AgentRegistrationExistsAsync_ReturnsTrue_WhenGetSucceeds()
    {
        using var handler = new TestHttpMessageHandler();

        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "reg-id-123", displayName = "My Agent" }))
        });

        var service = BuildService(handler);
        var result = await service.AgentRegistrationExistsAsync("tenant-id", "reg-id-123");

        result.Should().BeTrue(because: "a 200 OK response means the registration exists");
    }

    [Fact]
    public async Task AgentRegistrationExistsAsync_ReturnsFalse_WhenRegistrationNotFound()
    {
        using var handler = new TestHttpMessageHandler();

        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new System.Net.Http.StringContent("{\"error\":{\"message\":\"Not found\"}}")
        });

        var service = BuildService(handler);
        var result = await service.AgentRegistrationExistsAsync("tenant-id", "reg-id-deleted");

        result.Should().BeFalse(because: "a 404 means the registration no longer exists");
    }

    [Fact]
    public async Task AgentRegistrationExistsAsync_ReturnsNull_WhenGetFailsWithNon404()
    {
        using var handler = new TestHttpMessageHandler();

        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new System.Net.Http.StringContent("{\"error\":{\"message\":\"Forbidden\"}}")
        });

        var service = BuildService(handler);
        var result = await service.AgentRegistrationExistsAsync("tenant-id", "reg-id-abc");

        result.Should().BeNull(
            because: "a non-404 HTTP failure (e.g. 403) means the check is inconclusive — the registration may still exist");
    }

    [Fact]
    public async Task AgentRegistrationExistsAsync_RequestsCorrectPath()
    {
        System.Net.Http.HttpRequestMessage? capturedRequest = null;

        using var handler = new CapturingHttpMessageHandler(req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Get)
                capturedRequest = req;
        });

        handler.QueueResponse(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { id = "my-reg-id" }))
        });

        var service = BuildService(handler);
        await service.AgentRegistrationExistsAsync("tenant-id", "my-reg-id");

        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri!.ToString()
            .Should().Contain("/copilot/agentRegistrations/my-reg-id",
                because: "the request must target the registration by ID, not as a collection query");
    }
}
