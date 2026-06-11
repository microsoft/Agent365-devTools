// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading;

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers;

/// <summary>
/// Console input helpers that cooperate with cancellation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Console.ReadLine"/> blocks in a platform-native syscall (ReadConsoleW on
/// Windows, read(2) on POSIX) and does not honor managed <see cref="CancellationToken"/>s.
/// Under <c>System.CommandLine</c>, Ctrl+C is intercepted (the runtime's default
/// process-terminating handler is suppressed) and the command's token is cancelled — which
/// means a vanilla <c>Console.ReadLine()</c> would block forever after Ctrl+C.
/// </para>
/// <para>
/// This helper bridges the gap by registering a cancellation callback that calls
/// <see cref="Environment.Exit(int)"/> with exit code 130 (POSIX convention for SIGINT:
/// 128 + SIGINT(2)). <see cref="Environment.Exit(int)"/> terminates the process identically
/// on Windows, Linux, and macOS.
/// </para>
/// </remarks>
internal static class ConsoleHelper
{
    /// <summary>
    /// Test hook: when set, <see cref="ReadLineCancellable"/> returns this value immediately
    /// without touching <see cref="Console"/>. Avoids tests blocking on stdin.
    /// </summary>
    internal static System.Threading.AsyncLocal<Func<string?>?> ReadLineOverrideForTests { get; } = new();

    /// <summary>
    /// Reads a line from standard input. If the supplied <paramref name="ct"/> fires while
    /// the read is blocked, the process exits with code 130 (SIGINT convention).
    /// </summary>
    public static string? ReadLineCancellable(CancellationToken ct)
    {
        var testOverride = ReadLineOverrideForTests.Value;
        if (testOverride != null)
        {
            ct.ThrowIfCancellationRequested();
            return testOverride();
        }

        ct.ThrowIfCancellationRequested();

        // Console.ReadLine is uncancellable from managed code on all platforms because it
        // blocks in a native syscall. The only portable way to unblock it on cancellation
        // is to exit the process. Exit code 130 is the POSIX convention for SIGINT.
        using var registration = ct.Register(static () =>
        {
            try { Console.Error.WriteLine("Cancelled."); } catch { /* best effort */ }
            Environment.Exit(130);
        });

        return Console.ReadLine();
    }
}
