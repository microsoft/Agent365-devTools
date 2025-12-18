// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Constants;

/// <summary>
/// Centralized error and warning messages for Agent365 CLI.
/// Provides consistent, user-friendly messaging across commands.
/// </summary>
public static class ErrorMessages
{
    #region App Service Plan Messages

    /// <summary>
    /// Gets mitigation steps for App Service Plan quota exceeded errors.
    /// </summary>
    public static List<string> GetQuotaExceededMitigation(string location)
    {
        var locationDisplay = string.IsNullOrWhiteSpace(location) ? "(not specified)" : location;
        
        return new List<string>
        {
            "Your Azure subscription has reached its quota limit for App Service Plans in this SKU tier.",
            "For development/testing, update 'planSku' to 'F1' in a365.config.json to use Free tier.",
            "For production, request quota increase at: Azure Portal > Subscriptions > Usage + quotas",
            $"Current location: {locationDisplay}. Consider trying a different region if quota is unavailable."
        };
    }

    /// <summary>
    /// Gets mitigation steps for App Service Plan SKU not available errors.
    /// </summary>
    public static List<string> GetSkuNotAvailableMitigation(string location, string sku)
    {
        var locationDisplay = string.IsNullOrWhiteSpace(location) ? "(not specified)" : location;
        var skuDisplay = string.IsNullOrWhiteSpace(sku) ? "(not specified)" : sku;
        
        return new List<string>
        {
            $"SKU '{skuDisplay}' is not available in region '{locationDisplay}'.",
            "Update 'planSku' in a365.config.json to a supported SKU: F1, B1, B2, S1, S2, P1V2, P2V2",
            $"Or change 'location' in a365.config.json to a region that supports '{skuDisplay}'."
        };
    }

    /// <summary>
    /// Gets mitigation steps for App Service Plan authorization failed errors.
    /// </summary>
    public static List<string> GetAuthorizationFailedMitigation()
    {
        return new List<string>
        {
            "Insufficient permissions to create App Service Plans in this subscription or resource group.",
            "Required role: Contributor or Owner on the subscription or resource group.",
            "Contact your Azure administrator to grant the required permissions."
        };
    }

    /// <summary>
    /// Gets mitigation steps for App Service Plan verification timeout errors.
    /// </summary>
    public static List<string> GetVerificationTimeoutMitigation()
    {
        return new List<string>
        {
            "App Service Plan creation is taking longer than expected.",
            "Wait a few minutes and check Azure Portal to confirm the plan exists.",
            "If the plan exists, run the command again (it will skip creation).",
            "If issues persist, check Azure service status at https://status.azure.com"
        };
    }

    /// <summary>
    /// Gets generic mitigation steps for App Service Plan creation failures.
    /// </summary>
    public static List<string> GetGenericAppServicePlanMitigation()
    {
        return new List<string>
        {
            "App Service Plan creation failed.",
            "Verify your Azure subscription is active and has no billing issues.",
            "Try a different region by updating 'location' in a365.config.json.",
            "Check Azure service status at https://status.azure.com"
        };
    }

    #endregion

    #region Azure Authentication Messages

    public const string AzureCliNotAuthenticated = 
        "You are not logged in to Azure CLI. Please run 'az login' and select your subscription, then try again";

    public const string AzureCliInstallRequired = 
        "Azure CLI is not installed. Install from: https://aka.ms/azure-cli";

    #endregion

    #region Configuration Messages

    public const string ConfigFileNotFound = 
        "Configuration file not found. Run 'a365 config init' to create one";

    public const string InvalidConfigFormat = 
        "Configuration file has invalid JSON format";

    #endregion

    #region Client App Validation Messages

    public const string ClientAppValidationFailed = 
        "Client app validation FAILED:";

    public const string ClientAppValidationFixHeader = 
        "To fix this:";

    #endregion

    #region MOS Token and Prerequisites Messages

    public const string MosClientAppIdMissing = 
        "Custom client app ID not found in configuration. Run 'a365 config init' first.";

    public const string MosClientAppNotFound = 
        "Custom client app not found in tenant. Verify the app exists and you have access.";

    public const string MosTokenAcquisitionFailed = 
        "Failed to acquire MOS token. Check your authentication and permissions.";

    public const string MosAdminConsentRequired = 
        "Admin consent required for MOS API permissions. Visit the Azure Portal to grant consent.";

    /// <summary>
    /// Gets mitigation steps for MOS service principal creation failures.
    /// </summary>
    public static List<string> GetMosServicePrincipalMitigation(string appId)
    {
        return new List<string>
        {
            $"Insufficient privileges to create service principal for {appId}.",
            "Required role: Application Administrator, Cloud Application Administrator, or Global Administrator.",
            $"Ask your tenant administrator to run: az ad sp create --id {appId}"
        };
    }

    /// <summary>
    /// Gets mitigation steps for first-party client app service principal creation.
    /// </summary>
    public static List<string> GetFirstPartyClientAppServicePrincipalMitigation()
    {
        return new List<string>
        {
            "Insufficient privileges to create service principal for Microsoft first-party client app.",
            "This app is required for MOS token acquisition.",
            "Required role: Application Administrator, Cloud Application Administrator, or Global Administrator.",
            $"Ask your tenant administrator to run: az ad sp create --id {MosConstants.TpsAppServicesClientAppId}"
        };
    }

    /// <summary>
    /// Gets mitigation steps for all MOS resource app service principals.
    /// </summary>
    public static List<string> GetMosResourceAppsServicePrincipalMitigation()
    {
        return new List<string>
        {
            "Insufficient privileges to create service principals for MOS resource applications.",
            "Required role: Application Administrator, Cloud Application Administrator, or Global Administrator.",
            "Ask your tenant administrator to run:",
            "  az ad sp create --id 6ec511af-06dc-4fe2-b493-63a37bc397b1",
            "  az ad sp create --id 8578e004-a5c6-46e7-913e-12f58912df43",
            "  az ad sp create --id e8be65d6-d430-4289-a665-51bf2a194bda"
        };
    }

    /// <summary>
    /// Gets mitigation steps for MOS admin consent issues.
    /// </summary>
    public static List<string> GetMosAdminConsentMitigation(string clientAppId)
    {
        return new List<string>
        {
            "Admin consent required for MOS API permissions.",
            $"Grant consent at: https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/{clientAppId}",
            "Click 'Grant admin consent for [Your Organization]' and wait 1-2 minutes for propagation."
        };
    }

    #endregion
}
