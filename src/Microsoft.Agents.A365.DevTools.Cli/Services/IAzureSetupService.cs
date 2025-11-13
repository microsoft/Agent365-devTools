// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Handles Azure resource provisioning including App Service, Managed Identity, and Resource Groups.
/// C# equivalent of key portions from a365-setup.ps1
/// </summary>
public interface IAzureSetupService
{
    /// <summary>
    /// Runs the complete Azure setup workflow:
    /// 1. Create/verify resource group
    /// 2. Create/verify App Service Plan
    /// 3. Create/verify Web App with .NET 8 runtime
    /// 4. Assign system-managed identity
    /// </summary>
    Task<SetupResult> RunSetupAsync(Agent365Config config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or verifies an Azure Resource Group exists.
    /// </summary>
    Task<ResourceGroupResult> EnsureResourceGroupAsync(string subscriptionId, string resourceGroupName, string location, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or verifies an App Service Plan exists.
    /// </summary>
    Task<AppServicePlanResult> EnsureAppServicePlanAsync(string subscriptionId, string resourceGroupName, string planName, string sku, string location, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or verifies a Web App exists with the specified runtime.
    /// </summary>
    Task<WebAppResult> EnsureWebAppAsync(string subscriptionId, string resourceGroupName, string planName, string webAppName, string runtime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns or verifies system-managed identity for the Web App.
    /// </summary>
    Task<ManagedIdentityResult> EnsureManagedIdentityAsync(string subscriptionId, string resourceGroupName, string webAppName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of setup operations
/// </summary>
public class SetupResult
{
    public bool Success { get; set; }
    public string? ManagedIdentityPrincipalId { get; set; }
    public string? ErrorMessage { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class ResourceGroupResult
{
    public bool Success { get; set; }
    public bool AlreadyExisted { get; set; }
    public string? ErrorMessage { get; set; }
}

public class AppServicePlanResult
{
    public bool Success { get; set; }
    public bool AlreadyExisted { get; set; }
    public string? ErrorMessage { get; set; }
}

public class WebAppResult
{
    public bool Success { get; set; }
    public bool AlreadyExisted { get; set; }
    public string? WebAppUrl { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ManagedIdentityResult
{
    public bool Success { get; set; }
    public string? PrincipalId { get; set; }
    public bool AlreadyExisted { get; set; }
    public string? ErrorMessage { get; set; }
}
