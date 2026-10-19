// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers;

/// <summary>
/// Decides whether the CLI may write a secret (bearer token, client secret) to a project path.
/// a365.config.json is usually committed with the project, so paths it supplies are untrusted.
/// </summary>
internal static class SecretFileGuard
{
    /// <summary>
    /// Returns null when <paramref name="path"/> (relative paths resolve against <paramref name="rootDirectory"/>)
    /// is the root or inside it and no entry below the root is a symbolic link or junction;
    /// otherwise returns why a secret must not be written there.
    /// </summary>
    internal static string? GetUnsafeReason(string rootDirectory, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string root;
        string fullPath;
        try
        {
            root = NormalizeDriveLetter(Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory)));
            fullPath = NormalizeDriveLetter(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, root)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"'{path}' is not a valid path";
        }

        if (string.Equals(fullPath, root, StringComparison.Ordinal))
        {
            return null;
        }

        // Exact-case containment: case-sensitive volumes exist on every OS, so a case-only alias may be a different directory.
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return $"'{fullPath}' differs only in letter case from the directory that contains a365.config.json ('{root}'); use the exact case or a relative path";
            }

            return $"'{fullPath}' is outside the directory that contains a365.config.json ('{root}')";
        }

        var relativePath = fullPath.Substring(rootPrefix.Length);

        // A committed symlink or junction below the root could redirect the write anywhere.
        var current = root;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if (new FileInfo(current).LinkTarget != null)
                {
                    return $"'{current}' is a symbolic link or junction";
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Nothing can exist below a missing entry.
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"'{current}' could not be inspected: {ex.Message}";
            }
        }

        return null;
    }

    /// <summary>
    /// Re-checks <paramref name="path"/> immediately before a write, so a link swapped in after an earlier check is not followed.
    /// </summary>
    /// <exception cref="UnsafeDestinationException">The destination is no longer safe to write.</exception>
    internal static void ThrowIfUnsafe(string rootDirectory, string path)
    {
        var unsafeReason = GetUnsafeReason(rootDirectory, path);
        if (unsafeReason != null)
        {
            throw new UnsafeDestinationException(unsafeReason);
        }
    }

    // Drive letters are case-insensitive on every Windows volume, unlike directory names.
    private static string NormalizeDriveLetter(string path)
        => OperatingSystem.IsWindows() && path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0])
            ? char.ToUpperInvariant(path[0]) + path[1..]
            : path;
}

/// <summary>
/// Thrown by <see cref="SecretFileGuard.ThrowIfUnsafe"/> so callers can report a rejected destination, not an I/O failure.
/// </summary>
internal sealed class UnsafeDestinationException : IOException
{
    public UnsafeDestinationException(string reason)
        : base($"Refusing to write a project file because {reason}.")
    {
        Reason = reason;
    }

    /// <summary>Why the destination was rejected, as returned by <see cref="SecretFileGuard.GetUnsafeReason"/>.</summary>
    public string Reason { get; }
}
