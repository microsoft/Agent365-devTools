// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Result of endpoint registration operation
/// </summary>
public enum EndpointRegistrationResult
{
    /// <summary>
    /// Endpoint registration failed
    /// </summary>
    Failed,
    
    /// <summary>
    /// Endpoint was successfully created
    /// </summary>
    Created,
    
    /// <summary>
    /// Endpoint already exists (HTTP 409 Conflict)
    /// </summary>
    AlreadyExists
}
