// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

public class ManifestHelperGetScopesByAudienceTests : IDisposable
{
    private readonly string _tempDir;

    public ManifestHelperGetScopesByAudienceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteManifest(string json)
    {
        var path = Path.Combine(_tempDir, "ToolingManifest.json");
        File.WriteAllText(path, json);
        return path;
    }

    // ── V1 manifest ─────────────────────────────────────────────────────────

    [Fact]
    public async Task V1Manifest_AllEntriesGroupUnderAtgAppId()
    {
        // Arrange — V1: both entries share the ATG AppId audience
        var path = WriteManifest($$"""
            {
              "mcpServers": [
                {
                  "mcpServerName": "mcp_MailTools",
                  "url": "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools",
                  "scope": "McpServers.Mail.All",
                  "audience": "{{McpConstants.WorkIQToolsProdAppId}}"
                },
                {
                  "mcpServerName": "mcp_CalendarTools",
                  "url": "https://agent365.svc.cloud.microsoft/agents/servers/mcp_CalendarTools",
                  "scope": "McpServers.Calendar.All",
                  "audience": "{{McpConstants.WorkIQToolsProdAppId}}"
                }
              ]
            }
            """);

        // Act
        var result = await ManifestHelper.GetScopesByAudienceAsync(path);

        // Assert — single key: ATG AppId
        Assert.Single(result);
        Assert.True(result.ContainsKey(McpConstants.WorkIQToolsProdAppId));
        var scopes = result[McpConstants.WorkIQToolsProdAppId];
        Assert.Contains("McpServers.Mail.All", scopes);
        Assert.Contains("McpServers.Calendar.All", scopes);
        Assert.Contains("McpServersMetadata.Read.All", scopes);
    }

    [Fact]
    public async Task V1Manifest_NoAudienceField_FallsBackToAtgAppId()
    {
        // Arrange — V1 manifest with no audience field (older manifests)
        var path = WriteManifest("""
            {
              "mcpServers": [
                {
                  "mcpServerName": "mcp_MailTools",
                  "url": "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools",
                  "scope": "McpServers.Mail.All"
                }
              ]
            }
            """);

        // Act
        var result = await ManifestHelper.GetScopesByAudienceAsync(path);

        // Assert — falls back to ATG AppId
        Assert.True(result.ContainsKey(McpConstants.WorkIQToolsProdAppId));
        Assert.Contains("McpServers.Mail.All", result[McpConstants.WorkIQToolsProdAppId]);
    }

    [Fact]
    public async Task V1Manifest_ApiSlashAudienceFormat_FallsBackToAtgAppId()
    {
        // Arrange — legacy api:// audience written by older CLI versions
        var path = WriteManifest("""
            {
              "mcpServers": [
                {
                  "mcpServerName": "mcp_MailTools",
                  "url": "https://example.com",
                  "scope": "McpServers.Mail.All",
                  "audience": "api://mcp-mailtools"
                }
              ]
            }
            """);

        // Act
        var result = await ManifestHelper.GetScopesByAudienceAsync(path);

        // Assert — api:// audience treated as ATG fallback
        Assert.True(result.ContainsKey(McpConstants.WorkIQToolsProdAppId));
        Assert.Contains("McpServers.Mail.All", result[McpConstants.WorkIQToolsProdAppId]);
        Assert.DoesNotContain("api://mcp-mailtools", result.Keys);
    }

    // ── V2 manifest ─────────────────────────────────────────────────────────

    [Fact]
    public async Task V2Manifest_EntriesGroupByPerServerAppId()
    {
        // Arrange — V2: each server has its own GUID audience
        var path = WriteManifest("""
            {
              "mcpServers": [
                {
                  "mcpServerName": "mcp_MailTools",
                  "id": "3fb34f44-7f4e-4e9e-855f-072404166824",
                  "url": "https://test.agent365.svc.cloud.dev.microsoft/agents/servers/mcp_MailTools",
                  "scope": "McpServers.Mail.All",
                  "audience": "05879165-0320-489e-b644-f72b33f3edf0",
                  "publisher": "Microsoft"
                },
                {
                  "mcpServerName": "mcp_TeamsServer",
                  "id": "3fa2b1d9-6e2c-52b9-be4f-95148edff98e",
                  "url": "https://test.agent365.svc.cloud.dev.microsoft/agents/servers/mcp_TeamsServer",
                  "scope": "Tools.ListInvoke.All",
                  "audience": "2cc60bb0-1024-48c8-95f0-1fce211a04d8",
                  "publisher": "Microsoft"
                }
              ]
            }
            """);

        // Act
        var result = await ManifestHelper.GetScopesByAudienceAsync(path);

        // Assert — two per-server keys plus ATG (for McpServersMetadata.Read.All)
        Assert.True(result.ContainsKey("05879165-0320-489e-b644-f72b33f3edf0"));
        Assert.True(result.ContainsKey("2cc60bb0-1024-48c8-95f0-1fce211a04d8"));
        Assert.Contains("McpServers.Mail.All", result["05879165-0320-489e-b644-f72b33f3edf0"]);
        Assert.Contains("Tools.ListInvoke.All", result["2cc60bb0-1024-48c8-95f0-1fce211a04d8"]);
    }

    // ── Mixed manifest ───────────────────────────────────────────────────────

    [Fact]
    public async Task MixedManifest_ReturnsBothAtgAndPerServerKeys()
    {
        // Arrange — one V1 entry (ATG) + one V2 entry (per-server)
        var path = WriteManifest($$"""
            {
              "mcpServers": [
                {
                  "mcpServerName": "mcp_MailTools",
                  "scope": "McpServers.Mail.All",
                  "audience": "{{McpConstants.WorkIQToolsProdAppId}}"
                },
                {
                  "mcpServerName": "mcp_TeamsServer",
                  "scope": "Tools.ListInvoke.All",
                  "audience": "2cc60bb0-1024-48c8-95f0-1fce211a04d8"
                }
              ]
            }
            """);

        // Act
        var result = await ManifestHelper.GetScopesByAudienceAsync(path);

        // Assert — both keys present (additive by default)
        Assert.True(result.ContainsKey(McpConstants.WorkIQToolsProdAppId));
        Assert.True(result.ContainsKey("2cc60bb0-1024-48c8-95f0-1fce211a04d8"));
        Assert.Contains("McpServers.Mail.All", result[McpConstants.WorkIQToolsProdAppId]);
        Assert.Contains("Tools.ListInvoke.All", result["2cc60bb0-1024-48c8-95f0-1fce211a04d8"]);
    }

    [Fact]
    public async Task MixedManifest_ExcludeLegacyAtg_RemovesAtgKey()
    {
        // Arrange
        var path = WriteManifest($$"""
            {
              "mcpServers": [
                {
                  "mcpServerName": "mcp_MailTools",
                  "scope": "McpServers.Mail.All",
                  "audience": "{{McpConstants.WorkIQToolsProdAppId}}"
                },
                {
                  "mcpServerName": "mcp_TeamsServer",
                  "scope": "Tools.ListInvoke.All",
                  "audience": "2cc60bb0-1024-48c8-95f0-1fce211a04d8"
                }
              ]
            }
            """);

        // Act
        var result = await ManifestHelper.GetScopesByAudienceAsync(path, excludeLegacyAtg: true);

        // Assert — ATG key gone, per-server key remains
        Assert.False(result.ContainsKey(McpConstants.WorkIQToolsProdAppId));
        Assert.True(result.ContainsKey("2cc60bb0-1024-48c8-95f0-1fce211a04d8"));
        Assert.Contains("Tools.ListInvoke.All", result["2cc60bb0-1024-48c8-95f0-1fce211a04d8"]);
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyManifest_ReturnsOnlyMetadataScopeUnderAtgAppId()
    {
        // Arrange — valid manifest but no server entries
        var path = WriteManifest("""{"mcpServers":[]}""");

        // Act
        var result = await ManifestHelper.GetScopesByAudienceAsync(path);

        // Assert — McpServersMetadata.Read.All always seeded under ATG AppId
        Assert.True(result.ContainsKey(McpConstants.WorkIQToolsProdAppId));
        Assert.Contains("McpServersMetadata.Read.All", result[McpConstants.WorkIQToolsProdAppId]);
    }

    [Fact]
    public async Task ExcludeLegacyAtg_AllV1Entries_ReturnsEmptyDictionary()
    {
        // Arrange — all entries are V1 (ATG audience)
        var path = WriteManifest($$"""
            {
              "mcpServers": [
                {
                  "mcpServerName": "mcp_MailTools",
                  "scope": "McpServers.Mail.All",
                  "audience": "{{McpConstants.WorkIQToolsProdAppId}}"
                }
              ]
            }
            """);

        // Act
        var result = await ManifestHelper.GetScopesByAudienceAsync(path, excludeLegacyAtg: true);

        // Assert — all entries excluded, empty result
        Assert.Empty(result);
    }
}
