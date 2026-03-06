// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Runs prerequisite checks for a command and logs failures with actionable guidance.
/// </summary>
public class PrerequisiteRunner : IPrerequisiteRunner
{
    /// <inheritdoc />
    public async Task<bool> RunAsync(
        IEnumerable<IRequirementCheck> checks,
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var passed = true;

        foreach (var check in checks)
        {
            var result = await check.CheckAsync(config, logger, cancellationToken);

            if (!result.Passed)
            {
                passed = false;

                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    logger.LogError("{CheckName}: {ErrorMessage}", check.Name, result.ErrorMessage);

                if (!string.IsNullOrWhiteSpace(result.ResolutionGuidance))
                    logger.LogError("  Resolution: {ResolutionGuidance}", result.ResolutionGuidance);
            }
            else if (result.IsWarning && !string.IsNullOrWhiteSpace(result.Details))
            {
                logger.LogWarning("{CheckName}: {Details}", check.Name, result.Details);
            }
        }

        return passed;
    }
}
