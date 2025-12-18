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
}
