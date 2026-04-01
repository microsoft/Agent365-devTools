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
            loginHintResolver: () => Task.FromResult<string?>(null));
    }

    [Fact]
    public async Task RegisterAgentInstanceAsync_ReturnsInstanceId_OnSuccess()
    {
        var handler = new TestHttpMessageHandler();

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
        var handler = new TestHttpMessageHandler();

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
        var handler = new TestHttpMessageHandler();

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

        var handler = new CapturingHttpMessageHandler(req =>
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

        var handler = new CapturingHttpMessageHandler(req =>
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

        var handler = new CapturingHttpMessageHandler(req =>
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

        var handler = new CapturingHttpMessageHandler(req =>
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
}
