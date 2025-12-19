// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;

/// <summary>
/// Interface for requirement checks that can be executed as part of setup validation
/// </summary>
public interface IRequirementCheck
{
    /// <summary>
    /// Gets the name of the requirement check
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the description of what this requirement check validates
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets the category of this requirement (e.g., "Azure", "Authentication", "Configuration")
    /// </summary>
    string Category { get; }

    /// <summary>
    /// Executes the requirement check
    /// </summary>
    /// <param name="config">The Agent365 configuration</param>
    /// <param name="logger">Logger for output</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result of the requirement check</returns>
    Task<RequirementCheckResult> CheckAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken = default);
}