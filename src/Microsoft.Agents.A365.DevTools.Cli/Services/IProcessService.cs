// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Interface for process operations
/// </summary>
public interface IProcessService
{
    /// <summary>
    /// Starts a new process using the specified ProcessStartInfo
    /// </summary>
    /// <param name="startInfo">The ProcessStartInfo to use</param>
    /// <returns>The started Process or null if failed</returns>
    Process? Start(ProcessStartInfo startInfo);

    /// <summary>
    /// Starts a command in a new terminal window
    /// </summary>
    /// <param name="command">The command to execute</param>
    /// <param name="arguments">The command arguments</param>
    /// <param name="workingDirectory">Working directory for the process</param>
    /// <param name="logger">Logger for output</param>
    /// <returns>True if the process was started successfully, false otherwise</returns>
    bool StartInNewTerminal(string command, string arguments, string workingDirectory, ILogger logger);
}