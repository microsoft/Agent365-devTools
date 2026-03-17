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
    public bool BotInheritablePermissionsConfigured { get; set; }
    public bool GraphPermissionsConfigured { get; set; }
    public bool GraphInheritablePermissionsConfigured { get; set; }
    public bool CustomPermissionsConfigured { get; set; }

    // Batch phase results — set by AllSubcommand after BatchPermissionsOrchestrator completes.
    // These replace the per-resource flags for the setup all summary display.

    /// <summary>Phase 1: Service principal resolution completed for all specs.</summary>
    public bool BatchPermissionsPhase1Completed { get; set; }

    /// <summary>Phase 2: OAuth2 grants and inheritable permissions configured for all resources.</summary>
    public bool BatchPermissionsPhase2Completed { get; set; }

    /// <summary>
    /// Phase 3: Admin consent was granted or already existed.
    /// False with <see cref="AdminConsentUrl"/> set means the user is non-admin and consent is pending.
    /// </summary>
    public bool AdminConsentGranted { get; set; }

    /// <summary>
    /// Error message when Microsoft Graph inheritable permissions fail to configure.
    /// Non-null indicates failure. This is critical for agent token exchange functionality.
    /// </summary>
    public string? GraphInheritablePermissionsError { get; set; }

    /// <summary>
    /// Whether the Federated Identity Credential was configured for the managed identity.
    /// False (with FederatedCredentialError set) means agent token exchange may not work.
    /// </summary>
    public bool FederatedCredentialConfigured { get; set; }

    /// <summary>
    /// Error message when Federated Identity Credential configuration failed.
    /// </summary>
    public string? FederatedCredentialError { get; set; }
    
    // Idempotency tracking flags - track whether resources already existed (vs newly created)
    public bool InfrastructureAlreadyExisted { get; set; }
    public bool BlueprintAlreadyExisted { get; set; }
    public bool EndpointAlreadyExisted { get; set; }
    public bool McpPermissionsAlreadyExisted { get; set; }
    public bool InheritablePermissionsAlreadyExisted { get; set; }
    public bool BotApiPermissionsAlreadyExisted { get; set; }
    public bool BotInheritablePermissionsAlreadyExisted { get; set; }
    public bool GraphPermissionsAlreadyExisted { get; set; }
    public bool GraphInheritablePermissionsAlreadyExisted { get; set; }
    public bool CustomPermissionsAlreadyExisted { get; set; }

    /// <summary>
    /// Consent URL to present when admin consent was not granted because the user lacks an admin role.
    /// Non-null indicates a tenant administrator needs to complete consent at this URL.
    /// </summary>
    public string? AdminConsentUrl { get; set; }

    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    
    public bool HasErrors => Errors.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;
}
