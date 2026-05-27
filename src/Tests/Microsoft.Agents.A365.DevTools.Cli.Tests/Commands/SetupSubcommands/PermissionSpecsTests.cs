// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands.SetupSubcommands;

/// <summary>
/// Tests for blueprint permission spec construction.
///
/// <para>
/// The contract these tests encode (the target after the refactor) is a single
/// input-driven rule that applies to <strong>both</strong> DW and non-DW agents:
/// </para>
/// <list type="bullet">
///   <item><description>Observability API and Power Platform API are always included.</description></item>
///   <item><description>Microsoft Graph is always included with <c>AgentApplicationScopes</c>.</description></item>
///   <item><description>Messaging Bot API is included when <c>isM365 == true</c>.</description></item>
///   <item><description>Agent 365 Tools (MCP audiences from <c>ToolingManifest.json</c>) are included when a manifest is present.</description></item>
///   <item><description>Valid <c>CustomBlueprintPermissions</c> entries are appended.</description></item>
/// </list>
///
/// <para>
/// The DW tests pass against the current code because the DW pipeline already
/// satisfies the contract. The unified-rule tests for non-DW are expected to
/// <strong>fail on the pre-refactor code</strong> — they encode the bug fix and
/// will turn green once <c>BuildPermissionSpecsAsync</c> uses one input-driven
/// pipeline for both agent types.
/// </para>
/// </summary>
[Collection("ConfigTests")]
public class PermissionSpecsTests : IDisposable
{
    /// <summary>
    /// Always-required ATG scope seeded by <c>GetScopesByAudienceAsync</c> for V1 client compatibility.
    /// Hardcoded here because no production constant exposes it — kept inline so a
    /// future extraction-to-constant change updates one place.
    /// </summary>
    private const string McpServersMetadataReadAll = "McpServersMetadata.Read.All";

    /// <summary>
    /// V1 manifest audience format. <c>ResolveAudienceOrAtgFallback</c> collapses any
    /// <c>api://</c>-prefixed audience onto the ATG AppId, so this value never appears
    /// in the produced spec list — its scope merges into the ATG entry instead.
    /// </summary>
    private const string V1LegacyMailAudience = "api://mcp-mailtools";

    /// <summary>
    /// V1 mapped scope for the legacy Mail audience. Resolved by
    /// <c>McpConstants.ServerScopeMappings.ServerToScope["mcp_MailTools"]</c>.
    /// </summary>
    private const string V1MailScope = "McpServers.Mail.All";

    private readonly string _tempDir;

    public PermissionSpecsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DW path — locks in current behavior that must not regress through the refactor.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DwPath_NoManifest_NoCustom_ProducesBaselineSpecSet()
    {
        // Arrange
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var specs = await SetupHelpers.BuildConfiguredPermissionSpecsAsync(config, setInheritable: true, isM365: true);

        // Assert: the set of resource app IDs is exactly the baseline DW set.
        ResourceAppIds(specs).Should().BeEquivalentTo(new[]
        {
            AuthenticationConstants.MicrosoftGraphResourceAppId,
            ConfigConstants.MessagingBotApiAppId,
            ConfigConstants.ObservabilityApiAppId,
            PowerPlatformConstants.PowerPlatformApiResourceAppId,
            McpConstants.WorkIQToolsProdAppId,
        }, because: "the DW baseline spec set is the four fixed platform APIs plus the ATG AppId (seeded with McpServersMetadata.Read.All for V1 compatibility)");

        // Assert: ATG entry carries only the seeded V1-compat scope when no manifest is present.
        SpecFor(specs, McpConstants.WorkIQToolsProdAppId).Scopes.Should().BeEquivalentTo(new[] { McpServersMetadataReadAll },
            because: "without a manifest the ATG audience only carries the always-seeded V1-compat metadata read scope");

        // Assert: Graph carries the default agent application scopes.
        SpecFor(specs, AuthenticationConstants.MicrosoftGraphResourceAppId).Scopes
            .Should().BeEquivalentTo(ConfigConstants.DefaultAgentApplicationScopes,
                because: "Microsoft Graph spec scopes must come from Agent365Config.AgentApplicationScopes");
    }

    [Fact]
    public async Task DwPath_WithV1Manifest_CollapsesLegacyAudienceOntoAtgAppId()
    {
        // Arrange: a V1-style manifest entry with an api:// audience.
        WriteManifest(new ManifestServer("mcp_MailTools", "https://example.invalid/mail", V1MailScope, V1LegacyMailAudience));
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var specs = await SetupHelpers.BuildConfiguredPermissionSpecsAsync(config, setInheritable: true, isM365: true);

        // Assert: api:// audience does NOT appear as a separate resource.
        ResourceAppIds(specs).Should().NotContain(V1LegacyMailAudience,
            because: "ResolveAudienceOrAtgFallback collapses api:// audiences onto the ATG AppId — they must not appear as their own resource");

        // Assert: the V1 scope merges into the ATG entry alongside the seeded V1-compat scope.
        SpecFor(specs, McpConstants.WorkIQToolsProdAppId).Scopes
            .Should().BeEquivalentTo(new[] { V1MailScope, McpServersMetadataReadAll },
                because: "V1 api:// audiences merge their scope into the ATG AppId entry to preserve V1 client compatibility");
    }

    [Fact]
    public async Task DwPath_WithValidCustomPermission_AppendsCustomSpecExactlyOnce()
    {
        // Arrange
        const string customId = "a1b2c3d4-0000-0000-0000-000000000000";
        const string customScope = "Custom.Scope";
        var config = new Agent365Config
        {
            DeploymentProjectPath = _tempDir,
            CustomBlueprintPermissions = new List<CustomResourcePermission>
            {
                new() { ResourceAppId = customId, ResourceName = "My API", Scopes = new List<string> { customScope } }
            }
        };

        // Act
        var specs = await SetupHelpers.BuildConfiguredPermissionSpecsAsync(config, setInheritable: true, isM365: true);

        // Assert
        specs.Should().ContainSingle(s => s.ResourceAppId == customId,
            because: "each valid custom permission must appear exactly once in the spec list");
        SpecFor(specs, customId).Scopes.Should().BeEquivalentTo(new[] { customScope });
    }

    [Fact]
    public async Task DwPath_WithInvalidCustomPermission_ExcludesIt()
    {
        // Arrange: empty ResourceAppId fails CustomResourcePermission.Validate().
        var config = new Agent365Config
        {
            DeploymentProjectPath = _tempDir,
            CustomBlueprintPermissions = new List<CustomResourcePermission>
            {
                new() { ResourceAppId = string.Empty, ResourceName = "Bad", Scopes = new List<string> { "scope" } }
            }
        };

        // Act
        var specs = await SetupHelpers.BuildConfiguredPermissionSpecsAsync(config, setInheritable: false, isM365: true);

        // Assert
        specs.Should().NotContain(s => string.IsNullOrEmpty(s.ResourceAppId),
            because: "permissions failing CustomResourcePermission.Validate() must not be stamped on the blueprint");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Unified rule for both DW and non-DW agents.
    //
    // These tests describe the target contract. The non-DW cases are expected
    // to FAIL on the pre-refactor code (the bug). They will pass once the
    // refactor routes both agent types through a single input-driven pipeline.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unified_NoManifest_NotM365_IncludesGraphObsPpAndSeededAtgButNotBot()
    {
        // Arrange
        var config = new Agent365Config { DeploymentProjectPath = _tempDir, AiTeammate = false };

        // Act
        var specs = await BuildSpecsForAgent(config, isM365: false);

        // Assert: required resources
        ResourceAppIds(specs).Should().Contain(AuthenticationConstants.MicrosoftGraphResourceAppId,
            because: "Microsoft Graph must be stamped on every blueprint regardless of agent type or M365 flag");
        ResourceAppIds(specs).Should().Contain(ConfigConstants.ObservabilityApiAppId,
            because: "Observability API is required for every agent");
        ResourceAppIds(specs).Should().Contain(PowerPlatformConstants.PowerPlatformApiResourceAppId,
            because: "Power Platform API is required for every agent");

        // Assert: ATG audience must be present even without a manifest — the V1-compat metadata
        // read scope is always seeded so existing V1 clients keep working. This matches the DW contract.
        ResourceAppIds(specs).Should().Contain(McpConstants.WorkIQToolsProdAppId,
            because: "the ATG audience is always seeded with the V1-compat McpServersMetadata.Read.All scope — non-DW agents must match the DW invariant");
        SpecFor(specs, McpConstants.WorkIQToolsProdAppId).Scopes
            .Should().BeEquivalentTo(new[] { McpServersMetadataReadAll },
                because: "without a manifest the ATG audience must carry only the V1-compat metadata read scope (no extra MCP scopes)");

        // Assert: Messaging Bot must be absent
        ResourceAppIds(specs).Should().NotContain(ConfigConstants.MessagingBotApiAppId,
            because: "Messaging Bot API must be excluded when isM365 is false — it has no purpose without an M365 messaging surface");
    }

    [Fact]
    public async Task Unified_NoManifest_IsM365_StampsBotAndSeededAtgWithV1CompatScopeOnly()
    {
        // Arrange
        var config = new Agent365Config { DeploymentProjectPath = _tempDir, AiTeammate = false };

        // Act
        var specs = await BuildSpecsForAgent(config, isM365: true);

        // Assert
        ResourceAppIds(specs).Should().Contain(ConfigConstants.MessagingBotApiAppId,
            because: "Messaging Bot API must be stamped when isM365 is true so the agent has a messaging surface");
        SpecFor(specs, McpConstants.WorkIQToolsProdAppId).Scopes
            .Should().BeEquivalentTo(new[] { McpServersMetadataReadAll },
                because: "without a manifest the ATG audience must carry only the V1-compat metadata read scope, even for M365 agents");
    }

    [Fact]
    public async Task Unified_WithManifest_NotM365_AddsMcpAudienceButStillNoMessagingBot()
    {
        // Arrange
        WriteManifest(new ManifestServer("mcp_MailTools", "https://example.invalid/mail", V1MailScope, V1LegacyMailAudience));
        var config = new Agent365Config { DeploymentProjectPath = _tempDir, AiTeammate = false };

        // Act
        var specs = await BuildSpecsForAgent(config, isM365: false);

        // Assert
        ResourceAppIds(specs).Should().Contain(McpConstants.WorkIQToolsProdAppId,
            because: "MCP audiences from ToolingManifest.json must be stamped regardless of agent type or M365 flag — this is the bug the refactor fixes");
        SpecFor(specs, McpConstants.WorkIQToolsProdAppId).Scopes
            .Should().Contain(McpServersMetadataReadAll,
                because: "the V1-compat metadata read scope must be seeded on the ATG audience whenever any MCP scope is configured");

        ResourceAppIds(specs).Should().NotContain(ConfigConstants.MessagingBotApiAppId,
            because: "Messaging Bot must remain excluded when isM365 is false, even with a manifest present");
    }

    [Fact]
    public async Task Unified_WithManifest_IsM365_StampsFullSet()
    {
        // Arrange
        WriteManifest(new ManifestServer("mcp_MailTools", "https://example.invalid/mail", V1MailScope, V1LegacyMailAudience));
        var config = new Agent365Config { DeploymentProjectPath = _tempDir, AiTeammate = false };

        // Act
        var specs = await BuildSpecsForAgent(config, isM365: true);

        // Assert: the full unified set is present.
        ResourceAppIds(specs).Should().BeEquivalentTo(new[]
        {
            AuthenticationConstants.MicrosoftGraphResourceAppId,
            ConfigConstants.MessagingBotApiAppId,
            ConfigConstants.ObservabilityApiAppId,
            PowerPlatformConstants.PowerPlatformApiResourceAppId,
            McpConstants.WorkIQToolsProdAppId,
        }, because: "with a manifest present and isM365 true, blueprint agents must receive the same spec set as DW agents — this is the unified-pipeline contract");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Invariants that must survive the refactor.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ObservabilityApi_CarriesBothDelegatedScopeAndAppRole()
    {
        // Arrange: smallest config that produces the Observability spec on either path.
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var specs = await SetupHelpers.BuildConfiguredPermissionSpecsAsync(config, setInheritable: true, isM365: true);

        // Assert
        var obs = SpecFor(specs, ConfigConstants.ObservabilityApiAppId);
        obs.Scopes.Should().BeEquivalentTo(new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
            because: "Observability API delegated scope grants OtelWrite for the OBO path");
        obs.AppRoleScopes.Should().BeEquivalentTo(new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
            because: "Observability API app role grants OtelWrite for the s2s path — losing either side breaks one auth mode");
    }

    [Fact]
    public async Task SetInheritableFlag_PropagatesToEverySpec()
    {
        // Arrange
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var inheritable = await SetupHelpers.BuildConfiguredPermissionSpecsAsync(config, setInheritable: true, isM365: true);
        var notInheritable = await SetupHelpers.BuildConfiguredPermissionSpecsAsync(config, setInheritable: false, isM365: true);

        // Assert
        inheritable.Should().OnlyContain(s => s.SetInheritable,
            because: "the setInheritable parameter must apply uniformly to every produced spec");
        notInheritable.Should().OnlyContain(s => !s.SetInheritable,
            because: "the setInheritable parameter must apply uniformly to every produced spec");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test scaffolding
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the spec list using the unified entry point. Both DW and non-DW callers route
    /// through <see cref="SetupHelpers.BuildConfiguredPermissionSpecsAsync"/>; the only difference
    /// in produced output is that non-M365 agents (<c>isM365: false</c>) exclude Messaging Bot API.
    /// </summary>
    private static Task<List<ResourcePermissionSpec>> BuildSpecsForAgent(Agent365Config config, bool isM365) =>
        SetupHelpers.BuildConfiguredPermissionSpecsAsync(config, setInheritable: true, isM365: isM365);

    private static IEnumerable<string> ResourceAppIds(IEnumerable<ResourcePermissionSpec> specs) =>
        specs.Select(s => s.ResourceAppId);

    private static ResourcePermissionSpec SpecFor(IEnumerable<ResourcePermissionSpec> specs, string resourceAppId) =>
        specs.Single(s => string.Equals(s.ResourceAppId, resourceAppId, StringComparison.OrdinalIgnoreCase));

    private void WriteManifest(params ManifestServer[] servers)
    {
        var manifest = new
        {
            schema = "ToolingManifest",
            version = "1.1",
            mcpServers = servers.Select(s => new
            {
                mcpServerName = s.Name,
                mcpServerUniqueName = s.Name,
                url = s.Url,
                scope = s.Scope,
                audience = s.Audience,
            }).ToArray()
        };
        var path = Path.Combine(_tempDir, McpConstants.ToolingManifestFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record ManifestServer(string Name, string Url, string Scope, string Audience);
}
