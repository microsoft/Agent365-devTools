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
    /// <param name="ct">Cancellation token</param>
    /// <exception cref="Exceptions.ClientAppValidationException">Thrown when validation fails</exception>
    Task EnsureValidClientAppAsync(string clientAppId, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Ensures the client app has required redirect URIs configured for Microsoft Graph PowerShell SDK.
    /// Automatically adds missing redirect URIs if needed.
    /// </summary>
    /// <param name="clientAppId">The client app ID</param>
    /// <param name="graphToken">Microsoft Graph access token</param>
    /// <param name="ct">Cancellation token</param>
    Task EnsureRedirectUrisAsync(string clientAppId, string graphToken, CancellationToken ct = default);
}
