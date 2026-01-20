// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Text.Json;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class FederatedCredentialServiceTests
{
    private readonly ILogger<FederatedCredentialService> _logger;
    private readonly GraphApiService _graphApiService;
    private readonly FederatedCredentialService _service;
    private const string TestTenantId = "12345678-1234-1234-1234-123456789012";
    private const string TestBlueprintObjectId = "87654321-4321-4321-4321-210987654321";
    private const string TestMsiPrincipalId = "11111111-1111-1111-1111-111111111111";
    private const string TestIssuer = "https://login.microsoftonline.com/12345678-1234-1234-1234-123456789012/v2.0";
    private const string TestCredentialName = "TestCredential-MSI";

    public FederatedCredentialServiceTests()
    {
        _logger = Substitute.For<ILogger<FederatedCredentialService>>();
        
        // Use ForPartsOf to create a partial mock of the concrete GraphApiService class
        // This allows mocking of virtual methods (GraphGetAsync, GraphPostWithResponseAsync)
        var mockLogger = Substitute.For<ILogger<GraphApiService>>();
        var mockExecutor = Substitute.ForPartsOf<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        _graphApiService = Substitute.ForPartsOf<GraphApiService>(mockLogger, mockExecutor);
        
        _service = new FederatedCredentialService(_logger, _graphApiService);
    }

    [Fact]
    public async Task GetFederatedCredentialsAsync_WhenCredentialsExist_ReturnsListOfCredentials()
    {
        // Arrange
        var jsonResponse = $@"{{
            ""value"": [
                {{
                    ""id"": ""cred-id-1"",
                    ""name"": ""Credential1"",
                    ""issuer"": ""{TestIssuer}"",
                    ""subject"": ""{TestMsiPrincipalId}"",
                    ""audiences"": [""api://AzureADTokenExchange""]
                }},
                {{
                    ""id"": ""cred-id-2"",
                    ""name"": ""Credential2"",
                    ""issuer"": ""{TestIssuer}"",
                    ""subject"": ""different-principal"",
                    ""audiences"": [""api://AzureADTokenExchange""]
                }}
            ]
        }}";
        var jsonDoc = JsonDocument.Parse(jsonResponse);

        _graphApiService.GraphGetAsync(
            TestTenantId,
            $"/beta/applications/{TestBlueprintObjectId}/federatedIdentityCredentials",
            Arg.Any<CancellationToken>())
            .Returns(jsonDoc);

        // Act
        var result = await _service.GetFederatedCredentialsAsync(TestTenantId, TestBlueprintObjectId);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result[0].Name.Should().Be("Credential1");
        result[0].Subject.Should().Be(TestMsiPrincipalId);
        result[1].Name.Should().Be("Credential2");
    }

    [Fact]
    public async Task GetFederatedCredentialsAsync_WhenNoCredentials_ReturnsEmptyList()
    {
        // Arrange
        var jsonResponse = @"{""value"": []}";
        var jsonDoc = JsonDocument.Parse(jsonResponse);

        _graphApiService.GraphGetAsync(
            TestTenantId,
            $"/beta/applications/{TestBlueprintObjectId}/federatedIdentityCredentials",
            Arg.Any<CancellationToken>())
            .Returns(jsonDoc);

        // Act
        var result = await _service.GetFederatedCredentialsAsync(TestTenantId, TestBlueprintObjectId);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckFederatedCredentialExistsAsync_WhenMatchingCredentialExists_ReturnsTrue()
    {
        // Arrange
        var jsonResponse = $@"{{
            ""value"": [
                {{
                    ""id"": ""cred-id-1"",
                    ""name"": ""Credential1"",
                    ""issuer"": ""{TestIssuer}"",
                    ""subject"": ""{TestMsiPrincipalId}"",
                    ""audiences"": [""api://AzureADTokenExchange""]
                }}
            ]
        }}";
        var jsonDoc = JsonDocument.Parse(jsonResponse);

        _graphApiService.GraphGetAsync(
            TestTenantId,
            $"/beta/applications/{TestBlueprintObjectId}/federatedIdentityCredentials",
            Arg.Any<CancellationToken>())
            .Returns(jsonDoc);

        // Act
        var result = await _service.CheckFederatedCredentialExistsAsync(
            TestTenantId,
            TestBlueprintObjectId,
            TestMsiPrincipalId,
            TestIssuer);

        // Assert
        result.Should().NotBeNull();
        result.Exists.Should().BeTrue();
        result.ExistingCredential.Should().NotBeNull();
        result.ExistingCredential!.Name.Should().Be("Credential1");
        result.ExistingCredential.Subject.Should().Be(TestMsiPrincipalId);
    }

    [Fact]
    public async Task CheckFederatedCredentialExistsAsync_WhenNoMatchingCredential_ReturnsFalse()
    {
        // Arrange
        var jsonResponse = $@"{{
            ""value"": [
                {{
                    ""id"": ""cred-id-1"",
                    ""name"": ""Credential1"",
                    ""issuer"": ""{TestIssuer}"",
                    ""subject"": ""different-principal"",
                    ""audiences"": [""api://AzureADTokenExchange""]
                }}
            ]
        }}";
        var jsonDoc = JsonDocument.Parse(jsonResponse);

        _graphApiService.GraphGetAsync(
            TestTenantId,
            $"/beta/applications/{TestBlueprintObjectId}/federatedIdentityCredentials",
            Arg.Any<CancellationToken>())
            .Returns(jsonDoc);

        // Act
        var result = await _service.CheckFederatedCredentialExistsAsync(
            TestTenantId,
            TestBlueprintObjectId,
            TestMsiPrincipalId,
            TestIssuer);

        // Assert
        result.Should().NotBeNull();
        result.Exists.Should().BeFalse();
        result.ExistingCredential.Should().BeNull();
    }

    [Fact]
    public async Task CheckFederatedCredentialExistsAsync_IsCaseInsensitive()
    {
        // Arrange
        var jsonResponse = $@"{{
            ""value"": [
                {{
                    ""id"": ""cred-id-1"",
                    ""name"": ""Credential1"",
                    ""issuer"": ""{TestIssuer.ToLower()}"",
                    ""subject"": ""{TestMsiPrincipalId.ToUpper()}"",
                    ""audiences"": [""api://AzureADTokenExchange""]
                }}
            ]
        }}";
        var jsonDoc = JsonDocument.Parse(jsonResponse);

        _graphApiService.GraphGetAsync(
            TestTenantId,
            $"/beta/applications/{TestBlueprintObjectId}/federatedIdentityCredentials",
            Arg.Any<CancellationToken>())
            .Returns(jsonDoc);

        // Act - Pass in different casing
        var result = await _service.CheckFederatedCredentialExistsAsync(
            TestTenantId,
            TestBlueprintObjectId,
            TestMsiPrincipalId.ToLower(),
            TestIssuer.ToUpper());

        // Assert
        result.Should().NotBeNull();
        result.Exists.Should().BeTrue();
    }

    [Fact]
    public async Task CreateFederatedCredentialAsync_WhenSuccessful_ReturnsSuccess()
    {
        // Arrange
        var jsonResponse = $@"{{
            ""id"": ""cred-id-new"",
            ""name"": ""{TestCredentialName}"",
            ""issuer"": ""{TestIssuer}"",
            ""subject"": ""{TestMsiPrincipalId}""
        }}";
        var jsonDoc = JsonDocument.Parse(jsonResponse);
        var successResponse = new GraphApiService.GraphResponse
        {
            IsSuccess = true,
            StatusCode = 200,
            ReasonPhrase = "OK",
            Body = jsonResponse,
            Json = jsonDoc
        };

        _graphApiService.GraphPostWithResponseAsync(
            TestTenantId,
            $"/beta/applications/{TestBlueprintObjectId}/federatedIdentityCredentials",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Returns(successResponse);

        // Act
        var result = await _service.CreateFederatedCredentialAsync(
            TestTenantId,
            TestBlueprintObjectId,
            TestCredentialName,
            TestIssuer,
            TestMsiPrincipalId,
            new List<string> { "api://AzureADTokenExchange" });

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.AlreadyExisted.Should().BeFalse();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task CreateFederatedCredentialAsync_WhenHttp409Conflict_ReturnsSuccessWithAlreadyExisted()
    {
        // Arrange - Return HTTP 409 Conflict
        var conflictResponse = new GraphApiService.GraphResponse
        {
            IsSuccess = false,
            StatusCode = 409,
            ReasonPhrase = "Conflict",
            Body = @"{""error"": {""code"": ""Request_ResourceExists"", ""message"": ""Resource already exists""}}",
            Json = null
        };
        
        _graphApiService.GraphPostWithResponseAsync(
            TestTenantId,
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Returns(conflictResponse);

        // Act
        var result = await _service.CreateFederatedCredentialAsync(
            TestTenantId,
            TestBlueprintObjectId,
            TestCredentialName,
            TestIssuer,
            TestMsiPrincipalId,
            new List<string> { "api://AzureADTokenExchange" });

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue(); // 409 is treated as success
        result.AlreadyExisted.Should().BeTrue();
    }

    [Fact]
    public async Task CreateFederatedCredentialAsync_WhenStandardEndpointFails_TriesFallbackEndpoint()
    {
        // Arrange
        var standardEndpoint = $"/beta/applications/{TestBlueprintObjectId}/federatedIdentityCredentials";
        var fallbackEndpoint = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{TestBlueprintObjectId}/federatedIdentityCredentials";

        // Standard endpoint returns failure (e.g., HTTP 400)
        var standardFailureResponse = new GraphApiService.GraphResponse
        {
            IsSuccess = false,
            StatusCode = 400,
            ReasonPhrase = "Bad Request",
            Body = @"{""error"": {""message"": ""Agent Blueprints not supported on this endpoint""}}",
            Json = null
        };

        _graphApiService.GraphPostWithResponseAsync(
            TestTenantId,
            standardEndpoint,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Returns(standardFailureResponse);

        // Fallback endpoint succeeds
        var jsonResponse = $@"{{
            ""id"": ""cred-id-new"",
            ""name"": ""{TestCredentialName}""
        }}";
        var jsonDoc = JsonDocument.Parse(jsonResponse);
        var fallbackSuccessResponse = new GraphApiService.GraphResponse
        {
            IsSuccess = true,
            StatusCode = 200,
            ReasonPhrase = "OK",
            Body = jsonResponse,
            Json = jsonDoc
        };

        _graphApiService.GraphPostWithResponseAsync(
            TestTenantId,
            fallbackEndpoint,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Returns(fallbackSuccessResponse);

        // Act
        var result = await _service.CreateFederatedCredentialAsync(
            TestTenantId,
            TestBlueprintObjectId,
            TestCredentialName,
            TestIssuer,
            TestMsiPrincipalId,
            new List<string> { "api://AzureADTokenExchange" });

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();

        // Verify both endpoints were called
        await _graphApiService.Received(1).GraphPostWithResponseAsync(
            TestTenantId,
            standardEndpoint,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>());

        await _graphApiService.Received(1).GraphPostWithResponseAsync(
            TestTenantId,
            fallbackEndpoint,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>());
    }

    [Fact]
    public async Task CreateFederatedCredentialAsync_WhenBothEndpointsFail_ReturnsFailure()
    {
        // Arrange
        _graphApiService.GraphPostWithResponseAsync(
            TestTenantId,
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Throws(new Exception("General API failure"));

        // Act
        var result = await _service.CreateFederatedCredentialAsync(
            TestTenantId,
            TestBlueprintObjectId,
            TestCredentialName,
            TestIssuer,
            TestMsiPrincipalId,
            new List<string> { "api://AzureADTokenExchange" });

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("General API failure");
    }

    [Fact]
    public async Task GetFederatedCredentialsAsync_OnException_ReturnsEmptyList()
    {
        // Arrange
        _graphApiService.GraphGetAsync(
            TestTenantId,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Throws(new Exception("Network error"));

        // Act
        var result = await _service.GetFederatedCredentialsAsync(TestTenantId, TestBlueprintObjectId);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFederatedCredentialsAsync_WhenStandardEndpointReturnsEmpty_TriesFallbackEndpoint()
    {
        // Arrange
        var standardEndpoint = $"/beta/applications/{TestBlueprintObjectId}/federatedIdentityCredentials";
        var fallbackEndpoint = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{TestBlueprintObjectId}/federatedIdentityCredentials";

        // Standard endpoint returns empty array
        var emptyResponse = @"{""value"": []}";
        var emptyJsonDoc = JsonDocument.Parse(emptyResponse);

        _graphApiService.GraphGetAsync(
            TestTenantId,
            standardEndpoint,
            Arg.Any<CancellationToken>())
            .Returns(emptyJsonDoc);

        // Fallback endpoint returns credentials
        var fallbackResponse = $@"{{
            ""value"": [
                {{
                    ""id"": ""cred-id-1"",
                    ""name"": ""AgentBlueprintCredential"",
                    ""issuer"": ""{TestIssuer}"",
                    ""subject"": ""{TestMsiPrincipalId}"",
                    ""audiences"": [""api://AzureADTokenExchange""]
                }}
            ]
        }}";
        var fallbackJsonDoc = JsonDocument.Parse(fallbackResponse);

        _graphApiService.GraphGetAsync(
            TestTenantId,
            fallbackEndpoint,
            Arg.Any<CancellationToken>())
            .Returns(fallbackJsonDoc);

        // Act
        var result = await _service.GetFederatedCredentialsAsync(TestTenantId, TestBlueprintObjectId);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        result[0].Name.Should().Be("AgentBlueprintCredential");
        result[0].Subject.Should().Be(TestMsiPrincipalId);

        // Verify both endpoints were called
        await _graphApiService.Received(1).GraphGetAsync(
            TestTenantId,
            standardEndpoint,
            Arg.Any<CancellationToken>());

        await _graphApiService.Received(1).GraphGetAsync(
            TestTenantId,
            fallbackEndpoint,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFederatedCredentialsAsync_WithMalformedCredentials_ReturnsOnlyValidOnes()
    {
        // Arrange - JSON response with mixed valid and malformed credentials
        var jsonResponse = $@"{{
            ""value"": [
                {{
                    ""id"": ""cred-id-1"",
                    ""name"": ""ValidCredential1"",
                    ""issuer"": ""{TestIssuer}"",
                    ""subject"": ""{TestMsiPrincipalId}"",
                    ""audiences"": [""api://AzureADTokenExchange""]
                }},
                {{
                    ""id"": ""cred-id-2"",
                    ""name"": ""MissingSubject""
                }},
                {{
                    ""id"": ""cred-id-3"",
                    ""name"": ""ValidCredential2"",
                    ""issuer"": ""{TestIssuer}"",
                    ""subject"": ""different-principal"",
                    ""audiences"": [""api://AzureADTokenExchange""]
                }},
                {{
                    ""issuer"": ""{TestIssuer}"",
                    ""subject"": ""another-principal""
                }}
            ]
        }}";
        var jsonDoc = JsonDocument.Parse(jsonResponse);

        _graphApiService.GraphGetAsync(
            TestTenantId,
            $"/beta/applications/{TestBlueprintObjectId}/federatedIdentityCredentials",
            Arg.Any<CancellationToken>())
            .Returns(jsonDoc);

        // Act
        var result = await _service.GetFederatedCredentialsAsync(TestTenantId, TestBlueprintObjectId);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(2); // Only the 2 valid credentials
        result[0].Name.Should().Be("ValidCredential1");
        result[0].Subject.Should().Be(TestMsiPrincipalId);
        result[1].Name.Should().Be("ValidCredential2");
        result[1].Subject.Should().Be("different-principal");
    }
}
