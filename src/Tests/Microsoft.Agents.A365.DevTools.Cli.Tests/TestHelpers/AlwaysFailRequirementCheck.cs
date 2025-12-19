// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.TestHelpers;

/// <summary>
/// Test requirement check that always fails.
/// Used for testing RequirementsSubcommand behavior with failed checks.
/// </summary>
public class AlwaysFailRequirementCheck : RequirementCheck
{
    /// <inheritdoc />
    public override string Name => "Test Always Fail Check";

    /// <inheritdoc />
    public override string Description => "Test requirement check that always fails";

    /// <inheritdoc />
    public override string Category => "Test";

    /// <inheritdoc />
    public override async Task<RequirementCheckResult> CheckAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken = default)
    {
        return await ExecuteCheckWithLoggingAsync(config, logger, CheckImplementationAsync, cancellationToken);
    }

    private Task<RequirementCheckResult> CheckImplementationAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken)
    {
        return Task.FromResult(RequirementCheckResult.Failure(
            errorMessage: "This check always fails for testing purposes",
            resolutionGuidance: "This is a test check - no resolution needed",
            details: "Test failure details"));
    }
}
