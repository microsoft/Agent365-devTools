// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

public class DeployMcpException : Agent365Exception
{
    private const string Issue = "Deploy MCP operation failed";

    public DeployMcpException(string reason, Exception? innerException = null)
        : base(
            errorCode: ErrorCodes.DeploymentMcpFailed,
            issueDescription: Issue,
            errorDetails: new List<string> { reason },
            mitigationSteps: new List<string>
            {
                "Verify Azure authentication and tenant",
                "Ensure the blueprint exists and the account has sufficient permissions",
                "Retry with 'a365 deploy mcp' after resolving authentication issues"
            },
            innerException: innerException)
    {
    }

    public override int ExitCode => 3;
}
