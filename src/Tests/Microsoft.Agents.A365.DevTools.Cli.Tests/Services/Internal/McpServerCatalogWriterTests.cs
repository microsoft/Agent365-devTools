// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Internal;

/// <summary>
/// Tests in this class mutate the <c>A365_MCP_CATALOG_PATH</c> environment variable,
/// so they cannot run in parallel with any other test that reads or writes the catalog.
/// </summary>
[CollectionDefinition("McpServerCatalogWriterEnvVar", DisableParallelization = true)]
public class McpServerCatalogWriterEnvVarCollection { }

[Collection("McpServerCatalogWriterEnvVar")]
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

    // ── Env-var normalization tests for GetCatalogPath ──────────────────────
    // These pin the behavior of A365_MCP_CATALOG_PATH: trim whitespace, strip a
    // single pair of wrapping quotes, and fall back to the default temp path when
    // the result is empty (Copilot PR #418 review caught the empty-after-strip gap).

    [Fact]
    public void GetCatalogPath_EnvVarSetToOnlyDoubleQuotes_FallsBackToDefault()
    {
        // Grant/Copilot PR #418 review: setting A365_MCP_CATALOG_PATH=`""` (two
        // literal double-quote characters) currently strips the quotes and returns
        // the empty string, which makes File.WriteAllText/ReadAllText throw with
        // a confusing "path is empty" exception. After strip, the helper must
        // re-validate and fall back to the default temp path.
        var saved = Environment.GetEnvironmentVariable(McpServerCatalogWriter.CatalogPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(McpServerCatalogWriter.CatalogPathEnvVar, "\"\"");

            var result = McpServerCatalogWriter.GetCatalogPath();

            result.Should().NotBeNullOrWhiteSpace(
                because: "an env var of just wrapping quotes must not produce an empty path — File.* would throw downstream with an unhelpful error");
            result.Should().EndWith("mcpServerCatalog.json",
                because: "the helper must fall back to the default temp catalog file when the override resolves to nothing usable");
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServerCatalogWriter.CatalogPathEnvVar, saved);
        }
    }

    [Fact]
    public void GetCatalogPath_EnvVarSetToOnlySingleQuotes_FallsBackToDefault()
    {
        var saved = Environment.GetEnvironmentVariable(McpServerCatalogWriter.CatalogPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(McpServerCatalogWriter.CatalogPathEnvVar, "''");

            var result = McpServerCatalogWriter.GetCatalogPath();

            result.Should().NotBeNullOrWhiteSpace(
                because: "an env var of just wrapping single quotes must not produce an empty path");
            result.Should().EndWith("mcpServerCatalog.json",
                because: "single-quote-wrapped empty overrides must fall back the same way double-quote-wrapped empty overrides do");
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServerCatalogWriter.CatalogPathEnvVar, saved);
        }
    }

    [Fact]
    public void GetCatalogPath_EnvVarWithQuotedRealPath_StripsQuotesAndReturnsPath()
    {
        // Guardrail: legitimate quoted paths must still be honored after the
        // empty-after-strip fix lands.
        var saved = Environment.GetEnvironmentVariable(McpServerCatalogWriter.CatalogPathEnvVar);
        try
        {
            var expected = Path.Combine(Path.GetTempPath(), "a365-catalog-quoted-test.json");
            Environment.SetEnvironmentVariable(
                McpServerCatalogWriter.CatalogPathEnvVar, $"\"{expected}\"");

            var result = McpServerCatalogWriter.GetCatalogPath();

            result.Should().Be(expected,
                because: "a real path wrapped in quotes (e.g. `set VAR=\"C:\\path\"` on Windows) must be returned unquoted so File.* can use it directly");
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServerCatalogWriter.CatalogPathEnvVar, saved);
        }
    }
}
