// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Exception thrown when a retry operation exhausts all attempts without success.
/// This indicates a persistent failure condition that could not be resolved through retries.
/// </summary>
public class RetryExhaustedException : Agent365Exception
{
    public int MaxRetries { get; }
    public string Operation { get; }

    public RetryExhaustedException(string operation, int maxRetries, string reason)
        : base(
            errorCode: ErrorCodes.RetryExhausted,
            issueDescription: $"Operation failed after {maxRetries} retry attempts: {operation}",
            errorDetails: new List<string> { reason },
            mitigationSteps: new List<string>
            {
                "Check your network connectivity",
                "Verify the target service is accessible and operational",
                "Check Azure service health at https://status.azure.com",
                "Try again in a few minutes"
            })
    {
        Operation = operation;
        MaxRetries = maxRetries;
    }

    public override int ExitCode => 6; // Retry exhaustion error
    public override bool IsUserError => false; // This is a transient/infrastructure error
}
