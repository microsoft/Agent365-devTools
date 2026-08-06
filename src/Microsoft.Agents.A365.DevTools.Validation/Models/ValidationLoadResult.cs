// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// Result of loading configuration for validation.
/// </summary>
public sealed record ValidationLoadResult<TConfig>
{
    public bool IsSuccess { get; init; }

    public TConfig? Value { get; init; }

    public int ExitCode { get; init; }

    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];

    public static ValidationLoadResult<TConfig> Success(TConfig value) => new()
    {
        IsSuccess = true,
        Value = value,
        ExitCode = 0
    };

    public static ValidationLoadResult<TConfig> Failure(int exitCode, params ValidationIssue[] issues) => new()
    {
        IsSuccess = false,
        ExitCode = exitCode,
        Issues = issues
    };
}
