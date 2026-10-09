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
        : this(
            operation,
            isPermissionIssue ? ErrorCodes.GraphPermissionDenied : ErrorCodes.GraphApiFailed,
            new List<string> { reason },
            isPermissionIssue
                ? new List<string>
                {
                    "Ensure you have the required Graph API permissions",
                    "You need AgentIdentityBlueprint.ReadWrite.All permission for agent blueprint creation",
                    "Contact your tenant administrator to grant permissions",
                    $"See documentation: {ConfigConstants.CustomClientAppRegistrationUrl}"
                }
                : DefaultMitigationSteps())
    {
    }

    /// <summary>
    /// Creates a Graph failure with an operation-specific error code, details, mitigation steps, and optional context lines.
    /// </summary>
    public GraphApiException(
        string operation,
        string errorCode,
        List<string> errorDetails,
        List<string> mitigationSteps,
        Dictionary<string, string>? context = null)
        : base(
            errorCode: errorCode,
            issueDescription: $"Microsoft Graph API operation failed: {operation}",
            errorDetails: errorDetails,
            mitigationSteps: mitigationSteps,
            context: context)
    {
        Operation = operation;
    }

    /// <summary>Mitigation steps for Graph failures that are not tied to a specific cause.</summary>
    public static List<string> DefaultMitigationSteps() => new()
    {
        "Check your network connection",
        "Verify Microsoft Graph API status: https://status.cloud.microsoft",
        "Try again in a few minutes",
        "Run 'az login' to refresh authentication"
    };

    public override int ExitCode => 5; // Graph API error
}
