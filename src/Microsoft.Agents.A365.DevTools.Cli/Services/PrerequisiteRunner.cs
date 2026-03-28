// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Runs prerequisite checks for a command and aggregates pass/fail results.
/// Each check handles its own [PASS]/[FAIL]/[WARN] logging via ExecuteCheckWithLoggingAsync.
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
            }
        }

        return passed;
    }
}
