// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Represents a complete team configuration including shared resources and individual agents.
/// </summary>
public class TeamConfig
{
    /// <summary>
    /// Unique name for the team (alphanumeric, no spaces).
    /// Used as prefix for all generated resource names: {team-name}-{agent-name}-*
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Display name for the team (human-readable).
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Optional description of the team's purpose or capabilities.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Email address of the team manager who oversees all agents in this team.
    /// </summary>
    [JsonPropertyName("managerEmail")]
    public string? ManagerEmail { get; init; }

    /// <summary>
    /// Shared Azure resources used by all agents in the team.
    /// </summary>
    [JsonPropertyName("sharedResources")]
    public TeamSharedResources? SharedResources { get; init; }

    /// <summary>
    /// List of agents that belong to this team.
    /// </summary>
    [JsonPropertyName("agents")]
    public List<TeamAgentConfig>? Agents { get; init; }

    /// <summary>
    /// Validates the team configuration and returns a list of validation errors.
    /// </summary>
    /// <returns>List of validation error messages, or empty list if valid</returns>
    public List<string> Validate()
    {
        var errors = new List<string>();

        // Validate team name
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("Team name is required");
        }
        else if (!System.Text.RegularExpressions.Regex.IsMatch(Name, "^[a-zA-Z0-9-]+$"))
        {
            errors.Add("Team name must be alphanumeric with hyphens only (no spaces or special characters)");
        }

        // Validate display name
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            errors.Add("Team display name is required");
        }

        // Validate manager email
        if (!string.IsNullOrWhiteSpace(ManagerEmail))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(ManagerEmail, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            {
                errors.Add($"Manager email '{ManagerEmail}' is not a valid email address");
            }
        }

        // Validate shared resources
        if (SharedResources == null)
        {
            errors.Add("Shared resources are required");
        }
        else
        {
            // Validate shared resource properties
            if (string.IsNullOrWhiteSpace(SharedResources.TenantId))
            {
                errors.Add("Shared resources: TenantId is required");
            }
            else if (!Guid.TryParse(SharedResources.TenantId, out _))
            {
                errors.Add($"Shared resources: TenantId '{SharedResources.TenantId}' is not a valid GUID");
            }

            if (string.IsNullOrWhiteSpace(SharedResources.ClientAppId))
            {
                errors.Add("Shared resources: ClientAppId is required");
            }
            else if (!Guid.TryParse(SharedResources.ClientAppId, out _))
            {
                errors.Add($"Shared resources: ClientAppId '{SharedResources.ClientAppId}' is not a valid GUID");
            }

            if (string.IsNullOrWhiteSpace(SharedResources.SubscriptionId))
            {
                errors.Add("Shared resources: SubscriptionId is required");
            }
            else if (!Guid.TryParse(SharedResources.SubscriptionId, out _))
            {
                errors.Add($"Shared resources: SubscriptionId '{SharedResources.SubscriptionId}' is not a valid GUID");
            }

            if (string.IsNullOrWhiteSpace(SharedResources.ResourceGroup))
            {
                errors.Add("Shared resources: ResourceGroup is required");
            }

            if (string.IsNullOrWhiteSpace(SharedResources.Location))
            {
                errors.Add("Shared resources: Location is required");
            }

            if (string.IsNullOrWhiteSpace(SharedResources.AppServicePlanName))
            {
                errors.Add("Shared resources: AppServicePlanName is required");
            }
        }

        // Validate agents
        if (Agents == null || Agents.Count == 0)
        {
            errors.Add("At least one agent is required in the team");
        }
        else
        {
            // Check for duplicate agent names
            var agentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Agents.Count; i++)
            {
                var agent = Agents[i];
                var agentPrefix = $"Agent[{i}] ({agent.Name})";

                // Validate agent name
                if (string.IsNullOrWhiteSpace(agent.Name))
                {
                    errors.Add($"{agentPrefix}: Name is required");
                }
                else
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(agent.Name, "^[a-zA-Z0-9-]+$"))
                    {
                        errors.Add($"{agentPrefix}: Name must be alphanumeric with hyphens only (no spaces or special characters)");
                    }

                    if (!agentNames.Add(agent.Name))
                    {
                        errors.Add($"{agentPrefix}: Duplicate agent name '{agent.Name}' found");
                    }
                }

                // Validate required agent properties
                if (string.IsNullOrWhiteSpace(agent.DisplayName))
                {
                    errors.Add($"{agentPrefix}: DisplayName is required");
                }

                if (string.IsNullOrWhiteSpace(agent.AgentIdentityDisplayName))
                {
                    errors.Add($"{agentPrefix}: AgentIdentityDisplayName is required");
                }

                if (string.IsNullOrWhiteSpace(agent.AgentUserPrincipalName))
                {
                    errors.Add($"{agentPrefix}: AgentUserPrincipalName is required");
                }
                else if (!System.Text.RegularExpressions.Regex.IsMatch(agent.AgentUserPrincipalName, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                {
                    errors.Add($"{agentPrefix}: AgentUserPrincipalName '{agent.AgentUserPrincipalName}' is not a valid email address");
                }

                if (string.IsNullOrWhiteSpace(agent.AgentUserDisplayName))
                {
                    errors.Add($"{agentPrefix}: AgentUserDisplayName is required");
                }

                if (string.IsNullOrWhiteSpace(agent.DeploymentProjectPath))
                {
                    errors.Add($"{agentPrefix}: DeploymentProjectPath is required");
                }
                else if (!Path.Exists(agent.DeploymentProjectPath))
                {
                    errors.Add($"{agentPrefix}: DeploymentProjectPath '{agent.DeploymentProjectPath}' does not exist");
                }
            }
        }

        return errors;
    }
}
