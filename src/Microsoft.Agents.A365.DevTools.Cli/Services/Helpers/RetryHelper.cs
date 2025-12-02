// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

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
    public async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<T, bool> shouldRetry,
        int maxRetries = 5,
        int baseDelaySeconds = 2,
        CancellationToken cancellationToken = default)
    {
        int attempt = 0;
        Exception? lastException = null;

        while (attempt < maxRetries)
        {
            try
            {
                var result = await operation(cancellationToken);

                if (!shouldRetry(result))
                {
                    return result;
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

        if (lastException != null)
        {
            throw lastException;
        }

        return await operation(cancellationToken);
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
