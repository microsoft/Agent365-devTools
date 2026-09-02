// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Tests for <see cref="AzRestConsentRunner"/> — the <c>az rest</c>-based replacement for
/// the deprecated <c>PowerShellConsentRunner</c>. Mocks <see cref="CommandExecutor"/> so we
/// can assert the exact az invocations (URL, method, headers) and verify idempotency
/// (existing AllPrincipals grant with the requested scopes already merged → no PATCH/POST).
/// </summary>
public class AzRestConsentRunnerTests
{
    private const string BlueprintSpId = "11111111-1111-1111-1111-111111111111";
    private const string ResourceSpId = "22222222-2222-2222-2222-222222222222";
    private const string ExistingGrantId = "33333333-3333-3333-3333-333333333333";
    private const string ObsAppId = ConfigConstants.ObservabilityApiAppId;

    private readonly CommandExecutor _executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
    private readonly ILogger _logger = NullLogger.Instance;

    [Fact]
    public async Task InvalidBlueprintSpId_ReturnsNotAttempted()
    {
        var (attempted, succeeded) = await AzRestConsentRunner.TryRunAsync(
            _executor,
            blueprintSpObjectId: "not-a-guid",
            specs: new[] { ObsSpec() },
            _logger,
            ct: default);

        attempted.Should().BeFalse(because: "the GUID guard must reject invalid input before any az invocation");
        succeeded.Should().BeFalse();
        await _executor.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoDelegatedSpecs_ReturnsNotAttempted()
    {
        // Spec carries only S2S roles; the consent runner has no work to do.
        var specs = new[]
        {
            new ResourcePermissionSpec(ObsAppId, "Observability API",
                Scopes: Array.Empty<string>(),
                SetInheritable: false,
                AppRoleScopes: new[] { "Agent365.Observability.OtelWrite" })
        };

        var (attempted, succeeded) = await AzRestConsentRunner.TryRunAsync(
            _executor, BlueprintSpId, specs, _logger, ct: default);

        attempted.Should().BeFalse(because: "no delegated specs means there's nothing for the consent runner to grant");
        succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task UnsafeScopeValue_ReturnsNotAttempted()
    {
        var specs = new[]
        {
            new ResourcePermissionSpec(ObsAppId, "Observability API",
                Scopes: new[] { "Agent365.Observability.OtelWrite'; DROP TABLE --" },
                SetInheritable: false)
        };

        var (attempted, succeeded) = await AzRestConsentRunner.TryRunAsync(
            _executor, BlueprintSpId, specs, _logger, ct: default);

        attempted.Should().BeFalse(because: "scope values are interpolated into the OData filter and request body; the allowlist must reject anything outside [A-Za-z0-9._-]");
        succeeded.Should().BeFalse();
        await _executor.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResourceSpNotFoundInTenant_ReturnsAttemptedAndFailed()
    {
        // GET resource SP → empty value array. No write should be attempted.
        StubResourceSpLookup(returnsEmpty: true);

        var (attempted, succeeded) = await AzRestConsentRunner.TryRunAsync(
            _executor, BlueprintSpId, new[] { ObsSpec() }, _logger, ct: default);

        attempted.Should().BeTrue();
        succeeded.Should().BeFalse(because: "no resource SP means we cannot anchor the oauth2PermissionGrant; the operator must provision the SP first");
        await _executor.DidNotReceive().ExecuteAsync(
            "az", Arg.Is<string>(s => s.Contains("--method POST") || s.Contains("--method PATCH")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistingGrantAlreadyHasRequestedScopes_NoWriteIssued()
    {
        // GET resource SP → found. GET existing grant → already has the requested scope.
        // Runner must not PATCH (idempotent) and must report success.
        StubResourceSpLookup(returnsEmpty: false);
        StubExistingGrantLookup(existingScope: ConfigConstants.ObservabilityApiOtelWriteScope);

        var (attempted, succeeded) = await AzRestConsentRunner.TryRunAsync(
            _executor, BlueprintSpId, new[] { ObsSpec() }, _logger, ct: default);

        attempted.Should().BeTrue();
        succeeded.Should().BeTrue(because: "existing grant already covers the requested scope set — re-issuing the PATCH would be a no-op and waste a round-trip");
        await _executor.DidNotReceive().ExecuteAsync(
            "az", Arg.Is<string>(s => s.Contains("--method POST") || s.Contains("--method PATCH")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistingGrantWithSubsetOfScopes_PatchedWithMergedScopeSet()
    {
        // Existing grant covers a different scope. The runner must PATCH with the union.
        StubResourceSpLookup(returnsEmpty: false);
        StubExistingGrantLookup(existingScope: "SomeOtherScope");

        // PATCH succeeds.
        _executor
            .ExecuteAsync("az", Arg.Is<string>(s => s.Contains("--method PATCH") && s.Contains($"oauth2PermissionGrants/{ExistingGrantId}")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult { ExitCode = 0 }));

        var (attempted, succeeded) = await AzRestConsentRunner.TryRunAsync(
            _executor, BlueprintSpId, new[] { ObsSpec() }, _logger, ct: default);

        attempted.Should().BeTrue();
        succeeded.Should().BeTrue();
        // PATCH on the existing grant id, not a fresh POST.
        await _executor.Received().ExecuteAsync(
            "az", Arg.Is<string>(s => s.Contains("--method PATCH") && s.Contains($"oauth2PermissionGrants/{ExistingGrantId}")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _executor.DidNotReceive().ExecuteAsync(
            "az", Arg.Is<string>(s => s.Contains("--method POST")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoExistingGrant_PostedWithNewGrantBody()
    {
        // GET existing grant returns an empty value array → fresh POST.
        StubResourceSpLookup(returnsEmpty: false);
        StubExistingGrantLookup(empty: true);

        _executor
            .ExecuteAsync("az", Arg.Is<string>(s => s.Contains("--method POST") && s.Contains("/oauth2PermissionGrants")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult { ExitCode = 0 }));

        var (attempted, succeeded) = await AzRestConsentRunner.TryRunAsync(
            _executor, BlueprintSpId, new[] { ObsSpec() }, _logger,
            ct: default, graphBaseUrl: "https://graph.example");

        attempted.Should().BeTrue();
        succeeded.Should().BeTrue();
        await _executor.Received().ExecuteAsync(
            "az", Arg.Is<string>(s => s.Contains("https://graph.example/v1.0/oauth2PermissionGrants")
                && s.Contains("--method POST") && !s.Contains($"/{ExistingGrantId}")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _executor.DidNotReceive().ExecuteAsync(
            "az", Arg.Is<string>(s => s.Contains("--method PATCH")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AzPostExitsNonZero_OverallFails()
    {
        // POST returns exit 1 with stderr. Runner reports failure and the caller's existing
        // Action Required path surfaces the recovery URL.
        StubResourceSpLookup(returnsEmpty: false);
        StubExistingGrantLookup(empty: true);

        _executor
            .ExecuteAsync("az", Arg.Is<string>(s => s.Contains("--method POST")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "Insufficient privileges to complete the operation." }));

        var (attempted, succeeded) = await AzRestConsentRunner.TryRunAsync(
            _executor, BlueprintSpId, new[] { ObsSpec() }, _logger, ct: default);

        attempted.Should().BeTrue();
        succeeded.Should().BeFalse(because: "az exited non-zero — the grant was not created, so we must surface a failure so the orchestrator's Action Required path takes over");
    }

    [Fact]
    public void TryExtractFirstId_ValidOdataValueArray_ReturnsFirstId()
    {
        var json = "{\"value\":[{\"id\":\"" + ResourceSpId + "\"},{\"id\":\"another\"}]}";
        AzRestConsentRunner.TryExtractFirstId(json).Should().Be(ResourceSpId,
            because: "the runner only needs the first match; az's $filter=appId eq '...' query is uniquely keyed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"value\":[]}")]
    [InlineData("{\"unrelated\":true}")]
    public void TryExtractFirstId_InvalidOrEmpty_ReturnsNull(string? azOutput)
    {
        AzRestConsentRunner.TryExtractFirstId(azOutput).Should().BeNull(
            because: "missing data must yield null so the caller takes the warning path instead of dereferencing");
    }

    [Fact]
    public void TryExtractFirstGrantIdAndScope_ParsesIdAndScope()
    {
        var json = "{\"value\":[{\"id\":\"" + ExistingGrantId + "\",\"scope\":\"A B C\"}]}";
        var (id, scope) = AzRestConsentRunner.TryExtractFirstGrantIdAndScope(json);
        id.Should().Be(ExistingGrantId);
        scope.Should().Be("A B C");
    }

    [Fact]
    public void TryExtractFirstGrantIdAndScope_EmptyArray_ReturnsBothNull()
    {
        var (id, scope) = AzRestConsentRunner.TryExtractFirstGrantIdAndScope("{\"value\":[]}");
        id.Should().BeNull();
        scope.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test scaffolding
    // ─────────────────────────────────────────────────────────────────────────

    private static ResourcePermissionSpec ObsSpec() =>
        new(ObsAppId,
            "Observability API",
            new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
            SetInheritable: false);

    private void StubResourceSpLookup(bool returnsEmpty)
    {
        var json = returnsEmpty
            ? "{\"value\":[]}"
            : $"{{\"value\":[{{\"id\":\"{ResourceSpId}\"}}]}}";
        _executor
            .ExecuteAsync("az",
                Arg.Is<string>(s => s.Contains("/servicePrincipals?") && s.Contains($"appId eq '{ObsAppId}'")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = json }));
    }

    private void StubExistingGrantLookup(string? existingScope = null, bool empty = false)
    {
        string json;
        if (empty)
        {
            json = "{\"value\":[]}";
        }
        else
        {
            json = "{\"value\":[{\"id\":\"" + ExistingGrantId + "\",\"scope\":\"" + (existingScope ?? string.Empty) + "\"}]}";
        }
        _executor
            .ExecuteAsync("az",
                Arg.Is<string>(s => s.Contains("oauth2PermissionGrants?") && s.Contains($"clientId eq '{BlueprintSpId}'")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = json }));
    }
}
