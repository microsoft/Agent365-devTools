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
    public override async Task<RequirementCheckResult> CheckAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken = default)
    {
        return await ExecuteCheckWithLoggingAsync(config, logger, CheckImplementationAsync, cancellationToken);
    }

    /// <summary>
    /// The actual implementation of the Frontier Preview requirement check
    /// </summary>
    private Task<RequirementCheckResult> CheckImplementationAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogWarning("");
        logger.LogWarning("[WARNING] Frontier Preview Program Enrollment");
        logger.LogWarning("");
        logger.LogWarning("Important: Microsoft Agent 365 requires your tenant to be part of the");
        logger.LogWarning("Frontier Preview Program for early access to AI innovations.");
        logger.LogWarning("");
        logger.LogWarning("This check cannot be verified automatically. Please confirm your tenant");
        logger.LogWarning("is enrolled before proceeding.");
        logger.LogWarning("");
        logger.LogWarning("To verify enrollment or join the program:");
        logger.LogWarning("");
        logger.LogWarning("1. Learn more about Microsoft Agent 365 requirements:");
        logger.LogWarning("   https://learn.microsoft.com/en-us/microsoft-agent-365/developer/");
        logger.LogWarning("");
        logger.LogWarning("2. Apply for the Frontier Preview Program:");
        logger.LogWarning("   https://adoption.microsoft.com/en-us/copilot/frontier-program/");
        logger.LogWarning("");
        logger.LogWarning("Note: Frontier previews are subject to the existing preview terms of your");
        logger.LogWarning("customer agreements.");
        logger.LogWarning("");

        // Return success to allow user to proceed, but with warning details
        return Task.FromResult(RequirementCheckResult.Success(
            details: "WARNING: Cannot automatically verify Frontier Preview Program enrollment. " +
                    "Please ensure your tenant is enrolled before proceeding with setup. " +
                    "See above for enrollment information."
        ));
    }
}
