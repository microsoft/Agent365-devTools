// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.TestHelpers;

/// <summary>
/// Creates symbolic links and junctions for file system security tests.
/// </summary>
internal static class FileSystemLinks
{
    /// <summary>
    /// Runs a symbolic link creation and reports whether the OS allowed it.
    /// Windows needs Developer Mode or elevation for symlinks; Linux and macOS always allow them.
    /// </summary>
    public static bool TryCreateSymlink(Action create)
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

    /// <summary>
    /// Creates a directory junction with mklink /J. Windows only; junctions need no elevation.
    /// </summary>
    public static void CreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Directory junctions exist only on Windows.");

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start cmd.exe to create a junction.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !Directory.Exists(link))
            throw new InvalidOperationException($"mklink /J failed with exit code {process.ExitCode}: {output}");
    }

    /// <summary>
    /// Replaces <paramref name="directory"/> with a link to <paramref name="target"/> that needs no elevation:
    /// a junction on Windows and a symbolic link elsewhere.
    /// </summary>
    public static void ReplaceWithDirectoryLink(string directory, string target)
    {
        Directory.Delete(directory, recursive: true);
        if (OperatingSystem.IsWindows())
            CreateJunction(directory, target);
        else
            Directory.CreateSymbolicLink(directory, target);
    }
}
