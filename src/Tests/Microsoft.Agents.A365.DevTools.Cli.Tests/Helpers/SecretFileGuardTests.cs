// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

public class SecretFileGuardTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _root;
    private readonly string _outside;

    public SecretFileGuardTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "A365_SecretFileGuardTests_" + Guid.NewGuid());
        _root = Path.Combine(_baseDir, "repo");
        _outside = Path.Combine(_baseDir, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_baseDir)) Directory.Delete(_baseDir, recursive: true); } catch { /* ignore */ }
    }

    /// <summary>
    /// Symlink creation needs Developer Mode or elevation on Windows; Linux CI always exercises these tests.
    /// </summary>
    private static bool TryCreateSymlink(Action create)
    {
        try
        {
            create();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void GetUnsafeReason_ConfigDirectoryItself_IsSafe()
    {
        SecretFileGuard.GetUnsafeReason(_root, _root).Should().BeNull(
            because: "the directory that contains a365.config.json is the trusted project root");
    }

    [Fact]
    public void GetUnsafeReason_RegularFileInsideRoot_IsSafe()
    {
        var properties = Path.Combine(_root, "src", "Properties");
        Directory.CreateDirectory(properties);
        var launchSettings = Path.Combine(properties, "launchSettings.json");
        File.WriteAllText(launchSettings, "{}");

        SecretFileGuard.GetUnsafeReason(_root, launchSettings).Should().BeNull(
            because: "regular project files under the a365.config.json directory may receive secrets");
    }

    [Fact]
    public void GetUnsafeReason_RelativePathInsideRoot_ResolvesAgainstRoot()
    {
        SecretFileGuard.GetUnsafeReason(_root, Path.Combine("src", ".env")).Should().BeNull(
            because: "relative deploymentProjectPath values resolve against the a365.config.json directory");
    }

    [Fact]
    public void GetUnsafeReason_AbsolutePathOutsideRoot_IsUnsafe()
    {
        SecretFileGuard.GetUnsafeReason(_root, Path.Combine(_outside, ".env")).Should().NotBeNull(
            because: "a committed a365.config.json must not direct secrets outside its own directory");
    }

    [Fact]
    public void GetUnsafeReason_ParentTraversal_IsUnsafe()
    {
        SecretFileGuard.GetUnsafeReason(_root, Path.Combine("..", "outside", ".env")).Should().NotBeNull(
            because: "'..' segments must not escape the a365.config.json directory");
    }

    [Fact]
    public void GetUnsafeReason_SiblingDirectorySharingPrefix_IsUnsafe()
    {
        SecretFileGuard.GetUnsafeReason(_root, Path.Combine(_root + "-other", ".env")).Should().NotBeNull(
            because: "containment compares whole path segments, so 'repo-other' is not inside 'repo'");
    }

    [Fact]
    public void GetUnsafeReason_NetworkSharePath_IsUnsafe()
    {
        SecretFileGuard.GetUnsafeReason(_root, "//attacker.example/share/project").Should().NotBeNull(
            because: "a committed a365.config.json must not send secrets to a network share");

        if (OperatingSystem.IsWindows())
        {
            SecretFileGuard.GetUnsafeReason(_root, @"\\attacker.example\share\project").Should().NotBeNull(
                because: "UNC paths reach remote hosts on Windows");
        }
    }

    [Fact]
    public void GetUnsafeReason_InvalidPath_IsUnsafe()
    {
        SecretFileGuard.GetUnsafeReason(_root, "bad\0path").Should().NotBeNull(
            because: "a path that cannot be resolved cannot be proven to stay inside the project");
    }

    [Fact]
    public void GetUnsafeReason_SymlinkedFileInsideRoot_IsUnsafe()
    {
        var target = Path.Combine(_outside, "captured.env");
        File.WriteAllText(target, string.Empty);
        var link = Path.Combine(_root, ".env");
        if (!TryCreateSymlink(() => File.CreateSymbolicLink(link, target)))
            return;

        SecretFileGuard.GetUnsafeReason(_root, link).Should().NotBeNull(
            because: "a committed symlink would redirect the secret to the link target");
    }

    [Fact]
    public void GetUnsafeReason_DanglingSymlinkInsideRoot_IsUnsafe()
    {
        var link = Path.Combine(_root, ".env");
        if (!TryCreateSymlink(() => File.CreateSymbolicLink(link, Path.Combine(_outside, "missing.env"))))
            return;

        SecretFileGuard.GetUnsafeReason(_root, link).Should().NotBeNull(
            because: "writing through a dangling symlink creates the target file outside the project");
    }

    [Fact]
    public void GetUnsafeReason_FileBelowSymlinkedDirectory_IsUnsafe()
    {
        var link = Path.Combine(_root, "project");
        if (!TryCreateSymlink(() => Directory.CreateSymbolicLink(link, _outside)))
            return;

        SecretFileGuard.GetUnsafeReason(_root, Path.Combine(link, ".env")).Should().NotBeNull(
            because: "a symlinked directory below the root escapes the a365.config.json directory");
    }
}
