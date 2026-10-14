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
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"'{path}' is not a valid path";
        }

        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath == ".")
        {
            return null;
        }

        if (Path.IsPathRooted(relativePath) ||
            relativePath == ".." ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return $"'{fullPath}' is outside the directory that contains a365.config.json ('{root}')";
        }

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
    internal static void ThrowIfUnsafe(string rootDirectory, string path)
    {
        var unsafeReason = GetUnsafeReason(rootDirectory, path);
        if (unsafeReason != null)
        {
            throw new IOException($"Refusing to write a project file because {unsafeReason}.");
        }
    }
}
