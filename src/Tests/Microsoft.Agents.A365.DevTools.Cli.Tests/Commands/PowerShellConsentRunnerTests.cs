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
/// Tests for <see cref="PowerShellConsentRunner"/> — the delegated admin-consent
/// PowerShell fallback for the unified <c>/v2.0/adminconsent</c> URL path. Mirrors the
/// <see cref="PowerShellS2SRunnerTests"/> structure: the executor is mocked, the script
/// content is captured by reading the temp <c>-File</c> argument, and behavior is asserted
/// from the (Attempted, Succeeded) tuple plus the captured script.
/// </summary>
public class PowerShellConsentRunnerTests
{
    private readonly CommandExecutor _executor;
    private readonly ILogger _logger;

    public PowerShellConsentRunnerTests()
    {
        _logger = NullLogger.Instance;
        _executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
    }

    [Fact]
    public async Task TryRunAsync_InvalidTenantIdGuid_ReturnsFalseWithoutCallingExecutor()
    {
        var specs = new[]
        {
            new ResourcePermissionSpec(
                ConfigConstants.MessagingBotApiAppId,
                "Messaging Bot API",
                new[] { ConfigConstants.MessagingBotApiAdminConsentScope },
                SetInheritable: false)
        };

        var (attempted, succeeded) = await PowerShellConsentRunner.TryRunAsync(
            _executor,
            tenantId: "not-a-guid",
            blueprintSpObjectId: "00000000-0000-0000-0000-000000000002",
            specs: specs,
            _logger,
            ct: default);

        attempted.Should().BeFalse(because: "invalid tenant GUID must be rejected before launching pwsh — guards against script injection via malformed input");
        succeeded.Should().BeFalse();

        await _executor.DidNotReceive().ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task TryRunAsync_InvalidBlueprintSpObjectIdGuid_ReturnsFalseWithoutCallingExecutor()
    {
        var specs = new[]
        {
            new ResourcePermissionSpec(
                ConfigConstants.MessagingBotApiAppId,
                "Messaging Bot API",
                new[] { ConfigConstants.MessagingBotApiAdminConsentScope },
                SetInheritable: false)
        };

        var (attempted, succeeded) = await PowerShellConsentRunner.TryRunAsync(
            _executor,
            tenantId: "00000000-0000-0000-0000-000000000001",
            blueprintSpObjectId: "not-a-guid",
            specs: specs,
            _logger,
            ct: default);

        attempted.Should().BeFalse(because: "invalid blueprint SP id GUID must be rejected before launching pwsh");
        succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task TryRunAsync_NoDelegatedScopes_ReturnsFalseWithoutCallingExecutor()
    {
        // Spec carries app-role scopes but no delegated scopes — nothing for the
        // consent runner to do; that work belongs to PowerShellS2SRunner.
        var specs = new[]
        {
            new ResourcePermissionSpec(
                ConfigConstants.ObservabilityApiAppId,
                "Observability API",
                Scopes: Array.Empty<string>(),
                SetInheritable: false,
                AppRoleScopes: new[] { ConfigConstants.ObservabilityApiOtelWriteScope })
        };

        var (attempted, succeeded) = await PowerShellConsentRunner.TryRunAsync(
            _executor,
            tenantId: "00000000-0000-0000-0000-000000000001",
            blueprintSpObjectId: "00000000-0000-0000-0000-000000000002",
            specs: specs,
            _logger,
            ct: default);

        attempted.Should().BeFalse(because: "no delegated scopes means there is nothing for this runner to grant; S2S work flows through PowerShellS2SRunner");
        succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task TryRunAsync_UnsafeScopeValue_ReturnsFalseWithoutCallingExecutor()
    {
        // A scope value containing a quote/semicolon would let an attacker inject
        // arbitrary PowerShell into the script we synthesize. The runner must reject
        // anything outside the SafeScopePattern allowlist before invoking pwsh.
        var specs = new[]
        {
            new ResourcePermissionSpec(
                ConfigConstants.MessagingBotApiAppId,
                "Messaging Bot API",
                new[] { "AgentData.ReadWrite'; Remove-Item C:\\" },
                SetInheritable: false)
        };

        var (attempted, succeeded) = await PowerShellConsentRunner.TryRunAsync(
            _executor,
            tenantId: "00000000-0000-0000-0000-000000000001",
            blueprintSpObjectId: "00000000-0000-0000-0000-000000000002",
            specs: specs,
            _logger,
            ct: default);

        attempted.Should().BeFalse(because: "scope values are interpolated into the script and must be allowlist-validated to prevent script injection");
        succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task TryRunAsync_PwshNotFound_ReturnsFalseWithoutAttempting()
    {
        // Win32Exception with NativeErrorCode 2 = ERROR_FILE_NOT_FOUND / ENOENT.
        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<bool>())
            .Returns(Task.FromException<CommandResult>(new System.ComponentModel.Win32Exception(2)));

        var specs = new[]
        {
            new ResourcePermissionSpec(
                ConfigConstants.MessagingBotApiAppId,
                "Messaging Bot API",
                new[] { ConfigConstants.MessagingBotApiAdminConsentScope },
                SetInheritable: false)
        };

        var (attempted, succeeded) = await PowerShellConsentRunner.TryRunAsync(
            _executor,
            tenantId: "00000000-0000-0000-0000-000000000001",
            blueprintSpObjectId: "00000000-0000-0000-0000-000000000002",
            specs: specs,
            _logger,
            ct: default);

        attempted.Should().BeFalse(because: "pwsh missing from PATH is not a fault we want to surface as 'attempted but failed' — the caller falls through to Action Required messaging instead");
        succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task TryRunAsync_PwshExitsZero_ReturnsAttemptedAndSucceededAndScriptContainsExpectedValues()
    {
        var tenantId = "00000000-0000-0000-0000-000000000001";
        var blueprintSpId = "00000000-0000-0000-0000-000000000002";

        string? capturedScript = null;
        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(),
            Arg.Do<string>(args =>
            {
                var match = System.Text.RegularExpressions.Regex.Match(args, @"-File ""([^""]+)""");
                if (match.Success && System.IO.File.Exists(match.Groups[1].Value))
                    capturedScript = System.IO.File.ReadAllText(match.Groups[1].Value);
            }),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<bool>())
            .Returns(new CommandResult { ExitCode = 0 });

        var specs = new[]
        {
            new ResourcePermissionSpec(
                ConfigConstants.MessagingBotApiAppId,
                "Messaging Bot API",
                new[] { ConfigConstants.MessagingBotApiAdminConsentScope },
                SetInheritable: false)
        };

        var (attempted, succeeded) = await PowerShellConsentRunner.TryRunAsync(
            _executor,
            tenantId: tenantId,
            blueprintSpObjectId: blueprintSpId,
            specs: specs,
            _logger,
            ct: default);

        attempted.Should().BeTrue(because: "pwsh was launched and produced an exit code");
        succeeded.Should().BeTrue(because: "exit code 0 indicates the script completed successfully");

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain(tenantId,
            because: "the script must be scoped to the correct tenant — Connect-MgGraph -TenantId must match the blueprint's tenant");
        capturedScript.Should().Contain(blueprintSpId,
            because: "grants are created against the blueprint SP id supplied by the orchestrator; re-resolving inside the script is unnecessary and error-prone");
        capturedScript.Should().Contain(ConfigConstants.MessagingBotApiAppId,
            because: "the script must look up each resource SP by its appId");
        capturedScript.Should().Contain(ConfigConstants.MessagingBotApiAdminConsentScope,
            because: "the requested delegated scope must appear verbatim in the grant call");
        capturedScript.Should().Contain("DelegatedPermissionGrant.ReadWrite.All",
            because: "Connect-MgGraph must request the scope required to POST /oauth2PermissionGrants — the CLI's MSAL token does not carry it, which is the whole reason this fallback exists (PR #424 context)");
        capturedScript.Should().Contain("New-MgOauth2PermissionGrant",
            because: "the runner creates AllPrincipals grants via the Microsoft.Graph PowerShell SDK rather than the programmatic CLI path");
        capturedScript.Should().Contain("AllPrincipals",
            because: "consentType must be tenant-wide (AllPrincipals) to match the /v2.0/adminconsent browser path the fallback is replacing");
        capturedScript.Should().Contain("-ContextScope Process",
            because: "Connect-MgGraph must use process-scoped auth to bypass the persistent token cache — same hazard as PowerShellS2SRunner around stale DeviceCodeCredential causing NRE on repeat runs");
    }

    [Fact]
    public async Task TryRunAsync_PwshExitsNonZero_ReturnsAttemptedNotSucceeded()
    {
        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<bool>())
            .Returns(new CommandResult { ExitCode = 1 });

        var specs = new[]
        {
            new ResourcePermissionSpec(
                ConfigConstants.MessagingBotApiAppId,
                "Messaging Bot API",
                new[] { ConfigConstants.MessagingBotApiAdminConsentScope },
                SetInheritable: false)
        };

        var (attempted, succeeded) = await PowerShellConsentRunner.TryRunAsync(
            _executor,
            tenantId: "00000000-0000-0000-0000-000000000001",
            blueprintSpObjectId: "00000000-0000-0000-0000-000000000002",
            specs: specs,
            _logger,
            ct: default);

        attempted.Should().BeTrue(because: "pwsh was invoked and produced an exit code");
        succeeded.Should().BeFalse(because: "non-zero exit code indicates the script failed — caller surfaces the consent URL for manual completion");
    }

    [Fact]
    public async Task TryRunAsync_MultipleSpecs_ScriptContainsEverySpecAppIdAndScope()
    {
        string? capturedScript = null;
        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(),
            Arg.Do<string>(args =>
            {
                var match = System.Text.RegularExpressions.Regex.Match(args, @"-File ""([^""]+)""");
                if (match.Success && System.IO.File.Exists(match.Groups[1].Value))
                    capturedScript = System.IO.File.ReadAllText(match.Groups[1].Value);
            }),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<bool>())
            .Returns(new CommandResult { ExitCode = 0 });

        var specs = new[]
        {
            new ResourcePermissionSpec(
                ConfigConstants.MessagingBotApiAppId,
                "Messaging Bot API",
                new[] { ConfigConstants.MessagingBotApiAdminConsentScope },
                SetInheritable: false),
            new ResourcePermissionSpec(
                ConfigConstants.ObservabilityApiAppId,
                "Observability API",
                new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
                SetInheritable: false),
        };

        await PowerShellConsentRunner.TryRunAsync(
            _executor,
            tenantId: "00000000-0000-0000-0000-000000000001",
            blueprintSpObjectId: "00000000-0000-0000-0000-000000000002",
            specs: specs,
            _logger,
            ct: default);

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain(ConfigConstants.MessagingBotApiAppId);
        capturedScript.Should().Contain(ConfigConstants.MessagingBotApiAdminConsentScope);
        capturedScript.Should().Contain(ConfigConstants.ObservabilityApiAppId);
        capturedScript.Should().Contain(ConfigConstants.ObservabilityApiOtelWriteScope,
            because: "every spec must produce its own grant statement — missing any one would leave that resource un-consented");
    }
}
