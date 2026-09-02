// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

/// <summary>
/// Unit tests for the SetupHelpers bootstrap helper methods:
/// BuildConfiguredPermissionSpecsAsync, ResolveBootstrapTenantIdAsync, ResolveBootstrapEnvironmentAsync,
/// ResolveBootstrapClientAppIdAsync, and GetJsonString.
/// </summary>
[Collection("ConfigTests")]
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

        // Default: the well-known first-party application's service principal is not present.
        // This preserves the pre-existing custom-app (display-name lookup) behavior exercised by
        // most tests in this file. Tests that specifically cover the new first-party default path
        // override this per-test with Arg.Is<string>(id => id == AuthenticationConstants.WellKnownClientAppId).
        // Configuring this default here (rather than leaving it unmocked) is required: ResolveBootstrapClientAppIdAsync
        // now checks the first-party service principal before the display-name lookup, and this is a
        // partial substitute (Substitute.ForPartsOf) — an unconfigured virtual call falls through to the
        // real GraphApiService implementation, which would attempt a real Graph/MSAL token acquisition.
        _mockGraph.LookupServicePrincipalByAppIdWithResponseAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                StatusCode = 200
            });
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
        var specs = await SetupHelpers.BuildConfiguredPermissionSpecsAsync(config, setInheritable: true, scopesByAudience: precomputed);

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

    // ── ResolveBootstrapEnvironmentAsync ───────────────────────────────────────

    [Fact]
    public async Task ResolveBootstrapEnvironmentAsync_WhenEnvironmentVariableIsSet_UsesItWithoutCallingAzureCli()
    {
        const string expectedEnvironment = "gcc";
        var originalEnvironment = Environment.GetEnvironmentVariable("A365_ENVIRONMENT");
        Environment.SetEnvironmentVariable("A365_ENVIRONMENT", $" {expectedEnvironment} ");
        try
        {
            var result = await SetupHelpers.ResolveBootstrapEnvironmentAsync(
                _mockExecutor, NullLogger.Instance, CancellationToken.None);

            result.Should().Be(expectedEnvironment,
                because: "an explicit environment override must select sovereign-cloud endpoints before the first Graph bootstrap call");
            await _mockExecutor.DidNotReceive().ExecuteAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("A365_ENVIRONMENT", originalEnvironment);
        }
    }

    [Fact]
    public async Task ResolveBootstrapEnvironmentAsync_WhenUnset_UsesActiveAzureCliCloud()
    {
        var originalEnvironment = Environment.GetEnvironmentVariable("A365_ENVIRONMENT");
        Environment.SetEnvironmentVariable("A365_ENVIRONMENT", null);
        try
        {
            _mockExecutor.ExecuteAsync(
                    "az", "cloud show --query name -o tsv",
                    Arg.Any<string?>(), true, true, Arg.Any<CancellationToken>())
                .Returns(new CommandResult
                {
                    ExitCode = 0,
                    StandardOutput = "AzureCloud\n",
                    StandardError = string.Empty
                });

            var result = await SetupHelpers.ResolveBootstrapEnvironmentAsync(
                _mockExecutor, NullLogger.Instance, CancellationToken.None);

            result.Should().Be("AzureCloud",
                because: "config-free bootstrap must target the active Azure CLI cloud before resolving the client application");
        }
        finally
        {
            Environment.SetEnvironmentVariable("A365_ENVIRONMENT", originalEnvironment);
        }
    }

    [Fact]
    public async Task ResolveBootstrapEnvironmentAsync_WhenAzureCliUsesUsGovernment_RequiresExplicitEnvironment()
    {
        var originalEnvironment = Environment.GetEnvironmentVariable("A365_ENVIRONMENT");
        Environment.SetEnvironmentVariable("A365_ENVIRONMENT", null);
        try
        {
            _mockExecutor.ExecuteAsync(
                    "az", "cloud show --query name -o tsv",
                    Arg.Any<string?>(), true, true, Arg.Any<CancellationToken>())
                .Returns(new CommandResult
                {
                    ExitCode = 0,
                    StandardOutput = "AzureUSGovernment\n",
                    StandardError = string.Empty
                });

            var act = () => SetupHelpers.ResolveBootstrapEnvironmentAsync(
                _mockExecutor, NullLogger.Instance, CancellationToken.None);

            await act.Should().ThrowAsync<SetupValidationException>(
                because: "AzureUSGovernment cannot identify whether the tenant is GCC Moderate, GCC High, or DoD");
        }
        finally
        {
            Environment.SetEnvironmentVariable("A365_ENVIRONMENT", originalEnvironment);
        }
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

    // ── First-party default resolution ────────────────────────────────────────

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_WhenFirstPartyServicePrincipalExists_ReturnsWellKnownIdWithoutDisplayNameLookup()
    {
        // Arrange: the well-known first-party application's service principal is present in the
        // tenant (a customer tenant may have only this manager-created SP, no application object).
        _mockGraph.LookupServicePrincipalByAppIdWithResponseAsync(
                Arg.Any<string>(),
                Arg.Is<string>(id => id == AuthenticationConstants.WellKnownClientAppId),
                Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "first-party-sp-object-id",
                StatusCode = 200
            });

        // Act
        var result = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
            "tenant-id", _mockGraph, NullLogger.Instance, CancellationToken.None);

        // Assert
        result.Should().Be(AuthenticationConstants.WellKnownClientAppId,
            because: "the well-known first-party application must be the default identity whenever its service principal is present");

        await _mockGraph.DidNotReceive().FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _mockGraph.DidNotReceive().ApplicationExistsByAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_ChecksFirstPartyIdentityViaServicePrincipalsNotApplications()
    {
        // Arrange
        _mockGraph.LookupServicePrincipalByAppIdWithResponseAsync(
                Arg.Any<string>(),
                Arg.Is<string>(id => id == AuthenticationConstants.WellKnownClientAppId),
                Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "first-party-sp-object-id",
                StatusCode = 200
            });

        // Act
        await SetupHelpers.ResolveBootstrapClientAppIdAsync(
            "tenant-id", _mockGraph, NullLogger.Instance, CancellationToken.None);

        // Assert: the well-known default identity must be resolved/validated via GET /v1.0/servicePrincipals,
        // never GET /v1.0/applications — customer tenants may contain only the manager-created SP.
        await _mockGraph.Received(1).LookupServicePrincipalByAppIdWithResponseAsync(
            "tenant-id",
            AuthenticationConstants.WellKnownClientAppId,
            Arg.Any<CancellationToken>());
        await _mockGraph.DidNotReceive().ApplicationExistsByAppIdAsync(
            Arg.Any<string>(), AuthenticationConstants.WellKnownClientAppId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_WhenFirstPartyServicePrincipalAbsent_FallsBackToCustomAppDisplayNameLookup()
    {
        // Arrange: first-party SP absent (default stub from constructor already returns null for
        // any appId) — the legacy custom-app-by-display-name flow must still run unchanged.
        const string customAppId = "custom-app-id";
        _mockGraph.FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(customAppId));

        // Act
        var result = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
            "tenant-id", _mockGraph, NullLogger.Instance, CancellationToken.None);

        // Assert: preserves existing custom-app behavior when the first-party default is unavailable.
        result.Should().Be(customAppId,
            because: "when the first-party application's service principal is absent, resolution must fall back to the tenant-owned custom app discovered by display name");

        await _mockGraph.Received(1).FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), AuthenticationConstants.WellKnownClientAppDisplayName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_WhenFirstPartyLookupFails_DoesNotFallBackToCustomAppCreation()
    {
        _mockGraph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                AuthenticationConstants.WellKnownClientAppId,
                Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = false,
                StatusCode = 503,
                FailureReason = "Microsoft Graph service-principal lookup failed: HTTP 503 Service Unavailable."
            });

        Func<Task> act = async () => await SetupHelpers.ResolveBootstrapClientAppIdAsync(
            "tenant-id", _mockGraph, NullLogger.Instance, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ClientAppValidationException>(
            because: "an operational lookup failure must not be mistaken for an absent first-party service principal");
        exception.Which.ErrorDetails.Should().Contain(
            detail => detail.Contains("HTTP 503", StringComparison.Ordinal));
        await _mockGraph.DidNotReceive().FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _mockGraph.DidNotReceive().CreateCliClientAppAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_WhenFirstPartyLookupCannotAuthenticate_DoesNotFallBackSilently()
    {
        // "NoAuth" is what the presence probe reports when no Graph token could be acquired at all.
        // That is an operational failure, not evidence that the first-party application is absent.
        _mockGraph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                AuthenticationConstants.WellKnownClientAppId,
                Arg.Any<CancellationToken>())
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = false,
                StatusCode = 0,
                FailureReason = "Microsoft Graph service-principal lookup failed: NoAuth."
            });

        Func<Task> act = async () => await SetupHelpers.ResolveBootstrapClientAppIdAsync(
            "tenant-id", _mockGraph, NullLogger.Instance, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ClientAppValidationException>(
            because: "a token-acquisition failure must surface, not be downgraded to a silent custom-app fallback");
        exception.Which.ErrorDetails.Should().Contain(
            detail => detail.Contains("NoAuth", StringComparison.Ordinal),
            because: "the operator needs to see that authentication, not app absence, blocked the lookup");
        await _mockGraph.DidNotReceive().FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        _mockGraph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RoleCheckResult.DoesNotHaveRole));
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
        _mockGraph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RoleCheckResult.DoesNotHaveRole));

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
        _mockGraph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RoleCheckResult.DoesNotHaveRole));
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

    // ── Global Administrator menu (Create / Enter / Cancel) ───────────────────

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_Admin_ChooseCreate_CreatesAppAndReturnsId()
    {
        // Arrange: well-known Entra app missing, current user is Global Admin,
        // user picks (C)reate and confirms (Y)es to consent.
        const string newAppId = "new-app-id";
        const string newSpId = "new-sp-id";
        const string graphSpId = "graph-sp-id";

        _mockGraph.FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        _mockGraph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RoleCheckResult.HasRole));
        _mockGraph.CreateCliClientAppAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string?, string?)>((newAppId, newSpId)));
        _mockGraph.LookupServicePrincipalByAppIdAsync(
            Arg.Any<string>(),
            Arg.Is<string>(id => id == AuthenticationConstants.MicrosoftGraphResourceAppId),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(graphSpId));
        _mockGraph.CreateOrUpdateOauth2PermissionGrantAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult(true));

        var originalIn = Console.In;
        Console.SetIn(new StringReader("C\nY\n"));
        try
        {
            // Act
            var result = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
                "tenant-id", _mockGraph, NullLogger.Instance, CancellationToken.None);

            // Assert: the newly created app ID is returned and the permission grant was attempted.
            result.Should().Be(newAppId,
                because: "when an admin chooses (C)reate, the helper must return the appId returned by CreateCliClientAppAsync");

            await _mockGraph.Received(1).CreateCliClientAppAsync(
                Arg.Any<string>(),
                Arg.Is<string>(name => name == AuthenticationConstants.WellKnownClientAppDisplayName),
                Arg.Any<CancellationToken>());

            await _mockGraph.Received(1).CreateOrUpdateOauth2PermissionGrantAsync(
                Arg.Any<string>(),
                Arg.Is<string>(id => id == newSpId),
                Arg.Is<string>(id => id == graphSpId),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IEnumerable<string>?>());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_Admin_EntersExistingId_ReturnsId()
    {
        // Arrange: admin user types an existing app ID directly at the single prompt.
        const string existingId = "existing-app-id";

        _mockGraph.FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        _mockGraph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RoleCheckResult.HasRole));
        _mockGraph.ApplicationExistsByAppIdAsync(
            Arg.Any<string>(), existingId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var originalIn = Console.In;
        Console.SetIn(new StringReader($"{existingId}\n"));
        try
        {
            // Act
            var result = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
                "tenant-id", _mockGraph, NullLogger.Instance, CancellationToken.None);

            // Assert: the entered app ID is returned, app creation was NOT attempted.
            result.Should().Be(existingId,
                because: "an admin who types an existing app ID at the prompt receives that ID after Graph confirms it exists");

            await _mockGraph.DidNotReceive().CreateCliClientAppAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_Admin_EmptyInput_ReturnsNull()
    {
        // Arrange: admin presses Enter without typing — cancels the flow.
        _mockGraph.FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        _mockGraph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RoleCheckResult.HasRole));

        var originalIn = Console.In;
        Console.SetIn(new StringReader("\n"));
        try
        {
            // Act
            var result = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
                "tenant-id", _mockGraph, NullLogger.Instance, CancellationToken.None);

            // Assert
            result.Should().BeNull(
                because: "empty input at the admin prompt cancels and returns null without creating or verifying any app");

            await _mockGraph.DidNotReceive().CreateCliClientAppAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            await _mockGraph.DidNotReceive().ApplicationExistsByAppIdAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public async Task ResolveBootstrapClientAppIdAsync_NonAdmin_OnlyIdPrompt()
    {
        // Arrange: non-admin user — the menu must not be shown; only the existing
        // 'Enter your client app ID' prompt runs.
        const string existingId = "existing-app-id";

        _mockGraph.FindApplicationByDisplayNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        _mockGraph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RoleCheckResult.DoesNotHaveRole));
        _mockGraph.ApplicationExistsByAppIdAsync(
            Arg.Any<string>(), existingId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var originalIn = Console.In;
        // Single-line input — non-admin does not see the menu, so only the existing
        // 'Enter your client app ID' prompt consumes input.
        Console.SetIn(new StringReader($"{existingId}\n"));
        try
        {
            // Act
            var result = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
                "tenant-id", _mockGraph, NullLogger.Instance, CancellationToken.None);

            // Assert
            result.Should().Be(existingId,
                because: "non-admin users skip the create/enter/cancel menu and go straight to the app ID prompt");

            await _mockGraph.DidNotReceive().CreateCliClientAppAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
