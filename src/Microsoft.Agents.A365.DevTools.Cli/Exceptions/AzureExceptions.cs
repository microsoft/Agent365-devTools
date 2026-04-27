// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Exception thrown when Azure CLI authentication fails or is missing.
/// This is a USER ERROR - user needs to authenticate.
/// </summary>
public class AzureAuthenticationException : Agent365Exception
{
    public AzureAuthenticationException(string reason)
        : base(
            errorCode: ErrorCodes.AzureAuthFailed,
            issueDescription: "Azure CLI authentication failed",
            errorDetails: new List<string> { reason },
            mitigationSteps: new List<string>
            {
                "Ensure Azure CLI is installed: https://aka.ms/azure-cli",
                "Run 'az login' to authenticate",
                "Verify your account has the required permissions",
                "Run 'a365 setup all' again"
            })
    {
    }

    public override int ExitCode => 3; // Authentication error
}

/// <summary>
/// Exception thrown when Microsoft Graph API operations fail.
/// </summary>
public class GraphApiException : Agent365Exception
{
    public string Operation { get; }

    public GraphApiException(string operation, string reason, bool isPermissionIssue = false)
        : base(
            errorCode: isPermissionIssue ? "GRAPH_PERMISSION_DENIED" : "GRAPH_API_FAILED",
            issueDescription: $"Microsoft Graph API operation failed: {operation}",
            errorDetails: new List<string> { reason },
            mitigationSteps: isPermissionIssue
                ? new List<string>
                {
                    "Ensure you have the required Graph API permissions",
                    "You need AgentIdentityBlueprint.ReadWrite.All permission for agent blueprint creation",
                    "Contact your tenant administrator to grant permissions",
                    $"See documentation: {ConfigConstants.CustomClientAppRegistrationUrl}"
                }
                : new List<string>
                {
                    "Check your network connection",
                    "Verify Microsoft Graph API status: https://status.cloud.microsoft",
                    "Try again in a few minutes",
                    "Run 'az login' to refresh authentication"
                })
    {
        Operation = operation;
    }

    public override int ExitCode => 5; // Graph API error
}
