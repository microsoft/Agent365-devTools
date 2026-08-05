// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for interacting with Microsoft Agent 365 Tooling API endpoints for MCP server management in Dataverse
/// </summary>
public interface IAgent365ToolingService
{
    /// <summary>
    /// The target environment (test, preprod, prod)
    /// </summary>
    string Environment { get; }
    /// <summary>
    /// Lists all available Dataverse environments
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response containing list of Dataverse environments</returns>
    Task<DataverseEnvironmentsResponse?> ListEnvironmentsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists MCP servers in a specific Dataverse environment
    /// </summary>
    /// <param name="environmentId">Dataverse environment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response containing list of MCP servers</returns>
    Task<DataverseMcpServersResponse?> ListServersAsync(
        string environmentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an MCP server to a Dataverse environment via the platform's v2 publish endpoint,
    /// which performs the full elevation orchestration (PPMI provisioning and MOS upload).
    /// </summary>
    /// <param name="environmentId">Dataverse environment ID</param>
    /// <param name="serverName">MCP server name to publish</param>
    /// <param name="request">Publish request with alias, display name, the Public Clients Entra app id, and optional publisher name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response from the publish operation, including the underlying server's app id and OAuth scope (for the post-publish required-resource-access grant) and the echoed Public Clients app id</returns>
    Task<PublishMcpServerResponse?> PublishServerAsync(
        string environmentId,
        string serverName,
        PublishMcpServerRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unpublishes an MCP server from a Dataverse environment
    /// </summary>
    /// <param name="environmentId">Dataverse environment ID</param>
    /// <param name="serverName">MCP server name to unpublish</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if successful, false otherwise</returns>
    Task<bool> UnpublishServerAsync(
        string environmentId,
        string serverName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs telemetry for register-external-mcp-server usage before processing begins.
    /// </summary>
    Task LogRegisterUsageAsync(
        string serverName,
        string authType,
        int toolCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs telemetry for evaluate-mcp-server usage at the start of the evaluation workflow.
    /// Body is intentionally empty: server identifies the caller from the bearer token and may
    /// pull additional operation context from ServiceContext if upstream activities populated it.
    /// The CLI does not send customer-private content (e.g. evaluated server URL) in the payload.
    /// </summary>
    Task LogEvaluateUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a BYO (Bring Your Own) MCP server
    /// </summary>
    /// <param name="request">Add MCP server request</param>
    /// <param name="environmentId">Dataverse environment ID to set as x-ms-environment-id header</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response containing server details</returns>
    Task<AddMcpServerResponse?> AddMcpServerAsync(
        AddMcpServerRequest request,
        string? environmentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Provisions an identity for an external MCP server
    /// </summary>
    /// <param name="serverName">MCP server name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response containing the provisioned application ID</returns>
    Task<ProvisionIdentityResponse?> ProvisionIdentityAsync(
        string serverName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the PPMI application (client) ID for a BYO MCP server by name.
    /// Calls GET /agents/mcpServers/appIds?serverName={serverName} on the MCP Platform.
    /// </summary>
    /// <param name="serverName">MCP server name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response containing the MCP server's PPMI app ID, or null on failure</returns>
    Task<McpServerAppIdResponse?> GetMcpServerAppIdByNameAsync(
        string serverName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a BYO (Bring Your Own) MCP server and returns the associated app IDs for cleanup
    /// </summary>
    /// <param name="serverName">MCP server name to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response containing app IDs to clean up, or null on failure</returns>
    Task<CleanupMcpServerResponse?> DeleteMcpServerAsync(
        string serverName,
        bool force = false,
        CancellationToken cancellationToken = default);
}

