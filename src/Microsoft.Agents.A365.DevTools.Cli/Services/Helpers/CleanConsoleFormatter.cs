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
/// Errors are displayed in red, warnings in yellow, info is plain text.
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
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        // Check if we're writing to actual console (supports colors)
        bool isConsole = !Console.IsOutputRedirected;

        // Azure CLI pattern: red for errors, yellow for warnings, no color for info
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
                    Console.Write("WARNING: ");
                    Console.Write(message);
                    Console.ResetColor();
                    Console.WriteLine();
                }
                else
                {
                    textWriter.Write("WARNING: ");
                    textWriter.WriteLine(message);
                }
                break;
            default:
                textWriter.WriteLine(message);
                break;
        }

        // If there's an exception, include it (for debugging)
        if (logEntry.Exception != null)
        {
            if (isConsole)
            {
                Console.ForegroundColor = logEntry.LogLevel switch
                {
                    LogLevel.Error or LogLevel.Critical => ConsoleColor.Red,
                    LogLevel.Warning => ConsoleColor.Yellow,
                    _ => Console.ForegroundColor
                };
                Console.WriteLine(logEntry.Exception);
                Console.ResetColor();
            }
            else
            {
                textWriter.WriteLine(logEntry.Exception);
            }
        }
    }
}
