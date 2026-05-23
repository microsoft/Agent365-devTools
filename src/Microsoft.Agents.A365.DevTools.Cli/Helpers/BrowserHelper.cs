// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers;

/// <summary>
/// Helper methods for cross-platform browser and URL operations.
/// </summary>
public static class BrowserHelper
{
    /// <summary>
    /// Optional test-only override. When set, <see cref="TryOpenUrl"/> invokes this delegate
    /// instead of launching a real browser process. Tests that exercise admin-consent paths
    /// must set this to a no-op (and reset it in a finally/Dispose block) to avoid spawning a
    /// real browser during the test run. AsyncLocal scoping prevents leaks across parallel
    /// xUnit test classes. Not intended for production code.
    /// </summary>
    internal static Action<string, ILogger?>? OpenUrlOverrideForTests
    {
        get => _openUrlOverride.Value;
        set => _openUrlOverride.Value = value;
    }

    private static readonly AsyncLocal<Action<string, ILogger?>?> _openUrlOverride = new();

    /// <summary>
    /// Opens a URL in the system's default browser in a cross-platform way.
    /// Non-fatal: if the browser cannot be launched, logs a warning via <paramref name="logger"/>
    /// when provided, or writes to <see cref="Console.Error"/> when logger is null.
    /// The fallback URL is always emitted so the user can open it manually.
    /// </summary>
    /// <param name="url">The URL to open.</param>
    /// <param name="logger">Optional logger for diagnostic messages.</param>
    public static void TryOpenUrl(string url, ILogger? logger = null)
    {
        if (OpenUrlOverrideForTests is { } overrideAction)
        {
            overrideAction(url, logger);
            return;
        }

        try
        {
            ProcessStartInfo psi;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                psi = new ProcessStartInfo { FileName = url, UseShellExecute = true };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                psi = new ProcessStartInfo { FileName = "open" };
                psi.ArgumentList.Add(url);
            }
            else
            {
                psi = new ProcessStartInfo { FileName = "xdg-open" };
                psi.ArgumentList.Add(url);
            }
            using var process = new Process { StartInfo = psi };
            process.Start();
        }
        catch (Exception ex)
        {
            if (logger != null)
            {
                logger.LogWarning(ex, "Failed to open browser automatically for URL {Url}", url);
                logger.LogInformation("Please manually open: {Url}", url);
            }
            else
            {
                Console.Error.WriteLine($"Failed to open browser automatically: {ex.Message}");
                Console.Error.WriteLine($"Please manually open: {url}");
            }
        }
    }
}
