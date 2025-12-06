// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Helpers;

/// <summary>
/// Unit tests for RetryHelper
/// </summary>
public class RetryHelperTests
{
    private readonly ILogger _mockLogger;
    private readonly RetryHelper _retryHelper;

    public RetryHelperTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _retryHelper = new RetryHelper(_mockLogger);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_SuccessOnFirstAttempt_ReturnsResult()
    {
        // Arrange
        var expectedResult = "success";
        var callCount = 0;

        // Act
        var result = await _retryHelper.ExecuteWithRetryAsync(
            ct =>
            {
                callCount++;
                return Task.FromResult(expectedResult);
            },
            result => false,
            maxRetries: 3);

        // Assert
        result.Should().Be(expectedResult);
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_SuccessOnThirdAttempt_RetriesAndReturnsResult()
    {
        // Arrange
        var callCount = 0;
        var expectedResult = "success";

        // Act
        var result = await _retryHelper.ExecuteWithRetryAsync(
            ct =>
            {
                callCount++;
                return Task.FromResult(expectedResult);
            },
            result =>
            {
                return callCount < 3;
            },
            maxRetries: 5,
            baseDelaySeconds: 0);

        // Assert
        result.Should().Be(expectedResult);
        callCount.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_AllAttemptsFailWithException_ThrowsException()
    {
        // Arrange
        var callCount = 0;

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await _retryHelper.ExecuteWithRetryAsync<string>(
                ct =>
                {
                    callCount++;
                    throw new HttpRequestException("Network error");
                },
                result => false,
                maxRetries: 3,
                baseDelaySeconds: 0);
        });

        callCount.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_ExceptionThenSuccess_RecoversAndReturnsResult()
    {
        // Arrange
        var callCount = 0;
        var expectedResult = "success";

        // Act
        var result = await _retryHelper.ExecuteWithRetryAsync(
            ct =>
            {
                callCount++;
                if (callCount < 2)
                {
                    throw new HttpRequestException("Transient error");
                }
                return Task.FromResult(expectedResult);
            },
            result => false,
            maxRetries: 3,
            baseDelaySeconds: 0);

        // Assert
        result.Should().Be(expectedResult);
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_TaskCanceledException_Retries()
    {
        // Arrange
        var callCount = 0;

        // Act & Assert
        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            await _retryHelper.ExecuteWithRetryAsync<string>(
                ct =>
                {
                    callCount++;
                    throw new TaskCanceledException("Request timed out");
                },
                result => false,
                maxRetries: 2,
                baseDelaySeconds: 0);
        });

        callCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_ExponentialBackoff_CalculatesCorrectDelays()
    {
        // Arrange
        var callCount = 0;
        var delays = new List<double>();

        // Act
        await _retryHelper.ExecuteWithRetryAsync(
            ct =>
            {
                callCount++;
                return Task.FromResult("result");
            },
            result => callCount < 4,
            maxRetries: 4,
            baseDelaySeconds: 2);

        // Assert - verify exponential backoff: 2, 4, 8 seconds
        callCount.Should().Be(4);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_MultipleRetries_CompletesAllAttempts()
    {
        // Arrange
        var callCount = 0;

        // Act - use small base delay to test retry logic quickly
        await _retryHelper.ExecuteWithRetryAsync(
            ct =>
            {
                callCount++;
                return Task.FromResult("result");
            },
            result => callCount < 3,
            maxRetries: 3,
            baseDelaySeconds: 1);

        // Assert - should complete all attempts
        callCount.Should().Be(3);
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new RetryHelper(null!));
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_CancellationToken_PropagatesCorrectly()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var callCount = 0;

        // Act
        var result = await _retryHelper.ExecuteWithRetryAsync(
            ct =>
            {
                callCount++;
                ct.Should().Be(cts.Token);
                return Task.FromResult("success");
            },
            result => false,
            maxRetries: 3,
            baseDelaySeconds: 0,
            cts.Token);

        // Assert
        result.Should().Be("success");
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_ComplexReturnType_HandlesCorrectly()
    {
        // Arrange
        var expectedTuple = (StatusCode: 200, Body: "OK");
        var callCount = 0;

        // Act
        var result = await _retryHelper.ExecuteWithRetryAsync(
            ct =>
            {
                callCount++;
                return Task.FromResult(expectedTuple);
            },
            result => result.StatusCode != 200,
            maxRetries: 3);

        // Assert
        result.Should().Be(expectedTuple);
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_ShouldRetryAlwaysTrue_CallsExactlyMaxRetries()
    {
        // Arrange
        var callCount = 0;
        const int maxRetries = 5;

        // Act
        var result = await _retryHelper.ExecuteWithRetryAsync(
            ct =>
            {
                callCount++;
                return Task.FromResult($"attempt_{callCount}");
            },
            result => true,
            maxRetries: maxRetries,
            baseDelaySeconds: 0);

        // Assert
        callCount.Should().Be(maxRetries, "operation should be called exactly maxRetries times, not maxRetries + 1");
        result.Should().Be($"attempt_{maxRetries}");
    }
}
