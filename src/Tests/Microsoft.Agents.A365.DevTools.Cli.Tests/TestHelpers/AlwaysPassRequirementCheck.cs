// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.TestHelpers;

/// <summary>
/// Test requirement check that always passes.
/// Used for testing RequirementsSubcommand behavior with successful checks.
/// </summary>
public class AlwaysPassRequirementCheck : RequirementCheck
{
    /// <inheritdoc />
    public override string Name => "Test Always Pass Check";

    /// <inheritdoc />
    public override string Description => "Test requirement check that always passes";

    /// <inheritdoc />
    public override string Category => "Test";

    /// <inheritdoc />
    public override async Task<RequirementCheckResult> CheckAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken = default)
    {
        return await ExecuteCheckWithLoggingAsync(config, logger, CheckImplementationAsync, cancellationToken);
    }

    private Task<RequirementCheckResult> CheckImplementationAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken)
    {
        return Task.FromResult(RequirementCheckResult.Success("This check always passes for testing purposes"));
    }
}
