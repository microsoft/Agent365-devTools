namespace Microsoft.Agents.A365.DevTools.Cli.Services;

public interface IServerService
{
    /// <summary>
    /// Entry point for starting the Server programmatically from other applications.
    /// </summary>
    /// <param name="args">Command-line arguments to pass to the server</param>
    /// <returns>Task representing the running server</returns>
    Task StartAsync(string[] args);
}