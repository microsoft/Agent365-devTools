// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Tracks the results of each setup step for summary reporting
/// </summary>
public class SetupResults
{
    public bool InfrastructureCreated { get; set; }
    public bool BlueprintCreated { get; set; }
    public string? BlueprintId { get; set; }
    public bool McpPermissionsConfigured { get; set; }
    public bool BotApiPermissionsConfigured { get; set; }
    public bool MessagingEndpointRegistered { get; set; }
    public bool InheritablePermissionsConfigured { get; set; }
    
    // Idempotency tracking flags - track whether resources already existed (vs newly created)
    public bool InfrastructureAlreadyExisted { get; set; }
    public bool BlueprintAlreadyExisted { get; set; }
    public bool EndpointAlreadyExisted { get; set; }
    public bool McpPermissionsAlreadyExisted { get; set; }
    public bool InheritablePermissionsAlreadyExisted { get; set; }
    public bool BotApiPermissionsAlreadyExisted { get; set; }
    
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    
    public bool HasErrors => Errors.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;
}
