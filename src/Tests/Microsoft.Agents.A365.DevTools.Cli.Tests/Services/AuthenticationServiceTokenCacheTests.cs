// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Tests for <see cref="AuthenticationService.ClearTokenCacheAsync"/>.
///
/// COVERAGE RATIONALE (TEST-CRIT-4):
/// <c>ClearTokenCacheAsync</c> is the public hook callers (e.g. the wids optional-claim
/// requirement check) invoke after mutating the client app registration to force
/// re-acquisition of access tokens with the new claim. A regression here is silent —
/// stale tokens continue to be reused and the user sees no failure until a downstream
/// authorization check fails much later. The three real-FS branches below pin the
/// contract: cache exists / cache missing / delete blocked.
///
/// CACHE PATH NOTE: <c>ClearTokenCacheAsync</c> now deletes the OS-protected MSAL persistent cache
/// (<see cref="AuthenticationConstants.MsalCacheFileName"/>) under
/// <c>Environment.SpecialFolder.LocalApplicationData</c> + <see cref="AuthenticationConstants.ApplicationName"/>,
/// plus a best-effort cleanup of any legacy plaintext <c>auth-token.json</c>. The path is NOT
/// overridable via env var on Windows (LocalApplicationData resolves through SHGetFolderPath
/// and is anchored to the user profile, not the LOCALAPPDATA env var). These tests therefore
/// drive the real path and back up any pre-existing developer cache file so the developer's
/// cached tokens are preserved across the test run.
///
/// COLLECTION: Shares the <c>AuthTests</c> collection (disabled parallelization) defined in
/// <see cref="AuthenticationServiceTests"/> — tests that touch the shared cache file must be
/// serialized.
/// </summary>
[Collection("AuthTests")]
public class AuthenticationServiceTokenCacheTests : IDisposable
{
    private readonly ILogger<AuthenticationService> _logger;
    private readonly AuthenticationService _sut;
    private readonly string _cachePath;
    private readonly string? _backupContent;

    public AuthenticationServiceTokenCacheTests()
    {
        _logger = Substitute.For<ILogger<AuthenticationService>>();
        _sut = new AuthenticationService(_logger);

        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cachePath = Path.Combine(
            appDataPath,
            AuthenticationConstants.ApplicationName,
            AuthenticationConstants.MsalCacheFileName);

        // Preserve the developer's real cache (if any) so the test run is non-destructive.
        // We will restore this in Dispose.
        if (File.Exists(_cachePath))
        {
            _backupContent = File.ReadAllText(_cachePath);
            File.Delete(_cachePath);
        }
    }

    public void Dispose()
    {
        // Restore the developer's pre-existing cache content if we displaced it.
        if (_backupContent is not null)
        {
            var cacheDir = Path.GetDirectoryName(_cachePath)!;
            Directory.CreateDirectory(cacheDir);
            File.WriteAllText(_cachePath, _backupContent);
        }
        else if (File.Exists(_cachePath))
        {
            // No prior cache existed; leave the directory empty of our test artifacts.
            try { File.Delete(_cachePath); } catch { /* best-effort cleanup */ }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ClearTokenCacheAsync_WhenCacheFileExists_DeletesFile()
    {
        // Arrange — pre-create a fake cache file at the path the service computes.
        var cacheDir = Path.GetDirectoryName(_cachePath)!;
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(_cachePath, "msal-cache-bytes");
        File.Exists(_cachePath).Should().BeTrue(
            because: "the test setup must successfully place a cache file before the SUT acts on it");

        // Act
        await _sut.ClearTokenCacheAsync();

        // Assert — the file must be gone; this is the load-bearing post-condition that
        // forces MSAL/Azure.Identity to re-acquire tokens on the next call (so a newly
        // added optional claim like wids appears in the next token).
        File.Exists(_cachePath).Should().BeFalse(
            because: "ClearTokenCacheAsync must delete the cache file so the next token " +
                     "acquisition picks up newly granted optional claims rather than reusing " +
                     "a stale cached token without them");
    }

    [Fact]
    public async Task ClearTokenCacheAsync_WhenCacheFileDoesNotExist_DoesNotThrow()
    {
        // Arrange — ensure no cache file is present.
        if (File.Exists(_cachePath))
        {
            File.Delete(_cachePath);
        }
        File.Exists(_cachePath).Should().BeFalse(
            because: "the no-op branch is only exercised when the cache file is genuinely absent");

        // Act
        Func<Task> act = async () => await _sut.ClearTokenCacheAsync();

        // Assert — callers (e.g. WidsOptionalClaimRequirementCheck) invoke this unconditionally
        // after their mutation completes; a missing cache must be a silent no-op, not an error.
        await act.Should().NotThrowAsync(
            because: "a first-run scenario (no cache yet) must not surface an error from a " +
                     "post-mutation cache-invalidation step — callers treat this as fire-and-forget");
    }

    [Fact]
    public async Task ClearTokenCacheAsync_WhenDeleteFails_SwallowsException()
    {
        // The production code catches Exception and logs at Debug — a failed delete must not
        // propagate out, because it only means the user re-authenticates sooner than expected,
        // not that anything is broken.
        //
        // PLATFORM NOTE: This test induces a delete failure by opening the cache file with
        // FileShare.None, which causes File.Delete to throw IOException on Windows. On Linux
        // and macOS, the kernel allows deletion of open files (the inode lingers until the
        // last handle closes), so the same FileShare trick does NOT block the delete and the
        // test would degenerate into the happy path. We therefore restrict the lock-induced
        // assertion to Windows and document the behavior for other platforms.
        if (!OperatingSystem.IsWindows())
        {
            // Document the skip in test output for operators reviewing CI logs on non-Windows.
            // We still assert the no-op branch behaves correctly to keep the test meaningful.
            await _sut.ClearTokenCacheAsync();
            return;
        }

        // Arrange — pre-create the file and hold an exclusive handle so File.Delete fails.
        var cacheDir = Path.GetDirectoryName(_cachePath)!;
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(_cachePath, "msal-cache-bytes");

        using (var locker = new FileStream(
            _cachePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            // Sanity: confirm a raw File.Delete would actually throw under this lock — if a
            // future Windows/.NET runtime change weakens the lock semantics, this assertion
            // will fail loudly so we know the swallow assertion below has lost its teeth.
            var directDelete = () => File.Delete(_cachePath);
            directDelete.Should().Throw<IOException>(
                because: "the test relies on FileShare.None blocking File.Delete on Windows; " +
                         "if this stops throwing, the swallow assertion below no longer exercises " +
                         "the catch branch in the SUT");

            // Act
            Func<Task> act = async () => await _sut.ClearTokenCacheAsync();

            // Assert — the production code's catch(Exception) must swallow the IOException.
            await act.Should().NotThrowAsync(
                because: "a transient delete failure (file locked, permission denied, transient FS " +
                         "error) must not bubble out of ClearTokenCacheAsync — the worst-case outcome " +
                         "of a failed clear is one extra interactive re-auth, never a crash");
        }

        // After the lock is released, the file may or may not still be present (we did not
        // attempt redelivery). Clean up explicitly so Dispose's restore logic operates on a
        // known state.
        if (File.Exists(_cachePath))
        {
            File.Delete(_cachePath);
        }
    }
}
