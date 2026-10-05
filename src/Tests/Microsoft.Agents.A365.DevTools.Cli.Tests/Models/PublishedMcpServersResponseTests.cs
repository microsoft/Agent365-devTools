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
              "servers": [
                {
                  "mcpServerName": "zeta-alias",
                  "status": "Blocked",
                  "sourceMcpServerName": "msdyn_Zeta",
                  "sourceEnvironmentId": "env-zeta-id"
                },
                {
                  "mcpServerName": "alpha-alias",
                  "status": "PendingApproval",
                  "sourceMcpServerName": "msdyn_Alpha",
                  "sourceEnvironmentId": "env-alpha-id"
                }
              ]
            }
            """;

        // Act
        var response = JsonDeserializationHelper.DeserializeWithDoubleSerialization<PublishedMcpServersResponse>(
            json, NullLogger.Instance);

        // Assert
        response.Should().NotBeNull();
        response!.GetServers().Should().BeEquivalentTo(
            new[]
            {
                new PublishedMcpServer
                {
                    McpServerName = "zeta-alias",
                    Status = "Blocked",
                    SourceMcpServerName = "msdyn_Zeta",
                    SourceEnvironmentId = "env-zeta-id"
                },
                new PublishedMcpServer
                {
                    McpServerName = "alpha-alias",
                    Status = "PendingApproval",
                    SourceMcpServerName = "msdyn_Alpha",
                    SourceEnvironmentId = "env-alpha-id"
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
            """{"servers":null}""", NullLogger.Instance);

        // Assert
        response.Should().NotBeNull();
        response!.GetServers().Should().BeEmpty(
            because: "the command iterates the servers array, so a null array must read as no servers rather than crash");
    }
}
