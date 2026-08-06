// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// Represents a single validation issue.
/// </summary>
public sealed record ValidationIssue(
    string Code,
    string Message,
    ValidationSeverity Severity = ValidationSeverity.Error);
