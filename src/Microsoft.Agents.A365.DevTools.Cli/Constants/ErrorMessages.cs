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
            "Your Azure subscription has reached its quota limit for App Service Plans in this SKU tier",
            "Option 1: Request a quota increase in Azure Portal > Subscriptions > Usage + quotas",
            "Option 2: Use a Free tier (F1) for development/testing by updating 'planSku' to 'F1' in a365.config.json",
            "Option 3: Use a different Azure subscription with available quota",
            "Option 4: Delete unused App Service Plans to free up quota",
            $"Option 5: Try a different region - update 'location' in a365.config.json (current: {locationDisplay})",
            "Learn more: https://learn.microsoft.com/azure/app-service/app-service-plan-manage#quotas"
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
            $"The SKU '{skuDisplay}' is not available in region '{locationDisplay}'",
            "Option 1: Change the 'planSku' in a365.config.json to a supported SKU (F1, B1, B2, S1, S2, P1V2, P2V2)",
            $"Option 2: Change the 'location' in a365.config.json to a region that supports '{skuDisplay}'",
            "Option 3: Use Free tier (F1) for development/testing",
            "Check SKU availability: https://azure.microsoft.com/pricing/details/app-service/"
        };
    }

    /// <summary>
    /// Gets mitigation steps for App Service Plan authorization failed errors.
    /// </summary>
    public static List<string> GetAuthorizationFailedMitigation()
    {
        return new List<string>
        {
            "You don't have sufficient permissions to create App Service Plans in this subscription or resource group",
            "Required role: Contributor or Owner on the subscription or resource group",
            "Check your current role: Run 'az role assignment list --assignee $(az account show --query user.name -o tsv) --all'",
            "Contact your Azure administrator to grant the required permissions",
            "Verify you're using the correct subscription: 'az account show'"
        };
    }

    /// <summary>
    /// Gets mitigation steps for App Service Plan verification timeout errors.
    /// </summary>
    public static List<string> GetVerificationTimeoutMitigation()
    {
        return new List<string>
        {
            "The App Service Plan was created but is taking longer than expected to appear in Azure",
            "This usually indicates an Azure propagation delay or regional issue",
            "Option 1: Wait a few minutes and check Azure Portal to confirm the plan exists",
            "Option 2: If the plan exists in Portal, run the setup command again (it will skip creation)",
            "Option 3: If the plan doesn't exist after 5+ minutes, delete the resource group and retry",
            "Check Azure status: https://status.azure.com"
        };
    }

    /// <summary>
    /// Gets generic mitigation steps for App Service Plan creation failures.
    /// </summary>
    public static List<string> GetGenericAppServicePlanMitigation()
    {
        return new List<string>
        {
            "App Service Plan creation failed due to an unexpected Azure error",
            "Option 1: Check the error details above for specific Azure error messages",
            "Option 2: Verify your Azure subscription is active and has no billing issues",
            "Option 3: Try a different region by updating 'location' in a365.config.json",
            "Option 4: Check Azure service health: https://status.azure.com",
            "Learn more: https://learn.microsoft.com/azure/app-service/app-service-plan-manage"
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
}
