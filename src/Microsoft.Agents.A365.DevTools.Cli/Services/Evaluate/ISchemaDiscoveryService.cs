// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Discovers MCP tool schemas from a running MCP server using the Streamable HTTP transport.
/// This is Step 1 of the evaluation pipeline.
/// </summary>
public interface ISchemaDiscoveryService
{
    /// <summary>
    /// Connects to an MCP server via Streamable HTTP (JSON-RPC 2.0),
    /// performs the initialize handshake, and retrieves the list of tool schemas.
    /// </summary>
    /// <param name="serverUrl">The MCP server Streamable HTTP endpoint URL.</param>
    /// <param name="authToken">Optional Bearer token for server authentication.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A list of <see cref="ToolSchema"/> discovered from the server.</returns>
    Task<List<ToolSchema>> DiscoverToolsAsync(string serverUrl, string? authToken = null, CancellationToken cancellationToken = default);
}
