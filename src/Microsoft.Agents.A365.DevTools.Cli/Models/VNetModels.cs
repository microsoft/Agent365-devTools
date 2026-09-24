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
    /// Human-readable error message.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
