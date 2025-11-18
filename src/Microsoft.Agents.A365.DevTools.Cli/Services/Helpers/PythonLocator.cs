// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using Microsoft.Agents.A365.DevTools.Cli.Services;
using System.Runtime.InteropServices;

public static class PythonLocator
{
    public static async Task<string?> FindPythonExecutableAsync(CommandExecutor executor)
    {
        // 1. Try PATH first (fastest)
        var pathPython = await TryFindInPathAsync(executor);
        if (pathPython != null) return pathPython;

        // 2. Search common installation directories
        var commonPython = FindInCommonLocations();
        if (commonPython != null) return commonPython;

        // 3. Windows: Try Python Launcher as last resort
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var launcherPython = await TryPythonLauncherAsync(executor);
            if (launcherPython != null) return launcherPython;
        }

        return null;
    }

    private static async Task<string?> TryFindInPathAsync(CommandExecutor executor)
    {
        string[] commands = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "python", "python3" }
            : new[] { "python3", "python" };

        foreach (var cmd in commands)
        {
            var result = await executor.ExecuteAsync(
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which",
                cmd,
                captureOutput: true,
                suppressErrorLogging: true);

            if (result.Success && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                var path = result.StandardOutput
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.Trim();

                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return path;
            }
        }
        return null;
    }

    private static string? FindInCommonLocations()
    {
        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? GetWindowsCandidates()
            : GetUnixCandidates();

        return candidates.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> GetWindowsCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        // Microsoft Store (most common for newer Windows)
        yield return Path.Combine(localAppData, @"Microsoft\WindowsApps\python.exe");
        yield return Path.Combine(localAppData, @"Microsoft\WindowsApps\python3.exe");

        // Standard Python.org installations (3.13 down to 3.8)
        for (int ver = 313; ver >= 38; ver--)
        {
            yield return $@"C:\Python{ver}\python.exe";
            yield return Path.Combine(localAppData, $@"Programs\Python\Python{ver}\python.exe");
            yield return Path.Combine(programFiles, $@"Python{ver}\python.exe");
        }
    }

    private static IEnumerable<string> GetUnixCandidates()
    {
        return new[]
        {
            "/usr/bin/python3",
            "/usr/local/bin/python3",
            "/opt/homebrew/bin/python3",  // macOS ARM (M1/M2/M3)
            "/opt/local/bin/python3",      // MacPorts
            "/usr/bin/python",
            "/usr/local/bin/python"
        };
    }

    private static async Task<string?> TryPythonLauncherAsync(CommandExecutor executor)
    {
        // py.exe can locate Python even if not in PATH
        var result = await executor.ExecuteAsync(
            "py",
            "-3 -c \"import sys; print(sys.executable)\"",
            captureOutput: true,
            suppressErrorLogging: true);

        if (result.Success && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            var path = result.StandardOutput.Trim();
            if (File.Exists(path)) return path;
        }
        return null;
    }
}