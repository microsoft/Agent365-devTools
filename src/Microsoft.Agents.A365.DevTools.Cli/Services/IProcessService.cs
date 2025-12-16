// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;

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
}