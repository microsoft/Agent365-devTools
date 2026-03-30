// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Unit tests for V1/V2 migration logic in DevelopCommand:
///   - ATG audience resolution used by the add-mcp-servers legacy warning (Item 1)
///   - VERSION column derivation used by list-configured (Item 3)
/// </summary>
public class DevelopCommandV1V2MigrationTests
{
    // ── Item 1: audience resolution (ATG fallback logic) ────────────────────

    [Theory]
    [InlineData(null, true)]                                           // missing → ATG
    [InlineData("", true)]                                             // empty → ATG
    [InlineData("   ", true)]                                          // whitespace → ATG
    [InlineData("api://mcp-mailtools", true)]                          // legacy api:// → ATG
    [InlineData("API://UPPER", true)]                                  // api:// case-insensitive → ATG
    [InlineData("ea9ffc3e-8a23-4a7d-836d-234d7c7565c1", true)]        // explicit ATG AppId → ATG
    [InlineData("EA9FFC3E-8A23-4A7D-836D-234D7C7565C1", true)]        // ATG AppId upper-case → ATG
    [InlineData("05879165-0320-489e-b644-f72b33f3edf0", false)]        // per-server GUID → not ATG
    [InlineData("2cc60bb0-1024-48c8-95f0-1fce211a04d8", false)]        // different per-server GUID → not ATG
    public void AudienceResolution_MatchesAtgFallbackRules(string? rawAudience, bool expectsAtg)
    {
        // This mirrors the resolution logic in UpsertMcpServersInManifest:
        //   resolved = (null/empty/api://) ? ATG AppId : rawAudience
        var resolved = string.IsNullOrWhiteSpace(rawAudience) ||
            rawAudience.StartsWith("api://", StringComparison.OrdinalIgnoreCase)
            ? McpConstants.Agent365ToolsProdAppId
            : rawAudience;

        var isAtg = string.Equals(resolved, McpConstants.Agent365ToolsProdAppId,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(expectsAtg, isAtg);
    }

    // ── Item 3: VERSION column derivation ───────────────────────────────────

    [Theory]
    [InlineData("McpServers.Mail.All", "V1")]
    [InlineData("McpServers.Calendar.All", "V1")]
    [InlineData("mcpservers.teams.all", "V1")]          // case-insensitive V1
    [InlineData("Tools.ListInvoke.All", "V2")]
    [InlineData("tools.listinvoke.all", "V2")]          // case-insensitive V2
    [InlineData("Unknown.ScopeValue", "Unknown")]
    [InlineData("", "Unknown")]
    [InlineData(null, "Unknown")]
    public void VersionDerivation_FromScope_ReturnsExpectedColumn(string? scope, string expectedVersion)
    {
        // This mirrors the VERSION derivation logic in CreateListConfiguredSubcommand:
        //   version = IsV1Scope → "V1" : scope == V2ScopeValue → "V2" : "Unknown"
        var version = McpConstants.IsV1Scope(scope) ? "V1"
            : string.Equals(scope, McpConstants.V2ScopeValue, StringComparison.OrdinalIgnoreCase) ? "V2"
            : "Unknown";

        Assert.Equal(expectedVersion, version);
    }

    [Fact]
    public void V1ScopePattern_DoesNotMatchV2Scope()
    {
        Assert.False(McpConstants.IsV1Scope(McpConstants.V2ScopeValue),
            "V2 scope (Tools.ListInvoke.All) must not match the V1 pattern");
    }

    [Fact]
    public void V2ScopeValue_DoesNotMatchV1Pattern()
    {
        Assert.False(McpConstants.IsV1Scope("Tools.ListInvoke.All"));
    }

    [Fact]
    public void MetadataScope_IsNeither_V1_Nor_V2_ByDesign()
    {
        const string metadataScope = "McpServersMetadata.Read.All";

        // "McpServersMetadata.Read.All" does NOT start with "McpServers." (note the dot):
        // char 11 is 'M' not '.' — so IsV1Scope returns false.
        Assert.False(McpConstants.IsV1Scope(metadataScope),
            "McpServersMetadata.Read.All does not match the McpServers.*.All V1 pattern");

        Assert.False(string.Equals(metadataScope, McpConstants.V2ScopeValue, StringComparison.OrdinalIgnoreCase),
            "McpServersMetadata.Read.All is not the V2 scope");
    }
}
