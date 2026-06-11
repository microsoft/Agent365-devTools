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
/// Tests for <see cref="AzRestS2SRunner"/> — the <c>az rest</c>-based replacement for
/// <c>PowerShellS2SRunner</c>. Mocks <see cref="CommandExecutor"/> so we can assert the
/// exact az invocations and verify idempotency (existing (resourceId, appRoleId)
/// assignment on the blueprint SP → no fresh POST).
/// </summary>
public class AzRestS2SRunnerTests
{
    private const string BlueprintSpId = "11111111-1111-1111-1111-111111111111";
    private const string ResourceSpId = "22222222-2222-2222-2222-222222222222";
    private const string OtelWriteRoleId = "44444444-4444-4444-4444-444444444444";
    private const string ObsAppId = ConfigConstants.ObservabilityApiAppId;
    private const string OtelWriteRole = ConfigConstants.ObservabilityApiOtelWriteScope;

    private readonly CommandExecutor _executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
    private readonly ILogger _logger = NullLogger.Instance;

    [Fact]
    public async Task InvalidBlueprintSpId_ReturnsNotAttempted()
    {
        var (attempted, succeeded) = await AzRestS2SRunner.TryRunAsync(
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
    public async Task NoS2SSpecs_ReturnsNotAttempted()
    {
        // Spec carries only delegated scopes; the S2S runner has no work to do.
        var specs = new[]
        {
            new ResourcePermissionSpec(ObsAppId, "Observability API",
                Scopes: new[] { OtelWriteRole },
                SetInheritable: false,
                AppRoleScopes: null)
        };

        var (attempted, succeeded) = await AzRestS2SRunner.TryRunAsync(
            _executor, BlueprintSpId, specs, _logger, ct: default);

        attempted.Should().BeFalse(because: "no specs with AppRoleScopes means there's nothing for the S2S runner to assign");
        succeeded.Should().BeFalse();
        await _executor.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnsafeRoleValue_ReturnsNotAttempted()
    {
        var specs = new[]
        {
            new ResourcePermissionSpec(ObsAppId, "Observability API",
                Scopes: Array.Empty<string>(),
                SetInheritable: false,
                AppRoleScopes: new[] { "Agent365.Observability.OtelWrite'; DROP TABLE --" })
        };

        var (attempted, succeeded) = await AzRestS2SRunner.TryRunAsync(
            _executor, BlueprintSpId, specs, _logger, ct: default);

        attempted.Should().BeFalse(because: "role values are interpolated into the resource SP lookup filter and the request body; the allowlist must reject anything outside [A-Za-z0-9._-]");
        succeeded.Should().BeFalse();
        await _executor.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidResourceAppId_ReturnsNotAttempted()
    {
        var specs = new[]
        {
            new ResourcePermissionSpec(ResourceAppId: "not-a-guid",
                "Observability API",
                Scopes: Array.Empty<string>(),
                SetInheritable: false,
                AppRoleScopes: new[] { OtelWriteRole })
        };

        var (attempted, succeeded) = await AzRestS2SRunner.TryRunAsync(
            _executor, BlueprintSpId, specs, _logger, ct: default);

        attempted.Should().BeFalse(because: "ResourceAppId reaches the OData $filter and must pass the GUID allowlist before any az invocation");
        succeeded.Should().BeFalse();
        await _executor.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistingAssignmentsGetFails_ReturnsAttemptedAndFailed()
    {
        // The very first GET (existing assignments on the blueprint SP) errors out.
        // We cannot reason about idempotency without it, so the runner must short-circuit
        // with Attempted=true so the orchestrator's Action Required path surfaces the failure.
        StubExistingAssignmentsGet(failure: true);

        var (attempted, succeeded) = await AzRestS2SRunner.TryRunAsync(
            _executor, BlueprintSpId, new[] { ObsSpec() }, _logger, ct: default);

        attempted.Should().BeTrue();
        succeeded.Should().BeFalse(because: "the initial GET drives every per-role idempotency decision; if it fails we cannot safely proceed");
        await _executor.DidNotReceive().ExecuteAsync(
            "az", Arg.Is<string>(s => s.Contains("--method POST")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResourceSpNotFoundInTenant_ReturnsAttemptedAndFailed()
    {
        // GET existing assignments → empty (fine).
        // GET resource SP → empty value array.
        StubExistingAssignmentsGet(emptyValue: true);
        StubResourceSpWithAppRolesLookup(returnsEmpty: true);

        var (attempted, succeeded) = await AzRestS2SRunner.TryRunAsync(
            _executor, BlueprintSpId, new[] { ObsSpec() }, _logger, ct: default);

        attempted.Should().BeTrue();
        succeeded.Should().BeFalse(because: "no resource SP means we cannot resolve the appRoleId, and we must not attempt to POST against a non-existent resource");
        await _executor.DidNotReceive().ExecuteAsync(
            "az", Arg.Is<string>(s => s.Contains("--method POST")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistingAssignmentAlreadyPresent_NoPostIssued()
    {
        // The blueprint SP already has (ResourceSpId, OtelWriteRoleId) assigned.
        // Runner must skip the POST and report success.
        StubExistingAssignmentsGet(existingResourceId: ResourceSpId, existingAppRoleId: OtelWriteRoleId);
        StubResourceSpWithAppRolesLookup(returnsEmpty: false);

        var (attempted, succeeded) = await AzRestS2SRunner.TryRunAsync(
            _executor, BlueprintSpId, new[] { ObsSpec() }, _logger, ct: default);

        attempted.Should().BeTrue();
        succeeded.Should().BeTrue(because: "the requested role is already assigned — re-issuing the POST would return 4xx and waste a round-trip");
        await _executor.DidNotReceive().ExecuteAsync(
            "az", Arg.Is<string>(s => s.Contains("--method POST")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoExistingAssignment_PostedWithRoleBody()
    {
        // Blueprint SP has no existing assignments. Runner POSTs the new appRoleAssignment.
        StubExistingAssignmentsGet(emptyValue: true);
        StubResourceSpWithAppRolesLookup(returnsEmpty: false);

        _executor
            .ExecuteAsync("az", Arg.Is<string>(s => s.Contains("--method POST") && s.Contains($"/servicePrincipals/{BlueprintSpId}/appRoleAssignments")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult { ExitCode = 0 }));

        var (attempted, succeeded) = await AzRestS2SRunner.TryRunAsync(
            _executor, BlueprintSpId, new[] { ObsSpec() }, _logger, ct: default);

        attempted.Should().BeTrue();
        succeeded.Should().BeTrue();
        await _executor.Received().ExecuteAsync(
            "az", Arg.Is<string>(s => s.Contains("--method POST") && s.Contains($"/servicePrincipals/{BlueprintSpId}/appRoleAssignments")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RoleNotPublishedOnResource_ReturnsFailureForThatSpec()
    {
        // Resource SP exists but does not publish the requested role value.
        StubExistingAssignmentsGet(emptyValue: true);
        StubResourceSpWithAppRolesLookup(returnsEmpty: false, publishRole: false);

        var (attempted, succeeded) = await AzRestS2SRunner.TryRunAsync(
            _executor, BlueprintSpId, new[] { ObsSpec() }, _logger, ct: default);

        attempted.Should().BeTrue();
        succeeded.Should().BeFalse(because: "if the resource SP doesn't publish the requested role we cannot assign it; this is the operator's misconfiguration, not a runner failure mode we can recover from");
        await _executor.DidNotReceive().ExecuteAsync(
            "az", Arg.Is<string>(s => s.Contains("--method POST")),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AzPostExitsNonZero_OverallFails()
    {
        // POST returns exit 1. Runner reports failure so the orchestrator's Action Required
        // path surfaces the recovery URL.
        StubExistingAssignmentsGet(emptyValue: true);
        StubResourceSpWithAppRolesLookup(returnsEmpty: false);

        _executor
            .ExecuteAsync("az", Arg.Is<string>(s => s.Contains("--method POST")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "Insufficient privileges to complete the operation." }));

        var (attempted, succeeded) = await AzRestS2SRunner.TryRunAsync(
            _executor, BlueprintSpId, new[] { ObsSpec() }, _logger, ct: default);

        attempted.Should().BeTrue();
        succeeded.Should().BeFalse(because: "az exited non-zero — the assignment was not created, so we must surface a failure so the orchestrator's Action Required path takes over");
    }

    [Fact]
    public void TryExtractFirstSpIdAndAppRoles_ValidPayload_ReturnsIdAndRoleMap()
    {
        var json =
            "{\"value\":[{\"id\":\"" + ResourceSpId + "\",\"appRoles\":[" +
                "{\"id\":\"" + OtelWriteRoleId + "\",\"value\":\"" + OtelWriteRole + "\"}," +
                "{\"id\":\"55555555-5555-5555-5555-555555555555\",\"value\":\"SomeOther.Role\"}" +
            "]}]}";

        var (spId, roles) = AzRestS2SRunner.TryExtractFirstSpIdAndAppRoles(json);
        spId.Should().Be(ResourceSpId);
        roles.Should().ContainKey(OtelWriteRole).WhoseValue.Should().Be(OtelWriteRoleId);
        roles.Should().ContainKey("SomeOther.Role");
    }

    [Fact]
    public void TryExtractFirstSpIdAndAppRoles_RoleLookupIsCaseInsensitive()
    {
        // App role values are spelled with mixed case in Entra ("Agent365.Observability.OtelWrite"),
        // but our spec strings should resolve regardless of case differences from the SP record.
        var json =
            "{\"value\":[{\"id\":\"" + ResourceSpId + "\",\"appRoles\":[" +
                "{\"id\":\"" + OtelWriteRoleId + "\",\"value\":\"agent365.observability.otelwrite\"}" +
            "]}]}";

        var (_, roles) = AzRestS2SRunner.TryExtractFirstSpIdAndAppRoles(json);
        roles.TryGetValue(OtelWriteRole, out var id).Should().BeTrue(
            because: "Entra is case-insensitive for app role values; a case mismatch with the constant should not stop the assignment");
        id.Should().Be(OtelWriteRoleId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"value\":[]}")]
    [InlineData("{\"unrelated\":true}")]
    public void TryExtractFirstSpIdAndAppRoles_InvalidOrEmpty_ReturnsNullId(string? azOutput)
    {
        var (spId, roles) = AzRestS2SRunner.TryExtractFirstSpIdAndAppRoles(azOutput);
        spId.Should().BeNull(
            because: "missing data must yield a null SP id so the caller takes the warning path instead of dereferencing");
        roles.Should().BeEmpty();
    }

    [Fact]
    public void TryExtractFirstSpIdAndAppRoles_SpWithNoAppRoles_ReturnsIdAndEmptyMap()
    {
        // A resource SP that exists but has not published any app roles. SP id is still returned
        // so the caller can log a precise "role not published" message rather than a generic miss.
        var json = "{\"value\":[{\"id\":\"" + ResourceSpId + "\",\"appRoles\":[]}]}";
        var (spId, roles) = AzRestS2SRunner.TryExtractFirstSpIdAndAppRoles(json);
        spId.Should().Be(ResourceSpId);
        roles.Should().BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test scaffolding
    // ─────────────────────────────────────────────────────────────────────────

    private static ResourcePermissionSpec ObsSpec() =>
        new(ObsAppId,
            "Observability API",
            Scopes: Array.Empty<string>(),
            SetInheritable: false,
            AppRoleScopes: new[] { OtelWriteRole });

    /// <summary>
    /// Stubs the very first GET — <c>/servicePrincipals/{blueprintSpId}/appRoleAssignments</c>.
    /// Pass <paramref name="failure"/>=true to simulate a non-zero exit; pass
    /// <paramref name="emptyValue"/>=true for an empty value array; otherwise the response
    /// contains exactly one assignment with <paramref name="existingResourceId"/> and
    /// <paramref name="existingAppRoleId"/>.
    /// </summary>
    private void StubExistingAssignmentsGet(
        bool failure = false,
        bool emptyValue = false,
        string? existingResourceId = null,
        string? existingAppRoleId = null)
    {
        CommandResult result;
        if (failure)
        {
            result = new CommandResult { ExitCode = 1, StandardError = "Forbidden" };
        }
        else if (emptyValue)
        {
            result = new CommandResult { ExitCode = 0, StandardOutput = "{\"value\":[]}" };
        }
        else
        {
            var json = "{\"value\":[{\"resourceId\":\"" + existingResourceId + "\",\"appRoleId\":\"" + existingAppRoleId + "\"}]}";
            result = new CommandResult { ExitCode = 0, StandardOutput = json };
        }

        _executor
            .ExecuteAsync("az",
                Arg.Is<string>(s => s.Contains($"/servicePrincipals/{BlueprintSpId}/appRoleAssignments") && s.Contains("--method GET")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));
    }

    /// <summary>
    /// Stubs the per-spec resource SP lookup — <c>/servicePrincipals?$filter=appId eq '...'&amp;$select=id,appRoles</c>.
    /// When <paramref name="publishRole"/> is true (default) the response includes the OtelWrite role;
    /// false simulates a misconfigured resource that hasn't published the requested role.
    /// </summary>
    private void StubResourceSpWithAppRolesLookup(bool returnsEmpty, bool publishRole = true)
    {
        string json;
        if (returnsEmpty)
        {
            json = "{\"value\":[]}";
        }
        else if (publishRole)
        {
            json = "{\"value\":[{\"id\":\"" + ResourceSpId + "\",\"appRoles\":[{\"id\":\"" + OtelWriteRoleId + "\",\"value\":\"" + OtelWriteRole + "\"}]}]}";
        }
        else
        {
            json = "{\"value\":[{\"id\":\"" + ResourceSpId + "\",\"appRoles\":[]}]}";
        }

        _executor
            .ExecuteAsync("az",
                Arg.Is<string>(s => s.Contains("/servicePrincipals?") && s.Contains($"appId eq '{ObsAppId}'") && s.Contains("$select=id,appRoles")),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = json }));
    }
}
