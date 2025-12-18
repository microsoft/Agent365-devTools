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
}