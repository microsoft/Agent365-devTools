// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

/// <summary>
/// Simple retry helper for HTTP operations with exponential backoff
/// </summary>
public class RetryHelper
{
    private readonly ILogger _logger;

    public RetryHelper(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Execute an async operation with retry logic and exponential backoff
    /// </summary>
    /// <typeparam name="T">Return type of the operation</typeparam>
    /// <param name="operation">The async operation to execute. Receives a cancellation token and returns a result.</param>
    /// <param name="shouldRetry">Predicate that determines if retry is needed. Returns TRUE when the operation should be retried (operation failed), FALSE when operation succeeded and no retry is needed.</param>
    /// <param name="maxRetries">Maximum number of retry attempts before giving up (default: 5)</param>
    /// <param name="baseDelaySeconds">Base delay in seconds for exponential backoff calculation (default: 2). Actual delay doubles with each attempt.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Result of the operation when shouldRetry returns false (success), or the last result after all retries are exhausted (may be null/default(T) if operation never succeeded)</returns>
    /// <exception cref="HttpRequestException">Thrown when HTTP request fails on the last retry attempt</exception>
    /// <exception cref="TaskCanceledException">Thrown when operation is canceled on the last retry attempt</exception>
    public async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<T, bool> shouldRetry,
        int maxRetries = 5,
        int baseDelaySeconds = 2,
        CancellationToken cancellationToken = default)
    {
        int attempt = 0;
        Exception? lastException = null;
        T? lastResult = default;

        while (attempt < maxRetries)
        {
            try
            {
                lastResult = await operation(cancellationToken);

                if (!shouldRetry(lastResult))
                {
                    return lastResult;
                }

                if (attempt < maxRetries - 1)
                {
                    var delay = CalculateDelay(attempt, baseDelaySeconds);
                    _logger.LogInformation(
                        "Retry attempt {AttemptNumber} of {MaxRetries}. Waiting {DelaySeconds} seconds...",
                        attempt + 1, maxRetries, (int)delay.TotalSeconds);

                    await Task.Delay(delay, cancellationToken);
                }

                attempt++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
                _logger.LogWarning("Exception: {Message}", ex.Message);

                if (attempt < maxRetries - 1)
                {
                    var delay = CalculateDelay(attempt, baseDelaySeconds);
                    _logger.LogInformation(
                        "Retry attempt {AttemptNumber} of {MaxRetries}. Waiting {DelaySeconds} seconds...",
                        attempt + 1, maxRetries, (int)delay.TotalSeconds);

                    await Task.Delay(delay, cancellationToken);
                }

                attempt++;
            }
        }

        // If we had an exception on the last attempt, throw it
        if (lastException != null)
        {
            throw lastException;
        }

        // All retries exhausted - verify we have a result to return
        if (lastResult is null)
        {
            throw new RetryExhaustedException(
                "Async operation with retry",
                maxRetries,
                "Operation did not return a value and no exception was thrown");
        }

        return lastResult;
    }

    /// <summary>
    /// Execute an async operation with retry logic for exception-based retries only.
    /// </summary>
    /// <typeparam name="T">Return type of the operation</typeparam>
    /// <param name="operation">The operation to execute</param>
    /// <param name="maxRetries">Maximum number of retry attempts (default: 5)</param>
    /// <param name="baseDelaySeconds">Base delay in seconds for exponential backoff (default: 2)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the operation</returns>
    public async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxRetries = 5,
        int baseDelaySeconds = 2,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithRetryAsync(
            operation,
            _ => false,
            maxRetries,
            baseDelaySeconds,
            cancellationToken);
    }

    private static TimeSpan CalculateDelay(int attemptNumber, int baseDelaySeconds)
    {
        var exponentialDelay = baseDelaySeconds * Math.Pow(2, attemptNumber);
        var cappedDelay = Math.Min(exponentialDelay, 60);
        return TimeSpan.FromSeconds(cappedDelay);
    }
}
