// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Exception thrown when Azure CLI authentication fails or is missing.
/// This is a USER ERROR - user needs to authenticate.
/// </summary>
public class AzureAuthenticationException : Agent365Exception
{
    public AzureAuthenticationException(string reason)
        : base(
            errorCode: "AZURE_AUTH_FAILED",
            issueDescription: "Azure CLI authentication failed",
            errorDetails: new List<string> { reason },
            mitigationSteps: new List<string>
            {
                "Ensure Azure CLI is installed: https://aka.ms/azure-cli",
                "Run 'az login' to authenticate",
                "Verify your account has the required permissions",
                "Run 'a365 setup' again"
            })
    {
    }

    public override int ExitCode => 3; // Authentication error
}

/// <summary>
/// Exception thrown when Azure resource creation/update fails.
/// Could be user error (permissions) or system error (Azure outage).
/// </summary>
public class AzureResourceException : Agent365Exception
{
    public string ResourceType { get; }
    public string ResourceName { get; }

    public AzureResourceException(
        string resourceType,
        string resourceName,
        string reason,
        bool isPermissionIssue = false)
        : base(
            errorCode: isPermissionIssue ? "AZURE_PERMISSION_DENIED" : "AZURE_RESOURCE_FAILED",
            issueDescription: $"Failed to create/update {resourceType}: {resourceName}",
            errorDetails: new List<string> { reason },
            mitigationSteps: BuildMitigation(resourceType, isPermissionIssue))
    {
        ResourceType = resourceType;
        ResourceName = resourceName;
    }

    private static List<string> BuildMitigation(string resourceType, bool isPermissionIssue)
    {
        if (isPermissionIssue)
        {
            return new List<string>
            {
                "Check your Azure subscription permissions",
                $"Ensure you have Contributor or Owner role on the subscription",
                "Contact your Azure administrator if needed",
                "Run 'az account show' to verify your account"
            };
        }

        return new List<string>
        {
            $"Check Azure portal for {resourceType} status",
            "Verify your subscription is active and has available quota",
            "Try again in a few minutes (transient Azure issues)",
            "Check Azure status page: https://status.azure.com"
        };
    }

    public override int ExitCode => 4; // Resource operation error
    public override bool IsUserError => false; // Could be Azure service issue
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
                    "You need Application.ReadWrite.All permission for agent blueprint creation",
                    "Contact your tenant administrator to grant permissions",
                    "See documentation: https://aka.ms/agent365-permissions"
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
