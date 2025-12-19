namespace Microsoft.Agents.A365.DevTools.Cli.Services;

using Microsoft.Agents.A365.DevTools.MockToolingServer;

public class ServerService : IServerService
{
    /// <summary>
    /// Entry point for starting the Server programmatically from other applications.
    /// </summary>
    /// <param name="args">Command-line arguments to pass to the server</param>
    /// <returns>Task representing the running server</returns>
    public async Task StartAsync(string[] args)
    {
        await Server.Start(args);
    }
}