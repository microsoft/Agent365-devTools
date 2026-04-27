// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

/// <summary>
/// Extension methods for <see cref="ILogger"/> providing CLI output formatting helpers.
/// </summary>
internal static class LoggerExtensions
{
    /// <summary>
    /// Opens a log indent scope. All log messages emitted within the <c>using</c> block are
    /// indented by one additional level (4 spaces) when rendered by <see cref="CleanConsoleFormatter"/>.
    /// Scopes are nestable (up to 3 levels; deeper scopes are clamped).
    /// </summary>
    /// <example>
    /// <code>
    /// logger.LogInformation("Creating blueprint application...");
    /// using (logger.Indent())
    /// {
    ///     logger.LogInformation("Display Name: {Name}", name);
    ///     logger.LogInformation("Blueprint ID: {Id}", id);
    /// }
    /// </code>
    /// </example>
    public static IDisposable Indent(this ILogger logger) =>
        logger.BeginScope(LogIndentScope.Instance)
        ?? NullDisposable.Instance;

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}
