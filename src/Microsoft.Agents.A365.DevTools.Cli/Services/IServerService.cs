// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Interface for server operations
/// </summary>
public interface IServerService
{
    /// <summary>
    /// Entry point for starting servers programmatically from other applications.
    /// </summary>
    /// <param name="args">Command-line arguments to pass to the server</param>
    /// <returns>Task representing the running server</returns>
    Task StartAsync(string[] args);
}