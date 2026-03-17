// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Requirement check that validates tenant enrollment in the Microsoft Frontier Preview Program
/// This check cannot be verified programmatically and serves as an important reminder to users
/// </summary>
public class FrontierPreviewRequirementCheck : RequirementCheck
{
    /// <inheritdoc />
    public override string Name => "Frontier Preview Program";

    /// <inheritdoc />
    public override string Description => "Validates that your tenant is enrolled in the Microsoft Frontier Preview Program for early access to AI innovations";

    /// <inheritdoc />
    public override string Category => "Tenant Enrollment";

    /// <inheritdoc />
    public override Task<RequirementCheckResult> CheckAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken = default)
    {
        return ExecuteCheckWithLoggingAsync(config, logger, (_, __, ___) => Task.FromResult(
            RequirementCheckResult.Warning(
                message: "Tenant enrollment cannot be verified automatically",
                details: "Ensure your tenant is enrolled before proceeding. See: https://adoption.microsoft.com/copilot/frontier-program/"
            )), cancellationToken);
    }
}
