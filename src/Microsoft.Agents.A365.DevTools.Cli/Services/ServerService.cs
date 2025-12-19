// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

using Microsoft.Agents.A365.DevTools.MockToolingServer;

/// <summary>
/// Default implementation of IServerService
/// </summary>
public class ServerService : IServerService
{
    /// <inheritdoc/>
    public async Task StartAsync(string[] args)
    {
        await Server.Start(args);
    }
}