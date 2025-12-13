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
            errorCode: isPermissionIssue ? ErrorCodes.AzurePermissionDenied : ErrorCodes.AzureResourceFailed,
            issueDescription: $"Failed to create/update {resourceType}: {resourceName}",
            errorDetails: new List<string> { reason },
            mitigationSteps: BuildMitigation(resourceType, isPermissionIssue, reason))
    {
        ResourceType = resourceType;
        ResourceName = resourceName;
    }

    public AzureResourceException(
        string errorCode,
        string resourceType,
        string resourceName,
        string reason,
        List<string> mitigationSteps)
        : base(
            errorCode: errorCode,
            issueDescription: $"Failed to create/update {resourceType}: {resourceName}",
            errorDetails: new List<string> { reason },
            mitigationSteps: mitigationSteps)
    {
        ResourceType = resourceType;
        ResourceName = resourceName;
    }

    private static List<string> BuildMitigation(string resourceType, bool isPermissionIssue, string reason)
    {
        if (isPermissionIssue)
        {
            return new List<string>
            {
                "Check your Azure subscription permissions",
                $"Ensure you have Contributor or Owner role on the subscription or at least the Resource Group",
                "Contact your Azure administrator if needed",
                "Run 'az account show' to verify your account"
            };
        }

        // Check for web app name collision
        if (reason.Contains("already taken", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("globally unique", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string>
            {
                "Web app names must be globally unique across all Azure subscriptions",
                "Update the 'webAppName' in your a365.config.json to a different value",
                "Consider adding a unique suffix like your organization name or random characters"
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

/// <summary>
/// Exception thrown when App Service Plan creation or configuration fails.
/// Provides specific mitigation steps based on the error type (quota, SKU, permissions).
/// </summary>
public class AzureAppServicePlanException : Agent365Exception
{
    public string PlanName { get; }
    public string? Location { get; }
    public string? Sku { get; }
    public AppServicePlanErrorType ErrorType { get; }

    public AzureAppServicePlanException(
        string planName,
        string location,
        string sku,
        AppServicePlanErrorType errorType,
        string errorDetails)
        : base(
            errorCode: GetErrorCode(errorType),
            issueDescription: GetIssueDescription(planName, location, sku, errorType),
            errorDetails: new List<string> { errorDetails },
            mitigationSteps: GetMitigationSteps(errorType, location, sku))
    {
        PlanName = planName;
        Location = location;
        Sku = sku;
        ErrorType = errorType;
    }

    private static string GetErrorCode(AppServicePlanErrorType errorType) => errorType switch
    {
        AppServicePlanErrorType.QuotaExceeded => "APPSERVICE_QUOTA_EXCEEDED",
        AppServicePlanErrorType.SkuNotAvailable => "APPSERVICE_SKU_NOT_AVAILABLE",
        AppServicePlanErrorType.AuthorizationFailed => "APPSERVICE_PERMISSION_DENIED",
        AppServicePlanErrorType.VerificationTimeout => "APPSERVICE_VERIFICATION_TIMEOUT",
        _ => "APPSERVICE_CREATION_FAILED"
    };

    private static string GetIssueDescription(string planName, string location, string sku, AppServicePlanErrorType errorType)
    {
        var locationDisplay = string.IsNullOrWhiteSpace(location) ? "(not specified)" : location;
        var skuDisplay = string.IsNullOrWhiteSpace(sku) ? "(not specified)" : sku;

        return errorType switch
        {
            AppServicePlanErrorType.QuotaExceeded => $"Cannot create App Service Plan '{planName}' (SKU: {skuDisplay}, Region: {locationDisplay}) - Azure quota limit exceeded",
            AppServicePlanErrorType.SkuNotAvailable => $"Cannot create App Service Plan '{planName}' (SKU: {skuDisplay}, Region: {locationDisplay}) - SKU not available in this region",
            AppServicePlanErrorType.AuthorizationFailed => $"Cannot create App Service Plan '{planName}' (Region: {locationDisplay}) - insufficient permissions",
            AppServicePlanErrorType.VerificationTimeout => $"App Service Plan '{planName}' (Region: {locationDisplay}) creation succeeded but verification timed out",
            _ => $"Failed to create App Service Plan '{planName}' (SKU: {skuDisplay}, Region: {locationDisplay})"
        };
    }

    private static List<string> GetMitigationSteps(AppServicePlanErrorType errorType, string location, string sku)
    {
        return errorType switch
        {
            AppServicePlanErrorType.QuotaExceeded => ErrorMessages.GetQuotaExceededMitigation(location),
            AppServicePlanErrorType.SkuNotAvailable => ErrorMessages.GetSkuNotAvailableMitigation(location, sku),
            AppServicePlanErrorType.AuthorizationFailed => ErrorMessages.GetAuthorizationFailedMitigation(),
            AppServicePlanErrorType.VerificationTimeout => ErrorMessages.GetVerificationTimeoutMitigation(),
            _ => ErrorMessages.GetGenericAppServicePlanMitigation()
        };
    }

    public override int ExitCode => 4; // Resource operation error
    public override bool IsUserError => ErrorType != AppServicePlanErrorType.VerificationTimeout; // Verification timeout is likely an Azure issue
}

/// <summary>
/// Types of errors that can occur when creating an App Service Plan
/// </summary>
public enum AppServicePlanErrorType
{
    QuotaExceeded,
    SkuNotAvailable,
    AuthorizationFailed,
    VerificationTimeout,
    Other
}
