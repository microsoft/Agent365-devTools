// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Runs a set of prerequisite checks before a command executes.
/// Returns false and logs actionable errors if any blocking check fails.
/// Warnings are logged but do not block execution.
/// </summary>
public interface IPrerequisiteRunner
{
    /// <summary>
    /// Runs all checks in order.
    /// </summary>
    /// <param name="checks">The prerequisite checks to run.</param>
    /// <param name="config">The current Agent 365 configuration.</param>
    /// <param name="logger">Logger for output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if all blocking checks pass; false if any check fails.</returns>
    Task<bool> RunAsync(
        IEnumerable<IRequirementCheck> checks,
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken = default);
}
