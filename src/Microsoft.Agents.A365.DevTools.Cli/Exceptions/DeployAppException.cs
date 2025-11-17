// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Exception thrown during app deployment.
/// </summary>
public class DeployAppException : Agent365Exception
{
    private const string DeployAppIssueDescription = "App Deployment failed";

    public DeployAppException(string reason, Exception? innerException = null)
        : base(
            errorCode: ErrorCodes.DeploymentAppFailed,
            issueDescription: DeployAppIssueDescription,
            errorDetails: new List<string> { reason },
            mitigationSteps: new List<string>
            {
                "Please review the logs and retry the deployment",
            },
            innerException: innerException)
    {
    }

    public override int ExitCode => 1; // General deployment error
}
