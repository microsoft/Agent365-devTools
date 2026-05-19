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
using System.Text.Json;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Unit tests for BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync.
/// Focused on the non-fatal phase-independence contract: each phase failure
/// must not prevent subsequent phases from running.
/// </summary>
public class BatchPermissionsOrchestratorTests
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
    /// When a Global Administrator is prompted and explicitly declines the tenant-wide consent,
    /// Phase 2b (OAuth2 grants) and Phase 3 (browser consent) must both be skipped entirely.
    /// The return must be (false, null) — no URL, no browser open — so the caller summary
    /// shows the correct "declined" state rather than an unexpected "action required" URL.
    /// </summary>
    [Fact]
    public async Task ConfigureAllPermissions_WhenGaDeclines_SkipsPhase2bAndReturnsNoConsentUrl()
    {
        // Arrange — Phase 1 succeeds: pre-warm call returns a valid document.
        _graph.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(JsonDocument.Parse("{\"id\":\"user-id\"}"));

        _graph.LookupServicePrincipalByAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>("blueprint-sp-id"));

        _graph.EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>("resource-sp-id"));

        _graph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RoleCheckResult.HasRole));

        // Phase 2a — inheritable permissions succeed.
        _blueprintService.SetInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(bool ok, bool alreadyExists, string? error)>((true, false, null)));

        _blueprintService.VerifyInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<(bool exists, bool scopesAllAllowed, bool rolesAllAllowed, string? error)>((true, true, true, null)));

        // GA declines the prompt.
        var confirmationProvider = Substitute.For<IConfirmationProvider>();
        confirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(Task.FromResult(false));

        var config = new Agent365Config
        {
            TenantId = "tenant-id",
            AgentBlueprintId = "blueprint-app-id",
            AgentBlueprintDisplayName = "Test Blueprint"
        };

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
                tenantId: "tenant-id",
                specs: specs,
                _logger,
                setupResults: null,
                ct: default,
                confirmationProvider: confirmationProvider);

        // Assert — GA declined, so neither Phase 2b nor Phase 3 ran.
        consentGranted.Should().BeFalse(because: "GA declined the prompt");
        consentUrl.Should().BeNull(because: "no browser URL should be produced when GA explicitly declined");

        await _graph.DidNotReceive().CreateOrUpdateOauth2PermissionGrantWithDetailsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }

    /// <summary>
    /// When no confirmation provider is supplied, an unattended Global Administrator run should
    /// fire Phase 2b (OAuth2 grants) silently — the pre-PR #421 behavior.
    /// This guards backward compatibility so existing headless scripts continue to work.
    /// </summary>
    [Fact]
    public async Task ConfigureAllPermissions_WhenGaAndNoConfirmationProvider_GrantsFiredSilently()
    {
        // Arrange — Phase 1 succeeds.
        _graph.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(JsonDocument.Parse("{\"id\":\"user-id\"}"));

        _graph.LookupServicePrincipalByAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<string?>("blueprint-sp-id"));

        _graph.EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>("resource-sp-id"));

        _graph.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RoleCheckResult.HasRole));

        // Phase 2a — inheritable permissions succeed.
        _blueprintService.SetInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(bool ok, bool alreadyExists, string? error)>((true, false, null)));

        _blueprintService.VerifyInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<(bool exists, bool scopesAllAllowed, bool rolesAllAllowed, string? error)>((true, true, true, null)));

        // Phase 2b — OAuth2 grant call succeeds.
        _graph.CreateOrUpdateOauth2PermissionGrantWithDetailsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<(bool Success, int StatusCode, string? ErrorCode)>((true, 201, null)));

        var config = new Agent365Config
        {
            TenantId = "tenant-id",
            AgentBlueprintId = "blueprint-app-id"
        };

        // spec has no AppRoleScopes so PerformS2SGrantsAsync returns immediately.
        var specs = new[]
        {
            new ResourcePermissionSpec(
                AuthenticationConstants.MicrosoftGraphResourceAppId,
                "Microsoft Graph",
                new[] { "Mail.ReadWrite" },
                SetInheritable: true)
        };

        // Act — no confirmationProvider (headless / unattended GA script).
        var (blueprintUpdated, inheritedConfigured, consentGranted, consentUrl) =
            await BatchPermissionsOrchestrator.ConfigureAllPermissionsAsync(
                _graph, _blueprintService, config,
                blueprintAppId: "blueprint-app-id",
                tenantId: "tenant-id",
                specs: specs,
                _logger,
                setupResults: null,
                ct: default);

        // Assert — grants fired without a prompt and consent was granted.
        consentGranted.Should().BeTrue(because: "unattended GA should grant silently when no confirmationProvider is set");
        consentUrl.Should().BeNull(because: "no URL is needed when grants succeeded");

        await _graph.Received(1).CreateOrUpdateOauth2PermissionGrantWithDetailsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }
}
