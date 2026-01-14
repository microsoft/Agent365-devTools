// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Represents an individual agent configuration within a team.
/// Contains agent-specific properties that supplement the team's shared resources.
/// </summary>
public class TeamAgentConfig
{
    /// <summary>
    /// Unique name for the agent within the team (alphanumeric, no spaces).
    /// Used for generating resource names: {team-name}-{agent-name}-*
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Display name for the agent (human-readable).
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Optional description of the agent's role or capabilities.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Display name for the agent identity in Azure AD.
    /// </summary>
    [JsonPropertyName("agentIdentityDisplayName")]
    public string AgentIdentityDisplayName { get; init; } = string.Empty;

    /// <summary>
    /// User Principal Name (UPN) for the agentic user to be created in Azure AD.
    /// </summary>
    [JsonPropertyName("agentUserPrincipalName")]
    public string AgentUserPrincipalName { get; init; } = string.Empty;

    /// <summary>
    /// Display name for the agentic user to be created in Azure AD.
    /// </summary>
    [JsonPropertyName("agentUserDisplayName")]
    public string AgentUserDisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Relative or absolute path to the agent project directory for deployment.
    /// </summary>
    [JsonPropertyName("deploymentProjectPath")]
    public string DeploymentProjectPath { get; init; } = string.Empty;

    /// <summary>
    /// Optional display name for the agent blueprint application.
    /// Used for manifest updates and Teams app registration.
    /// If not specified, defaults to agent identity display name.
    /// </summary>
    [JsonPropertyName("agentBlueprintDisplayName")]
    public string? AgentBlueprintDisplayName { get; init; }

    /// <summary>
    /// Merges this agent configuration with team shared resources to create a complete Agent365Config.
    /// </summary>
    /// <param name="teamName">The name of the team this agent belongs to</param>
    /// <param name="sharedResources">The team's shared Azure resources</param>
    /// <param name="managerEmail">The team manager's email address</param>
    /// <returns>A fully populated Agent365Config ready for deployment</returns>
    public Agent365Config ToAgent365Config(string teamName, TeamSharedResources sharedResources, string? managerEmail)
    {
        // Generate resource names with team-agent pattern
        var webAppName = $"{teamName}-{Name}-webapp";

        return new Agent365Config
        {
            // From shared resources
            TenantId = sharedResources.TenantId,
            ClientAppId = sharedResources.ClientAppId,
            SubscriptionId = sharedResources.SubscriptionId,
            ResourceGroup = sharedResources.ResourceGroup,
            Location = sharedResources.Location,
            AppServicePlanName = sharedResources.AppServicePlanName,
            AppServicePlanSku = sharedResources.AppServicePlanSku ?? "B1",
            Environment = sharedResources.Environment ?? "prod",
            AgentUserUsageLocation = sharedResources.AgentUserUsageLocation ?? "US",

            // From agent-specific config
            WebAppName = webAppName,
            AgentIdentityDisplayName = AgentIdentityDisplayName,
            AgentBlueprintDisplayName = AgentBlueprintDisplayName ?? AgentIdentityDisplayName,
            AgentUserPrincipalName = AgentUserPrincipalName,
            AgentUserDisplayName = AgentUserDisplayName,
            DeploymentProjectPath = DeploymentProjectPath,

            // From team
            ManagerEmail = managerEmail,

            // Defaults
            NeedDeployment = true
        };
    }
}
