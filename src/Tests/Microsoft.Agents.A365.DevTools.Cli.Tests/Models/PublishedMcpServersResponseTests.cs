// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Models;

public class PublishedMcpServersResponseTests
{
    [Fact]
    public void Deserialize_PublishedMcpServersBody_MapsEveryFieldInResponseOrder()
    {
        // Arrange
        const string json = """
            {
              "count": 2,
              "servers": [
                {
                  "mcpServerName": "zeta-alias",
                  "displayName": "Zeta Server",
                  "description": null,
                  "url": "https://tenant.example/agents/zeta-alias",
                  "status": "Blocked",
                  "sourceEnvironmentId": "env-zeta-id",
                  "sourceEnvironmentName": "Zeta Environment",
                  "sourceServerName": "msdyn_Zeta"
                },
                {
                  "mcpServerName": "alpha-alias",
                  "displayName": "Alpha Server",
                  "description": "Alpha description",
                  "url": "https://tenant.example/agents/alpha-alias",
                  "status": "PendingApproval",
                  "sourceEnvironmentId": "env-alpha-id",
                  "sourceEnvironmentName": "Alpha Environment",
                  "sourceServerName": "msdyn_Alpha"
                }
              ]
            }
            """;

        // Act
        var response = JsonDeserializationHelper.DeserializeWithDoubleSerialization<PublishedMcpServersResponse>(
            json, NullLogger.Instance);

        // Assert
        response.Should().NotBeNull();
        response!.Count.Should().Be(2);
        response.GetServers().Should().BeEquivalentTo(
            new[]
            {
                new PublishedMcpServer
                {
                    McpServerName = "zeta-alias",
                    DisplayName = "Zeta Server",
                    Description = null,
                    Url = "https://tenant.example/agents/zeta-alias",
                    Status = "Blocked",
                    SourceEnvironmentId = "env-zeta-id",
                    SourceEnvironmentName = "Zeta Environment",
                    SourceServerName = "msdyn_Zeta"
                },
                new PublishedMcpServer
                {
                    McpServerName = "alpha-alias",
                    DisplayName = "Alpha Server",
                    Description = "Alpha description",
                    Url = "https://tenant.example/agents/alpha-alias",
                    Status = "PendingApproval",
                    SourceEnvironmentId = "env-alpha-id",
                    SourceEnvironmentName = "Alpha Environment",
                    SourceServerName = "msdyn_Alpha"
                }
            },
            options => options.WithStrictOrdering(),
            because: "the CLI must read every camelCase field the publishedMcpServers route returns, keeping the platform's order");
    }

    [Fact]
    public void GetServers_NullServersArray_ReturnsEmpty()
    {
        // Act
        var response = JsonDeserializationHelper.DeserializeWithDoubleSerialization<PublishedMcpServersResponse>(
            """{"count":0,"servers":null}""", NullLogger.Instance);

        // Assert
        response.Should().NotBeNull();
        response!.GetServers().Should().BeEmpty(
            because: "the command iterates the servers array, so a null array must read as no servers rather than crash");
    }
}
