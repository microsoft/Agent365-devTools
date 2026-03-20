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
    }
}
