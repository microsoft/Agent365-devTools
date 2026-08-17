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
        var jsonResponse = $$"""
            {
              "value": {
                "id": "{{TestObjectId}}",
                "appId": "{{TestAppId}}",
                "displayName": "{{TestDisplayName}}"
              }
            }
            """;
        var jsonDoc = JsonDocument.Parse(jsonResponse);

        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Is<string>(s =>
                s.Contains($"/beta/applications/{TestObjectId}/microsoft.graph.agentIdentityBlueprint") &&
                !s.Contains("$filter")),
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
    public async Task GetApplicationByObjectIdAsync_WhenGraphReturnsMismatchedObjectId_ReturnsNotFound()
    {
        var mismatchedObjectId = "99999999-9999-9999-9999-999999999999";
        _graphApiService.GraphGetAsync(
                TestTenantId,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                null)
            .Returns(JsonDocument.Parse($$"""
                {
                  "value": {
                    "id": "{{mismatchedObjectId}}",
                    "appId": "{{TestAppId}}",
                    "displayName": "{{TestDisplayName}}"
                  }
                }
                """));

        var result = await _service.GetApplicationByObjectIdAsync(TestTenantId, TestObjectId);

        result.Found.Should().BeFalse(
            because: "a Graph result for another object must not satisfy exact blueprint verification");
        result.ErrorMessage.Should().Contain(
            "incomplete or inconsistent",
            because: "the caller must be able to distinguish a mismatched response from an absent blueprint");
    }

    [Fact]
    public async Task GetApplicationByObjectIdAsync_WhenBlueprintNotFound_ReturnsNotFound()
    {
        // Arrange
        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains($"/beta/applications/{TestObjectId}/microsoft.graph.agentIdentityBlueprint")),
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
    public async Task GetApplicationByObjectIdAsync_WhenObjectIdIsNotAGuid_ReturnsNotFoundWithoutCallingGraph()
    {
        var result = await _service.GetApplicationByObjectIdAsync(TestTenantId, "not-a-guid");

        result.Found.Should().BeFalse(
            because: "malformed cached object IDs must be rejected before any Graph call");
        await _graphApiService.DidNotReceiveWithAnyArgs().GraphGetAsync(
            default!,
            default!,
            default,
            default);
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

        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("/beta/applications?$filter=")),
            Arg.Any<CancellationToken>())
            .Returns(jsonDoc);

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

        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("/beta/applications?$filter=")),
            Arg.Any<CancellationToken>())
            .Returns(jsonDoc);

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

        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("Test%27%27Blueprint%27%27Name")), // URL encoded double single quotes
            Arg.Any<CancellationToken>(),
            null)
            .Returns(jsonDoc);

        // Act
        await _service.GetApplicationByDisplayNameAsync(TestTenantId, displayNameWithQuotes);

        // Assert
        await _graphApiService.Received(1).GraphGetAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("Test%27%27Blueprint%27%27Name")),
            Arg.Any<CancellationToken>(),
            null);
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
            Arg.Is<string>(s => s.Contains($"/beta/applications/{TestObjectId}/microsoft.graph.agentIdentityBlueprint")),
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
    public async Task GetApplicationByObjectIdAsync_WhenCancelled_PropagatesCancellation()
    {
        _graphApiService.GraphGetAsync(
                TestTenantId,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                null)
            .Returns(Task.FromException<JsonDocument?>(new OperationCanceledException()));

        var act = async () => await _service.GetApplicationByObjectIdAsync(TestTenantId, TestObjectId);

        await act.Should().ThrowAsync<OperationCanceledException>(
            because: "cancellation must not be converted into a not-found result");
    }

    [Fact]
    public async Task GetApplicationByDisplayNameAsync_WhenMultipleBlueprintsFound_ReturnsFirst()
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

        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("/beta/applications?$filter=")),
            Arg.Any<CancellationToken>())
            .Returns(jsonDoc);

        // Act
        var result = await _service.GetApplicationByDisplayNameAsync(TestTenantId, TestDisplayName);

        // Assert
        result.Should().NotBeNull();
        result.Found.Should().BeTrue();
        result.ObjectId.Should().Be(objectId1); // Should return the first match
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

        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("/beta/applications?$filter=") && s.Contains("NewAgent")),
            Arg.Any<CancellationToken>())
            .Returns(jsonDoc);

        // Act
        var result = await _service.GetApplicationByDisplayNameAsync(TestTenantId, newDisplayName);

        // Assert
        result.Should().NotBeNull();
        result.Found.Should().BeFalse("searching by new displayName should not find old cached blueprint");
        result.LookupMethod.Should().Be("displayName");
        result.RequiresPersistence.Should().BeFalse("no blueprint found means nothing to persist");
    }

    // -----------------------------------------------------------------------
    // ListBlueprintsAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ListBlueprintsAsync_WhenBlueprintsExist_ReturnsAllWithDetails()
    {
        var jsonResponse = $@"{{
            ""value"": [
                {{ ""id"": ""{TestObjectId}"", ""appId"": ""{TestAppId}"", ""displayName"": ""{TestDisplayName}"" }},
                {{ ""id"": ""22222222-2222-2222-2222-222222222222"", ""appId"": ""33333333-3333-3333-3333-333333333333"", ""displayName"": ""Second Blueprint"" }}
            ]
        }}";
        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("/beta/applications/microsoft.graph.agentIdentityBlueprint")),
            Arg.Any<CancellationToken>(),
            null)
            .Returns(JsonDocument.Parse(jsonResponse));

        var result = await _service.ListBlueprintsAsync(TestTenantId);

        result.Should().HaveCount(2, because: "both blueprints returned by Graph must be included");
        result[0].Found.Should().BeTrue();
        result[0].AppId.Should().Be(TestAppId);
        result[0].DisplayName.Should().Be(TestDisplayName);
        result[1].AppId.Should().Be("33333333-3333-3333-3333-333333333333");
    }

    [Fact]
    public async Task ListBlueprintsAsync_WhenTenantHasNoBlueprints_ReturnsEmptyList()
    {
        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            null)
            .Returns(JsonDocument.Parse(@"{""value"": []}"));

        var result = await _service.ListBlueprintsAsync(TestTenantId);

        result.Should().BeEmpty(because: "an empty tenant must be reported as a successful empty list, not an error");
    }

    [Fact]
    public async Task ListBlueprintsAsync_FollowsODataNextLinkPagination()
    {
        var page1 = $@"{{
            ""value"": [ {{ ""id"": ""{TestObjectId}"", ""appId"": ""{TestAppId}"", ""displayName"": ""Page1 Blueprint"" }} ],
            ""@odata.nextLink"": ""https://graph.microsoft.com/beta/applications/microsoft.graph.agentIdentityBlueprint?$skiptoken=abc""
        }}";
        var page2 = @"{""value"": [ { ""id"": ""44444444-4444-4444-4444-444444444444"", ""appId"": ""55555555-5555-5555-5555-555555555555"", ""displayName"": ""Page2 Blueprint"" } ] }";

        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("$top=100") && !s.Contains("skiptoken")),
            Arg.Any<CancellationToken>(),
            null)
            .Returns(JsonDocument.Parse(page1));
        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("skiptoken")),
            Arg.Any<CancellationToken>(),
            null)
            .Returns(JsonDocument.Parse(page2));

        var result = await _service.ListBlueprintsAsync(TestTenantId);

        result.Should().HaveCount(2, because: "both pages of results must be aggregated");
        result.Select(r => r.DisplayName).Should().Contain(["Page1 Blueprint", "Page2 Blueprint"]);
    }

    [Fact]
    public async Task ListBlueprintsAsync_WhenGraphQueryFails_ThrowsInvalidOperationException()
    {
        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            null)
            .Returns((JsonDocument?)null);

        var act = async () => await _service.ListBlueprintsAsync(TestTenantId);

        await act.Should().ThrowAsync<InvalidOperationException>(
            because: "an auth/query failure must surface as an exception so the caller returns a non-zero exit code instead of reporting an empty list as success");
    }

    // -----------------------------------------------------------------------
    // GetBlueprintByAppIdAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetBlueprintByAppIdAsync_WhenBlueprintExists_ReturnsFoundWithDetails()
    {
        var jsonResponse = $@"{{
            ""value"": [ {{ ""id"": ""{TestObjectId}"", ""appId"": ""{TestAppId}"", ""displayName"": ""{TestDisplayName}"" }} ]
        }}";
        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Is<string>(s => s.Contains("/beta/applications/microsoft.graph.agentIdentityBlueprint") && s.Contains(TestAppId)),
            Arg.Any<CancellationToken>(),
            null)
            .Returns(JsonDocument.Parse(jsonResponse));

        var result = await _service.GetBlueprintByAppIdAsync(TestTenantId, TestAppId);

        result.Found.Should().BeTrue();
        result.ObjectId.Should().Be(TestObjectId);
        result.AppId.Should().Be(TestAppId);
        result.DisplayName.Should().Be(TestDisplayName);
    }

    [Fact]
    public async Task GetBlueprintByAppIdAsync_WhenGraphReturnsMismatchedAppId_ReturnsNotFound()
    {
        var mismatchedAppId = "99999999-9999-9999-9999-999999999999";
        _graphApiService.GraphGetAsync(
                TestTenantId,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                null)
            .Returns(JsonDocument.Parse($$"""
                {
                  "value": [
                    {
                      "id": "{{TestObjectId}}",
                      "appId": "{{mismatchedAppId}}",
                      "displayName": "{{TestDisplayName}}"
                    }
                  ]
                }
                """));

        var result = await _service.GetBlueprintByAppIdAsync(TestTenantId, TestAppId);

        result.Found.Should().BeFalse(
            because: "a Graph result for another app must not satisfy exact tenant verification");
        result.ErrorMessage.Should().Contain(
            "incomplete or inconsistent",
            because: "the caller must be able to distinguish a mismatched response from an absent blueprint");
    }

    [Fact]
    public async Task GetBlueprintByAppIdAsync_WhenGraphReturnsMissingDisplayName_ReturnsNotFound()
    {
        _graphApiService.GraphGetAsync(
                TestTenantId,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                null)
            .Returns(JsonDocument.Parse($$"""
                {
                  "value": [
                    {
                      "id": "{{TestObjectId}}",
                      "appId": "{{TestAppId}}"
                    }
                  ]
                }
                """));

        var result = await _service.GetBlueprintByAppIdAsync(TestTenantId, TestAppId);

        result.Found.Should().BeFalse(
            because: "an incomplete Graph record must not be persisted as an authoritative blueprint");
    }

    [Fact]
    public async Task GetBlueprintByAppIdAsync_WhenNotFoundInTenant_ReturnsNotFound()
    {
        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            null)
            .Returns(JsonDocument.Parse(@"{""value"": []}"));

        var result = await _service.GetBlueprintByAppIdAsync(TestTenantId, TestAppId);

        result.Found.Should().BeFalse(
            because: "a blueprint that does not exist in this tenant (e.g. belongs to a different tenant) must not be treated as found");
    }

    [Fact]
    public async Task GetBlueprintByAppIdAsync_WhenAppIdIsNotAGuid_ReturnsNotFoundWithoutCallingGraph()
    {
        var result = await _service.GetBlueprintByAppIdAsync(TestTenantId, "not-a-guid");

        result.Found.Should().BeFalse(because: "malformed input must be rejected before any Graph call is made");
        await _graphApiService.DidNotReceiveWithAnyArgs().GraphGetAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task GetBlueprintByAppIdAsync_OnGraphFailure_ReturnsNotFound()
    {
        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            null)
            .Returns((JsonDocument?)null);

        var result = await _service.GetBlueprintByAppIdAsync(TestTenantId, TestAppId);

        result.Found.Should().BeFalse(because: "a query failure must not be misreported as a successful not-found result being confused with success");
    }

    [Fact]
    public async Task GetBlueprintByAppIdAsync_WhenCancelled_PropagatesCancellation()
    {
        _graphApiService.GraphGetAsync(
                TestTenantId,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                null)
            .Returns(Task.FromException<JsonDocument?>(new OperationCanceledException()));

        var act = async () => await _service.GetBlueprintByAppIdAsync(TestTenantId, TestAppId);

        await act.Should().ThrowAsync<OperationCanceledException>(
            because: "cancellation must not be converted into a failed lookup");
    }
}
