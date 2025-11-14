// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Azure account information from Azure CLI
/// </summary>
public class AzureAccountInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public AzureUser User { get; set; } = new();
    public string State { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}

/// <summary>
/// Azure user information
/// </summary>
public class AzureUser
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// Azure resource group information
/// </summary>
public class AzureResourceGroup
{
    public string Name { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
}

/// <summary>
/// Azure app service plan information
/// </summary>
public class AzureAppServicePlan
{
    public string Name { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
}

/// <summary>
/// Azure location information
/// </summary>
public class AzureLocation
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RegionalDisplayName { get; set; } = string.Empty;
}
