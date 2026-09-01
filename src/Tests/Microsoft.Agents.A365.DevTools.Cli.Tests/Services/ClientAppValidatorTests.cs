// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Globalization;
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

    // The well-known Microsoft app ID is the only trigger for the non-mutating first-party path.
    private const string FirstPartyClientAppId = AuthenticationConstants.WellKnownClientAppId;
    private const string ValidTenantId = "12345678-1234-1234-1234-123456789012";
    private const string InvalidGuid = "not-a-guid";
    private const string AppObjId = "object-id-123";
    private const string SpObjId = "sp-object-id-123";

    // Stable test GUIDs for required permissions — must match between SetupPermissionResolution
    // and SetupAppInfoWithAllPermissions so the validation resolves all permissions as present.
    private const string AgentBlueprintReadWriteAllId = "aaaa0002-0000-0000-0000-000000000000";
    private const string AgentBlueprintPrincipalCreateId = "aaaa0003-0000-0000-0000-000000000000";
    private const string UserReadId = "aaaa0008-0000-0000-0000-000000000000";
    private const string AgentRegistrationReadWriteAllId = "aaaa000a-0000-0000-0000-000000000000";
    private const string ApplicationReadAllId = "aaaa000b-0000-0000-0000-000000000000";

    // Separate SP object ID used only by the consent-grant path (GetConsentedPermissionsAsync)
    // so it does not conflict with SetupAdminConsentSp / SetupAdminConsentGrantsEmpty.
    private const string ConsentSpObjId = "consent-check-sp-id-999";

    // Pinning the exact issue descriptions keeps "inconclusive lookup" and "confirmed absent"
    // distinguishable: asserting only the absence of "not found" is satisfied by every other
    // failure factory in ClientAppValidationException.
    private const string AppNotFoundIssue = "Client app not found in tenant";
    private const string LookupFailedIssue = "Unable to verify the client app registration in the tenant";
    private const string TokenRevokedIssue = "Azure authentication token revoked — re-authentication required";

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

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(async () =>
            await _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Be(AppNotFoundIssue,
            because: "a successful Graph response with an empty result set is the only proof that the app is absent");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenGraphQueryFails_ThrowsClientAppValidationException()
    {
        // Simulate a 401 on both the first attempt and the retry after cache invalidation.
        // TokenRevoked is only thrown when the failure is specifically a 401 (auth error),
        // not for transient failures like 503 — which report an inconclusive lookup instead.
        SetupAppInfoGetFailure(401, "Unauthorized");

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        exception.IssueDescription.Should().Be(TokenRevokedIssue,
            because: "a persistent 401 from Graph indicates a CAE token revocation, not a transient error");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenApplicationLookupIsForbidden_DoesNotReportAppNotFound()
    {
        // Regression (#489): reading application metadata requires Application.Read.All. A 403
        // leaves the app's existence unknown; reporting "not found" sends operators to re-create
        // an app registration that is already present in the tenant.
        SetupAppInfoGetFailure(403, "Forbidden");

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Be(LookupFailedIssue,
            because: "HTTP 403 is an authorization failure and must be reported as an inconclusive lookup, never as proof the app is absent");
        exception.ErrorDetails.Should().Contain(d => d.Contains("403", StringComparison.Ordinal),
            because: "the HTTP status must be preserved so operators can identify the authorization failure");
        exception.MitigationSteps.Should().Contain(s => s.Contains(AuthenticationConstants.ApplicationReadAllScope, StringComparison.Ordinal),
            because: "reading application metadata requires the Application.Read.All Microsoft Graph permission");
        exception.MitigationSteps.Should().NotContain(s => s.Contains("from scratch", StringComparison.OrdinalIgnoreCase),
            because: "the app-not-found remediation re-creates the app registration and must never be offered on an unproven absence");
        exception.Context.Should().Contain(new KeyValuePair<string, string>("statusCode", "403"),
            because: "the status must be machine-readable in the exception context, not only embedded in prose");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenForbiddenAfter401Retry_DoesNotReportTokenRevoked()
    {
        // Regression (#489): a stale ambient token yields 401, and the refreshed retry then hits
        // the real 403. Reporting revocation sends operators to 'az login', which cannot fix a
        // missing Application.Read.All grant.
        var attempts = 0;
        _graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("displayName")),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult(Interlocked.Increment(ref attempts) == 1
                ? new GraphApiService.GraphResponse { IsSuccess = false, StatusCode = 401, ReasonPhrase = "Unauthorized" }
                : new GraphApiService.GraphResponse { IsSuccess = false, StatusCode = 403, ReasonPhrase = "Forbidden" }));

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Be(LookupFailedIssue,
            because: "only a second 401 proves token revocation; the retry's own status must be reported instead");
        exception.ErrorDetails.Should().Contain(d => d.Contains("403", StringComparison.Ordinal),
            because: "the status returned by the refreshed attempt is the actionable one and must survive into the error");
    }

    [Theory]
    [InlineData(429, "Too Many Requests")]
    [InlineData(500, "Internal Server Error")]
    [InlineData(503, "Service Unavailable")]
    public async Task EnsureValidClientAppAsync_WhenApplicationLookupCannotComplete_PreservesStatusAndDoesNotReportAppNotFound(
        int statusCode, string reasonPhrase)
    {
        SetupAppInfoGetFailure(statusCode, reasonPhrase);

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Be(LookupFailedIssue,
            because: "throttling and server errors leave the app's existence unknown");
        exception.ErrorDetails.Should().Contain(d => d.Contains(statusCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            because: "the HTTP status must survive into the error so operators can distinguish throttling from a server fault");
        exception.MitigationSteps.Should().NotContain(s => s.Contains(AuthenticationConstants.ApplicationReadAllScope, StringComparison.Ordinal),
            because: "only HTTP 403 indicates a permission gap; suggesting an admin grant for a transient failure misdirects the operator");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenTokenAcquisitionFailsBeforeResponse_SurfacesTheUnderlyingReason()
    {
        // The shape GraphGetWithResponseAsync returns when no HTTP response was ever received.
        SetupAppInfoGetFailure(0, "NoAuth");

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Be(LookupFailedIssue,
            because: "a failure before any response leaves the app's existence unknown");
        exception.ErrorDetails.Should().Contain(d => d.Contains("NoAuth", StringComparison.Ordinal),
            because: "with no HTTP status available the reason phrase is the only diagnostic, so it must be preserved");
    }

    [Theory]
    [InlineData("""{"unexpected": true}""", "a response without a 'value' array does not prove the application is absent")]
    [InlineData("""{"value": {}}""", "a non-array 'value' is a malformed response, not a confirmed absence")]
    [InlineData("""{"value": ["app"]}""", "a non-object array element is a malformed response, not a confirmed absence")]
    [InlineData("""{"value": [{"displayName": "Test App"}]}""", "an application result without an object ID is unusable, not a confirmed absence")]
    [InlineData("""{"value": [{"id": 42}]}""", "a non-string object ID is a malformed response, not a confirmed absence")]
    public async Task EnsureValidClientAppAsync_WhenApplicationLookupResponseIsMalformed_DoesNotReportAppNotFound(
        string body, string reason)
    {
        _graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("displayName")),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDocument.Parse(body)
            }));

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Be(LookupFailedIssue, because: reason);
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenCustomClientAppIdIsResolved_LooksUpApplicationAmbiently()
    {
        // Regression (#489): after the bootstrap resolves a tenant-owned client app,
        // GraphApiService.CustomClientAppId is set. Authenticating the existence probe as that
        // app yields a User.Read token that Graph rejects with 403 on /applications.
        _graphApiService.CustomClientAppId = ValidClientAppId;
        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();

        await _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId);

        await _graphApiService.Received().GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("/v1.0/applications")),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            GraphAuthenticationMode.Ambient);
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenApplicationLookupReturns401_RetriesAmbientlyWithFreshToken()
    {
        _graphApiService.CustomClientAppId = ValidClientAppId;
        SetupAppInfoGetFailure(401, "Unauthorized");

        await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        await _graphApiService.Received(1).GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("/v1.0/applications")),
            true,
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            GraphAuthenticationMode.Ambient);
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenApplicationLookupSucceedsAfter401Retry_CompletesValidation()
    {
        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();

        var appJson = BuildAppInfoJson(ValidClientAppId, BuildAllPermissionsResourceAccess());
        var attempts = 0;
        _graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("displayName")),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult(Interlocked.Increment(ref attempts) == 1
                ? new GraphApiService.GraphResponse { IsSuccess = false, StatusCode = 401, ReasonPhrase = "Unauthorized" }
                : new GraphApiService.GraphResponse { IsSuccess = true, StatusCode = 200, Json = JsonDocument.Parse(appJson) }));

        await _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId);

        await _graphApiService.Received(1).GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("/v1.0/applications")),
            true,
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            GraphAuthenticationMode.Ambient);
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenApplicationLookupThrows_ReportsLookupFailureNotAppNotFound()
    {
        _graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("displayName")),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<GraphAuthenticationMode>())
            .Returns<GraphApiService.GraphResponse>(_ => throw new HttpRequestException("connection reset"));

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Be(LookupFailedIssue,
            because: "a network failure leaves the app's existence unknown");
        exception.ErrorDetails.Should().Contain(d => d.Contains("connection reset", StringComparison.Ordinal),
            because: "the underlying transport failure must be surfaced so the operator can act on it");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenApplicationLookupTimesOut_ReportsLookupFailureNotCancellation()
    {
        // HttpClient surfaces its own timeout as TaskCanceledException with no cancellation
        // requested; treating it as a cancel would abort the command instead of reporting a
        // recoverable lookup failure.
        _graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("displayName")),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<GraphAuthenticationMode>())
            .Returns<GraphApiService.GraphResponse>(_ =>
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Be(LookupFailedIssue,
            because: "a transport timeout is a lookup failure, not a caller cancellation or a confirmed absence");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenPostProvisioningRefetchFails_ReportsLookupFailureNotMissingPermissions()
    {
        // The confirming re-read after permission provisioning uses the same lookup. A throttled
        // re-read must not be reported as a permission gap the operator has to fix by hand.
        SetupAppInfoGet(ValidClientAppId, requiredResourceAccess: "[]");
        SetupPermissionResolution();

        var appJson = BuildAppInfoJson(ValidClientAppId, "[]");
        var attempts = 0;
        _graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("displayName")),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult(Interlocked.Increment(ref attempts) == 1
                ? new GraphApiService.GraphResponse { IsSuccess = true, StatusCode = 200, Json = JsonDocument.Parse(appJson) }
                : new GraphApiService.GraphResponse { IsSuccess = false, StatusCode = 429, ReasonPhrase = "Too Many Requests" }));

        // Permission provisioning succeeds so the flow reaches the confirming re-read.
        _graphApiService.GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(Task.FromResult(true));

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId, skipConfirmation: true));

        exception.IssueDescription.Should().Be(LookupFailedIssue,
            because: "a throttled confirmation read leaves the provisioning outcome unknown and must not be reported as missing permissions");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenCallerCancelsDuringApplicationLookup_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        _graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("displayName")),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<GraphAuthenticationMode>())
            .Returns<GraphApiService.GraphResponse>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        Func<Task> act = async () => await _validator.EnsureValidClientAppAsync(
            ValidClientAppId, ValidTenantId, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            because: "Ctrl+C must abort the command rather than being reported as a client app validation failure");
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
        // Only AgentIdentityBlueprint.ReadWrite.All present — missing the other required permissions
        var requiredResourceAccess = $$"""
        [
            {
                "resourceAppId": "{{AuthenticationConstants.MicrosoftGraphResourceAppId}}",
                "resourceAccess": [
                    {"id": "{{AgentBlueprintReadWriteAllId}}", "type": "Scope"}
                ]
            }
        ]
        """;

        SetupAppInfoGet(ValidClientAppId, requiredResourceAccess: requiredResourceAccess);
        SetupPermissionResolution();

        await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenMissingAgentRegistrationPermission_ThrowsNamingThatPermission()
    {
        // App has all permissions except AgentRegistration.ReadWrite.All
        var requiredResourceAccess = $$"""
        [
            {
                "resourceAppId": "{{AuthenticationConstants.MicrosoftGraphResourceAppId}}",
                "resourceAccess": [
                    {"id": "{{AgentBlueprintReadWriteAllId}}", "type": "Scope"},
                    {"id": "{{AgentBlueprintPrincipalCreateId}}", "type": "Scope"},
                    {"id": "{{ApplicationReadAllId}}", "type": "Scope"},
                    {"id": "{{UserReadId}}", "type": "Scope"}
                ]
            }
        ]
        """;

        SetupAppInfoGet(ValidClientAppId, requiredResourceAccess: requiredResourceAccess);
        SetupPermissionResolution();
        SetupConsentGrantForAgentIdentityCreate();

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        exception.ErrorDetails.Should().Contain(d => d.Contains("AgentRegistration.ReadWrite.All"),
            because: "the missing permission must be identified by name in the error details");
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

    #region EnsureValidClientAppAsync - First-Party App Tests

    private static string BuildTokenWithScopes(params string[] scopes)
    {
        var header = Base64UrlEncode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncode($$"""{"scp":"{{string.Join(' ', scopes)}}"}""");
        return $"{header}.{payload}.sig";
    }

    private static string Base64UrlEncode(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static IEnumerable<object[]> UnreadableScopeClaimTokens()
    {
        var header = Base64UrlEncode("""{"alg":"none","typ":"JWT"}""");
        yield return new object[] { $"{header}.%%%.sig", "invalid Base64Url" };
        yield return new object[] { $"{header}.{Base64UrlEncode("not-json")}.sig", "not valid JSON" };
        yield return new object[]
        {
            $"{header}.{Base64UrlEncode("""{"scp":["User.Read"]}""")}.sig",
            "'scp' claim is not a string"
        };
    }

    private void SetupFirstPartyServicePrincipalLookup(
        string clientAppId = FirstPartyClientAppId,
        string? servicePrincipalId = SpObjId)
    {
        _graphApiService.LookupServicePrincipalByAppIdWithResponseAsync(
                ValidTenantId,
                clientAppId,
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.ResolvedClientApp)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = servicePrincipalId,
                StatusCode = 200
            });
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_FirstParty_WhenServicePrincipalMissing_ThrowsAppNotFound()
    {
        SetupFirstPartyServicePrincipalLookup(servicePrincipalId: null);

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(FirstPartyClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Contain("not found",
            because: "a missing first-party service principal must surface the same AppNotFound failure as a missing custom app");

        // The identity must be resolved via /servicePrincipals — /applications must never be queried
        // in first-party mode, since a customer tenant may have no local application object.
        await _graphApiService.Received(1).LookupServicePrincipalByAppIdWithResponseAsync(
            ValidTenantId,
            FirstPartyClientAppId,
            Arg.Any<CancellationToken>(),
            GraphAuthenticationMode.ResolvedClientApp);
        await _graphApiService.DidNotReceive().ApplicationExistsByAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_FirstParty_WhenServicePrincipalLookupThrows_PreservesFirstPartyGuidance()
    {
        _graphApiService.LookupServicePrincipalByAppIdWithResponseAsync(
                ValidTenantId,
                FirstPartyClientAppId,
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.ResolvedClientApp)
            .Returns(Task.FromException<GraphApiService.ServicePrincipalLookupResult>(
                new HttpRequestException("service-principal lookup unavailable")));

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(
                FirstPartyClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Contain("service principal",
            because: "a Graph lookup failure must remain distinguishable from a missing service principal");
        exception.ErrorDetails.Should().Contain(
            detail => detail.Contains("lookup unavailable", StringComparison.Ordinal),
            because: "the underlying Graph failure must remain visible for diagnosis");
        exception.MitigationSteps.Should().NotContain(
            step => step.Contains("App registrations", StringComparison.OrdinalIgnoreCase),
            because: "lookup failures must never direct customers to modify Microsoft's application registration");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_FirstParty_WhenServicePrincipalLookupReturnsHttpFailure_ReportsLookupFailure()
    {
        _graphApiService.LookupServicePrincipalByAppIdWithResponseAsync(
                ValidTenantId,
                FirstPartyClientAppId,
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.ResolvedClientApp)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = false,
                StatusCode = 403,
                FailureReason = "Microsoft Graph service-principal lookup failed: HTTP 403 Forbidden."
            });

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(
                FirstPartyClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Contain("Unable to verify",
            because: "an HTTP failure must remain distinguishable from a successful lookup with no matching service principal");
        exception.IssueDescription.Should().NotContain("not found",
            because: "an authorization or transport failure must not be misreported as confirmed service-principal absence");
        exception.ErrorDetails.Should().Contain(
            detail => detail.Contains("HTTP 403", StringComparison.Ordinal),
            because: "operators need the Graph status to diagnose authorization failures");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_FirstParty_WhenTokenHasAllRequiredScopes_DoesNotThrowOrMutate()
    {
        SetupFirstPartyServicePrincipalLookup();

        var token = BuildTokenWithScopes(AuthenticationConstants.RequiredClientAppPermissions);
        _graphApiService.GetClientAppAccessTokenAsync(
            ValidTenantId, FirstPartyClientAppId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(token));

        await _validator.EnsureValidClientAppAsync(FirstPartyClientAppId, ValidTenantId);

        // No app-registration mutation of any kind must be attempted on a first-party application.
        await _graphApiService.DidNotReceive().GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>());
        await _graphApiService.DidNotReceive().GraphPostAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>());
        // A tenant-local oauth2PermissionGrant must not be required when the token itself proves authorization.
        await _graphApiService.DidNotReceive().GraphGetAsync(
            Arg.Any<string>(), Arg.Is<string>(p => p.Contains("oauth2PermissionGrants")), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>());
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_FirstParty_RequestsBlueprintAndRegistrationScopesSeparately()
    {
        SetupFirstPartyServicePrincipalLookup();

        var requestedScopeSets = new List<string[]>();
        _graphApiService.GetClientAppAccessTokenAsync(
            ValidTenantId, FirstPartyClientAppId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var scopes = callInfo.ArgAt<IEnumerable<string>>(2).ToArray();
                requestedScopeSets.Add(scopes);
                return Task.FromResult<string?>(BuildTokenWithScopes(scopes));
            });

        await _validator.EnsureValidClientAppAsync(
            FirstPartyClientAppId, ValidTenantId);

        requestedScopeSets.Should().HaveCount(2,
            because: "the registration scope must be acquired separately so Entra can return a token whose scp claim identifies authorization for each operation group");
        requestedScopeSets.Single(scopes =>
                !scopes.Contains(AuthenticationConstants.AgentRegistrationReadWriteAllScope))
            .Should().BeEquivalentTo(
                AuthenticationConstants.BlueprintOperationScopes,
            because: "every blueprint-operation scope must be validated from the token scp claim");
        requestedScopeSets.Single(scopes =>
                scopes.Contains(AuthenticationConstants.AgentRegistrationReadWriteAllScope))
            .Should().Equal(
                [AuthenticationConstants.AgentRegistrationReadWriteAllScope],
            because: "AgentRegistration.ReadWrite.All must be validated without combining it with blueprint scopes");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_FirstParty_WhenTokenMissingARequiredScope_ThrowsMissingPermissionsNamingIt()
    {
        SetupFirstPartyServicePrincipalLookup();

        var grantedScopes = AuthenticationConstants.RequiredClientAppPermissions
            .Where(s => s != "User.Read")
            .ToArray();
        var token = BuildTokenWithScopes(grantedScopes);
        _graphApiService.GetClientAppAccessTokenAsync(
            ValidTenantId, FirstPartyClientAppId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(token));

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(FirstPartyClientAppId, ValidTenantId));

        exception.ErrorDetails.Should().Contain(d => d.Contains("User.Read"),
            because: "every required scope must be checked individually so the operator knows exactly which scope the token is missing");
        exception.MitigationSteps.Should().NotContain(
            step => step.Contains("App registrations", StringComparison.OrdinalIgnoreCase),
            because: "customers must not be told to modify Microsoft's first-party app registration");
        exception.MitigationSteps.Should().Contain(
            step => step.Contains("enterprise application", StringComparison.OrdinalIgnoreCase),
            because: "first-party authorization failures must direct tenant administrators to the enterprise application");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_FirstParty_WhenTokenAcquisitionFails_ThrowsClearValidationFailure()
    {
        SetupFirstPartyServicePrincipalLookup();

        _graphApiService.GetClientAppAccessTokenAsync(
            ValidTenantId, FirstPartyClientAppId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(FirstPartyClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Contain("access token",
            because: "a failed token acquisition must surface a clear, explicit failure rather than being silently swallowed as \'missing permissions\'");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_FirstParty_WhenTokenAcquisitionThrows_SurfacesActionableFailure()
    {
        SetupFirstPartyServicePrincipalLookup();
        _graphApiService.GetClientAppAccessTokenAsync(
            ValidTenantId, FirstPartyClientAppId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(
                new InvalidOperationException("interactive acquisition denied")));

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(
                FirstPartyClientAppId, ValidTenantId));

        exception.ErrorDetails.Should().Contain(
            detail => detail.Contains("interactive acquisition denied", StringComparison.Ordinal),
            because: "token acquisition exceptions must remain visible as explicit validation failures");
        exception.MitigationSteps.Should().NotContain(
            step => step.Contains("App registrations", StringComparison.OrdinalIgnoreCase),
            because: "a first-party token failure must never recommend changing Microsoft's application registration");
    }

    [Theory]
    [MemberData(nameof(UnreadableScopeClaimTokens))]
    public async Task EnsureValidClientAppAsync_FirstParty_WhenTokenScopeClaimCannotBeDecoded_ReportsAuthorizationFailure(
        string token,
        string expectedReason)
    {
        SetupFirstPartyServicePrincipalLookup();
        _graphApiService.GetClientAppAccessTokenAsync(
                ValidTenantId, FirstPartyClientAppId,
                Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(token));

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(
                FirstPartyClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Contain("access token",
            because: "an unreadable token must be reported as an authorization-validation failure");
        exception.ErrorDetails.Should().Contain(
            detail => detail.Contains(expectedReason, StringComparison.Ordinal),
            because: "the operator must be able to distinguish malformed token data from genuinely missing delegated scopes");
        exception.ErrorDetails.Should().NotContain(
            detail => detail.Contains("Missing scopes", StringComparison.Ordinal),
            because: "missing-scope guidance is valid only after a string scp claim was decoded successfully");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WellKnownId_SkipsCustomAppMutationPath()
    {
        SetupFirstPartyServicePrincipalLookup(AuthenticationConstants.WellKnownClientAppId);
        _graphApiService.GetClientAppAccessTokenAsync(
            ValidTenantId, AuthenticationConstants.WellKnownClientAppId,
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var scopes = callInfo.ArgAt<IEnumerable<string>>(2).ToArray();
                return Task.FromResult<string?>(BuildTokenWithScopes(scopes));
            });

        await _validator.EnsureValidClientAppAsync(
            AuthenticationConstants.WellKnownClientAppId, ValidTenantId);

        var mutationCalls = _graphApiService.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name is nameof(GraphApiService.GraphPatchAsync) or nameof(GraphApiService.GraphPostAsync))
            .ToList();
        mutationCalls.Should().BeEmpty(
            because: "the well-known Microsoft application ID alone must select the non-mutating validation path");
    }

    [Fact]
    public async Task GetUnconsentedRequiredPermissionsAsync_FirstParty_ReturnsEmptyWithoutQueryingGrants()
    {
        var result = await _validator.GetUnconsentedRequiredPermissionsAsync(
            AuthenticationConstants.WellKnownClientAppId, ValidTenantId);

        result.Should().BeEmpty(
            because: "the acquired token scp claim, not a tenant-local oauth2PermissionGrant, proves first-party authorization");
        _graphApiService.ReceivedCalls().Should().BeEmpty(
            because: "first-party preauthorization must not trigger any tenant grant or service-principal query");
    }

    [Fact]
    public async Task GrantConsentForPermissionsAsync_FirstParty_ThrowsWithoutPatchingGrant()
    {
        Func<Task> act = async () => await _validator.GrantConsentForPermissionsAsync(
            AuthenticationConstants.WellKnownClientAppId, ["User.Read"], ValidTenantId);

        await act.Should().ThrowAsync<InvalidOperationException>(
            because: "tenant-local consent configuration must never be mutated for Microsoft's first-party application");
        var mutationCalls = _graphApiService.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name is nameof(GraphApiService.GraphPatchAsync) or nameof(GraphApiService.GraphPostAsync))
            .ToList();
        mutationCalls.Should().BeEmpty(
            because: "rejecting the operation must happen before any Graph consent mutation");
    }

    [Fact]
    public async Task EnsureRedirectUrisAsync_FirstParty_ReturnsWithoutReadingOrPatchingApplication()
    {
        await _validator.EnsureRedirectUrisAsync(
            AuthenticationConstants.WellKnownClientAppId, ValidTenantId);

        _graphApiService.ReceivedCalls().Should().BeEmpty(
            because: "customers cannot read or modify redirect URIs on Microsoft's first-party application object");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_CustomApp_PreservesExistingMutationBehavior()
    {
        // Regression guard: a tenant-owned client app ID must keep the full custom-app validation
        // path (existence via /applications, permission/consent self-healing), unchanged.
        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();

        await _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId);

        await _graphApiService.Received().GraphGetWithResponseAsync(
            Arg.Any<string>(), Arg.Is<string>(p => p.Contains("displayName")), Arg.Any<bool>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>(), Arg.Any<GraphAuthenticationMode>());
    }

    #endregion

    #region HasWidsClaimOnIssuedAccessTokenAsync Tests

    private static string BuildTokenWithPayload(string payloadJson) =>
        $"{Base64UrlEncode("""{"alg":"none","typ":"JWT"}""")}.{Base64UrlEncode(payloadJson)}.sig";

    [Fact]
    public async Task HasWidsClaimOnIssuedAccessTokenAsync_WhenTokenCarriesWids_ReturnsTrue()
    {
        _graphApiService.GetClientAppAccessTokenAsync(
                ValidTenantId, FirstPartyClientAppId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(
                BuildTokenWithPayload("""{"wids":["62e90394-69f5-4237-9190-012177145e10"]}""")));

        var result = await _validator.HasWidsClaimOnIssuedAccessTokenAsync(FirstPartyClientAppId, ValidTenantId);

        result.Should().BeTrue(
            because: "the claim on a token actually issued to the app is the only evidence available for an app registration the tenant cannot read");
    }

    [Fact]
    public async Task HasWidsClaimOnIssuedAccessTokenAsync_WhenTokenOmitsWids_ReturnsFalse()
    {
        _graphApiService.GetClientAppAccessTokenAsync(
                ValidTenantId, FirstPartyClientAppId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(BuildTokenWithPayload("""{"scp":"User.Read"}""")));

        var result = await _validator.HasWidsClaimOnIssuedAccessTokenAsync(FirstPartyClientAppId, ValidTenantId);

        result.Should().BeFalse(
            because: "a decodable token without the claim proves the claim is absent from issued tokens");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    public async Task HasWidsClaimOnIssuedAccessTokenAsync_WhenTokenUnavailableOrUnreadable_ReturnsNull(string? token)
    {
        _graphApiService.GetClientAppAccessTokenAsync(
                ValidTenantId, FirstPartyClientAppId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(token));

        var result = await _validator.HasWidsClaimOnIssuedAccessTokenAsync(FirstPartyClientAppId, ValidTenantId);

        result.Should().BeNull(
            because: "an unavailable or undecodable token is inconclusive and must never be reported as a confirmed absent claim");
    }

    [Fact]
    public async Task HasWidsClaimOnIssuedAccessTokenAsync_WhenTokenAcquisitionThrows_ReturnsNull()
    {
        _graphApiService.GetClientAppAccessTokenAsync(
                ValidTenantId, FirstPartyClientAppId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new InvalidOperationException("token acquisition failed")));

        var result = await _validator.HasWidsClaimOnIssuedAccessTokenAsync(FirstPartyClientAppId, ValidTenantId);

        result.Should().BeNull(
            because: "a token acquisition failure is inconclusive, not proof that the claim is missing");
    }

    [Fact]
    public async Task HasWidsClaimOnIssuedAccessTokenAsync_WhenTokenAcquisitionTimesOut_ReturnsNull()
    {
        _graphApiService.GetClientAppAccessTokenAsync(
                ValidTenantId, FirstPartyClientAppId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(
                new TaskCanceledException("token provider timed out")));

        var result = await _validator.HasWidsClaimOnIssuedAccessTokenAsync(
            FirstPartyClientAppId, ValidTenantId);

        result.Should().BeNull(
            because: "a provider timeout without caller cancellation is inconclusive and must not abort all requirement checks");
    }

    [Fact]
    public async Task HasWidsClaimOnIssuedAccessTokenAsync_WhenCallerCancels_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _graphApiService.GetClientAppAccessTokenAsync(
                ValidTenantId, FirstPartyClientAppId, Arg.Any<IEnumerable<string>>(), cts.Token)
            .Returns(Task.FromException<string?>(new OperationCanceledException(cts.Token)));

        Func<Task> act = async () => await _validator.HasWidsClaimOnIssuedAccessTokenAsync(
            FirstPartyClientAppId, ValidTenantId, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            because: "caller cancellation must abort the command rather than be reported as an inconclusive claim");
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
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>());
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenPublicClientFlowsDisabled_PatchesPublicClientFlows()
    {
        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();
        SetupPublicClientFlowsGet(enabled: false);
        _graphApiService.GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(Task.FromResult(true));
        // Redirect URIs GET returns null (unmatched) → no separate PATCH

        await _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId);

        // Exactly one PATCH — the public client flows enable
        await _graphApiService.Received(1).GraphPatchAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains(AppObjId)),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>());
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

    #region Confirmation Prompt Tests

    private ClientAppValidator CreateValidatorWithConfirmation(IConfirmationProvider confirmationProvider)
    {
        var executorLogger = Substitute.For<ILogger<CommandExecutor>>();
        var executor = Substitute.For<CommandExecutor>(executorLogger);
        var graphServiceLogger = Substitute.For<ILogger<GraphApiService>>();
        var graphApiService = Substitute.For<GraphApiService>(graphServiceLogger, executor);

        // Wire up the same app/permission mocks used by the happy-path tests
        var requiredResourceAccess = $$"""
        [
            {
                "resourceAppId": "{{AuthenticationConstants.MicrosoftGraphResourceAppId}}",
                "resourceAccess": [
                    {"id": "{{AgentBlueprintReadWriteAllId}}", "type": "Scope"},
                    {"id": "{{AgentBlueprintPrincipalCreateId}}", "type": "Scope"},
                    {"id": "{{AgentRegistrationReadWriteAllId}}", "type": "Scope"},
                    {"id": "{{ApplicationReadAllId}}", "type": "Scope"},
                    {"id": "{{UserReadId}}", "type": "Scope"}
                ]
            }
        ]
        """;

        var appJson = $$"""
        {
            "value": [{
                "id": "{{AppObjId}}",
                "appId": "{{ValidClientAppId}}",
                "displayName": "Test App",
                "requiredResourceAccess": {{requiredResourceAccess}}
            }]
        }
        """;

        graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("displayName")),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDocument.Parse(appJson)
            }));

        var permJson = $$"""
        {
            "value": [{
                "id": "graph-sp-id-123",
                "oauth2PermissionScopes": [
                    {"id": "{{AgentBlueprintReadWriteAllId}}", "value": "AgentIdentityBlueprint.ReadWrite.All"},
                    {"id": "{{AgentRegistrationReadWriteAllId}}", "value": "AgentRegistration.ReadWrite.All"},
                    {"id": "{{ApplicationReadAllId}}", "value": "Application.Read.All"},
                    {"id": "{{UserReadId}}", "value": "User.Read"}
                ]
            }]
        }
        """;

        graphApiService.GraphGetAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("oauth2PermissionScopes")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(permJson)));

        return new ClientAppValidator(_logger, graphApiService, confirmationProvider);
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenUserDeclinesWithMissingPermissions_ThrowsImmediately()
    {
        var confirmationProvider = Substitute.For<IConfirmationProvider>();
        confirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(Task.FromResult(false));

        var executorLogger = Substitute.For<ILogger<CommandExecutor>>();
        var executor = Substitute.For<CommandExecutor>(executorLogger);
        var graphServiceLogger = Substitute.For<ILogger<GraphApiService>>();
        var graphApiService = Substitute.For<GraphApiService>(graphServiceLogger, executor);

        // App exists but has no permissions → triggers missing permissions mutation
        var appJson = $$"""
        {
            "value": [{
                "id": "{{AppObjId}}",
                "appId": "{{ValidClientAppId}}",
                "displayName": "Test App",
                "requiredResourceAccess": []
            }]
        }
        """;

        graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("displayName")),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult(new GraphApiService.GraphResponse
            {
                IsSuccess = true, StatusCode = 200, Json = JsonDocument.Parse(appJson)
            }));

        var permJson = $$"""
        {
            "value": [{
                "id": "graph-sp-id-123",
                "oauth2PermissionScopes": [
                    {"id": "{{AgentBlueprintReadWriteAllId}}", "value": "AgentIdentityBlueprint.ReadWrite.All"}
                ]
            }]
        }
        """;

        graphApiService.GraphGetAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("oauth2PermissionScopes")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(permJson)));

        graphApiService.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Cli.Models.RoleCheckResult.HasRole));

        var validator = new ClientAppValidator(_logger, graphApiService, confirmationProvider);

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Contain("declined");
        exception.ErrorDetails.Should().Contain(d => d.Contains("Missing permissions"));

        // Confirm no Graph mutations were attempted after the decline
        await graphApiService.DidNotReceive().GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>());
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenUserDeclinesWithMissingRedirectUris_ThrowsImmediately()
    {
        var confirmationProvider = Substitute.For<IConfirmationProvider>();
        confirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(Task.FromResult(false));

        var validator = CreateValidatorWithConfirmation(confirmationProvider);

        // Return empty redirect URIs → triggers missing redirect URI mutation
        var redirectUriJson = $$"""
        {
            "value": [{
                "id": "{{AppObjId}}",
                "publicClient": { "redirectUris": [] }
            }]
        }
        """;

        // Wire redirect URI GET on the underlying graphApiService — re-fetch it via reflection isn't clean;
        // instead call EnsureRedirectUrisAsync indirectly via EnsureValidClientAppAsync.
        // The CreateValidatorWithConfirmation graph mock returns null for unmatched calls,
        // so set up the publicClient query on the shared _graphApiService substitute.
        _graphApiService.GraphGetAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("publicClient") && !p.Contains("displayName")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(redirectUriJson)));

        // Build a fresh validator wired to _graphApiService so the redirect URI mock is reachable
        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();
        _graphApiService.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Cli.Models.RoleCheckResult.HasRole));
        var validatorWithSharedGraph = new ClientAppValidator(_logger, _graphApiService, confirmationProvider);

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => validatorWithSharedGraph.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Contain("declined");
        exception.ErrorDetails.Should().Contain(d => d.Contains("redirect URI"));

        await _graphApiService.DidNotReceive().GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>());
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenUserDeclinesWithPublicClientDisabled_ThrowsImmediately()
    {
        var confirmationProvider = Substitute.For<IConfirmationProvider>();
        confirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(Task.FromResult(false));

        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();
        SetupPublicClientFlowsGet(enabled: false);
        _graphApiService.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Cli.Models.RoleCheckResult.HasRole));

        var validator = new ClientAppValidator(_logger, _graphApiService, confirmationProvider);

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Contain("declined");
        exception.ErrorDetails.Should().Contain(d => d.Contains("public client flows"));

        await _graphApiService.DidNotReceive().GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>());
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenUserDeclinesWithAllMutationsPending_ThrowsWithAllDetails()
    {
        var confirmationProvider = Substitute.For<IConfirmationProvider>();
        confirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(Task.FromResult(false));

        // App missing permissions
        SetupAppInfoGet(ValidClientAppId, requiredResourceAccess: "[]");
        SetupPermissionResolution();
        SetupPublicClientFlowsGet(enabled: false);

        var redirectUriJson = $$"""
        {
            "value": [{
                "id": "{{AppObjId}}",
                "publicClient": { "redirectUris": [] }
            }]
        }
        """;
        _graphApiService.GraphGetAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("publicClient") && !p.Contains("displayName")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(redirectUriJson)));

        _graphApiService.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Cli.Models.RoleCheckResult.HasRole));

        var validator = new ClientAppValidator(_logger, _graphApiService, confirmationProvider);

        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Contain("declined");
        exception.ErrorDetails.Should().Contain(d => d.Contains("Missing permissions"));
        exception.ErrorDetails.Should().Contain(d => d.Contains("redirect URI"));
        exception.ErrorDetails.Should().Contain(d => d.Contains("public client flows"));
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenUserAcceptsConfirmation_ProceedsWithFixes()
    {
        var confirmationProvider = Substitute.For<IConfirmationProvider>();
        confirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(Task.FromResult(true));

        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();
        SetupPublicClientFlowsGet(enabled: false);
        _graphApiService.GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(Task.FromResult(true));
        _graphApiService.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Cli.Models.RoleCheckResult.HasRole));

        var validator = new ClientAppValidator(_logger, _graphApiService, confirmationProvider);

        // Should not throw — fixes were accepted and applied
        await validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId);

        await _graphApiService.Received(1).GraphPatchAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains(AppObjId)),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>());
    }

    // ── Unknown-role probe tests ────────────────────────────────────────────────
    //
    // When IsCurrentUserAdminAsync returns Unknown (wids claim absent from the access token),
    // the validator falls back to using the wids PATCH itself as the admin probe:
    //   - PATCH succeeds → caller is admin → continue applying remaining fixes (no prompt)
    //   - PATCH fails to land wids → caller is non-admin → throw the existing GA-required error
    // This keeps wids-only role detection while breaking the chicken-and-egg in tenants where
    // the app was provisioned before wids was a required optional claim.

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAdminRoleUnknownAndWidsProbeSucceeds_DoesNotPrompt()
    {
        // Arrange
        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();

        // First GET on /v1.0/applications?$select=id,optionalClaims returns "no wids" so the
        // pre-flight reports it missing. Second GET (after the PATCH) returns "wids present" so
        // TryProbeAdminViaWidsPatchAsync concludes Admin.
        var noWidsJson = $$"""{ "value": [ { "id": "{{AppObjId}}", "optionalClaims": null } ] }""";
        var withWidsJson = $$"""
        { "value": [ { "id": "{{AppObjId}}", "optionalClaims": { "accessToken": [ { "name": "wids", "essential": false, "additionalProperties": [] } ] } } ] }
        """;
        _graphApiService.GraphGetAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("optionalClaims")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(
                _ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(noWidsJson)),    // pre-flight read
                _ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(noWidsJson)),    // inside EnsureWids read
                _ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(withWidsJson))); // post-probe read

        _graphApiService.GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(Task.FromResult(true));

        // First call returns Unknown to exercise the probe path; subsequent calls (if any)
        // return HasRole so downstream code can verify admin authority normally.
        var adminCheckCallCount = 0;
        _graphApiService.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var result = adminCheckCallCount == 0 ? Cli.Models.RoleCheckResult.Unknown : Cli.Models.RoleCheckResult.HasRole;
                adminCheckCallCount++;
                return Task.FromResult(result);
            });

        var confirmationProvider = Substitute.For<IConfirmationProvider>();
        var validator = new ClientAppValidator(_logger, _graphApiService, confirmationProvider);

        // Act
        await validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId);

        // Assert
        // The Unknown-probe path skips the confirmation prompt — the PATCH itself proves admin authority.
        await confirmationProvider.DidNotReceive().ConfirmAsync(Arg.Any<string>());

        await _graphApiService.Received().GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>());

        // Cache must be invalidated after the successful wids PATCH (per D1) so subsequent
        // token acquisitions pick up the new claim.
        await _graphApiService.Received().ClearTokenCacheAsync();
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAdminRoleUnknownAndWidsProbeFails_ThrowsGlobalAdministratorRequiredError()
    {
        // Arrange
        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();

        // GET always returns "no wids" — simulating a PATCH that didn't land (caller lacks
        // directory-role write authority on the app, i.e. not admin).
        var noWidsJson = $$"""{ "value": [ { "id": "{{AppObjId}}", "optionalClaims": null } ] }""";
        _graphApiService.GraphGetAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("optionalClaims")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(noWidsJson)));

        _graphApiService.GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(Task.FromResult(false));  // PATCH returns false — wids does not land

        _graphApiService.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Cli.Models.RoleCheckResult.Unknown));

        var confirmationProvider = Substitute.For<IConfirmationProvider>();
        var validator = new ClientAppValidator(_logger, _graphApiService, confirmationProvider);

        // Act + Assert
        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.IssueDescription.Should().Contain("Global Administrator",
            because: "when the probe cannot land wids, the validator must surface the existing non-admin error");

        // The Unknown-probe path skips the prompt whether the probe succeeds or fails.
        await confirmationProvider.DidNotReceive().ConfirmAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAdminRoleUnknownAndProbeIsInconclusive_ThrowsGlobalAdministratorRequiredError()
    {
        // Arrange — TryProbeAdminViaWidsPatchAsync returns ProbeResult.Inconclusive only when an
        // exception escapes the helpers it calls. The internal helpers all catch internally, but
        // the probe's own LogInformation call ("Admin authority confirmed..." / "Admin authority
        // NOT confirmed...") is outside any catch, so an exception there reaches the probe's outer
        // catch (Exception) → returns Inconclusive. We use a logger that throws on those two
        // specific probe-internal messages to force the Inconclusive branch.
        //
        // Contract under test: when the probe is Inconclusive, the validator must NOT propagate a
        // wrapped "Unexpected error" exception; it must degrade gracefully and surface the same
        // Global-Administrator-required error as the NotAdmin branch (per the HIGH-1 review fix).
        var throwingLogger = new ThrowOnProbeAuthorityLogger();
        var graphApiService = BuildGraphApiServiceForProbe();

        var confirmationProvider = Substitute.For<IConfirmationProvider>();
        var validator = new ClientAppValidator(throwingLogger, graphApiService, confirmationProvider);

        // Act + Assert — must throw the validation exception, NOT a raw InvalidOperationException
        // from the logger and NOT a "Unexpected error during client app validation" wrapper.
        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed,
            because: "Inconclusive must surface the standard validation error, not a wrapped 'unexpected error' from the probe failure");
        exception.IssueDescription.Should().Contain("Global Administrator",
            because: "Inconclusive must route to the same non-admin guidance as NotAdmin per the HIGH-1 review fix (graceful degradation)");
        exception.IssueDescription.Should().NotContain("Unexpected error",
            because: "the probe's transient failure must not be re-wrapped as a generic unexpected error — that would convert a network blip into a worse failure than the baseline Unknown path");

        throwingLogger.AdminAuthorityLogAttempted.Should().BeTrue(
            because: "the probe must reach its outer LogInformation call so that our injected exception forces the Inconclusive branch");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAdminRoleUnknownAndProbeIsInconclusive_DoesNotPromptUser()
    {
        // Arrange — same setup as the Inconclusive throws-correctly test. The user has already
        // been categorised as non-admin (Inconclusive → DoesNotHaveRole), so a confirmation prompt
        // would be meaningless (there is nothing for the non-admin user to confirm: the existing
        // non-admin guidance is unconditional).
        var throwingLogger = new ThrowOnProbeAuthorityLogger();
        var graphApiService = BuildGraphApiServiceForProbe();

        var confirmationProvider = Substitute.For<IConfirmationProvider>();
        var validator = new ClientAppValidator(throwingLogger, graphApiService, confirmationProvider);

        // Act
        await Assert.ThrowsAsync<ClientAppValidationException>(
            () => validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        // Assert
        await confirmationProvider.DidNotReceive().ConfirmAsync(Arg.Any<string>());
    }

    /// <summary>
    /// Builds a GraphApiService substitute wired up so the Unknown-probe path is reached:
    /// app exists with all required permissions, IsCurrentUserAdminAsync returns Unknown,
    /// and the optionalClaims read reports wids missing (so needsWidsClaim=true).
    /// Used by the Inconclusive-branch tests.
    /// </summary>
    private GraphApiService BuildGraphApiServiceForProbe()
    {
        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();

        // Wids optional claim is missing — triggers the Unknown-probe path.
        var noWidsJson = $$"""{ "value": [ { "id": "{{AppObjId}}", "optionalClaims": null } ] }""";
        _graphApiService.GraphGetAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("optionalClaims")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(noWidsJson)));

        // PATCH succeeds (so the probe's success-path LogInformation is reached and our injected
        // logger exception fires there — exercising the Inconclusive catch). The choice of true
        // vs. false on the PATCH itself does not matter for this test, since the logger exception
        // is what forces Inconclusive, not the PATCH outcome.
        _graphApiService.GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(Task.FromResult(true));

        _graphApiService.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Cli.Models.RoleCheckResult.Unknown));

        return _graphApiService;
    }

    /// <summary>
    /// Test-only ILogger that throws on the probe-internal "Admin authority..." log messages
    /// (both "Admin authority confirmed via wids PATCH probe" and "Admin authority NOT confirmed").
    /// These two LogInformation calls sit outside any try/catch inside
    /// TryProbeAdminViaWidsPatchAsync, so throwing there is the only realistic way to force the
    /// ProbeResult.Inconclusive branch (every helper the probe calls catches exceptions internally).
    /// Other log messages pass through silently.
    /// </summary>
    private sealed class ThrowOnProbeAuthorityLogger : ILogger<ClientAppValidator>
    {
        public bool AdminAuthorityLogAttempted { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message.Contains("Admin authority", StringComparison.Ordinal))
            {
                AdminAuthorityLogAttempted = true;
                throw new InvalidOperationException("simulated probe-internal logger fault");
            }
        }
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenSkipConfirmationTrue_AppliesFixesWithoutPrompting()
    {
        SetupAppInfoWithAllPermissions(ValidClientAppId);
        SetupPermissionResolution();
        SetupPublicClientFlowsGet(enabled: false);
        _graphApiService.GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(Task.FromResult(true));

        var confirmationProvider = Substitute.For<IConfirmationProvider>();
        var validator = new ClientAppValidator(_logger, _graphApiService, confirmationProvider);

        await validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId, skipConfirmation: true);

        // Prompt must never be shown when skipConfirmation=true
        await confirmationProvider.DidNotReceive().ConfirmAsync(Arg.Any<string>());

        // But fix must still be applied
        await _graphApiService.Received(1).GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>());
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
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>());
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
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(Task.FromResult(true));

        await _validator.EnsureRedirectUrisAsync(ValidClientAppId, ValidTenantId);

        await _graphApiService.Received(1).GraphPatchAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains(AppObjId)),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>());
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
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(Task.FromResult(true));

        await _validator.EnsureRedirectUrisAsync(ValidClientAppId, ValidTenantId);

        await _graphApiService.Received(1).GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>());
    }

    [Fact]
    public async Task EnsureRedirectUrisAsync_WhenGetFails_LogsWarningAndContinues()
    {
        // GraphGetAsync returns null (unmatched default) — simulates Graph API failure

        await _validator.EnsureRedirectUrisAsync(ValidClientAppId, ValidTenantId);

        await _graphApiService.DidNotReceive().GraphPatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>());
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
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>());
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Sets up the app info GET (select includes displayName) to return an app with the given requiredResourceAccess JSON.
    /// Pass "null" to simulate a null requiredResourceAccess; pass "[]" for an empty array.
    /// </summary>
    private void SetupAppInfoGet(string appId, string requiredResourceAccess = "[]")
    {
        var json = BuildAppInfoJson(appId, requiredResourceAccess);

        // GetClientAppInfoAsync now calls GraphGetWithResponseAsync; GraphGetAsync is used by
        // subsequent steps (permission resolution, consent checks, redirect URIs, etc.).
        _graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("displayName")),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDocument.Parse(json)
            }));
    }

    private static string BuildAppInfoJson(string appId, string requiredResourceAccess) => $$"""
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

    /// <summary>
    /// Sets up the app info GET to fail with the given HTTP status. Status 0 models a
    /// token-acquisition or network failure where no response was received.
    /// </summary>
    private void SetupAppInfoGetFailure(int statusCode, string reasonPhrase)
    {
        _graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("displayName")),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult(new GraphApiService.GraphResponse
            {
                IsSuccess = false,
                StatusCode = statusCode,
                ReasonPhrase = reasonPhrase
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
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDocument.Parse("""{"value": []}""")
            }));
    }

    private static string BuildAllPermissionsResourceAccess() => $$"""
        [
            {
                "resourceAppId": "{{AuthenticationConstants.MicrosoftGraphResourceAppId}}",
                "resourceAccess": [
                    {"id": "{{AgentBlueprintReadWriteAllId}}", "type": "Scope"},
                    {"id": "{{AgentBlueprintPrincipalCreateId}}", "type": "Scope"},
                    {"id": "{{AgentRegistrationReadWriteAllId}}", "type": "Scope"},
                    {"id": "{{ApplicationReadAllId}}", "type": "Scope"},
                    {"id": "{{UserReadId}}", "type": "Scope"}
                ]
            }
        ]
        """;

    /// <summary>
    /// Sets up the app info GET with all required permissions. The 5 GUID-resolvable permissions
    /// match those returned by SetupPermissionResolution; AgentIdentity.Read.All and
    /// AgentIdentity.DeleteRestore.All are resolved via the GetConsentedPermissionsAsync fallback.
    /// </summary>
    private void SetupAppInfoWithAllPermissions(string appId)
    {
        SetupAppInfoGet(appId, requiredResourceAccess: BuildAllPermissionsResourceAccess());
        SetupConsentGrantForAgentIdentityCreate();
    }

    /// <summary>
    /// Sets up the Microsoft Graph SP permission resolution GET (select includes oauth2PermissionScopes).
    /// Returns the 5 required permissions with GUIDs matching the test constants.
    /// </summary>
    private void SetupPermissionResolution()
    {
        var json = $$"""
        {
            "value": [
                {
                    "id": "graph-sp-id-123",
                    "oauth2PermissionScopes": [
                        {"id": "{{AgentBlueprintReadWriteAllId}}", "value": "AgentIdentityBlueprint.ReadWrite.All"},
                        {"id": "{{AgentBlueprintPrincipalCreateId}}", "value": "AgentIdentityBlueprintPrincipal.Create"},
                        {"id": "{{AgentRegistrationReadWriteAllId}}", "value": "AgentRegistration.ReadWrite.All"},
                        {"id": "{{ApplicationReadAllId}}", "value": "Application.Read.All"},
                        {"id": "{{UserReadId}}", "value": "User.Read"}
                    ]
                }
            ]
        }
        """;

        _graphApiService.GraphGetAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("oauth2PermissionScopes")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(json)));
    }

    /// <summary>
    /// Sets up the consent-grant fallback path for AgentIdentity.Create.All.
    /// This permission has no GUID in v1.0 oauth2PermissionScopes, so ClientAppValidator
    /// resolves it via GetConsentedPermissionsAsync (step 3.5). Uses a distinct SP object ID
    /// (ConsentSpObjId) so this mock does not interfere with SetupAdminConsentSp/SetupAdminConsentGrantsEmpty.
    /// </summary>
    private void SetupConsentGrantForAgentIdentityCreate()
    {
        // SP lookup used by GetConsentedPermissionsAsync: $select=id (no extra fields).
        // Discriminated from ValidateAdminConsentAsync ($select=id,appId) by EndsWith.
        var spJson = $$"""{"value": [{"id": "{{ConsentSpObjId}}"}]}""";
        _graphApiService.GraphGetAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("servicePrincipals") && p.EndsWith("&$select=id")),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(spJson)));

        // Grants for ConsentSpObjId — contains all four no-GUID scopes so they are removed from missingPermissions.
        var grantsJson = """{"value": [{"scope": "AgentIdentity.Create.All AgentIdentityBlueprint.DeleteRestore.All AgentIdentity.DeleteRestore.All AgentIdentity.Read.All"}]}""";
        _graphApiService.GraphGetAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("oauth2PermissionGrants") && p.Contains(ConsentSpObjId)),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(grantsJson)));
        // Also mock GraphGetWithResponseAsync for the same grants endpoint — production code now
        // uses this method to detect 403 (caller lacks DelegatedPermissionGrant.Read.All) without
        // burying the status code. Returning a 200 with parsed JSON preserves the original
        // consent-resolution behavior these tests rely on.
        _graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("oauth2PermissionGrants") && p.Contains(ConsentSpObjId)),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDocument.Parse(grantsJson)
            }));
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
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
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
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse("""{"value": []}""")));
        // Mirror for GraphGetWithResponseAsync (used by GetConsentedPermissionsAsync to detect 403).
        _graphApiService.GraphGetWithResponseAsync(
            Arg.Any<string>(),
            Arg.Is<string>(p => p.Contains("oauth2PermissionGrants") && p.Contains(spObjectId)),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDocument.Parse("""{"value": []}""")
            }));
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
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
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
            Arg.Any<IEnumerable<string>?>(), Arg.Any<GraphAuthenticationMode>())
            .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(json)));
    }

    #endregion
}
