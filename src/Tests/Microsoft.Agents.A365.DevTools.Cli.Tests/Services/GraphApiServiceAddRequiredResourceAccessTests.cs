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

public class GraphApiServiceAddRequiredResourceAccessTests
{
    private const string TenantId = "test-tenant-id";
    private const string AppId = "test-app-id";
    private const string ResourceAppId = "resource-app-id";
    private const string ObjectId = "object-id-123";
    private const string SpObjectId = "sp-object-id-456";

    [Fact]
    public async Task AddRequiredResourceAccessAsync_Success_WithValidPermissionIds()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = CreateMockExecutor();
        var service = new GraphApiService(logger, executor, handler);

        // Queue responses
        QueueApplicationLookupResponse(handler, hasId: true);
        QueueServicePrincipalLookupResponse(handler);
        QueueServicePrincipalPermissionsResponse(handler, hasValidId: true);
        QueuePatchResponse(handler);

        // Act
        var result = await service.AddRequiredResourceAccessAsync(
            TenantId,
            AppId,
            ResourceAppId,
            new[] { "User.Read", "Mail.Send" });

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task AddRequiredResourceAccessAsync_HandlesNullPermissionId_Gracefully()
    {
        // Arrange - This test catches the null reference bug
        var handler = new FakeHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = CreateMockExecutor();
        var service = new GraphApiService(logger, executor, handler);

        // Queue responses
        QueueApplicationLookupResponse(handler, hasId: true);
        QueueServicePrincipalLookupResponse(handler);
        QueueServicePrincipalPermissionsResponse(handler, hasValidId: false); // NULL id property

        // Act
        var result = await service.AddRequiredResourceAccessAsync(
            TenantId,
            AppId,
            ResourceAppId,
            new[] { "User.Read" });

        // Assert - Should handle null gracefully, not throw NullReferenceException
        result.Should().BeFalse(); // No valid permission IDs found
    }

    [Fact]
    public async Task AddRequiredResourceAccessAsync_FailsWhen_ApplicationNotFound()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = CreateMockExecutor();
        var service = new GraphApiService(logger, executor, handler);

        // Queue empty application response
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = Array.Empty<object>() }))
        });

        // Act
        var result = await service.AddRequiredResourceAccessAsync(
            TenantId,
            AppId,
            ResourceAppId,
            new[] { "User.Read" });

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task AddRequiredResourceAccessAsync_FailsWhen_ApplicationIdIsNull()
    {
        // Arrange - Tests the null safety check we just added
        var handler = new FakeHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = CreateMockExecutor();
        var service = new GraphApiService(logger, executor, handler);

        // Queue application response with null id
        QueueApplicationLookupResponse(handler, hasId: false);

        // Act
        var result = await service.AddRequiredResourceAccessAsync(
            TenantId,
            AppId,
            ResourceAppId,
            new[] { "User.Read" });

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task AddRequiredResourceAccessAsync_SkipsInvalidScopes_AndProcessesValid()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = CreateMockExecutor();
        var service = new GraphApiService(logger, executor, handler);

        // Queue responses
        QueueApplicationLookupResponse(handler, hasId: true);
        QueueServicePrincipalLookupResponse(handler);

        // SP with only User.Read permission (Mail.Send will be invalid)
        var permissions = new
        {
            oauth2PermissionScopes = new[]
            {
                new { value = "User.Read", id = "valid-permission-id" }
                // Mail.Send is missing - will be skipped with warning
            }
        };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(permissions))
        });

        QueuePatchResponse(handler);

        // Act
        var result = await service.AddRequiredResourceAccessAsync(
            TenantId,
            AppId,
            ResourceAppId,
            new[] { "User.Read", "Mail.Send" });

        // Assert
        result.Should().BeTrue(); // Should succeed with at least one valid permission
    }

    [Fact]
    public async Task AddRequiredResourceAccessAsync_MergesWithExisting_Permissions()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = CreateMockExecutor();
        var service = new GraphApiService(logger, executor, handler);

        // Application with existing requiredResourceAccess
        var existingApp = new
        {
            value = new[]
            {
                new
                {
                    id = ObjectId,
                    requiredResourceAccess = new[]
                    {
                        new
                        {
                            resourceAppId = ResourceAppId,
                            resourceAccess = new[]
                            {
                                new { id = "existing-permission-id", type = "Scope" }
                            }
                        }
                    }
                }
            }
        };

        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(existingApp))
        });

        QueueServicePrincipalLookupResponse(handler);
        QueueServicePrincipalPermissionsResponse(handler, hasValidId: true);
        QueuePatchResponse(handler);

        // Act
        var result = await service.AddRequiredResourceAccessAsync(
            TenantId,
            AppId,
            ResourceAppId,
            new[] { "User.Read" });

        // Assert
        result.Should().BeTrue();
    }

    private static CommandExecutor CreateMockExecutor()
    {
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        
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
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });

                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        return executor;
    }

    private static void QueueApplicationLookupResponse(FakeHttpMessageHandler handler, bool hasId)
    {
        object app = hasId
            ? new { id = ObjectId, requiredResourceAccess = Array.Empty<object>() }
            : new { requiredResourceAccess = Array.Empty<object>() }; // Missing 'id' property

        var response = new { value = new[] { app } };
        
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response))
        });
    }

    private static void QueueServicePrincipalLookupResponse(FakeHttpMessageHandler handler)
    {
        var sp = new { value = new[] { new { id = SpObjectId } } };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(sp))
        });
    }

    private static void QueueServicePrincipalPermissionsResponse(FakeHttpMessageHandler handler, bool hasValidId)
    {
        object permissions;
        
        if (hasValidId)
        {
            permissions = new
            {
                oauth2PermissionScopes = new[]
                {
                    new { value = "User.Read", id = "permission-id-1" },
                    new { value = "Mail.Send", id = "permission-id-2" }
                }
            };
        }
        else
        {
            // Simulate null id - this is the bug we're catching
            permissions = new
            {
                oauth2PermissionScopes = new[]
                {
                    new { value = "User.Read", id = (string?)null } // NULL id
                }
            };
        }

        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(permissions))
        });
    }

    private static void QueuePatchResponse(FakeHttpMessageHandler handler)
    {
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));
    }
}
