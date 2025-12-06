// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

/// <summary>
/// Helper for creating consistent logger factories across the application.
/// Ensures all loggers use the clean console formatter without class names.
/// </summary>
public static class LoggerFactoryHelper
{
    /// <summary>
    /// Creates a logger factory with clean console output (no timestamps, no class names).
    /// Follows Azure CLI output patterns with colored errors (red) and warnings (yellow).
    /// </summary>
    /// <param name="minimumLevel">Minimum log level (default: Information)</param>
    public static ILoggerFactory CreateCleanLoggerFactory(LogLevel minimumLevel = LogLevel.Information)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minimumLevel);
            builder.AddConsoleFormatter<CleanConsoleFormatter, ConsoleFormatterOptions>();
            builder.AddConsole(options =>
            {
                options.FormatterName = "clean";
            });
        });
    }
}
