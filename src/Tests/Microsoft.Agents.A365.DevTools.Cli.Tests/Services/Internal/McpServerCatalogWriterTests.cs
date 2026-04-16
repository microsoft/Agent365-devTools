// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Internal;

public class McpServerCatalogWriterTests
{
    [Fact]
    public void WriteCatalog_V1WrappedObject_WritesUnchanged()
    {
        // Arrange
        var v1Json = """{"mcpServers":[{"mcpServerName":"mcp_MailTools","url":"https://example.com"}]}""";

        // Act
        var path = McpServerCatalogWriter.WriteCatalog(v1Json);
        var written = File.ReadAllText(path);

        // Assert — root element still has the mcpServers wrapper
        using var doc = JsonDocument.Parse(written);
        Assert.True(doc.RootElement.TryGetProperty("mcpServers", out var servers));
        Assert.Equal(1, servers.GetArrayLength());
    }

    [Fact]
    public void WriteCatalog_V2RawArray_WrapsInMcpServersEnvelope()
    {
        // Arrange — V2 endpoint returns a bare array
        var v2Json = """[{"mcpServerName":"mcp_MailTools","url":"https://example.com"}]""";

        // Act
        var path = McpServerCatalogWriter.WriteCatalog(v2Json);
        var written = File.ReadAllText(path);

        // Assert — normalized to wrapped format
        using var doc = JsonDocument.Parse(written);
        Assert.True(doc.RootElement.TryGetProperty("mcpServers", out var servers));
        Assert.Equal(1, servers.GetArrayLength());
    }

    [Fact]
    public void WriteCatalog_V2RawArray_PreservesAllV2Fields()
    {
        // Arrange
        var v2Json = """
            [
              {
                "mcpServerName": "mcp_TeamsServer",
                "id": "3fa2b1d9-6e2c-52b9-be4f-95148edff98e",
                "url": "https://test.agent365.svc.cloud.dev.microsoft/agents/servers/mcp_TeamsServer",
                "scope": "Tools.ListInvoke.All",
                "audience": "2cc60bb0-1024-48c8-95f0-1fce211a04d8",
                "publisher": "Microsoft"
              }
            ]
            """;

        // Act
        var path = McpServerCatalogWriter.WriteCatalog(v2Json);
        var written = File.ReadAllText(path);

        // Assert — all V2 fields survive the normalization
        using var doc = JsonDocument.Parse(written);
        var server = doc.RootElement.GetProperty("mcpServers")[0];
        Assert.Equal("mcp_TeamsServer", server.GetProperty("mcpServerName").GetString());
        Assert.Equal("3fa2b1d9-6e2c-52b9-be4f-95148edff98e", server.GetProperty("id").GetString());
        Assert.Equal("Tools.ListInvoke.All", server.GetProperty("scope").GetString());
        Assert.Equal("2cc60bb0-1024-48c8-95f0-1fce211a04d8", server.GetProperty("audience").GetString());
        Assert.Equal("Microsoft", server.GetProperty("publisher").GetString());
    }

    [Theory]
    [InlineData("McpServers.Mail.All", true)]
    [InlineData("McpServers.Calendar.All", true)]
    [InlineData("McpServers.OneDriveSharepoint.All", true)]
    [InlineData("Tools.ListInvoke.All", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void McpConstants_IsV1Scope_ReturnsExpected(string? scope, bool expected)
    {
        Assert.Equal(expected, McpConstants.IsV1Scope(scope));
    }

    [Fact]
    public void McpConstants_V2ScopeValue_IsToolsListInvokeAll()
    {
        Assert.Equal("Tools.ListInvoke.All", McpConstants.V2ScopeValue);
    }

}
