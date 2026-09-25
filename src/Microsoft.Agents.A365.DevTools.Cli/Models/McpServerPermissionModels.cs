// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// The Entra resource that represents a BYO MCP server.
/// </summary>
/// <param name="ServerName">MCP server name as supplied by the caller.</param>
/// <param name="DisplayName">Display name of the backing Entra application ("{ServerName} - BYO").</param>
/// <param name="AppId">Application (client) ID of the BYO application.</param>
/// <param name="ServicePrincipalObjectId">Object ID of the BYO service principal, used as the grant resourceId.</param>
public sealed record McpServerResource(
    string ServerName,
    string DisplayName,
    string AppId,
    string ServicePrincipalObjectId);

/// <summary>
/// An agent instance and whether it already holds the MCP server scope.
/// </summary>
/// <param name="ServicePrincipalObjectId">Object ID of the agent identity service principal.</param>
/// <param name="DisplayName">Display name of the agent identity, when Entra returns one.</param>
/// <param name="HasScope">True when an existing grant already covers the required scope.</param>
public sealed record AgentInstancePermissionStatus(
    string ServicePrincipalObjectId,
    string? DisplayName,
    bool HasScope);
