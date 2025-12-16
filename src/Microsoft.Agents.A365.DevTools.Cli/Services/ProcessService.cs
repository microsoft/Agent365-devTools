// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Default implementation of IProcessService
/// </summary>
public class ProcessService : IProcessService
{
    public Process? Start(ProcessStartInfo startInfo)
    {
        return Process.Start(startInfo);
    }
}