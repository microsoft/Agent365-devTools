// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Request body for linking a virtual network enterprise policy to the tenant's Agent 365
/// Power Platform environment.
/// </summary>
public class VNetLinkRequest
{
    /// <summary>
    /// The policy's properties.systemId, shaped
    /// /regions/{region}/providers/Microsoft.PowerPlatform/enterprisePolicies/{guid}.
    /// Resolved from the ARM policy id by the CLI, using the caller's own Azure session.
    /// </summary>
    [JsonPropertyName("policySystemId")]
    public string? PolicySystemId { get; set; }

    /// <summary>
    /// The policy's ARM resource id. Carried for display and audit only.
    /// </summary>
    [JsonPropertyName("policyArmId")]
    public string? PolicyArmId { get; set; }

    /// <summary>
    /// Whether an existing link to a different policy may be replaced. Mirrors the -Swap switch
    /// on Enable-SubnetInjection.
    /// </summary>
    [JsonPropertyName("swap")]
    public bool Swap { get; set; }
}

/// <summary>
/// Request body for unlinking the virtual network enterprise policy from the tenant's Agent 365
/// Power Platform environment.
/// </summary>
/// <remarks>
/// The platform holds no copy of the systemId, so this is required on every unlink. The CLI reads
/// the linked policy's ARM id from the status endpoint and resolves it against ARM on the caller's
/// own Azure session — the same thing Disable-SubnetInjection does internally.
/// </remarks>
public class VNetUnlinkRequest
{
    /// <summary>
    /// The policy's properties.systemId, shaped
    /// /regions/{region}/providers/Microsoft.PowerPlatform/enterprisePolicies/{guid}.
    /// </summary>
    [JsonPropertyName("policySystemId")]
    public string? PolicySystemId { get; set; }
}

/// <summary>
/// Status of the tenant's virtual network link, and the shape returned by link and unlink
/// once they settle.
/// </summary>
public class VNetStatusResponse
{
    /// <summary>
    /// NotLinked, Running, Linked, Failed, or Unknown.
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// ARM id of the linked policy as reported by the platform. Null when nothing is linked.
    /// </summary>
    [JsonPropertyName("policyArmId")]
    public string? PolicyArmId { get; set; }

    /// <summary>
    /// Handle for an operation that is still running, or has recently settled.
    /// </summary>
    [JsonPropertyName("operationId")]
    public string? OperationId { get; set; }

    /// <summary>
    /// Failure reason, when the platform has one to report.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// Error body returned by the platform's virtual network endpoints.
/// </summary>
public class VNetErrorResponse
{
    /// <summary>
    /// Human-readable error message. The role-check 403 puts a sentence here; a scope failure puts
    /// the code "insufficient_scope" and the sentence in <see cref="ErrorDescription"/>.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>
    /// Sentence describing a scope failure. Absent on the role-check 403.
    /// </summary>
    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }

    /// <summary>
    /// Scope the token was missing. Present only on a scope failure, which is what tells the two
    /// kinds of 403 apart.
    /// </summary>
    [JsonPropertyName("required_scope")]
    public string? RequiredScope { get; set; }
}
