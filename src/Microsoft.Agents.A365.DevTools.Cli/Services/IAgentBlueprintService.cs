// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Handles Agent Blueprint creation, consent flows, and Graph API operations.
/// C# equivalent of portions from a365-setup.ps1 and a365-createinstance.ps1
/// </summary>
public interface IAgentBlueprintService
{
    /// <summary>
    /// Creates an Agent Blueprint (Agent Identity Blueprint application) in Azure AD.
    /// </summary>
    Task<BlueprintResult> CreateAgentBlueprintAsync(
        string tenantId,
        string displayName,
        string? managedIdentityPrincipalId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a client secret for the Agent Blueprint application.
    /// </summary>
    Task<string> CreateClientSecretAsync(
        string tenantId,
        string blueprintObjectId,
        string blueprintAppId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Configures inheritable permissions for Agent Identities created from this blueprint.
    /// </summary>
    Task<bool> ConfigureInheritablePermissionsAsync(
        string tenantId,
        string blueprintObjectId,
        List<string> scopes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens browser for admin consent and polls for completion.
    /// </summary>
    Task<bool> RequestAdminConsentAsync(
        string consentUrl,
        string appId,
        string tenantId,
        string description,
        int timeoutSeconds = 300,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of blueprint creation
/// </summary>
public class BlueprintResult
{
    public bool Success { get; set; }
    public string? AppId { get; set; }
    public string? ObjectId { get; set; }
    public string? ServicePrincipalId { get; set; }
    public string? ErrorMessage { get; set; }
}
