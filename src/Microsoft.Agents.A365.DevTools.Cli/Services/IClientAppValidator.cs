// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Interface for validating client app configuration.
/// Enables testability and follows Interface Segregation Principle.
/// </summary>
public interface IClientAppValidator
{
    /// <summary>
    /// Ensures the client app exists and has required permissions granted.
    /// Throws ClientAppValidationException if validation fails.
    /// </summary>
    /// <param name="clientAppId">The client app ID to validate</param>
    /// <param name="tenantId">The tenant ID where the app should exist</param>
    /// <param name="skipConfirmation">When true, applies any required app registration fixes without prompting the user.
    /// Use for non-interactive or CI scenarios. Defaults to false (prompt before modifying the app registration).</param>
    /// <param name="ct">Cancellation token</param>
    /// <exception cref="Exceptions.ClientAppValidationException">Thrown when validation fails</exception>
    Task EnsureValidClientAppAsync(string clientAppId, string tenantId, bool skipConfirmation = false, CancellationToken ct = default);

    /// <summary>
    /// Ensures the client app has required redirect URIs configured for Microsoft Graph PowerShell SDK.
    /// Automatically adds missing redirect URIs if needed.
    /// </summary>
    /// <param name="clientAppId">The client app ID</param>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="ct">Cancellation token</param>
    Task EnsureRedirectUrisAsync(string clientAppId, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns the subset of required permissions that are not yet present in the client app's
    /// oauth2PermissionGrant (i.e. not consented). Used to prompt the user before granting.
    /// </summary>
    Task<List<string>> GetUnconsentedRequiredPermissionsAsync(string clientAppId, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Extends the client app's oauth2PermissionGrant to include the given permissions.
    /// </summary>
    Task GrantConsentForPermissionsAsync(string clientAppId, List<string> permissions, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the client app declares the <c>wids</c> optional claim on its access tokens.
    /// Without this claim, downstream role checks (<c>IsCurrentUserAdminAsync</c>) cannot determine
    /// whether the signed-in user holds Global Administrator and will silently skip role-gated work
    /// (e.g. Phase 2b AllPrincipals OAuth2 grants on the blueprint SP).
    /// Returns false when the claim is absent OR when the optionalClaims object cannot be read; the
    /// caller decides how to surface either case to the operator.
    /// </summary>
    Task<bool> HasWidsAccessTokenOptionalClaimAsync(string clientAppId, string tenantId, CancellationToken ct = default);
}
