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
    /// Helper method to log check success
    /// </summary>
    protected virtual void LogCheckSuccess(ILogger logger, string? details = null)
    {
        logger.LogInformation("Pass: {Name}{Details}", Name,
            string.IsNullOrWhiteSpace(details) ? "" : $" ({details})");
    }

    /// <summary>
    /// Helper method to log check warning
    /// </summary>
    protected virtual void LogCheckWarning(ILogger logger, string? message = null)
    {
        logger.LogWarning("Warn: {Name}{Details}", Name,
            string.IsNullOrWhiteSpace(message) ? "" : $" - {message}");
    }

    /// <summary>
    /// Helper method to log check failure
    /// </summary>
    protected virtual void LogCheckFailure(ILogger logger, string errorMessage, string resolutionGuidance)
    {
        // Name logged at Error level (red) — formatter already prefixes ERROR:
        logger.LogError("[FAIL] {Name}", Name);

        // Error details in red (split multi-line messages into separate lines)
        foreach (var line in errorMessage.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            logger.LogError("  {Line}", line.TrimEnd());

        // Resolution guidance in white (not red) — it is helpful guidance, not an error
        if (!string.IsNullOrWhiteSpace(resolutionGuidance))
        {
            logger.LogInformation("");
            foreach (var step in resolutionGuidance.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                logger.LogInformation("  {Step}", step.TrimEnd());
        }
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

        try
        {
            var result = await checkImplementation(config, logger, cancellationToken);

            if (result.Passed)
            {
                if (result.IsWarning)
                {
                    var warningMessage = (!string.IsNullOrWhiteSpace(result.ErrorMessage) && !string.IsNullOrWhiteSpace(result.Details))
                        ? $"{result.ErrorMessage} - {result.Details}"
                        : result.ErrorMessage ?? result.Details;
                    LogCheckWarning(logger, warningMessage);
                }
                else
                {
                    LogCheckSuccess(logger, result.Details);
                }
            }
            else
            {
                LogCheckFailure(logger, result.ErrorMessage ?? "Check failed", result.ResolutionGuidance ?? "No guidance available");
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errorMessage = $"Exception during check: {ex.Message}";
            var resolutionGuidance = "Please check the logs for more details and ensure all prerequisites are met";

            LogCheckFailure(logger, errorMessage, resolutionGuidance);

            return RequirementCheckResult.Failure(errorMessage, resolutionGuidance, ex.ToString());
        }
    }
}