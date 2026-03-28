// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using System.IO;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

/// <summary>
/// Custom console formatter that outputs clean messages without timestamps or category names.
/// Follows Azure CLI output patterns for user-friendly CLI experience.
/// Errors are displayed in red, warnings in yellow, info is plain text, debug/trace in dark gray.
/// </summary>
public sealed class CleanConsoleFormatter : ConsoleFormatter
{
    public CleanConsoleFormatter() 
        : base("clean")
    {
    }

    // Constructor required by AddConsoleFormatter
    public CleanConsoleFormatter(Microsoft.Extensions.Options.IOptionsMonitor<ConsoleFormatterOptions> options)
        : base("clean")
    {
        // Options not used - formatter has fixed behavior
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message == null)
        {
            return;
        }

        // Check if we're writing to actual console (supports colors)
        bool isConsole = !Console.IsOutputRedirected;

        // Allow empty strings as intentional blank lines for visual spacing.
        // Must use Console.WriteLine (not textWriter) when on a real console so the blank line
        // is written to the same stream as non-empty messages — mixing the two causes buffering
        // ordering issues where the blank line appears after the next message instead of before it.
        if (message.Length == 0)
        {
            if (isConsole)
                Console.WriteLine();
            else
                textWriter.WriteLine();
            return;
        }

        // Azure CLI pattern: red for errors, yellow for warnings, dark gray for debug/trace, no color for info
        switch (logEntry.LogLevel)
        {
            case LogLevel.Error:
            case LogLevel.Critical:
                if (isConsole)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("ERROR: ");
                    Console.Write(message);
                    Console.ResetColor();
                    Console.WriteLine();
                }
                else
                {
                    textWriter.Write("ERROR: ");
                    textWriter.WriteLine(message);
                }
                break;
            case LogLevel.Warning:
                if (isConsole)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write(message);
                    Console.ResetColor();
                    Console.WriteLine();
                }
                else
                {
                    textWriter.WriteLine(message);
                }
                break;
            case LogLevel.Debug:
                if (isConsole)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("[DEBUG] ");
                    Console.Write(message);
                    Console.ResetColor();
                    Console.WriteLine();
                }
                else
                {
                    textWriter.Write("[DEBUG] ");
                    textWriter.WriteLine(message);
                }
                break;
            case LogLevel.Trace:
                if (isConsole)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("[TRACE] ");
                    Console.Write(message);
                    Console.ResetColor();
                    Console.WriteLine();
                }
                else
                {
                    textWriter.Write("[TRACE] ");
                    textWriter.WriteLine(message);
                }
                break;
            default: // Information
                if (isConsole)
                {
                    Console.ResetColor();
                    Console.WriteLine(message);
                }
                else
                {
                    textWriter.WriteLine(message);
                }
                break;
        }

        // Exception details (stack traces) are intentionally suppressed from console output.
        // The file logger captures the full exception for diagnostics. Showing stack traces
        // on the console is noise for end users and was the root cause of call stacks appearing
        // in CLI output whenever any logger call included an exception parameter.
    }
}
