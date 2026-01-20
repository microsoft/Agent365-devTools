// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Information about a federated identity credential.
/// </summary>
public class FederatedCredentialInfo
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Issuer { get; set; }
    public string? Subject { get; set; }
    public List<string> Audiences { get; set; } = new();
}

/// <summary>
/// Result of checking if a federated credential exists.
/// </summary>
public class FederatedCredentialCheckResult
{
    public bool Exists { get; set; }
    public FederatedCredentialInfo? ExistingCredential { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result of creating a federated credential.
/// </summary>
public class FederatedCredentialCreateResult
{
    public bool Success { get; set; }
    public bool AlreadyExisted { get; set; }
    public string? ErrorMessage { get; set; }
    public bool ShouldRetry { get; set; }
}
