// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using System.Reflection;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Internal;

/// <summary>
/// Unit tests for HttpClientFactory
/// </summary>
public class HttpClientFactoryTests
{
    [Fact]
    public void CreateAuthenticatedClient_WithDefaultUserAgent_SetsCorrectUserAgentHeader()
    {
        // Arrange
        var expectedVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();

        // Act
        using var client = HttpClientFactory.CreateAuthenticatedClient();

        // Assert
        client.DefaultRequestHeaders.UserAgent.Should().NotBeEmpty();
        var userAgentString = client.DefaultRequestHeaders.UserAgent.ToString();
        userAgentString.Should().StartWith($"{HttpClientFactory.DefaultUserAgentPrefix}/");
        userAgentString.Should().Contain(expectedVersion ?? "");
    }

    [Fact]
    public void CreateAuthenticatedClient_WithCustomUserAgentPrefix_SetsCustomPrefix()
    {
        // Arrange
        const string customPrefix = "CustomAgent";

        // Act
        using var client = HttpClientFactory.CreateAuthenticatedClient(userAgentPrefix: customPrefix);

        // Assert
        var userAgentString = client.DefaultRequestHeaders.UserAgent.ToString();
        userAgentString.Should().StartWith($"{customPrefix}/");
    }

    [Fact]
    public void CreateAuthenticatedClient_WithEmptyUserAgentPrefix_SetsEmptyPrefix()
    {
        // Arrange
        const string emptyPrefix = "";

        // Act
        using var client = HttpClientFactory.CreateAuthenticatedClient(userAgentPrefix: emptyPrefix);

        // Assert
        var userAgentString = client.DefaultRequestHeaders.UserAgent.ToString();
        userAgentString.Should().StartWith($"{HttpClientFactory.DefaultUserAgentPrefix}/");
    }

    [Fact]
    public void CreateAuthenticatedClient_WithSpecialCharactersInPrefix_HandlesCorrectly()
    {
        // Arrange
        const string specialPrefix = "Agent-365_CLI.v2.0";

        // Act
        using var client = HttpClientFactory.CreateAuthenticatedClient(userAgentPrefix: specialPrefix);

        // Assert
        var userAgentString = client.DefaultRequestHeaders.UserAgent.ToString();
        userAgentString.Should().Contain(specialPrefix);
    }

    [Fact]
    public void CreateAuthenticatedClient_WithBothParameters_SetsBothHeaders()
    {
        // Arrange
        const string testToken = "test-token";
        const string customPrefix = "MyCustomAgent";

        // Act
        using var client = HttpClientFactory.CreateAuthenticatedClient(testToken, customPrefix);

        // Assert
        client.DefaultRequestHeaders.Authorization.Should().NotBeNull();
        client.DefaultRequestHeaders.Authorization!.Parameter.Should().Be(testToken);

        var userAgentString = client.DefaultRequestHeaders.UserAgent.ToString();
        userAgentString.Should().StartWith($"{customPrefix}/");
    }

    [Fact]
    public void CreateAuthenticatedClient_UserAgentHeader_ContainsVersionNumber()
    {
        // Arrange
        var expectedVersion = Assembly.GetExecutingAssembly().GetName().Version;

        // Act
        using var client = HttpClientFactory.CreateAuthenticatedClient();

        // Assert
        var userAgentString = client.DefaultRequestHeaders.UserAgent.ToString();
        userAgentString.Should().Contain(expectedVersion?.ToString() ?? "");
    }

    [Fact]
    public void CreateAuthenticatedClient_WithNullCorrelationId_GeneratesCorrelationId()
    {
        // Act
        using var client = HttpClientFactory.CreateAuthenticatedClient(correlationId: null);

        // Assert
        client.DefaultRequestHeaders.Contains(HttpClientFactory.CorrelationIdHeaderName).Should().BeTrue();
        var correlationId = client.DefaultRequestHeaders.GetValues(HttpClientFactory.CorrelationIdHeaderName).First();
        correlationId.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(correlationId, out _).Should().BeTrue("Generated correlation ID should be a valid GUID");
    }

    [Fact]
    public void CreateAuthenticatedClient_WithEmptyCorrelationId_GeneratesCorrelationId()
    {
        // Act
        using var client = HttpClientFactory.CreateAuthenticatedClient(correlationId: "");

        // Assert
        client.DefaultRequestHeaders.Contains(HttpClientFactory.CorrelationIdHeaderName).Should().BeTrue();
        var correlationId = client.DefaultRequestHeaders.GetValues(HttpClientFactory.CorrelationIdHeaderName).First();
        correlationId.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(correlationId, out _).Should().BeTrue("Generated correlation ID should be a valid GUID");
    }

    [Fact]
    public void CreateAuthenticatedClient_WithWhitespaceCorrelationId_GeneratesCorrelationId()
    {
        // Act
        using var client = HttpClientFactory.CreateAuthenticatedClient(correlationId: "   ");

        // Assert
        client.DefaultRequestHeaders.Contains(HttpClientFactory.CorrelationIdHeaderName).Should().BeTrue();
        var correlationId = client.DefaultRequestHeaders.GetValues(HttpClientFactory.CorrelationIdHeaderName).First();
        correlationId.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(correlationId, out _).Should().BeTrue("Generated correlation ID should be a valid GUID");
    }

    [Fact]
    public void CreateAuthenticatedClient_WithProvidedCorrelationId_UsesProvidedValue()
    {
        // Arrange
        const string providedCorrelationId = "test-correlation-id-12345";

        // Act
        using var client = HttpClientFactory.CreateAuthenticatedClient(correlationId: providedCorrelationId);

        // Assert
        client.DefaultRequestHeaders.Contains(HttpClientFactory.CorrelationIdHeaderName).Should().BeTrue();
        var correlationId = client.DefaultRequestHeaders.GetValues(HttpClientFactory.CorrelationIdHeaderName).First();
        correlationId.Should().Be(providedCorrelationId);
    }

    [Fact]
    public void CreateAuthenticatedClient_WithAllParameters_SetsAllHeaders()
    {
        // Arrange
        const string testToken = "test-token";
        const string customPrefix = "MyCustomAgent";
        const string providedCorrelationId = "workflow-correlation-id";

        // Act
        using var client = HttpClientFactory.CreateAuthenticatedClient(testToken, customPrefix, providedCorrelationId);

        // Assert
        client.DefaultRequestHeaders.Authorization.Should().NotBeNull();
        client.DefaultRequestHeaders.Authorization!.Parameter.Should().Be(testToken);

        var userAgentString = client.DefaultRequestHeaders.UserAgent.ToString();
        userAgentString.Should().StartWith($"{customPrefix}/");

        var correlationId = client.DefaultRequestHeaders.GetValues(HttpClientFactory.CorrelationIdHeaderName).First();
        correlationId.Should().Be(providedCorrelationId);
    }

    [Fact]
    public void GenerateCorrelationId_ReturnsValidGuid()
    {
        // Act
        var correlationId = HttpClientFactory.GenerateCorrelationId();

        // Assert
        correlationId.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(correlationId, out _).Should().BeTrue("Generated correlation ID should be a valid GUID");
    }

    [Fact]
    public void GenerateCorrelationId_ReturnsUniqueValues()
    {
        // Act
        var id1 = HttpClientFactory.GenerateCorrelationId();
        var id2 = HttpClientFactory.GenerateCorrelationId();

        // Assert
        id1.Should().NotBe(id2, "Each call should generate a unique correlation ID");
    }

    [Fact]
    public void CreateAuthenticatedClient_SetsClientRequestIdHeader()
    {
        // Act
        using var client = HttpClientFactory.CreateAuthenticatedClient();

        // Assert
        client.DefaultRequestHeaders.Contains(HttpClientFactory.ClientRequestIdHeaderName).Should().BeTrue();
        var clientRequestId = client.DefaultRequestHeaders.GetValues(HttpClientFactory.ClientRequestIdHeaderName).First();
        clientRequestId.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(clientRequestId, out _).Should().BeTrue("Client request ID should be a valid GUID");
    }

    [Fact]
    public void CreateAuthenticatedClient_ClientRequestIdMatchesCorrelationId()
    {
        // Arrange
        const string providedCorrelationId = "matching-correlation-id-12345";

        // Act
        using var client = HttpClientFactory.CreateAuthenticatedClient(correlationId: providedCorrelationId);

        // Assert
        var correlationId = client.DefaultRequestHeaders.GetValues(HttpClientFactory.CorrelationIdHeaderName).First();
        var clientRequestId = client.DefaultRequestHeaders.GetValues(HttpClientFactory.ClientRequestIdHeaderName).First();

        correlationId.Should().Be(providedCorrelationId);
        clientRequestId.Should().Be(providedCorrelationId);
        clientRequestId.Should().Be(correlationId, "Client request ID should match correlation ID for Graph API tracing");
    }

    [Fact]
    public void CreateAuthenticatedClient_WithGeneratedCorrelationId_BothHeadersMatch()
    {
        // Act
        using var client = HttpClientFactory.CreateAuthenticatedClient();

        // Assert
        var correlationId = client.DefaultRequestHeaders.GetValues(HttpClientFactory.CorrelationIdHeaderName).First();
        var clientRequestId = client.DefaultRequestHeaders.GetValues(HttpClientFactory.ClientRequestIdHeaderName).First();

        correlationId.Should().Be(clientRequestId, "Both headers should have the same auto-generated correlation ID");
    }
}