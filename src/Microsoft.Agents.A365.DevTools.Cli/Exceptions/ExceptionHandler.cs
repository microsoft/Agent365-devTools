// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Centralized exception handling utility for Agent365 CLI.
/// Provides consistent error display and logging.
/// Follows Microsoft CLI best practices (Azure CLI, dotnet CLI patterns).
/// </summary>
public static class ExceptionHandler
{
    /// <summary>
    /// Handles Agent365Exception with user-friendly output (no stack traces for user errors).
    /// Displays formatted error messages to console and logs for diagnostics.
    /// </summary>
    /// <param name="ex">The Agent365Exception to handle</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    /// <param name="logFilePath">Optional path to the log file for troubleshooting</param>
    public static void HandleAgent365Exception(Agent365Exception ex, ILogger? logger = null, string? logFilePath = null)
    {
        // Get the full formatted message
        var message = ex.GetFormattedMessage();
        
        // Split into lines to color only the ERROR line
        var lines = message.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
        
        foreach (var line in lines)
        {
            if (line.StartsWith("ERROR:", StringComparison.Ordinal))
            {
                // Color the ERROR line red
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(line);
                Console.ResetColor();
            }
            else
            {
                // Rest in default color
                Console.Error.WriteLine(line);
            }
        }

        // For system errors (not user errors), show log file path and suggest reporting as bug.
        // User errors (e.g. missing config) have self-explanatory messages — log path adds no value.
        if (!ex.IsUserError)
        {
            if (!string.IsNullOrEmpty(logFilePath))
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"For more details, see the log file at: {logFilePath}");
            }
            Console.Error.WriteLine("If this error persists, please report it at:");
            Console.Error.WriteLine("https://github.com/microsoft/Agent365-devTools/issues");
            Console.Error.WriteLine();
        }

        // Log for diagnostics (but don't show stack trace to user)
        logger?.LogError("Operation failed. ErrorCode={ErrorCode}, IssueDescription={IssueDescription}",
            ex.ErrorCode, ex.IssueDescription);
    }

    /// <summary>
    /// Signals a clean, intentional exit with the given exit code.
    /// Throws CleanExitException instead of calling Environment.Exit() directly,
    /// which avoids deadlocks on all platforms when System.CommandLine's
    /// CancelOnProcessTermination middleware is active.
    /// </summary>
    /// <param name="exitCode">The exit code to return (0 for success, non-zero for errors)</param>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public static void ExitWithCleanup(int exitCode)
    {
        Console.Out.Flush();
        Console.Error.Flush();
        Console.ResetColor();
        throw new CleanExitException(exitCode);
    }
}