// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Serilog;

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
    /// Displays formatted error messages to console and logs to Serilog for diagnostics.
    /// </summary>
    /// <param name="ex">The Agent365Exception to handle</param>
    public static void HandleAgent365Exception(Agent365Exception ex)
    {
        // Display formatted error message
        Console.Error.Write(ex.GetFormattedMessage());
        
        // For system errors (not user errors), suggest reporting as bug
        if (!ex.IsUserError)
        {
            Console.Error.WriteLine("If this error persists, please report it at:");
            Console.Error.WriteLine("https://github.com/microsoft/Agent365-devTools/issues");
            Console.Error.WriteLine();
        }

        // Log for diagnostics (but don't show stack trace to user)
        Log.Error("Operation failed. ErrorCode={ErrorCode}, IssueDescription={IssueDescription}",
            ex.ErrorCode, ex.IssueDescription);
    }
}