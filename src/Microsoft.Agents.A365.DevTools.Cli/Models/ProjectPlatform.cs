// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Supported project platforms for deployment
/// </summary>
public enum ProjectPlatform
{
    /// <summary>
    /// Unknown or unsupported platform
    /// </summary>
    Unknown,
    
    /// <summary>
    /// .NET (C#, F#, VB.NET)
    /// </summary>
    DotNet,
    
    /// <summary>
    /// Node.js / JavaScript / TypeScript
    /// </summary>
    NodeJs,
    
    /// <summary>
    /// Python
    /// </summary>
    Python
}
