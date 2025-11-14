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
    /// Publishes an MCP server to a Dataverse environment
    /// </summary>
    /// <param name="environmentId">Dataverse environment ID</param>
    /// <param name="serverName">MCP server name to publish</param>
    /// <param name="request">Publish request with alias, display name, and description</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response from the publish operation</returns>
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
    /// Approves an MCP server
    /// </summary>
    /// <param name="serverName">MCP server name to approve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if successful, false otherwise</returns>
    Task<bool> ApproveServerAsync(
        string serverName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Blocks an MCP server
    /// </summary>
    /// <param name="serverName">MCP server name to block</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if successful, false otherwise</returns>
    Task<bool> BlockServerAsync(
        string serverName,
        CancellationToken cancellationToken = default);
}

