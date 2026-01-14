// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Represents shared Azure resources configuration for a team of agents.
/// These resources are common across all agents in the team.
/// </summary>
public class TeamSharedResources
{
    /// <summary>
    /// Azure AD Tenant ID where resources will be created.
    /// </summary>
    [JsonPropertyName("tenantId")]
    public string TenantId { get; init; } = string.Empty;

    /// <summary>
    /// Client Application ID for interactive authentication with Microsoft Graph.
    /// </summary>
    [JsonPropertyName("clientAppId")]
    public string ClientAppId { get; init; } = string.Empty;

    /// <summary>
    /// Azure Subscription ID for resource deployment.
    /// </summary>
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; init; } = string.Empty;

    /// <summary>
    /// Azure Resource Group name where all team resources will be deployed.
    /// </summary>
    [JsonPropertyName("resourceGroup")]
    public string ResourceGroup { get; init; } = string.Empty;

    /// <summary>
    /// Azure region for resource deployment (e.g., "westus", "eastus").
    /// </summary>
    [JsonPropertyName("location")]
    public string Location { get; init; } = string.Empty;

    /// <summary>
    /// Name of the shared App Service Plan for hosting team agent web apps.
    /// </summary>
    [JsonPropertyName("appServicePlanName")]
    public string AppServicePlanName { get; init; } = string.Empty;

    /// <summary>
    /// App Service Plan SKU/pricing tier (e.g., "B1", "S1", "P1v2").
    /// Optional - defaults to "B1" if not specified.
    /// </summary>
    [JsonPropertyName("appServicePlanSku")]
    public string? AppServicePlanSku { get; init; }

    /// <summary>
    /// Target environment for Agent 365 services (test, preprod, prod).
    /// Optional - defaults to "prod" if not specified.
    /// </summary>
    [JsonPropertyName("environment")]
    public string? Environment { get; init; }

    /// <summary>
    /// Two-letter country code for agent users' usage location (required for license assignment).
    /// Optional - defaults to "US" if not specified.
    /// </summary>
    [JsonPropertyName("agentUserUsageLocation")]
    public string? AgentUserUsageLocation { get; init; }
}
