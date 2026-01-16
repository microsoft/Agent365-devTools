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

/// <summary>
/// Tests for GraphApiService.AddApplicationOwnerAsync method.
/// Verifies idempotency, owner checking, and error handling.
/// </summary>
public class GraphApiServiceAddApplicationOwnerTests
{
    private readonly ILogger<GraphApiService> _mockLogger;
    private readonly CommandExecutor _mockExecutor;

    public GraphApiServiceAddApplicationOwnerTests()
    {
        _mockLogger = Substitute.For<ILogger<GraphApiService>>();
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);

        // Mock Azure CLI authentication
        _mockExecutor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
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
    }

    [Fact]
    public async Task AddApplicationOwnerAsync_WhenUserNotOwner_AddsOwnerSuccessfully()
    {
        // Arrange
        var handler = new TestHttpMessageHandler();
        var service = new GraphApiService(_mockLogger, _mockExecutor, handler);

        var tenantId = "tenant-123";
        var appObjectId = "app-obj-456";
        var userObjectId = "user-789";

        // Queue response for checking existing owners (empty list)
        var emptyOwnersResponse = new { value = Array.Empty<object>() };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(emptyOwnersResponse))
        });

        // Queue response for adding owner (success)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));

        // Act
        var result = await service.AddApplicationOwnerAsync(tenantId, appObjectId, userObjectId);

        // Assert
        result.Should().BeTrue("owner should be added successfully");
    }

    [Fact]
    public async Task AddApplicationOwnerAsync_WhenUserAlreadyOwner_ReturnsTrue()
    {
        // Arrange
        var handler = new TestHttpMessageHandler();
        var service = new GraphApiService(_mockLogger, _mockExecutor, handler);

        var tenantId = "tenant-123";
        var appObjectId = "app-obj-456";
        var userObjectId = "user-789";

        // Queue response for checking existing owners (user is already an owner)
        var existingOwnersResponse = new
        {
            value = new[]
            {
                new { id = userObjectId },
                new { id = "other-user-999" }
            }
        };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(existingOwnersResponse))
        });

        // No POST should be made since user is already an owner

        // Act
        var result = await service.AddApplicationOwnerAsync(tenantId, appObjectId, userObjectId);

        // Assert
        result.Should().BeTrue("user is already an owner");
    }

    [Fact]
    public async Task AddApplicationOwnerAsync_WhenConflictError_ReturnsTrue()
    {
        // Arrange
        var handler = new TestHttpMessageHandler();
        var service = new GraphApiService(_mockLogger, _mockExecutor, handler);

        var tenantId = "tenant-123";
        var appObjectId = "app-obj-456";
        var userObjectId = "user-789";

        // Queue response for checking existing owners (empty list)
        var emptyOwnersResponse = new { value = Array.Empty<object>() };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(emptyOwnersResponse))
        });

        // Queue response for adding owner (409 Conflict - race condition)
        var conflictError = new
        {
            error = new
            {
                code = "Request_BadRequest",
                message = "One or more added object references already exist"
            }
        };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(JsonSerializer.Serialize(conflictError))
        });

        // Act
        var result = await service.AddApplicationOwnerAsync(tenantId, appObjectId, userObjectId);

        // Assert
        result.Should().BeTrue("409 conflict means user is already an owner");
    }

    [Fact]
    public async Task AddApplicationOwnerAsync_WithoutUserObjectId_RetrievesCurrentUser()
    {
        // Arrange
        HttpRequestMessage? capturedMeRequest = null;
        var handler = new CapturingHttpMessageHandler((req) =>
        {
            if (req.RequestUri?.AbsolutePath.Contains("/me") == true)
            {
                capturedMeRequest = req;
            }
        });
        var service = new GraphApiService(_mockLogger, _mockExecutor, handler);

        var tenantId = "tenant-123";
        var appObjectId = "app-obj-456";

        // Queue response for /me request
        var meResponse = new { id = "current-user-999" };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(meResponse))
        });

        // Queue response for checking existing owners (empty list)
        var emptyOwnersResponse = new { value = Array.Empty<object>() };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(emptyOwnersResponse))
        });

        // Queue response for adding owner (success)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));

        // Act
        var result = await service.AddApplicationOwnerAsync(tenantId, appObjectId, userObjectId: null);

        // Assert
        result.Should().BeTrue("owner should be added successfully");
        capturedMeRequest.Should().NotBeNull("should have called /me endpoint to get current user");
        capturedMeRequest!.RequestUri!.Query.Should().Contain("$select=id");
    }

    [Fact]
    public async Task AddApplicationOwnerAsync_WhenGetCurrentUserFails_ReturnsFalse()
    {
        // Arrange
        var handler = new TestHttpMessageHandler();
        var service = new GraphApiService(_mockLogger, _mockExecutor, handler);

        var tenantId = "tenant-123";
        var appObjectId = "app-obj-456";

        // Queue response for /me request (failure)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Authentication failed")
        });

        // Act
        var result = await service.AddApplicationOwnerAsync(tenantId, appObjectId, userObjectId: null);

        // Assert
        result.Should().BeFalse("should fail when unable to get current user");
    }

    [Fact]
    public async Task AddApplicationOwnerAsync_WhenAddFails_ReturnsFalse()
    {
        // Arrange
        var handler = new TestHttpMessageHandler();
        var service = new GraphApiService(_mockLogger, _mockExecutor, handler);

        var tenantId = "tenant-123";
        var appObjectId = "app-obj-456";
        var userObjectId = "user-789";

        // Queue response for checking existing owners (empty list)
        var emptyOwnersResponse = new { value = Array.Empty<object>() };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(emptyOwnersResponse))
        });

        // Queue response for adding owner (failure - forbidden)
        var forbiddenError = new
        {
            error = new
            {
                code = "Authorization_RequestDenied",
                message = "Insufficient privileges to complete the operation"
            }
        };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(JsonSerializer.Serialize(forbiddenError))
        });

        // Act
        var result = await service.AddApplicationOwnerAsync(tenantId, appObjectId, userObjectId);

        // Assert
        result.Should().BeFalse("should fail when insufficient privileges");
    }

    [Fact]
    public async Task AddApplicationOwnerAsync_UsesBetaEndpoint()
    {
        // Arrange
        HttpRequestMessage? capturedAddRequest = null;
        var handler = new CapturingHttpMessageHandler((req) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.AbsolutePath.Contains("/owners/") == true)
            {
                capturedAddRequest = req;
            }
        });
        var service = new GraphApiService(_mockLogger, _mockExecutor, handler);

        var tenantId = "tenant-123";
        var appObjectId = "app-obj-456";
        var userObjectId = "user-789";

        // Queue response for checking existing owners (empty list)
        var emptyOwnersResponse = new { value = Array.Empty<object>() };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(emptyOwnersResponse))
        });

        // Queue response for adding owner (success)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));

        // Act
        var result = await service.AddApplicationOwnerAsync(tenantId, appObjectId, userObjectId);

        // Assert
        result.Should().BeTrue();
        capturedAddRequest.Should().NotBeNull("should have made POST request to add owner");
        capturedAddRequest!.RequestUri!.AbsolutePath.Should().Contain("/beta/applications/",
            "should use beta endpoint as recommended in documentation");
        capturedAddRequest.RequestUri.AbsolutePath.Should().Contain($"/{appObjectId}/owners/$ref");
    }

    [Fact]
    public async Task AddApplicationOwnerAsync_SendsCorrectPayload()
    {
        // Arrange
        string? capturedPayload = null;
        var handler = new CapturingHttpMessageHandler((req) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.AbsolutePath.Contains("/owners/") == true)
            {
                capturedPayload = req.Content?.ReadAsStringAsync().Result;
            }
        });
        var service = new GraphApiService(_mockLogger, _mockExecutor, handler);

        var tenantId = "tenant-123";
        var appObjectId = "app-obj-456";
        var userObjectId = "user-789";

        // Queue response for checking existing owners (empty list)
        var emptyOwnersResponse = new { value = Array.Empty<object>() };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(emptyOwnersResponse))
        });

        // Queue response for adding owner (success)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));

        // Act
        var result = await service.AddApplicationOwnerAsync(tenantId, appObjectId, userObjectId);

        // Assert
        result.Should().BeTrue();
        capturedPayload.Should().NotBeNullOrWhiteSpace("should have sent payload");

        // Verify payload structure (uses camelCase naming policy, so "@odata.id" becomes "odataid")
        var payload = JsonSerializer.Deserialize<JsonElement>(capturedPayload!);
        payload.TryGetProperty("odataid", out var odataId).Should().BeTrue("payload should have odataid property (camelCase naming)");
        odataId.GetString().Should().Contain($"/directoryObjects/{userObjectId}",
            "payload should reference the user object");
    }

    [Fact]
    public async Task AddApplicationOwnerAsync_IsCaseInsensitiveForUserIdComparison()
    {
        // Arrange
        var handler = new TestHttpMessageHandler();
        var service = new GraphApiService(_mockLogger, _mockExecutor, handler);

        var tenantId = "tenant-123";
        var appObjectId = "app-obj-456";
        var userObjectId = "user-abc-123";

        // Queue response for checking existing owners (user ID with different casing)
        var existingOwnersResponse = new
        {
            value = new[]
            {
                new { id = "USER-ABC-123" } // Different case
            }
        };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(existingOwnersResponse))
        });

        // No POST should be made since user is already an owner

        // Act
        var result = await service.AddApplicationOwnerAsync(tenantId, appObjectId, userObjectId);

        // Assert
        result.Should().BeTrue("should match user ID case-insensitively");
    }
}

// Test helper classes are defined in GraphApiServiceTests.cs
