// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Validation errors that occur during `a365 setup all` (user-fixable issues).
/// </summary>
public sealed class SetupValidationException : Agent365Exception
{
    public override int ExitCode => 2;

    public SetupValidationException(
        string issueDescription,
        List<string>? errorDetails = null,
        List<string>? mitigationSteps = null,
        Dictionary<string, string>? context = null,
        Exception? innerException = null)
        : base(
            errorCode: ErrorCodes.SetupValidationFailed,
            issueDescription: issueDescription,
            errorDetails: errorDetails,
            mitigationSteps: mitigationSteps,
            context: context,
            innerException: innerException)
    {
    }
}