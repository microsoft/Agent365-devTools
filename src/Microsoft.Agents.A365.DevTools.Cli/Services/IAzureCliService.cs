// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for interacting with Azure CLI to fetch account information, resource groups, and other Azure data
/// </summary>
public interface IAzureCliService
{
    /// <summary>
    /// Gets the current Azure account information from Azure CLI
    /// </summary>
    /// <returns>Current Azure account info or null if not logged in</returns>
    Task<AzureAccountInfo?> GetCurrentAccountAsync();

    /// <summary>
    /// Lists all resource groups in the current subscription
    /// </summary>
    /// <returns>List of resource groups</returns>
    Task<List<AzureResourceGroup>> ListResourceGroupsAsync();

    /// <summary>
    /// Lists all app service plans in the current subscription
    /// </summary>
    /// <returns>List of app service plans</returns>
    Task<List<AzureAppServicePlan>> ListAppServicePlansAsync();

    /// <summary>
    /// Lists all available Azure locations
    /// </summary>
    /// <returns>List of available locations</returns>
    Task<List<AzureLocation>> ListLocationsAsync();

    /// <summary>
    /// Checks if Azure CLI is available and user is logged in
    /// </summary>
    /// <returns>True if Azure CLI is available and logged in</returns>
    Task<bool> IsLoggedInAsync();
}
