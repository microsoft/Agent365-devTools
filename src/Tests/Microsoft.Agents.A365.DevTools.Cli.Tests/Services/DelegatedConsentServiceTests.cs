// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Unit tests for <see cref="DelegatedConsentService"/> covering input validation,
/// auth failure, and — critically — the 403 vs 5xx routing through <c>ScopeGrantResult</c>.
/// </summary>
public class DelegatedConsentServiceTests
{
    private static readonly string ValidAppId = "11111111-1111-1111-1111-111111111111";
    private static readonly string ValidTenantId = "22222222-2222-2222-2222-222222222222";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static GraphApiService FakeGraphServiceWithToken(string? token)
    {
        var svc = Substitute.ForPartsOf<GraphApiService>();
        svc.GetGraphAccessTokenAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(token));
        return svc;
    }

    private static HttpResponseMessage SpResponse(string spId) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = spId } } }))
        };

    private static HttpResponseMessage GrantsWithScope(string grantId, string scope) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                value = new[] { new { id = grantId, scope } }
            }))
        };

    private static HttpResponseMessage EmptyGrants() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = Array.Empty<object>() }))
        };

    private static HttpResponseMessage CreatedGrant(string grantId) =>
        new(HttpStatusCode.Created)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { id = grantId }))
        };

    // ── Input validation ──────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureBlueprintPermissionGrantAsync_InvalidCallingAppId_ReturnsFalse()
    {
        var svc = new DelegatedConsentService(NullLogger.Instance, Substitute.ForPartsOf<GraphApiService>());

        var result = await svc.EnsureBlueprintPermissionGrantAsync("not-a-guid", ValidTenantId);

        result.Should().BeFalse(because: "a non-GUID callingAppId must be rejected before any network call");
    }

    [Fact]
    public async Task EnsureBlueprintPermissionGrantAsync_InvalidTenantId_ReturnsFalse()
    {
        var svc = new DelegatedConsentService(NullLogger.Instance, Substitute.ForPartsOf<GraphApiService>());

        var result = await svc.EnsureBlueprintPermissionGrantAsync(ValidAppId, "not-a-guid");

        result.Should().BeFalse(because: "a non-GUID tenantId must be rejected before any network call");
    }

    // ── Token acquisition failure ─────────────────────────────────────────────

    [Fact]
    public async Task EnsureBlueprintPermissionGrantAsync_WhenTokenIsNull_ReturnsFalse()
    {
        var svc = new DelegatedConsentService(NullLogger.Instance, FakeGraphServiceWithToken(null));

        var result = await svc.EnsureBlueprintPermissionGrantAsync(ValidAppId, ValidTenantId);

        result.Should().BeFalse(because: "when Graph token acquisition fails the method must abort without HTTP calls");
    }

    // ── Scope already on grant ────────────────────────────────────────────────

    [Fact]
    public async Task EnsureBlueprintPermissionGrantAsync_WhenScopeAlreadyGranted_ReturnsTrue()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueResponse(SpResponse("client-sp-id"));
        handler.QueueResponse(SpResponse("graph-sp-id"));
        handler.QueueResponse(GrantsWithScope("grant-id", "AgentIdentityBlueprint.ReadWrite.All"));

        var svc = new DelegatedConsentService(NullLogger.Instance, FakeGraphServiceWithToken("tok"), handler);

        var result = await svc.EnsureBlueprintPermissionGrantAsync(ValidAppId, ValidTenantId);

        result.Should().BeTrue(because: "when the required scope is already present, no update is needed");
    }

    // ── PATCH 403 → consent URL, not a retry message ─────────────────────────

    [Fact]
    public async Task EnsureBlueprintPermissionGrantAsync_WhenPatchReturns403_LogsConsentUrlNotRetry()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueResponse(SpResponse("client-sp-id"));
        handler.QueueResponse(SpResponse("graph-sp-id"));
        handler.QueueResponse(GrantsWithScope("grant-id", "other-scope")); // target scope absent
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"error\":{\"code\":\"Authorization_RequestDenied\"}}")
        });

        var logger = new CapturingLogger();
        var svc = new DelegatedConsentService(logger, FakeGraphServiceWithToken("tok"), handler);

        var result = await svc.EnsureBlueprintPermissionGrantAsync(ValidAppId, ValidTenantId);

        result.Should().BeFalse(because: "403 means the caller lacks permission to update the grant");
        logger.ErrorMessages.Should().Contain(m => m.Contains("adminconsent"),
            because: "403 is a permissions failure — the admin-consent URL must be surfaced");
        logger.ErrorMessages.Should().NotContain(m => m.Contains("transient", StringComparison.OrdinalIgnoreCase) ||
                                                       m.Contains("retry", StringComparison.OrdinalIgnoreCase),
            because: "403 must not be misdiagnosed as a transient server error");
    }

    // ── PATCH 500 → retry message, not a consent URL ─────────────────────────

    [Fact]
    public async Task EnsureBlueprintPermissionGrantAsync_WhenPatchReturns500_LogsRetryNotConsentUrl()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueResponse(SpResponse("client-sp-id"));
        handler.QueueResponse(SpResponse("graph-sp-id"));
        handler.QueueResponse(GrantsWithScope("grant-id", "other-scope")); // target scope absent
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"internal server error\"}")
        });

        var logger = new CapturingLogger();
        var svc = new DelegatedConsentService(logger, FakeGraphServiceWithToken("tok"), handler);

        var result = await svc.EnsureBlueprintPermissionGrantAsync(ValidAppId, ValidTenantId);

        result.Should().BeFalse(because: "a 5xx response means the server-side operation did not complete");
        logger.ErrorMessages.Should().Contain(m => m.Contains("transient", StringComparison.OrdinalIgnoreCase) ||
                                                    m.Contains("retry", StringComparison.OrdinalIgnoreCase),
            because: "a 5xx is a transient server error — the user must be told to retry, not to get admin consent");
        logger.ErrorMessages.Should().NotContain(m => m.Contains("adminconsent"),
            because: "a transient 5xx must not be misdiagnosed as a permissions problem requiring admin consent");
    }

    // ── No existing grant, POST succeeds ──────────────────────────────────────

    [Fact]
    public async Task EnsureBlueprintPermissionGrantAsync_WhenNoGrantExists_CreatesNewGrant_ReturnsTrue()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueResponse(SpResponse("client-sp-id"));
        handler.QueueResponse(SpResponse("graph-sp-id"));
        handler.QueueResponse(EmptyGrants());
        handler.QueueResponse(CreatedGrant("new-grant-id"));

        var svc = new DelegatedConsentService(NullLogger.Instance, FakeGraphServiceWithToken("tok"), handler);

        var result = await svc.EnsureBlueprintPermissionGrantAsync(ValidAppId, ValidTenantId);

        result.Should().BeTrue(because: "when no grant exists and POST succeeds, setup must proceed");
    }

    // ── Test helpers ──────────────────────────────────────────────────────────

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentBag<string> _errors = new();

        public IReadOnlyCollection<string> ErrorMessages => _errors;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                _errors.Add(formatter(state, exception));
        }
    }
}
