// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

/// <summary>
/// Marker scope type for CLI output indentation.
/// Push onto the logging scope stack via <see cref="LoggerExtensions.Indent"/>.
/// Each instance in the scope stack adds one indent level (4 spaces) to log messages
/// rendered by <see cref="CleanConsoleFormatter"/>.
/// </summary>
internal sealed class LogIndentScope
{
    // Singleton — contents are irrelevant; CleanConsoleFormatter counts instances in the stack.
    public static readonly LogIndentScope Instance = new();

    private LogIndentScope() { }
}
