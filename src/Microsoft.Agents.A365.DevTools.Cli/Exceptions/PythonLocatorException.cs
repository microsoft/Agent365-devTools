// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Exception thrown when Python Locator fails to find a valid Python installation.
/// </summary>
public class PythonLocatorException : Agent365Exception
{
    private const string PythonLocatorIssueDescription = "Python Locator failed";

    public PythonLocatorException(string reason)
        : base(
            errorCode: ErrorCodes.PythonNotFound,
            issueDescription: PythonLocatorIssueDescription,
            errorDetails: new List<string> { reason },
            mitigationSteps: new List<string>
            {
                "Python not found. Please install Python from https://www.python.org/. If you have already installed it, please include it in path",
                "Ensure pip is installed with Python",
            })
    {
    }

    public override int ExitCode => 2; // Configuration error
}
