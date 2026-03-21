// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Requirements;

/// <summary>
/// Unit tests for PowerShellModulesRequirementCheck WSL detection logic.
/// Covers both detection paths: WSL_DISTRO_NAME env var and /proc/version file.
/// </summary>
[Collection("EnvTests")]
public class PowerShellModulesRequirementCheckTests
{
    private readonly ILogger _mockLogger;
    private readonly PowerShellModulesRequirementCheck _check;

    public PowerShellModulesRequirementCheckTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _check = new PowerShellModulesRequirementCheck();
    }

    // ── IsWslEnvironmentAsync ──────────────────────────────────────────────

    [Fact]
    public async Task IsWslEnvironment_WhenProcVersionFileDoesNotExist_ReturnsFalse()
    {
        // On Windows (or any non-WSL Linux), /proc/version does not exist.
        // This validates the fallback path returns false rather than throwing.
        if (File.Exists("/proc/version"))
        {
            return; // Running inside a real Linux container — skip the "file absent" assertion.
        }

        var result = await PowerShellModulesRequirementCheck.IsWslEnvironmentAsync(CancellationToken.None);

        result.Should().BeFalse("'/proc/version' does not exist on this system");
    }

    [Fact]
    public async Task IsWslEnvironment_WhenProcVersionContainsMicrosoft_ReturnsTrue()
    {
        // This test is only meaningful on Linux where /proc/version exists.
        // On Windows this test exits early without asserting (platform-conditional).
        if (!File.Exists("/proc/version"))
        {
            return; // Not on Linux — skip Linux-only path.
        }

        var procContent = await File.ReadAllTextAsync("/proc/version");
        var expectedResult = procContent.Contains("microsoft", StringComparison.OrdinalIgnoreCase);

        var result = await PowerShellModulesRequirementCheck.IsWslEnvironmentAsync(CancellationToken.None);

        result.Should().Be(expectedResult, "result should reflect actual /proc/version content");
    }

    // ── WSL guidance in CheckAsync ─────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_WhenPwshMissingAndWslDistroNameSet_ResolutionGuidanceContainsLinuxUrl()
    {
        // Use injected runner that reports pwsh unavailable — no real process spawned.
        var noRunner = NoPwshRunner();
        var check = new PowerShellModulesRequirementCheck(noRunner);
        var config = new Agent365Config();

        var original = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME");
        try
        {
            Environment.SetEnvironmentVariable("WSL_DISTRO_NAME", "Ubuntu-22.04");

            var result = await check.CheckAsync(config, _mockLogger);

            result.Passed.Should().BeFalse();
            result.ResolutionGuidance.Should().Contain(
                "https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell-on-linux",
                "WSL resolution should point to the Linux install guide");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WSL_DISTRO_NAME", original);
        }
    }

    [Fact]
    public async Task CheckAsync_WhenPwshMissingAndNotWsl_ResolutionGuidanceContainsGeneralUrl()
    {
        // Use injected runner that reports pwsh unavailable — no real process spawned.
        var noRunner = NoPwshRunner();
        var check = new PowerShellModulesRequirementCheck(noRunner);
        var config = new Agent365Config();

        // Ensure WSL_DISTRO_NAME is not set so the non-WSL branch is taken.
        var original = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME");
        try
        {
            Environment.SetEnvironmentVariable("WSL_DISTRO_NAME", null);

            var result = await check.CheckAsync(config, _mockLogger);

            result.Passed.Should().BeFalse();
            result.ResolutionGuidance.Should().Contain(
                "https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell",
                "non-WSL resolution should point to the general install guide");
            result.ResolutionGuidance.Should().NotContain(
                "installing-powershell-on-linux",
                "non-WSL resolution should not mention the Linux-specific URL");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WSL_DISTRO_NAME", original);
        }
    }

    // ── CheckAsync smoke test ──────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_ShouldReturnResult_WithoutThrowing()
    {
        // Use injected runner that reports pwsh available with modules installed — no real process spawned.
        var config = new Agent365Config();
        var check = new PowerShellModulesRequirementCheck(AllModulesInstalledRunner());

        var result = await check.CheckAsync(config, _mockLogger);

        // The key assertion: CheckAsync completes without throwing regardless of environment.
        result.Should().NotBeNull();
        result.Passed.Should().BeTrue();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Returns a command runner that reports pwsh as unavailable (exit 1).</summary>
    private static Func<string, string, CancellationToken, Task<(bool success, string? output)>> NoPwshRunner()
        => (_, _, _) => Task.FromResult((false, (string?)null));

    /// <summary>Returns a command runner that reports pwsh 7 available and all required modules installed.</summary>
    private static Func<string, string, CancellationToken, Task<(bool success, string? output)>> AllModulesInstalledRunner()
        => (_, command, _) =>
        {
            // Availability check: "$PSVersionTable.PSVersion.Major"
            if (command.Contains("PSVersionTable"))
                return Task.FromResult((true, (string?)"7"));
            // Module check: returns the module name
            if (command.Contains("Microsoft.Graph.Authentication"))
                return Task.FromResult((true, (string?)"Microsoft.Graph.Authentication"));
            if (command.Contains("Microsoft.Graph.Applications"))
                return Task.FromResult((true, (string?)"Microsoft.Graph.Applications"));
            return Task.FromResult((true, (string?)string.Empty));
        };
}
