// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Signals a clean, intentional process exit with a specific exit code.
/// Thrown by ExitWithCleanup() to avoid calling Environment.Exit() directly inside
/// async System.CommandLine handlers, which can deadlock on all platforms when
/// CancelOnProcessTermination middleware is active.
/// Caught by UseExceptionHandler in Program.cs, which sets context.ExitCode and
/// returns normally — letting the runtime exit cleanly without Environment.Exit.
/// </summary>
public sealed class CleanExitException : Exception
{
    public int ExitCode { get; }

    public CleanExitException(int exitCode) : base($"Exit {exitCode}")
    {
        ExitCode = exitCode;
    }
}
