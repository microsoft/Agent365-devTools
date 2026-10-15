// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Tests.TestHelpers;
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
    public void GetUnsafeReason_CaseOnlyAliasOfRoot_IsUnsafe()
    {
        var caseAlias = Path.Combine(_baseDir, "REPO");

        SecretFileGuard.GetUnsafeReason(_root, Path.Combine(caseAlias, ".env")).Should().NotBeNull(
            because: "on a case-sensitive volume 'REPO' is a sibling of 'repo', so containment must compare exact case");
        SecretFileGuard.GetUnsafeReason(_root, Path.Combine("..", "REPO", ".env")).Should().NotBeNull(
            because: "a committed relative path must not reach a case-only sibling of the config directory");
    }

    [Fact]
    public void GetUnsafeReason_CaseOnlyMismatch_ExplainsLetterCase()
    {
        var caseAlias = Path.Combine(_baseDir, "REPO");

        SecretFileGuard.GetUnsafeReason(_root, Path.Combine(caseAlias, ".env")).Should().Contain("letter case",
            because: "on case-insensitive volumes this is the user's own directory, so the warning must say why it was rejected");
        SecretFileGuard.GetUnsafeReason(_root, Path.Combine(_outside, ".env")).Should().NotContain("letter case",
            because: "only a path that matches the config directory apart from letter case gets the letter-case explanation");
    }

    [Fact]
    public void GetUnsafeReason_DriveLetterCaseDiffers_IsSafeOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var fullRoot = Path.GetFullPath(_root);
        var otherCaseDrive = (char.IsUpper(fullRoot[0]) ? char.ToLowerInvariant(fullRoot[0]) : char.ToUpperInvariant(fullRoot[0])) + fullRoot[1..];

        SecretFileGuard.GetUnsafeReason(_root, Path.Combine(otherCaseDrive, ".env")).Should().BeNull(
            because: "Windows drive letters are case-insensitive on every volume, so 'c:' and 'C:' name the same directory");
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
        if (!FileSystemLinks.TryCreateSymlink(() => File.CreateSymbolicLink(link, target)))
            return;

        SecretFileGuard.GetUnsafeReason(_root, link).Should().NotBeNull(
            because: "a committed symlink would redirect the secret to the link target");
    }

    [Fact]
    public void GetUnsafeReason_DanglingSymlinkInsideRoot_IsUnsafe()
    {
        var link = Path.Combine(_root, ".env");
        if (!FileSystemLinks.TryCreateSymlink(() => File.CreateSymbolicLink(link, Path.Combine(_outside, "missing.env"))))
            return;

        SecretFileGuard.GetUnsafeReason(_root, link).Should().NotBeNull(
            because: "writing through a dangling symlink creates the target file outside the project");
    }

    [Fact]
    public void GetUnsafeReason_FileBelowSymlinkedDirectory_IsUnsafe()
    {
        var link = Path.Combine(_root, "project");
        if (!FileSystemLinks.TryCreateSymlink(() => Directory.CreateSymbolicLink(link, _outside)))
            return;

        SecretFileGuard.GetUnsafeReason(_root, Path.Combine(link, ".env")).Should().NotBeNull(
            because: "a symlinked directory below the root escapes the a365.config.json directory");
    }

    [Fact]
    public void GetUnsafeReason_FileBelowJunction_IsUnsafe()
    {
        if (!OperatingSystem.IsWindows())
            return; // Junctions exist only on Windows; Linux and macOS are covered by the symlink tests.

        var link = Path.Combine(_root, "project");
        FileSystemLinks.CreateJunction(link, _outside);

        SecretFileGuard.GetUnsafeReason(_root, Path.Combine(link, ".env")).Should().NotBeNull(
            because: "a junction below the root redirects writes outside the a365.config.json directory");
    }

    [Fact]
    public void ThrowIfUnsafe_PathInsideRoot_DoesNotThrow()
    {
        var act = () => SecretFileGuard.ThrowIfUnsafe(_root, Path.Combine(_root, ".env"));

        act.Should().NotThrow(because: "the write-time re-check must not block regular files inside the project");
    }

    [Fact]
    public void ThrowIfUnsafe_LinkSwappedInAfterFirstCheck_Throws()
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        var envPath = Path.Combine(project, ".env");
        SecretFileGuard.GetUnsafeReason(_root, envPath).Should().BeNull(
            because: "the destination is a regular directory when it is first checked");

        FileSystemLinks.ReplaceWithDirectoryLink(project, _outside);

        var act = () => SecretFileGuard.ThrowIfUnsafe(_root, envPath);

        act.Should().Throw<IOException>(
            because: "a link swapped in after the first check must be caught immediately before the secret is written");
    }
}
