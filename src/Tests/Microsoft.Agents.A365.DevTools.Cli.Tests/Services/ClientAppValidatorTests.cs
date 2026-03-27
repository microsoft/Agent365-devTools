// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Unit tests for ClientAppValidator service.
/// Tests validation logic for client app existence, permissions, and admin consent.
/// Uses GraphApiService mocks (via NSubstitute virtual method substitution) for direct HTTP calls
/// — no az-subprocess spawning.
/// </summary>
public class ClientAppValidatorTests
{
    private readonly ILogger<ClientAppValidator> _logger;
    private readonly GraphApiService _graphApiService;
    private readonly ClientAppValidator _validator;

    private const string ValidClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6";
    private const string ValidTenantId = "12345678-1234-1234-1234-123456789012";
    private const string InvalidGuid = "not-a-guid";
    private const string AppObjId = "object-id-123";
    private const string SpObjId = "sp-object-id-123";

    // Stable test GUIDs for required permissions — must match between SetupPermissionResolution
    // and SetupAppInfoWithAllPermissions so the validation resolves all permissions as present.
    private const string ApplicationReadWriteAllId = "aaaa0001-0000-0000-0000-000000000000";
    private const string AgentBlueprintReadWriteAllId = "aaaa0002-0000-0000-0000-000000000000";
    private const string AgentBlueprintUpdateAuthId = "aaaa0003-0000-0000-0000-000000000000";
    private const string AgentBlueprintAddRemoveCredsId = "aaaa0004-0000-0000-0000-000000000000";
    private const string DelegatedPermissionGrantReadWriteAllId = "aaaa0005-0000-0000-0000-000000000000";
    private const string DirectoryReadAllId = "aaaa0006-0000-0000-0000-000000000000";

    public ClientAppValidatorTests()
    {
        _logger = Substitute.For<ILogger<ClientAppValidator>>();

        // Use Substitute.For<> (full mock) so unmatched GraphGetAsync calls return
        // Task.FromResult<JsonDocument?>(null) — the null path in ClientAppValidator is
        // always a graceful "best-effort check" or early return, never an exception.
        var executorLogger = Substitute.For<ILogger<CommandExecutor>>();
        var executor = Substitute.For<CommandExecutor>(executorLogger);
        var graphServiceLogger = Substitute.For<ILogger<GraphApiService>>();
        _graphApiService = Substitute.For<GraphApiService>(graphServiceLogger, executor);

        _validator = new ClientAppValidator(_logger, _graphApiService);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new ClientAppValidator(null!, _graphApiService));

        exception.ParamName.Should().Be("logger");
    }

    [Fact]
    public void Constructor_WithNullGraphApiService_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new ClientAppValidator(_logger, null!));

        exception.ParamName.Should().Be("graphApiService");
    }

    #endregion

    #region EnsureValidClientAppAsync - Input Validation Tests

    [Fact]
    public async Task EnsureValidClientAppAsync_WithNullClientAppId_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _validator.EnsureValidClientAppAsync(null!, ValidTenantId));
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WithEmptyClientAppId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _validator.EnsureValidClientAppAsync(string.Empty, ValidTenantId));
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WithInvalidClientAppIdFormat_ThrowsClientAppValidationException()
    {
        await Assert.ThrowsAsync<ClientAppValidationException>(async () =>
            await _validator.EnsureValidClientAppAsync(InvalidGuid, ValidTenantId));
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WithInvalidTenantIdFormat_ThrowsClientAppValidationException()
    {
        await Assert.ThrowsAsync<ClientAppValidationException>(async () =>
            await _validator.EnsureValidClientAppAsync(ValidClientAppId, InvalidGuid));
    }

    #endregion

    #region EnsureValidClientAppAsync - App Existence Tests

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAppDoesNotExist_ThrowsClientAppValidationException()
    {
        SetupAppInfoGetEmpty();

        await Assert.ThrowsAsync<ClientAppValidationException>(async () =>
            await _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenGraphQueryFails_ThrowsClientAppValidationException()
    {
        // Simulate a 401 on both the first attempt and the retry after cache invalidation.
        // TokenRevoked is only thrown when the failure is specifically a 401 (auth error),
        // not for transient failures like 503 — which would produce AppNotFound instead.
        _graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("displayName")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Returns(_ => Task.FromResult(new GraphApiService.GraphResponse
            {
                IsSuccess = false,
                StatusCode = 401,
                ReasonPhrase = "Unauthorized"
            }));

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        exception.IssueDescription.Should().Contain("revoked",
            because: "a persistent 401 from Graph indicates a CAE token revocation, not a transient error");
    }

    #endregion

    #region EnsureValidClientAppAsync - Permission Validation Tests

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAppHasNoRequiredResourceAccess_ThrowsMissingPermissions()
    {
        // requiredResourceAccess: null → all permissions reported as missing
        SetupAppInfoGet(ValidClientAppId, requiredResourceAccess: "null");
        SetupPermissionResolution();

        await Assert.ThrowsAsync<ClientAppValidationException>(async () =>
            await _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAppMissingGraphPermissions_ThrowsClientAppValidationException()
    {
        var requiredResourceAccess = """
        [
            {
                "resourceAppId": "some-other-app-id",
                "resourceAccess": []
            }
        ]
        """;

        SetupAppInfoGet(ValidClientAppId, requiredResourceAccess: requiredResourceAccess);
        SetupPermissionResolution();

        await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAppMissingSomePermissions_ThrowsClientAppValidationException()
    {
        // Only Application.ReadWrite.All present — missing the other 5
        var requiredResourceAccess = $$"""
        [
            {
                "resourceAppId": "{{AuthenticationConstants.MicrosoftGraphResourceAppId}}",
                "resourceAccess": [
                    {"id": "{{ApplicationReadWriteAllId}}", "type": "Scope"}
                ]
            }
        ]
        """;

        SetupAppInfoGet(ValidClientAppId, requiredResourceAccess: requiredResourceAccess);
        SetupPermissionResolution();

        await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));
    }

    #endregion

    #region EnsureValidClientAppAsync - Success Tests

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAllValidationsPass_DoesNotThrow()
    {
        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();
        // Admin consent: SP query returns null (unmatched) → best-effort returns true
        // Redirect URIs / public client flows: null → silent skip — no exception

        await _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId);
    }

    #endregion

    #region EnsureValidClientAppAsync - Exception Detail Tests

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAppNotFound_ThrowsWithCorrectErrorCode()
    {
        SetupAppInfoGetEmpty();

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        exception.IssueDescription.Should().Contain("not found in tenant");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenMissingPermissions_ThrowsWithCorrectMessage()
    {
        SetupAppInfoGet(ValidClientAppId, requiredResourceAccess: "[]");
        SetupPermissionResolution();

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        exception.IssueDescription.Should().Contain("missing required API permissions");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenMissingAdminConsent_ThrowsWithCorrectMessage()
    {
        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();
        SetupAdminConsentSp(ValidClientAppId, SpObjId);
        SetupAdminConsentGrantsEmpty(SpObjId);

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        exception.IssueDescription.Should().Contain("Admin consent");
    }

    #endregion

    #region EnsurePublicClientFlowsEnabledAsync Tests

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenPublicClientFlowsAlreadyEnabled_DoesNotPatchPublicClientFlows()
    {
        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();
        SetupPublicClientFlowsGet(enabled: true);
        // Redirect URIs GET returns null (unmatched) → no PATCH for redirect URIs

        await _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId);

        // Neither redirect URIs nor public client flows should issue a PATCH
        await _graphApiService.DidNotReceive().GraphPatchAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>());
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenPublicClientFlowsDisabled_PatchesPublicClientFlows()
    {
        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();
        SetupPublicClientFlowsGet(enabled: false);
        _graphApiService.GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult(true));
        // Redirect URIs GET returns null (unmatched) → no separate PATCH

        await _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId);

        // Exactly one PATCH — the public client flows enable
        await _graphApiService.Received(1).GraphPatchAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains(AppObjId)),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>());
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenPublicClientFlowsPatchFails_DoesNotThrow()
    {
        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();
        SetupPublicClientFlowsGet(enabled: false);
        // GraphPatchAsync returns false (default) — operation is non-fatal

        await _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId);
    }

    #endregion

    #region EnsureRedirectUrisAsync Tests

    [Fact]
    public async Task EnsureRedirectUrisAsync_WhenAllUrisPresent_DoesNotUpdate()
    {
        var wamBrokerUri = $"ms-appx-web://microsoft.aad.brokerplugin/{ValidClientAppId}";
        var appResponseJson = $$"""
        {
            "value": [{
                "id": "{{AppObjId}}",
                "publicClient": {
                    "redirectUris": ["http://localhost", "http://localhost:8400/", "{{wamBrokerUri}}"]
                }
            }]
        }
        """;
        SetupRedirectUrisGet(appResponseJson);

        await _validator.EnsureRedirectUrisAsync(ValidClientAppId, ValidTenantId);

        await _graphApiService.DidNotReceive().GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }

    [Fact]
    public async Task EnsureRedirectUrisAsync_WhenUrisMissing_AddsThemSuccessfully()
    {
        var appResponseJson = $$"""
        {
            "value": [{
                "id": "{{AppObjId}}",
                "publicClient": {
                    "redirectUris": ["http://localhost:8400/"]
                }
            }]
        }
        """;
        SetupRedirectUrisGet(appResponseJson);
        _graphApiService.GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult(true));

        await _validator.EnsureRedirectUrisAsync(ValidClientAppId, ValidTenantId);

        await _graphApiService.Received(1).GraphPatchAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains(AppObjId)),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>());
    }

    [Fact]
    public async Task EnsureRedirectUrisAsync_WhenNoRedirectUris_AddsAllRequired()
    {
        var appResponseJson = $$"""
        {
            "value": [{
                "id": "{{AppObjId}}",
                "publicClient": {
                    "redirectUris": []
                }
            }]
        }
        """;
        SetupRedirectUrisGet(appResponseJson);
        _graphApiService.GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult(true));

        await _validator.EnsureRedirectUrisAsync(ValidClientAppId, ValidTenantId);

        await _graphApiService.Received(1).GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }

    [Fact]
    public async Task EnsureRedirectUrisAsync_WhenGetFails_LogsWarningAndContinues()
    {
        // GraphGetAsync returns null (unmatched default) — simulates Graph API failure

        await _validator.EnsureRedirectUrisAsync(ValidClientAppId, ValidTenantId);

        await _graphApiService.DidNotReceive().GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }

    [Fact]
    public async Task EnsureRedirectUrisAsync_WhenPatchFails_LogsWarningButDoesNotThrow()
    {
        var appResponseJson = $$"""
        {
            "value": [{
                "id": "{{AppObjId}}",
                "publicClient": {
                    "redirectUris": []
                }
            }]
        }
        """;
        SetupRedirectUrisGet(appResponseJson);
        // GraphPatchAsync returns false (default) — non-fatal

        await _validator.EnsureRedirectUrisAsync(ValidClientAppId, ValidTenantId);

        await _graphApiService.Received(1).GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>());
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Sets up the app info GET (select includes displayName) to return an app with the given requiredResourceAccess JSON.
    /// Pass "null" to simulate a null requiredResourceAccess; pass "[]" for an empty array.
    /// </summary>
    private void SetupAppInfoGet(string appId, string requiredResourceAccess = "[]")
    {
        var json = $$"""
        {
            "value": [
                {
                    "id": "{{AppObjId}}",
                    "appId": "{{appId}}",
                    "displayName": "Test App",
                    "requiredResourceAccess": {{requiredResourceAccess}}
                }
            ]
        }
        """;

        // GetClientAppInfoAsync now calls GraphGetWithResponseAsync; GraphGetAsync is used by
        // subsequent steps (permission resolution, consent checks, redirect URIs, etc.).
        _graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("displayName")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Returns(_ => Task.FromResult(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDocument.Parse(json)
            }));
    }

    /// <summary>
    /// Sets up the app info GET to return an empty value array (app not found).
    /// </summary>
    private void SetupAppInfoGetEmpty()
    {
        _graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("displayName")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Returns(_ => Task.FromResult(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDocument.Parse("""{"value": []}""")
            }));
    }

    /// <summary>
    /// Sets up the app info GET with all 6 required permissions.
    /// The permission GUIDs match those returned by SetupPermissionResolution so validation passes.
    /// </summary>
    private void SetupAppInfoWithAllPermissions(string appId)
    {
        var requiredResourceAccess = $$"""
        [
            {
                "resourceAppId": "{{AuthenticationConstants.MicrosoftGraphResourceAppId}}",
                "resourceAccess": [
                    {"id": "{{ApplicationReadWriteAllId}}", "type": "Scope"},
                    {"id": "{{AgentBlueprintReadWriteAllId}}", "type": "Scope"},
                    {"id": "{{AgentBlueprintUpdateAuthId}}", "type": "Scope"},
                    {"id": "{{AgentBlueprintAddRemoveCredsId}}", "type": "Scope"},
                    {"id": "{{DelegatedPermissionGrantReadWriteAllId}}", "type": "Scope"},
                    {"id": "{{DirectoryReadAllId}}", "type": "Scope"}
                ]
            }
        ]
        """;

        SetupAppInfoGet(appId, requiredResourceAccess: requiredResourceAccess);
    }

    /// <summary>
    /// Sets up the Microsoft Graph SP permission resolution GET (select includes oauth2PermissionScopes).
    /// Returns the 6 required permissions with GUIDs matching the test constants.
    /// </summary>
    private void SetupPermissionResolution()
    {
        var json = $$"""
        {
            "value": [
                {
                    "id": "graph-sp-id-123",
                    "oauth2PermissionScopes": [
                        {"id": "{{ApplicationReadWriteAllId}}", "value": "Application.ReadWrite.All"},
                        {"id": "{{AgentBlueprintReadWriteAllId}}", "value": "AgentIdentityBlueprint.ReadWrite.All"},
                        {"id": "{{AgentBlueprintUpdateAuthId}}", "value": "AgentIdentityBlueprint.UpdateAuthProperties.All"},
                        {"id": "{{AgentBlueprintAddRemoveCredsId}}", "value": "AgentIdentityBlueprint.AddRemoveCreds.All"},
                        {"id": "{{DelegatedPermissionGrantReadWriteAllId}}", "value": "DelegatedPermissionGrant.ReadWrite.All"},
                        {"id": "{{DirectoryReadAllId}}", "value": "Directory.Read.All"}
                    ]
                }
            ]
        }
        """;

        _graphApiService.GraphGetAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("oauth2PermissionScopes")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(json)));
    }

    /// <summary>
    /// Sets up the admin consent SP GET (select includes id,appId — used by ValidateAdminConsentAsync).
    /// </summary>
    private void SetupAdminConsentSp(string clientAppId, string spObjectId)
    {
        var json = $$"""
        {
            "value": [
                {
                    "id": "{{spObjectId}}",
                    "appId": "{{clientAppId}}"
                }
            ]
        }
        """;

        _graphApiService.GraphGetAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("servicePrincipals") && p.Contains("id,appId")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(json)));
    }

    /// <summary>
    /// Sets up the oauth2PermissionGrants GET for a given SP object ID to return no grants.
    /// </summary>
    private void SetupAdminConsentGrantsEmpty(string spObjectId)
    {
        _graphApiService.GraphGetAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("oauth2PermissionGrants") && p.Contains(spObjectId)),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse("""{"value": []}""")));
    }

    /// <summary>
    /// Sets up the redirect URIs GET (select includes publicClient).
    /// </summary>
    private void SetupRedirectUrisGet(string appResponseJson)
    {
        _graphApiService.GraphGetAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("publicClient") && !p.Contains("displayName")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(appResponseJson)));
    }

    /// <summary>
    /// Sets up the public client flows GET (select includes isFallbackPublicClient).
    /// </summary>
    private void SetupPublicClientFlowsGet(bool enabled)
    {
        var json = $$"""
        {
            "value": [{
                "id": "{{AppObjId}}",
                "isFallbackPublicClient": {{(enabled ? "true" : "false")}}
            }]
        }
        """;

        _graphApiService.GraphGetAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("isFallbackPublicClient")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(json)));
    }

    #endregion
}
