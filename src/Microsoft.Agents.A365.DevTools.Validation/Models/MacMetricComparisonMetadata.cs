// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// Comparison details for a single MAC metric.
/// </summary>
public sealed class MacMetricComparisonMetadata
{
    /// <summary>Canonical metric key (e.g., kpi.invocations.rl7).</summary>
    public string MetricKey { get; init; } = string.Empty;

    /// <summary>Baseline value.</summary>
    public double Before { get; init; }

    /// <summary>Post-conversation value.</summary>
    public double After { get; init; }

    /// <summary>After - Before.</summary>
    public double Delta { get; init; }

    /// <summary>True when delta is positive.</summary>
    public bool Increased { get; init; }

    /// <summary>True when this metric is the exception-rate metric.</summary>
    public bool IsExceptionRate { get; init; }

    /// <summary>Final pass/fail for this metric after applying rule exceptions.</summary>
    public bool Passed { get; init; }

    /// <summary>Human-readable reason for this comparison result.</summary>
    public string? Reason { get; init; }
}
