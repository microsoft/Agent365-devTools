// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;

/// <summary>
/// Base class for requirement checks providing common functionality
/// </summary>
public abstract class RequirementCheck : IRequirementCheck
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public abstract string Category { get; }

    /// <inheritdoc />
    public abstract Task<RequirementCheckResult> CheckAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken = default);

    /// <summary>
    /// Helper method to log check start
    /// </summary>
    protected virtual void LogCheckStart(ILogger logger)
    {
        logger.LogInformation("Checking: {Description}", Description);
    }

    /// <summary>
    /// Helper method to log check success
    /// </summary>
    protected virtual void LogCheckSuccess(ILogger logger, string? details = null)
    {
        logger.LogInformation("[PASS] {Name}: PASSED", Name);
        if (!string.IsNullOrWhiteSpace(details))
        {
            logger.LogInformation("  Details: {Details}", details);
        }
    }

    /// <summary>
    /// Helper method to log check failure
    /// </summary>
    protected virtual void LogCheckFailure(ILogger logger, string errorMessage, string resolutionGuidance)
    {
        logger.LogError("[FAIL] {Name}: FAILED", Name);
        logger.LogError("  Issue: {ErrorMessage}", errorMessage);
        logger.LogError("  Resolution: {ResolutionGuidance}", resolutionGuidance);
    }

    /// <summary>
    /// Helper method to execute the check with consistent logging
    /// </summary>
    protected async Task<RequirementCheckResult> ExecuteCheckWithLoggingAsync(
        Agent365Config config, 
        ILogger logger, 
        Func<Agent365Config, ILogger, CancellationToken, Task<RequirementCheckResult>> checkImplementation,
        CancellationToken cancellationToken = default)
    {
        LogCheckStart(logger);
        
        try
        {
            var result = await checkImplementation(config, logger, cancellationToken);
            
            if (result.Passed)
            {
                LogCheckSuccess(logger, result.Details);
            }
            else
            {
                LogCheckFailure(logger, result.ErrorMessage ?? "Check failed", result.ResolutionGuidance ?? "No guidance available");
            }
            
            return result;
        }
        catch (Exception ex)
        {
            var errorMessage = $"Exception during check: {ex.Message}";
            var resolutionGuidance = "Please check the logs for more details and ensure all prerequisites are met";
            
            LogCheckFailure(logger, errorMessage, resolutionGuidance);
            
            return RequirementCheckResult.Failure(errorMessage, resolutionGuidance, ex.ToString());
        }
    }
}