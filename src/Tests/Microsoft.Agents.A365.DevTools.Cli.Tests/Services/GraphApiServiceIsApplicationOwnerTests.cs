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
/// Tests for GraphApiService.IsApplicationOwnerAsync method.
/// Verifies read-only owner validation without attempting to add owners.
/// </summary>
/// <remarks>
/// HttpResponseMessage objects are created inline and queued to test handlers.
/// The test handlers (TestHttpMessageHandler and CapturingHttpMessageHandler) properly dispose
/// all queued responses in their Dispose methods. Suppressing CA2000 for this pattern.
/// </remarks>
#pragma warning disable CA2000 // Dispose objects before losing scope
public class GraphApiServiceIsApplicationOwnerTests
{
    private readonly ILogger<GraphApiService> _mockLogger;
    private readonly CommandExecutor _mockExecutor;

    public GraphApiServiceIsApplicationOwnerTests()
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
    public async Task IsApplicationOwnerAsync_WhenUserIsOwner_ReturnsTrue()
    {
        // Arrange
        using var handler = new TestHttpMessageHandler();
        var service = new GraphApiService(_mockLogger, _mockExecutor, handler);

        var tenantId = "tenant-123";
        var appObjectId = "app-obj-456";
        var userObjectId = "user-789";

        // Queue response for checking existing owners (user is an owner)
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

        // Act
        var result = await service.IsApplicationOwnerAsync(tenantId, appObjectId, userObjectId);

        // Assert
        result.Should().BeTrue("user is an owner");
    }

    [Fact]
    public async Task IsApplicationOwnerAsync_WhenUserIsNotOwner_ReturnsFalse()
    {
        // Arrange
        using var handler = new TestHttpMessageHandler();
        var service = new GraphApiService(_mockLogger, _mockExecutor, handler);

        var tenantId = "tenant-123";
        var appObjectId = "app-obj-456";
        var userObjectId = "user-789";

        // Queue response for checking existing owners (user is NOT an owner)
        var existingOwnersResponse = new
        {
            value = new[]
            {
                new { id = "other-user-111" },
                new { id = "other-user-999" }
            }
        };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(existingOwnersResponse))
        });

        // Act
        var result = await service.IsApplicationOwnerAsync(tenantId, appObjectId, userObjectId);

        // Assert
        result.Should().BeFalse("user is not an owner");
    }

    [Fact]
    public async Task IsApplicationOwnerAsync_WhenNoOwners_ReturnsFalse()
    {
        // Arrange
        using var handler = new TestHttpMessageHandler();
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

        // Act
        var result = await service.IsApplicationOwnerAsync(tenantId, appObjectId, userObjectId);

        // Assert
        result.Should().BeFalse("no owners exist");
    }

    [Fact]
    public async Task IsApplicationOwnerAsync_WithoutUserObjectId_RetrievesCurrentUser()
    {
        // Arrange
        HttpRequestMessage? capturedMeRequest = null;
        using var handler = new CapturingHttpMessageHandler((req) =>
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

        // Queue response for checking existing owners (current user is owner)
        var existingOwnersResponse = new
        {
            value = new[]
            {
                new { id = "current-user-999" }
            }
        };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(existingOwnersResponse))
        });

        // Act
        var result = await service.IsApplicationOwnerAsync(tenantId, appObjectId, userObjectId: null);

        // Assert
        result.Should().BeTrue("current user is an owner");
        capturedMeRequest.Should().NotBeNull("should have called /me endpoint to get current user");
        capturedMeRequest!.RequestUri!.Query.Should().Contain("$select=id");
    }

    [Fact]
    public async Task IsApplicationOwnerAsync_WhenGetCurrentUserFails_ReturnsFalse()
    {
        // Arrange
        using var handler = new TestHttpMessageHandler();
        var service = new GraphApiService(_mockLogger, _mockExecutor, handler);

        var tenantId = "tenant-123";
        var appObjectId = "app-obj-456";

        // Queue response for /me request (failure)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Authentication failed")
        });

        // Act
        var result = await service.IsApplicationOwnerAsync(tenantId, appObjectId, userObjectId: null);

        // Assert
        result.Should().BeFalse("should fail when unable to get current user");
    }

    [Fact]
    public async Task IsApplicationOwnerAsync_IsCaseInsensitiveForUserIdComparison()
    {
        // Arrange
        using var handler = new TestHttpMessageHandler();
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

        // Act
        var result = await service.IsApplicationOwnerAsync(tenantId, appObjectId, userObjectId);

        // Assert
        result.Should().BeTrue("should match user ID case-insensitively");
    }

    [Fact]
    public async Task IsApplicationOwnerAsync_WhenGraphApiCallFails_ReturnsFalse()
    {
        // Arrange
        using var handler = new TestHttpMessageHandler();
        var service = new GraphApiService(_mockLogger, _mockExecutor, handler);

        var tenantId = "tenant-123";
        var appObjectId = "app-obj-456";
        var userObjectId = "user-789";

        // Queue response for checking existing owners (failure)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("Insufficient privileges")
        });

        // Act
        var result = await service.IsApplicationOwnerAsync(tenantId, appObjectId, userObjectId);

        // Assert
        result.Should().BeFalse("should fail when Graph API call fails");
    }

    [Fact]
    public async Task IsApplicationOwnerAsync_UsesV1Endpoint()
    {
        // Arrange
        HttpRequestMessage? capturedOwnersRequest = null;
        using var handler = new CapturingHttpMessageHandler((req) =>
        {
            if (req.RequestUri?.AbsolutePath.Contains("/owners") == true)
            {
                capturedOwnersRequest = req;
            }
        });
        var service = new GraphApiService(_mockLogger, _mockExecutor, handler);

        var tenantId = "tenant-123";
        var appObjectId = "app-obj-456";
        var userObjectId = "user-789";

        // Queue response for checking existing owners
        var existingOwnersResponse = new { value = Array.Empty<object>() };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(existingOwnersResponse))
        });

        // Act
        var result = await service.IsApplicationOwnerAsync(tenantId, appObjectId, userObjectId);

        // Assert
        result.Should().BeFalse();
        capturedOwnersRequest.Should().NotBeNull("should have made GET request to check owners");
        capturedOwnersRequest!.RequestUri!.AbsolutePath.Should().Contain("/v1.0/applications/",
            "should use v1.0 endpoint for reading owners");
        capturedOwnersRequest.RequestUri.AbsolutePath.Should().Contain($"/{appObjectId}/owners");
        capturedOwnersRequest.RequestUri.Query.Should().Contain("$select=id");
    }
}
#pragma warning restore CA2000 // Dispose objects before losing scope

// Test helper classes are defined in GraphApiServiceTests.cs
