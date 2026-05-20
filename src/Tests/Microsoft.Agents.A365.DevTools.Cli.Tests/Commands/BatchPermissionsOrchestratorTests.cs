// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Unit tests for BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync.
/// Focused on the non-fatal phase-independence contract: each phase failure
/// must not prevent subsequent phases from running.
/// </summary>
public class BatchPermissionsOrchestratorTests : IDisposable
{
    private readonly GraphApiService _graph;
    private readonly AgentBlueprintService _blueprintService;
    private readonly ILogger _logger;

    public BatchPermissionsOrchestratorTests()
    {
        _logger = NullLogger.Instance;
        _graph = Substitute.ForPartsOf<GraphApiService>();
        _blueprintService = Substitute.ForPartsOf<AgentBlueprintService>(
            Substitute.For<ILogger<AgentBlueprintService>>(), _graph);

        // Suppress real browser launches and 180s consent polls during orchestrator tests.
        // Reset in Dispose so state does not leak into other test classes.
        BrowserHelper.OpenUrlOverrideForTests = (_, _) => { };
        AdminConsentHelper.BypassConsentChecksForTests = true;
    }

    public void Dispose()
    {
        BrowserHelper.OpenUrlOverrideForTests = null;
        AdminConsentHelper.BypassConsentChecksForTests = false;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// When no specs are supplied the orchestrator returns success immediately
    /// without making any service calls. This guards against empty-state panics
    /// and ensures callers with no resources to configure do not trigger
    /// unnecessary Graph authentication.
    /// </summary>
    [Fact]
    public async Task ConfigureAllPermissions_EmptySpecs_ReturnsTrueWithoutCallingServices()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "tenant-id",
            AgentBlueprintId = "app-id"
        };

        // Act
        var (blueprintUpdated, inheritedConfigured, consentGranted, consentUrl) =
            await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                _graph, _blueprintService, config,
                blueprintAppId: "app-id",
                tenantId: "tenant-id",
                specs: Array.Empty<ResourcePermissionSpec>(),
                _logger,
                setupResults: null,
                ct: default);

        // Assert
        blueprintUpdated.Should().BeTrue();
        inheritedConfigured.Should().BeTrue();
        consentGranted.Should().BeTrue();
        consentUrl.Should().BeNull();

        // No Graph calls should be made for an empty spec list
        await _graph.DidNotReceive().GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }

    /// <summary>
    /// When Phase 1 fails (Graph authentication unavailable), Phase 2 is skipped
    /// but Phase 3 still runs and returns a non-null consent URL for non-admins.
    ///
    /// This is the key non-admin contract: even with no Graph access the caller
    /// always receives a URL to present to the tenant administrator, rather than
    /// getting an exception or an empty result with no recovery path.
    /// </summary>
    [Fact]
    public async Task ConfigureAllPermissions_WhenPhase1AuthFails_Phase2SkippedAndPhase3ReturnsConsentUrl()
    {
        // Arrange — GraphGetAsync returns null, simulating delegated auth failure in Phase 1
        _graph.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns((JsonDocument?)null);

        // Phase 3 checks whether the current user is an admin; return DoesNotHaveRole (non-admin path)
        _graph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RoleCheckResult.DoesNotHaveRole);

        var config = new Agent365Config
        {
            TenantId = "tenant-123",
            AgentBlueprintId = "blueprint-app-id",
            ClientAppId = "client-app-id"
        };

        // Include a Microsoft Graph spec so Phase 3 builds a consent URL.
        // GrantAdminConsentAsync only generates a URL for Graph scopes (non-Graph resources
        // use inheritable permissions, not the /v2.0/adminconsent URL).
        var specs = new[]
        {
            new ResourcePermissionSpec(
                AuthenticationConstants.MicrosoftGraphResourceAppId,
                "Microsoft Graph",
                new[] { "Mail.ReadWrite" },
                SetInheritable: true)
        };

        // Act
        var (blueprintUpdated, inheritedConfigured, consentGranted, consentUrl) =
            await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                _graph, _blueprintService, config,
                blueprintAppId: "blueprint-app-id",
                tenantId: "tenant-123",
                specs: specs,
                _logger,
                setupResults: null,
                ct: default);

        // Assert — Phase 1 failed, Phase 2 was skipped
        blueprintUpdated.Should().BeFalse("Phase 1 auth failure should mark blueprint permissions as not updated");
        inheritedConfigured.Should().BeFalse("Phase 2 must be skipped when Phase 1 fails");

        // Phase 3 ran and returned a consent URL for the non-admin user
        consentGranted.Should().BeFalse("non-admin cannot grant consent interactively");
        consentUrl.Should().NotBeNullOrWhiteSpace("non-admin must always receive a consent URL for the tenant admin");
        consentUrl.Should().Contain("tenant-123", "consent URL must be scoped to the correct tenant");
        consentUrl.Should().Contain("blueprint-app-id", "consent URL must reference the blueprint application");

        // state parameter must be a random GUID (not the old hardcoded "xyz123")
        var stateMatch = System.Text.RegularExpressions.Regex.Match(consentUrl!, @"[?&]state=([^&]+)");
        stateMatch.Success.Should().BeTrue(because: "consent URL must include a state parameter for CSRF protection");
        Guid.TryParse(stateMatch.Groups[1].Value, out _).Should().BeTrue(
            because: "state parameter must be a random GUID, not a hardcoded value like 'xyz123'");
    }

    /// <summary>
    /// The orchestrator now relies exclusively on the unified /v2.0/adminconsent URL (no direct
    /// POST to /oauth2PermissionGrants). When specs include non-Graph resources (Messaging Bot
    /// API, Observability API, etc.) the consent URL must fully-qualify those scopes as
    /// api://{appId}/{scope} so a single browser prompt covers every delegated permission.
    /// This test exercises the non-admin path so URL construction is validated without
    /// triggering a real browser launch.
    /// </summary>
    [Fact]
    public async Task ConfigureAllPermissions_NonAdmin_BuildsUnifiedConsentUrlCoveringAllResources()
    {
        // Arrange — Phase 1 succeeds but the user is not a tenant admin, so Phase 3 returns
        // the consent URL without opening a browser. This validates URL construction across
        // Graph + non-Graph resources.
        _graph.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(JsonDocument.Parse("{\"id\":\"user-id\"}"));

        _graph.LookupServicePrincipalByAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>(null));

        _graph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RoleCheckResult.DoesNotHaveRole));

        var config = new Agent365Config
        {
            TenantId = "tenant-id",
            AgentBlueprintId = "blueprint-app-id"
        };

        var specs = new[]
        {
            new ResourcePermissionSpec(
                AuthenticationConstants.MicrosoftGraphResourceAppId,
                "Microsoft Graph",
                new[] { "Mail.ReadWrite" },
                SetInheritable: true),
            new ResourcePermissionSpec(
                ConfigConstants.MessagingBotApiAppId,
                "Messaging Bot API",
                new[] { "BotApi.Scope" },
                SetInheritable: true),
            new ResourcePermissionSpec(
                ConfigConstants.ObservabilityApiAppId,
                "Observability API",
                new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
                SetInheritable: true)
        };

        // Act
        var (_, _, consentGranted, consentUrl) =
            await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                _graph, _blueprintService, config,
                blueprintAppId: "blueprint-app-id",
                tenantId: "tenant-id",
                specs: specs,
                _logger,
                setupResults: null,
                ct: default);

        // Assert — the orchestrator no longer calls /oauth2PermissionGrants for any resource;
        // the unified consent URL must cover Graph + non-Graph scopes together.
        consentGranted.Should().BeFalse(
            because: "non-admin cannot grant consent interactively");
        consentUrl.Should().NotBeNullOrWhiteSpace(
            because: "every delegated-scope path now goes through /v2.0/adminconsent");
        consentUrl.Should().Contain("tenant-id",
            because: "the consent URL must be scoped to the correct tenant");
        consentUrl.Should().Contain("blueprint-app-id",
            because: "the consent URL must target the blueprint application");
        consentUrl.Should().Contain("Mail.ReadWrite",
            because: "Graph scopes must be included in the unified consent URL");
        consentUrl.Should().Contain(ConfigConstants.MessagingBotApiAppId,
            because: "non-Graph resources must be encoded as api://{appId}/{scope} in the unified URL");
        consentUrl.Should().Contain(ConfigConstants.ObservabilityApiAppId,
            because: "every delegated resource — including the Observability API — must be covered by a single browser prompt");

        await _graph.DidNotReceive().CreateOrUpdateOauth2PermissionGrantWithDetailsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }

    // The old GA-Phase-2b tests (`ConfigureAllPermissions_WhenGaDeclines_SkipsPhase2bAndReturnsNoConsentUrl`
    // and `ConfigureAllPermissions_WhenGaAndNoConfirmationProvider_GrantsFiredSilently`) were removed
    // when Phase 2b was retired. The orchestrator no longer issues a confirmation prompt and no longer
    // calls /oauth2PermissionGrants — every delegated-scope path now goes through /v2.0/adminconsent,
    // which is covered by `ConfigureAllPermissions_NonAdmin_BuildsUnifiedConsentUrlCoveringAllResources`
    // above and by `ConfigureAllPermissions_WhenPhase1AuthFails_Phase2SkippedAndPhase3ReturnsConsentUrl`
    // for the Graph-only non-admin path. The admin (browser-launching) path is not unit-tested to
    // avoid invoking a real browser in CI.
}
