// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Exception thrown when MCP server schema evaluation fails.
/// Covers schema discovery errors, checklist generation errors,
/// and report generation errors.
/// </summary>
public sealed class EvaluationException : Agent365Exception
{
    public override int ExitCode => 3;

    public EvaluationException(
        string errorCode,
        string issueDescription,
        List<string>? errorDetails = null,
        List<string>? mitigationSteps = null,
        Dictionary<string, string>? context = null,
        Exception? innerException = null)
        : base(
            errorCode: errorCode,
            issueDescription: issueDescription,
            errorDetails: errorDetails,
            mitigationSteps: mitigationSteps,
            context: context,
            innerException: innerException)
    {
    }
}
