// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Interface for subcommands that require validation before execution.
/// Implements separation of validation and execution phases to fail fast on configuration issues.
/// </summary>
public interface ISubCommand
{
    /// <summary>
    /// Validates prerequisites for the subcommand without performing any actions.
    /// This should check configuration, authentication, and environment requirements.
    /// </summary>
    /// <param name="config">The Agent365 configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of validation errors, empty if validation passes</returns>
    Task<List<string>> ValidateAsync(Agent365Config config, CancellationToken cancellationToken = default);
}
