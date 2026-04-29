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
    AlreadyExists,

    /// <summary>
    /// Defensive fallback: the server rejected the request with a known contract-mismatch
    /// signature (e.g. a breaking API change the CLI hasn't been updated for). Callers should
    /// treat this as non-fatal, surface it in the summary as "manual config required", and
    /// direct the user to the Teams Developer Portal to register the endpoint manually.
    /// The current detection heuristic keys on the pre-migration ABS field name
    /// (<c>AzureBotServiceInstanceName</c>) — extend the heuristic in
    /// <c>TeamsGraphBackendConfigurator.IsContractMismatchResponse</c> if a future contract
    /// break needs to be caught the same way.
    /// </summary>
    SkippedContractMismatch
}
