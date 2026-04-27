// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

/// <summary>
/// Unit tests for the SetupHelpers bootstrap helper methods:
/// BuildConfiguredPermissionSpecsAsync, ResolveBootstrapTenantIdAsync,
/// ResolveBootstrapClientAppIdAsync, and GetJsonString.
/// </summary>
public class SetupHelpersBootstrapTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CommandExecutor _mockExecutor;
    private readonly GraphApiService _mockGraph;

    public SetupHelpersBootstrapTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        var execLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.For<CommandExecutor>(execLogger);

        _mockGraph = Substitute.ForPartsOf<GraphApiService>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── BuildConfiguredPermissionSpecsAsync ───────────────────────────────────

    [Fact]
    public async Task BuildConfiguredPermissionSpecsAsync_NoManifest_IncludesGraphAndFixedSpecs()
    {
        // Arrange: no ToolingManifest.json in tempDir — manifest read falls back to empty scopes
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var specs = await SetupHelpers.BuildConfiguredPermissionSpecsAsync(config, setInheritable: true);

        // Assert: Graph spec is always present
        specs.Should().Contain(s => s.ResourceAppId == AuthenticationConstants.MicrosoftGraphResourceAppId,
            because: "Microsoft Graph is always included in the DW permission spec list");

        // Assert: fixed platform APIs (Messaging Bot, Observability, Power Platform)
        specs.Should().Contain(s => s.ResourceAppId == ConfigConstants.MessagingBotApiAppId,
            because: "Messaging Bot API is a fixed DW permission");
        specs.Should().Contain(s => s.ResourceAppId == ConfigConstants.ObservabilityApiAppId,
            because: "Observability API is a fixed DW permission");
        specs.Should().Contain(s => s.ResourceAppId == PowerPlatformConstants.PowerPlatformApiResourceAppId,
            because: "Power Platform API is a fixed DW permission");

        // Assert: Graph spec carries the default agent application scopes
        var graphSpec = specs.First(s => s.ResourceAppId == AuthenticationConstants.MicrosoftGraphResourceAppId);
        graphSpec.Scopes.Should().NotBeEmpty(
            because: "AgentApplicationScopes always includes at least the default set of delegated Graph scopes");
    }

    [Fact]
    public async Task BuildConfiguredPermissionSpecsAsync_WithValidCustomPermission_IncludesCustomSpec()
    {
        // Arrange
        var config = new Agent365Config
        {
            DeploymentProjectPath = _tempDir,
            CustomBlueprintPermissions = new List<CustomResourcePermission>
            {
                new() { ResourceAppId = "a1b2c3d4-0000-0000-0000-000000000000", ResourceName = "My API", Scopes = new List<string> { "custom.scope" } }
            }
        };

        // Act
        var specs = await SetupHelpers.BuildConfiguredPermissionSpecsAsync(config, setInheritable: false);

        // Assert
        specs.Should().Contain(s => s.ResourceAppId == "a1b2c3d4-0000-0000-0000-000000000000" && s.Scopes.Contains("custom.scope"),
            because: "valid custom permissions must be appended to the spec list");

        // Assert: no duplicates for the custom permission
        specs.Count(s => s.ResourceAppId == "a1b2c3d4-0000-0000-0000-000000000000").Should().Be(1,
            because: "each custom permission must appear exactly once in the spec list");
    }

    [Fact]
    public async Task BuildConfiguredPermissionSpecsAsync_WithInvalidCustomPermission_ExcludesIt()
    {
        // Arrange: custom permission with empty ResourceAppId is invalid
        var config = new Agent365Config
        {
            DeploymentProjectPath = _tempDir,
            CustomBlueprintPermissions = new List<CustomResourcePermission>
            {
                new() { ResourceAppId = string.Empty, ResourceName = "Bad Perm", Scopes = new List<string> { "scope" } }
            }
        };

        // Act
        var specs = await SetupHelpers.BuildConfiguredPermissionSpecsAsync(config, setInheritable: false);

        // Assert
        specs.Should().NotContain(s => string.IsNullOrEmpty(s.ResourceAppId),
            because: "permissions with an empty ResourceAppId fail validation and must be excluded");
    }

    [Fact]
    public async Task BuildConfiguredPermissionSpecsAsync_WithPreComputedScopes_DoesNotReadManifest()
    {
        // Arrange: point DeploymentProjectPath at a non-existent directory so any attempt to
        // read the manifest from disk would return empty scopes. Provide a pre-computed
        // scopesByAudience with a custom audience entry — if the method reads the manifest
        // instead of using the provided dict, the audience entry will be absent.
        var config = new Agent365Config
        {
            DeploymentProjectPath = Path.Combine(_tempDir, "nonexistent")
        };
        var precomputed = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "injected-audience-app-id", new[] { "Injected.Scope" } }
        };

        // Act
        var specs = await SetupHelpers.BuildConfiguredPermissionSpecsAsync(config, setInheritable: true, precomputed);

        // Assert: the injected audience must appear in the result
        specs.Should().Contain(s => s.ResourceAppId == "injected-audience-app-id" && s.Scopes.Contains("Injected.Scope"),
            because: "when scopesByAudience is supplied the method must use it instead of reading the manifest from disk");
    }

    // ── ResolveBootstrapTenantIdAsync ─────────────────────────────────────────

    [Fact]
    public async Task ResolveBootstrapTenantIdAsync_WhenFlagProvided_ReturnsFlagWithoutCallingExecutor()
    {
        // Arrange
        const string tenantIdFlag = "explicit-tenant-id";
        var logger = NullLogger.Instance;

        // Act
        var result = await SetupHelpers.ResolveBootstrapTenantIdAsync(tenantIdFlag, _mockExecutor, logger);

        // Assert
        result.Should().Be(tenantIdFlag, because: "an explicit --tenant-id flag bypasses az account show");

        await _mockExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveBootstrapTenantIdAsync_WhenNoFlag_DetectsFromAzAccountShow()
    {
        // Arrange
        const string expectedTenantId = "detected-tenant-id";
        var logger = NullLogger.Instance;

        // TenantDetectionHelper calls: az account show --query tenantId -o tsv
        // which returns the raw tenant ID string (not JSON) as StandardOutput.
        _mockExecutor.ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult
            {
                ExitCode = 0,
                StandardOutput = expectedTenantId,
                StandardError = string.Empty
            }));

        // Act
        var result = await SetupHelpers.ResolveBootstrapTenantIdAsync(null, _mockExecutor, logger);

        // Assert
        result.Should().Be(expectedTenantId,
            because: "when no flag is provided the tenant is detected from az account show output");
    }

    [Fact]
    public async Task ResolveBootstrapTenantIdAsync_WhenNoFlag_AndExecutorFails_ReturnsNull()
    {
        // Arrange
        var logger = NullLogger.Instance;

        _mockExecutor.ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult
            {
                ExitCode = 1,
                StandardOutput = string.Empty,
                StandardError = "az: command not found"
            }));

        // Act
        var result = await SetupHelpers.ResolveBootstrapTenantIdAsync(null, _mockExecutor, logger);

        // Assert
        result.Should().BeNull(because: "a failed az account show must return null, not throw");
    }

    // ── ResolveBootstrapClientAppIdAsync ──────────────────────────────────────

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_WhenGraphServiceIsNull_ReturnsNull()
    {
        // Act
        var result = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
            "tenant-id", graphApiService: null, NullLogger.Instance, CancellationToken.None);

        // Assert
        result.Should().BeNull(because: "without a GraphApiService there is no way to resolve the client app ID");
    }

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_WhenEntraLookupSucceeds_ReturnsClientAppId()
    {
        // Arrange
        const string clientAppId = "resolved-client-app-id";
        _mockGraph.FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(clientAppId));

        // Act
        var result = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
            "tenant-id", _mockGraph, NullLogger.Instance, CancellationToken.None,
            preferLocalConfig: false);

        // Assert
        result.Should().Be(clientAppId,
            because: "when Entra lookup succeeds the resolved app ID is returned");
    }

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_DoesNotMutateGraphServiceCustomClientAppId()
    {
        // Arrange: the side effect (graphApiService.CustomClientAppId = ...) was removed from the
        // helper. Callers are responsible for setting CustomClientAppId after receiving the result.
        const string clientAppId = "some-app-id";
        _mockGraph.FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(clientAppId));

        var idBefore = _mockGraph.CustomClientAppId;

        // Act
        await SetupHelpers.ResolveBootstrapClientAppIdAsync(
            "tenant-id", _mockGraph, NullLogger.Instance, CancellationToken.None);

        // Assert
        _mockGraph.CustomClientAppId.Should().Be(idBefore,
            because: "ResolveBootstrapClientAppIdAsync must not mutate graphApiService.CustomClientAppId — the caller owns that assignment");
    }

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_WhenPreferLocalConfig_AndTenantMatches_UsesLocalConfig()
    {
        // Arrange: write an a365.config.json whose tenantId matches
        const string tenantId = "matching-tenant";
        const string configClientAppId = "config-client-app-id";
        var configJson = $"{{\"tenantId\":\"{tenantId}\",\"clientAppId\":\"{configClientAppId}\"}}";
        var configPath = Path.Combine(_tempDir, ConfigConstants.DefaultConfigFileName);
        await File.WriteAllTextAsync(configPath, configJson);

        // Save and restore CWD to isolate the test
        var originalCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _tempDir;
        try
        {
            // Act
            var result = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
                tenantId, _mockGraph, NullLogger.Instance, CancellationToken.None,
                preferLocalConfig: true);

            // Assert
            result.Should().Be(configClientAppId,
                because: "when preferLocalConfig=true and the local config tenant matches, the config value is used");

            // Entra lookup must not be called when the local config provides the value
            await _mockGraph.DidNotReceive().FindApplicationByDisplayNameAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
        }
    }

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_WhenPreferLocalConfig_AndTenantMismatch_FallsBackToEntra()
    {
        // Arrange: local config has a different tenantId
        const string activeTenantId = "active-tenant";
        const string configTenantId = "stale-tenant";
        const string entraClientAppId = "entra-resolved-app-id";

        var configJson = $"{{\"tenantId\":\"{configTenantId}\",\"clientAppId\":\"stale-client-id\"}}";
        var configPath = Path.Combine(_tempDir, ConfigConstants.DefaultConfigFileName);
        await File.WriteAllTextAsync(configPath, configJson);

        _mockGraph.FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(entraClientAppId));

        var originalCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _tempDir;
        try
        {
            // Act
            var result = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
                activeTenantId, _mockGraph, NullLogger.Instance, CancellationToken.None,
                preferLocalConfig: true);

            // Assert
            result.Should().Be(entraClientAppId,
                because: "when the local config tenant does not match the active tenant, the Entra lookup must be used");
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
        }
    }

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_WhenLookupFails_AndUserEntersValidId_ReturnsId()
    {
        // Arrange
        const string enteredId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        _mockGraph.FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        _mockGraph.ApplicationExistsByAppIdAsync(
            Arg.Any<string>(), enteredId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var originalIn = Console.In;
        Console.SetIn(new StringReader(enteredId + "\n"));
        try
        {
            // Act
            var result = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
                "tenant-id", _mockGraph, NullLogger.Instance, CancellationToken.None);

            // Assert
            result.Should().Be(enteredId,
                because: "a valid app ID entered at the prompt must be returned after Graph confirms it exists");
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_WhenLookupFails_AndUserCancels_ReturnsNull()
    {
        // Arrange
        _mockGraph.FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var originalIn = Console.In;
        Console.SetIn(new StringReader("\n"));
        try
        {
            // Act
            var result = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
                "tenant-id", _mockGraph, NullLogger.Instance, CancellationToken.None);

            // Assert
            result.Should().BeNull(because: "pressing Enter (empty input) must cancel and return null");
            await _mockGraph.DidNotReceive().ApplicationExistsByAppIdAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_WhenLookupFails_AndUserEntersNonExistentId_ReturnsNull()
    {
        // Arrange
        const string badId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        _mockGraph.FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        _mockGraph.ApplicationExistsByAppIdAsync(
            Arg.Any<string>(), badId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var originalIn = Console.In;
        Console.SetIn(new StringReader(badId + "\n"));
        try
        {
            // Act
            var result = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
                "tenant-id", _mockGraph, NullLogger.Instance, CancellationToken.None);

            // Assert
            result.Should().BeNull(
                because: "an app ID that Graph cannot find must fail fast rather than proceeding with an invalid ID");
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    // ── GetJsonString ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{\"key\":\"value\"}", "key", "value")]
    [InlineData("{\"key\":\"\"}", "key", "")]
    [InlineData("{\"key\":null}", "key", null)]
    [InlineData("{\"other\":\"x\"}", "key", null)]
    [InlineData("{\"key\":42}", "key", null)]
    public void GetJsonString_ReturnsExpected(string json, string key, string? expected)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = SetupHelpers.GetJsonString(doc.RootElement, key);
        result.Should().Be(expected);
    }
}
