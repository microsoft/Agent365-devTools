// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Requirement check that validates the location is configured.
/// Location is required by the endpoint registration API regardless of the needDeployment setting.
/// </summary>
public class LocationRequirementCheck : RequirementCheck
{
    /// <inheritdoc />
    public override string Name => "Location Configuration";

    /// <inheritdoc />
    public override string Description => "Validates that a location is configured for Bot Framework endpoint registration";

    /// <inheritdoc />
    public override string Category => "Configuration";

    /// <inheritdoc />
    public override async Task<RequirementCheckResult> CheckAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken = default)
    {
        return await ExecuteCheckWithLoggingAsync(config, logger, CheckImplementationAsync, cancellationToken);
    }

    private static Task<RequirementCheckResult> CheckImplementationAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Location))
        {
            return Task.FromResult(RequirementCheckResult.Failure(
                errorMessage: ErrorMessages.EndpointLocationRequiredForCreate,
                resolutionGuidance: $"{ErrorMessages.EndpointLocationAddToConfig} {ErrorMessages.EndpointLocationExample}",
                details: "The location field is required for the Bot Framework endpoint registration API, even when needDeployment is set to false (external hosting)."
            ));
        }

        return Task.FromResult(RequirementCheckResult.Success(
            details: $"Location is configured: {config.Location}"
        ));
    }
}
