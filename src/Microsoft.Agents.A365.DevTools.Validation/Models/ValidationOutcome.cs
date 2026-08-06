// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// Represents the outcome of a validation operation.
/// </summary>
public sealed class ValidationOutcome
{
    public bool IsValid { get; init; }

    public int ExitCode { get; init; }

    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];

    public static ValidationOutcome Success() => new()
    {
        IsValid = true,
        ExitCode = 0
    };

    public static ValidationOutcome Failure(params ValidationIssue[] issues) => new()
    {
        IsValid = false,
        ExitCode = 1,
        Issues = issues
    };
}
