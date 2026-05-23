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

public class PowerShellS2SRunnerTests
{
    private readonly CommandExecutor _executor;
    private readonly ILogger _logger;

    public PowerShellS2SRunnerTests()
    {
        _logger = NullLogger.Instance;
        _executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
    }

    /// <summary>
    /// GUID validation rejects non-GUID tenantId/blueprintAppId without calling the executor.
    /// This guards against script injection via malformed input.
    /// </summary>
    [Fact]
    public async Task TryRunAsync_InvalidTenantIdGuid_ReturnsFalseWithoutCallingExecutor()
    {
        // Arrange
        var specs = new[]
        {
            new ResourcePermissionSpec(
                ConfigConstants.ObservabilityApiAppId,
                "Observability API",
                new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
                SetInheritable: false,
                AppRoleScopes: new[] { ConfigConstants.ObservabilityApiOtelWriteScope })
        };

        // Act
        var (attempted, succeeded) = await PowerShellS2SRunner.TryRunAsync(
            _executor,
            tenantId: "not-a-guid",
            blueprintAppId: "00000000-0000-0000-0000-000000000002",
            specs: specs,
            _logger,
            ct: default);

        // Assert
        attempted.Should().BeFalse(because: "invalid GUID must be rejected before launching pwsh");
        succeeded.Should().BeFalse();

        await _executor.DidNotReceive().ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<bool>());
    }

    /// <summary>
    /// When pwsh is not found on the system (ExecuteWithStreamingAsync throws Win32Exception
    /// with NativeErrorCode 2 — ERROR_FILE_NOT_FOUND / ENOENT),
    /// TryRunAsync returns (false, false, false) — the caller falls through to Action Required.
    /// </summary>
    [Fact]
    public async Task TryRunAsync_PwshNotFound_ReturnsFalseWithoutAttempting()
    {
        // Arrange
        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<bool>())
            .Returns(Task.FromException<CommandResult>(new System.ComponentModel.Win32Exception(2)));

        var specs = new[]
        {
            new ResourcePermissionSpec(
                ConfigConstants.ObservabilityApiAppId,
                "Observability API",
                new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
                SetInheritable: false,
                AppRoleScopes: new[] { ConfigConstants.ObservabilityApiOtelWriteScope })
        };

        // Act
        var (attempted, succeeded) = await PowerShellS2SRunner.TryRunAsync(
            _executor,
            tenantId: "00000000-0000-0000-0000-000000000001",
            blueprintAppId: "00000000-0000-0000-0000-000000000002",
            specs: specs,
            _logger,
            ct: default);

        // Assert
        attempted.Should().BeFalse(because: "pwsh not found means no execution was possible");
        succeeded.Should().BeFalse();
    }

    /// <summary>
    /// When pwsh is available and the script exits with code 0, TryRunAsync returns
    /// (Attempted=true, Succeeded=true). The script passed to the executor must contain
    /// the tenantId, blueprintAppId, and role values.
    /// </summary>
    [Fact]
    public async Task TryRunAsync_ValidInputsAndPwshSucceeds_ScriptContainsExpectedValuesAndReturnsSucceeded()
    {
        // Arrange
        var tenantId = "00000000-0000-0000-0000-000000000001";
        var blueprintAppId = "00000000-0000-0000-0000-000000000002";

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
                ConfigConstants.ObservabilityApiAppId,
                "Observability API",
                new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
                SetInheritable: false,
                AppRoleScopes: new[] { ConfigConstants.ObservabilityApiOtelWriteScope })
        };

        // Act
        var (attempted, succeeded) = await PowerShellS2SRunner.TryRunAsync(
            _executor,
            tenantId: tenantId,
            blueprintAppId: blueprintAppId,
            specs: specs,
            _logger,
            ct: default);

        // Assert
        attempted.Should().BeTrue(because: "pwsh was found and the script was executed");
        succeeded.Should().BeTrue(because: "pwsh exited with code 0");

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain(tenantId,
            because: "the script must be scoped to the correct tenant");
        capturedScript.Should().Contain(blueprintAppId,
            because: "the script must target the blueprint application");
        capturedScript.Should().Contain(ConfigConstants.ObservabilityApiAppId,
            because: "the script must reference the resource app ID for each spec");
        capturedScript.Should().Contain(ConfigConstants.ObservabilityApiOtelWriteScope,
            because: "the script must include the app role value to look up");
        capturedScript.Should().Contain("-ContextScope Process",
            because: "Connect-MgGraph must use process-scoped auth to bypass the persistent token cache and avoid DeviceCodeCredential NRE on repeat runs");
        capturedScript.Should().Contain("Microsoft.Graph.Authentication",
            because: "Connect-MgGraph lives in Authentication, which must be pinned and imported before Applications to avoid version-mismatch assembly conflicts");
    }

    /// <summary>
    /// When pwsh runs but exits with a non-zero exit code, TryRunAsync returns
    /// (Attempted=true, Succeeded=false). Success is now determined purely by exit code
    /// since stdout is no longer redirected back to the parent.
    /// </summary>
    [Fact]
    public async Task TryRunAsync_PwshExitsNonZero_ReturnsAttemptedNotSucceeded()
    {
        // Arrange — pwsh ran but exited with code 1 (e.g. assignment failed)
        _executor.ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<bool>())
            .Returns(new CommandResult { ExitCode = 1 });

        var specs = new[]
        {
            new ResourcePermissionSpec(
                ConfigConstants.ObservabilityApiAppId,
                "Observability API",
                new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
                SetInheritable: false,
                AppRoleScopes: new[] { ConfigConstants.ObservabilityApiOtelWriteScope })
        };

        // Act
        var (attempted, succeeded) = await PowerShellS2SRunner.TryRunAsync(
            _executor,
            tenantId: "00000000-0000-0000-0000-000000000001",
            blueprintAppId: "00000000-0000-0000-0000-000000000002",
            specs: specs,
            _logger,
            ct: default);

        // Assert
        attempted.Should().BeTrue(because: "pwsh was invoked and produced an exit code");
        succeeded.Should().BeFalse(because: "non-zero exit code indicates the script failed");
    }

    /// <summary>
    /// GUID validation rejects an invalid blueprintAppId even when tenantId is valid,
    /// without calling the executor.
    /// </summary>
    [Fact]
    public async Task TryRunAsync_InvalidBlueprintAppIdGuid_ReturnsFalseWithoutCallingExecutor()
    {
        // Arrange
        var specs = new[]
        {
            new ResourcePermissionSpec(
                ConfigConstants.ObservabilityApiAppId,
                "Observability API",
                new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
                SetInheritable: false,
                AppRoleScopes: new[] { ConfigConstants.ObservabilityApiOtelWriteScope })
        };

        // Act
        var (attempted, succeeded) = await PowerShellS2SRunner.TryRunAsync(
            _executor,
            tenantId: "00000000-0000-0000-0000-000000000001",
            blueprintAppId: "not-a-guid",
            specs: specs,
            _logger,
            ct: default);

        // Assert
        attempted.Should().BeFalse(because: "invalid blueprintAppId GUID must be rejected before launching pwsh");
        succeeded.Should().BeFalse();

        await _executor.DidNotReceive().ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<bool>());
    }

    /// <summary>
    /// When all specs have no AppRoleScopes, TryRunAsync returns (false, false, false)
    /// immediately without invoking the executor.
    /// </summary>
    [Fact]
    public async Task TryRunAsync_SpecsWithNoAppRoleScopes_ReturnsFalseWithoutCallingExecutor()
    {
        // Arrange — spec with delegated-only scopes, no AppRoleScopes
        var specs = new[]
        {
            new ResourcePermissionSpec(
                ConfigConstants.ObservabilityApiAppId,
                "Observability API",
                new[] { ConfigConstants.ObservabilityApiOtelWriteScope },
                SetInheritable: false,
                AppRoleScopes: Array.Empty<string>())
        };

        // Act
        var (attempted, succeeded) = await PowerShellS2SRunner.TryRunAsync(
            _executor,
            tenantId: "00000000-0000-0000-0000-000000000001",
            blueprintAppId: "00000000-0000-0000-0000-000000000002",
            specs: specs,
            _logger,
            ct: default);

        // Assert
        attempted.Should().BeFalse(because: "no S2S specs means there is nothing to assign via PowerShell");
        succeeded.Should().BeFalse();

        await _executor.DidNotReceive().ExecuteWithStreamingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<Func<string, string?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<bool>());
    }
}
