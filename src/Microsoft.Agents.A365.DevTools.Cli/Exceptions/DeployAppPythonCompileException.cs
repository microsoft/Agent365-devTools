// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Exception thrown during app deployment.
/// </summary>
public class DeployAppPythonCompileException : Agent365Exception
{
    private const string DeployAppCompileFailureDescription = "py_compile failure";
    public override bool IsUserError => true;

    public DeployAppPythonCompileException(string reason)
        : base(
            errorCode: ErrorCodes.DeploymentAppCompileFailed,
            issueDescription: DeployAppCompileFailureDescription,
            errorDetails: new List<string> { reason },
            mitigationSteps: new List<string>
            {
                "Please fix the python files and try again.",
            })
    {
    }
    public override int ExitCode => 1; // Compilation errors in user's code
}
