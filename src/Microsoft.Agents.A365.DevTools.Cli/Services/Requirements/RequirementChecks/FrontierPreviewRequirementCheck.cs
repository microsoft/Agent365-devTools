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
        logger.LogInformation("Requirement: {Name}", Name);

        Console.WriteLine();
        logger.LogWarning("While Microsoft Agent 365 is in preview, Frontier Preview Program enrollment is required.");
        Console.WriteLine("  - Enrollment cannot be verified automatically.");
        Console.WriteLine("  - Please confirm your tenant is enrolled before continuing.");
        Console.WriteLine();
        Console.WriteLine("Documentation:");
        Console.WriteLine("  - https://learn.microsoft.com/microsoft-agent-365/developer/");
        Console.WriteLine("  - https://adoption.microsoft.com/copilot/frontier-program/");

        // Return warning without using base class logging (already logged above)
        return Task.FromResult(RequirementCheckResult.Warning(
            message: "Cannot automatically verify Frontier Preview Program enrollment",
            details: "Tenant must be enrolled in Frontier Preview Program during Agent 365 preview. Check documentation to verify if this requirement still applies."
        ));
    }
}
