// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;

/// <summary>
/// Result of a requirement check execution
/// </summary>
public class RequirementCheckResult
{
    /// <summary>
    /// Whether the requirement check passed
    /// </summary>
    public bool Passed { get; set; }

    /// <summary>
    /// Error message if the check failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Guidance on how to resolve the issue if the check failed
    /// </summary>
    public string? ResolutionGuidance { get; set; }

    /// <summary>
    /// Additional details about the check result
    /// </summary>
    public string? Details { get; set; }

    /// <summary>
    /// Creates a successful result
    /// </summary>
    public static RequirementCheckResult Success(string? details = null)
    {
        return new RequirementCheckResult
        {
            Passed = true,
            Details = details
        };
    }

    /// <summary>
    /// Creates a failed result
    /// </summary>
    public static RequirementCheckResult Failure(string errorMessage, string resolutionGuidance, string? details = null)
    {
        return new RequirementCheckResult
        {
            Passed = false,
            ErrorMessage = errorMessage,
            ResolutionGuidance = resolutionGuidance,
            Details = details
        };
    }
}