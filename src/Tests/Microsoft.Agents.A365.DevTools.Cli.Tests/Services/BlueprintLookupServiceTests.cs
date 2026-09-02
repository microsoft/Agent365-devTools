// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class BlueprintLookupServiceTests
{
    private readonly ILogger<BlueprintLookupService> _logger;
    private readonly GraphApiService _graphApiService;
    private readonly BlueprintLookupService _service;
    private const string TestTenantId = "12345678-1234-1234-1234-123456789012";
    private const string TestObjectId = "87654321-4321-4321-4321-210987654321";
    private const string TestAppId = "11111111-1111-1111-1111-111111111111";
    private const string TestDisplayName = "Test Blueprint";

    public BlueprintLookupServiceTests()
    {
        _logger = Substitute.For<ILogger<BlueprintLookupService>>();
        _graphApiService = Substitute.For<GraphApiService>();
        _service = new BlueprintLookupService(_logger, _graphApiService);
    }

    [Fact]
    public async Task GetApplicationByObjectIdAsync_WhenBlueprintExists_ReturnsFoundWithDetails()
    {
        // Arrange
        var jsonResponse = $@"{{
            ""id"": ""{TestObjectId}"",
            ""appId"": ""{TestAppId}"",
            ""displayName"": ""{TestDisplayName}""
        }}";
        var jsonDoc = JsonDocument.Parse(jsonResponse);

        _graphApiService.GraphGetAsync(
            TestTenantId,
            $"/beta/applications/{TestObjectId}",
            Arg.Any<CancellationToken>(),
            null)
            .Returns(jsonDoc);

        // Act
        var result = await _service.GetApplicationByObjectIdAsync(TestTenantId, TestObjectId);

        // Assert
        result.Should().NotBeNull();
        result.Found.Should().BeTrue();
        result.ObjectId.Should().Be(TestObjectId);
        result.AppId.Should().Be(TestAppId);
        result.DisplayName.Should().Be(TestDisplayName);
        result.LookupMethod.Should().Be("objectId");
        result.RequiresPersistence.Should().BeFalse(); // objectId lookup doesn't require persistence
    }

    [Fact]
    public async Task GetApplicationByObjectIdAsync_WhenBlueprintNotFound_ReturnsNotFound()
    {
        // Arrange
        _graphApiService.GraphGetAsync(
            TestTenantId,
            $"/beta/applications/{TestObjectId}",
            Arg.Any<CancellationToken>())
            .Returns((JsonDocument?)null);

        // Act
        var result = await _service.GetApplicationByObjectIdAsync(TestTenantId, TestObjectId);

        // Assert
        result.Should().NotBeNull();
        result.Found.Should().BeFalse();
        result.LookupMethod.Should().Be("objectId");
    }

    [Fact]
    public async Task GetApplicationByDisplayNameAsync_WhenBlueprintExists_ReturnsFoundWithRequiresPersistence()
    {
        // Arrange
        var jsonResponse = $@"{{
            ""value"": [
                {{
                    ""id"": ""{TestObjectId}"",
                    ""appId"": ""{TestAppId}"",
                    ""displayName"": ""{TestDisplayName}""
                }}
            ]
        }}";
        var jsonDoc = JsonDocument.Parse(jsonResponse);

        _graphApiService.GraphGetWithResponseAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("/beta/applications?$filter=")),
            false,
            Arg.Is<IEnumerable<string>?>(scopes => scopes != null && scopes.Contains("Application.Read.All")),
            Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.GraphResponse { IsSuccess = true, StatusCode = 200, Json = jsonDoc });

        // Act
        var result = await _service.GetApplicationByDisplayNameAsync(TestTenantId, TestDisplayName);

        // Assert
        result.Should().NotBeNull();
        result.Found.Should().BeTrue();
        result.ObjectId.Should().Be(TestObjectId);
        result.AppId.Should().Be(TestAppId);
        result.DisplayName.Should().Be(TestDisplayName);
        result.LookupMethod.Should().Be("displayName");
        result.RequiresPersistence.Should().BeTrue(); // displayName lookup requires persistence for migration
    }

    [Fact]
    public async Task GetApplicationByDisplayNameAsync_WhenNoBlueprintsFound_ReturnsNotFound()
    {
        // Arrange
        var jsonResponse = @"{""value"": []}";
        var jsonDoc = JsonDocument.Parse(jsonResponse);

        _graphApiService.GraphGetWithResponseAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("/beta/applications?$filter=")),
            false,
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.GraphResponse { IsSuccess = true, StatusCode = 200, Json = jsonDoc });

        // Act
        var result = await _service.GetApplicationByDisplayNameAsync(TestTenantId, TestDisplayName);

        // Assert
        result.Should().NotBeNull();
        result.Found.Should().BeFalse();
        result.LookupMethod.Should().Be("displayName");
        result.RequiresPersistence.Should().BeFalse();
    }

    [Fact]
    public async Task GetApplicationByDisplayNameAsync_EscapesSingleQuotes()
    {
        // Arrange
        var displayNameWithQuotes = "Test'Blueprint'Name";
        var jsonResponse = @"{""value"": []}";
        var jsonDoc = JsonDocument.Parse(jsonResponse);

        _graphApiService.GraphGetWithResponseAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("Test%27%27Blueprint%27%27Name")), // URL encoded double single quotes
            false,
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.GraphResponse { IsSuccess = true, StatusCode = 200, Json = jsonDoc });

        // Act
        await _service.GetApplicationByDisplayNameAsync(TestTenantId, displayNameWithQuotes);

        // Assert
        await _graphApiService.Received(1).GraphGetWithResponseAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("Test%27%27Blueprint%27%27Name")),
            false,
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetServicePrincipalByAppIdAsync_WhenSPExists_ReturnsFoundWithDetails()
    {
        // Arrange
        var spObjectId = "22222222-2222-2222-2222-222222222222";

        // LookupServicePrincipalByAppIdAsync handles ConsistencyLevel header internally
        _graphApiService.LookupServicePrincipalByAppIdAsync(
            TestTenantId,
            TestAppId,
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Returns(spObjectId);

        // Act
        var result = await _service.GetServicePrincipalByAppIdAsync(TestTenantId, TestAppId);

        // Assert
        result.Should().NotBeNull();
        result.Found.Should().BeTrue();
        result.ObjectId.Should().Be(spObjectId);
        result.AppId.Should().Be(TestAppId);
        result.LookupMethod.Should().Be("appId");
    }

    [Fact]
    public async Task GetServicePrincipalByObjectIdAsync_WhenSPExists_ReturnsFoundWithDetails()
    {
        // Arrange
        var spObjectId = "33333333-3333-3333-3333-333333333333";
        var jsonResponse = $@"{{
            ""id"": ""{spObjectId}"",
            ""appId"": ""{TestAppId}"",
            ""displayName"": ""Test SP""
        }}";
        var jsonDoc = JsonDocument.Parse(jsonResponse);

        _graphApiService.GraphGetAsync(
            TestTenantId,
            $"/v1.0/servicePrincipals/{spObjectId}",
            Arg.Any<CancellationToken>(),
            null)
            .Returns(jsonDoc);

        // Act
        var result = await _service.GetServicePrincipalByObjectIdAsync(TestTenantId, spObjectId);

        // Assert
        result.Should().NotBeNull();
        result.Found.Should().BeTrue();
        result.ObjectId.Should().Be(spObjectId);
        result.AppId.Should().Be(TestAppId);
        result.LookupMethod.Should().Be("objectId");
    }

    [Fact]
    public async Task GetApplicationByObjectIdAsync_OnException_ReturnsNotFoundWithError()
    {
        // Arrange
        _graphApiService.GraphGetAsync(
            TestTenantId,
            $"/beta/applications/{TestObjectId}",
            Arg.Any<CancellationToken>())
            .Returns(Task.FromException<JsonDocument?>(new Exception("Graph API error")));

        // Act
        var result = await _service.GetApplicationByObjectIdAsync(TestTenantId, TestObjectId);

        // Assert
        result.Should().NotBeNull();
        result.Found.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graph API error");
    }

    [Fact]
    public async Task GetApplicationByDisplayNameAsync_WhenMultipleBlueprintsFoundWithoutPreferredId_ReturnsInconclusiveError()
    {
        // Arrange - Simulate multiple results (shouldn't happen with proper naming, but test resilience)
        var objectId1 = "44444444-4444-4444-4444-444444444444";
        var objectId2 = "55555555-5555-5555-5555-555555555555";
        var jsonResponse = $@"{{
            ""value"": [
                {{
                    ""id"": ""{objectId1}"",
                    ""appId"": ""{TestAppId}"",
                    ""displayName"": ""{TestDisplayName}""
                }},
                {{
                    ""id"": ""{objectId2}"",
                    ""appId"": ""66666666-6666-6666-6666-666666666666"",
                    ""displayName"": ""{TestDisplayName}""
                }}
            ]
        }}";
        var jsonDoc = JsonDocument.Parse(jsonResponse);

        _graphApiService.GraphGetWithResponseAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("/beta/applications?$filter=")),
            false,
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.GraphResponse { IsSuccess = true, StatusCode = 200, Json = jsonDoc });

        // Act
        var result = await _service.GetApplicationByDisplayNameAsync(TestTenantId, TestDisplayName);

        // Assert
        result.Found.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Multiple blueprints",
            because: "setup must not select an arbitrary application when display names are ambiguous");
    }

    [Fact]
    public async Task GetApplicationByDisplayNameAsync_WhenMultipleBlueprintsFound_PrefersCachedObjectId()
    {
        // Arrange
        var objectId1 = "44444444-4444-4444-4444-444444444444";
        var objectId2 = "55555555-5555-5555-5555-555555555555";
        var jsonDoc = JsonDocument.Parse($$"""
            {
              "value": [
                { "id": "{{objectId1}}", "appId": "{{TestAppId}}", "displayName": "{{TestDisplayName}}" },
                { "id": "{{objectId2}}", "appId": "66666666-6666-6666-6666-666666666666", "displayName": "{{TestDisplayName}}" }
              ]
            }
            """);

        _graphApiService.GraphGetWithResponseAsync(
            TestTenantId,
            Arg.Any<string>(),
            false,
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.GraphResponse { IsSuccess = true, StatusCode = 200, Json = jsonDoc });

        // Act
        var result = await _service.GetApplicationByDisplayNameAsync(
            TestTenantId,
            TestDisplayName,
            preferredObjectId: objectId2);

        // Assert
        result.ObjectId.Should().Be(objectId2,
            because: "a cached blueprint object ID must win when duplicate display names exist");
    }

    [Fact]
    public async Task GetApplicationByDisplayNameAsync_WhenGraphRequestFails_ReturnsInconclusiveError()
    {
        // Arrange
        _graphApiService.GraphGetWithResponseAsync(
            TestTenantId,
            Arg.Any<string>(),
            false,
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = false,
                StatusCode = 403,
                ReasonPhrase = "Forbidden",
                Body = """{"error":{"code":"Authorization_RequestDenied"}}"""
            });

        // Act
        var result = await _service.GetApplicationByDisplayNameAsync(TestTenantId, TestDisplayName);

        // Assert
        result.Found.Should().BeFalse();
        result.ErrorMessage.Should().Contain("HTTP 403 Forbidden",
            because: "authorization failures must remain distinguishable from a successful empty lookup");
    }

    [Fact]
    public async Task GetApplicationByDisplayNameAsync_WhenCanceled_PropagatesCancellation()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _graphApiService.GraphGetWithResponseAsync(
            TestTenantId,
            Arg.Any<string>(),
            false,
            Arg.Any<IEnumerable<string>?>(),
            cts.Token)
            .Returns<Task<GraphApiService.GraphResponse>>(_ => throw new OperationCanceledException(cts.Token));

        // Act
        var act = () => _service.GetApplicationByDisplayNameAsync(
            TestTenantId,
            TestDisplayName,
            cancellationToken: cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>(
            because: "Ctrl+C must remain cancellation rather than being reported as a permission failure");
    }

    [Fact]
    public async Task GetApplicationByDisplayNameAsync_WhenDisplayNameMismatch_ReturnsNotFound()
    {
        // Arrange - Regression test for idempotency bug
        // Scenario: User changes displayName in a365.config.json but cached objectId points to old name
        // Expected: Search by new displayName should return NotFound (not the cached blueprint)
        //
        // Bug History:
        // - Step 1: 'a365 setup all' creates "MyAgent Blueprint" -> saves objectId to config
        // - Step 2: User edits a365.config.json -> changes displayName to "NewAgent Blueprint"
        // - Step 3: 'a365 setup all' searches by new displayName -> should NOT find old blueprint
        //
        // Fix: BlueprintSubcommand now always uses displayName-first discovery (lines 547-578)
        // This test verifies the lookup service correctly returns NotFound when displayName doesn't match

        var newDisplayName = "NewAgent Blueprint";
        var jsonResponse = @"{""value"": []}"; // No blueprints match the new displayName
        var jsonDoc = JsonDocument.Parse(jsonResponse);

        _graphApiService.GraphGetWithResponseAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("/beta/applications?$filter=") && s.Contains("NewAgent")),
            false,
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.GraphResponse { IsSuccess = true, StatusCode = 200, Json = jsonDoc });

        // Act
        var result = await _service.GetApplicationByDisplayNameAsync(TestTenantId, newDisplayName);

        // Assert
        result.Should().NotBeNull();
        result.Found.Should().BeFalse("searching by new displayName should not find old cached blueprint");
        result.LookupMethod.Should().Be("displayName");
        result.RequiresPersistence.Should().BeFalse("no blueprint found means nothing to persist");
    }
}
