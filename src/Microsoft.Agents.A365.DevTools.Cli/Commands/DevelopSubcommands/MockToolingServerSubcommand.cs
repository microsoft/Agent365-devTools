// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.DevelopSubcommands;

/// <summary>
/// Subcommand to start the Mock Tooling Server
/// </summary>
internal static class MockToolingServerSubcommand
{
    /// <summary>
    /// Creates the start-mock-tooling-server subcommand to start the MockToolingServer for development
    /// </summary>
    /// <param name="logger">Logger for progress reporting</param>
    /// <param name="processService">Process service for starting processes</param>
    /// <returns>
    /// A <see cref="Command"/> object representing the 'start-mock-tooling-server'
    /// subcommand, used to start the Mock Tooling Server for local development and testing.
    /// </returns>
    public static Command CreateCommand(
        ILogger logger,
        IProcessService processService,
        IServerService serverService)
    {
        var command = new Command("start-mock-tooling-server", "Start the Mock Tooling Server for local development and testing");
        command.AddAlias("mts");

        var portOption = new Option<int?>(
            ["--port", "-p"],
            description: "Port number to run the server on (default: 5309)"
        );
        command.AddOption(portOption);

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Enable verbose logging"
        );
        command.AddOption(verboseOption);

        var dryRunOption = new Option<bool>(
            ["--dry-run"],
            description: "Show what would be done without executing"
        );
        command.AddOption(dryRunOption);

        var backgroundOption = new Option<bool>(
            ["--background", "-bg"],
            description: "Run the server in the background (opens new terminal to run server)"
        );
        command.AddOption(backgroundOption);

        command.SetHandler(async (port, verbose, dryRun, background) => {
            await HandleStartServer(port, verbose, dryRun, background, logger, processService, serverService);
        }, portOption, verboseOption, dryRunOption, backgroundOption);

        return command;
    }

    /// <summary>
    /// Handles the start server command execution
    /// </summary>
    /// <param name="port">The port number to run the server on</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="dryRun">Show what would be done without executing</param>
    /// <param name="background">Run the server in the background (opens new terminal to run server)</param>
    /// <param name="logger">Logger for progress reporting</param>
    /// <param name="processService">Process service for starting processes</param>
    /// <param name="serverService">Server service for managing the server</param>
    public static async Task HandleStartServer(
        int? port,
        bool verbose,
        bool dryRun,
        bool background,
        ILogger logger,
        IProcessService processService,
        IServerService serverService)
    {
        var serverPort = port ?? 5309;
        if (serverPort < 1 || serverPort > 65535)
        {
            logger.LogError("Invalid port number: {Port}. Port must be between 1 and 65535.", serverPort);
            return;
        }

        if (dryRun)
        {
            logger.LogInformation("[DRY RUN] Would start Mock Tooling Server on port {Port}", serverPort);
            logger.LogInformation("[DRY RUN] Would use verbose logging: {Verbose}", verbose);
            logger.LogInformation("[DRY RUN] Background mode: {Background}", background);

            if (background)
            {
                var dryRunArguments = $"develop mts --port {serverPort}";
                if (verbose) dryRunArguments += " --verbose";
                logger.LogInformation("[DRY RUN] Would start in new terminal: a365 {Arguments}", dryRunArguments);
                return;
            }

            logger.LogInformation("[DRY RUN] Would run MockToolingServer in foreground (blocking current terminal)");

            return;
        }

        if (verbose)
        {
            logger.LogInformation("Verbose logging enabled");
        }

        try
        {
            if (!background)
            {
                // Run in foreground (blocks current terminal) using MockToolingServer.Start()
                logger.LogInformation("Starting Up MockToolingServer.");
                logger.LogInformation("Press Ctrl+C to stop the server.");
                var args = verbose ?
                new[] { "--urls", $"http://localhost:{serverPort}", "--verbose" } :
                new[] { "--urls", $"http://localhost:{serverPort}" };

                // This will run in foreground and block the current terminal until the server is stopped
                await serverService.StartAsync(args);
                return;
            }

            // Start in new terminal with same command without background flag
            var arguments = new[] { "develop", "mts", "--port", serverPort.ToString() };
            if (verbose)
            {
                arguments = [.. arguments, "--verbose"];
                logger.LogInformation("Starting in new terminal: a365 {Arguments}", arguments);
            }

            var success = processService.StartInNewTerminal("a365", arguments, Environment.CurrentDirectory, logger);

            if (!success)
            {
                logger.LogError("Failed to start Mock Tooling Server in new terminal.");
                return;
            }

            logger.LogInformation("The server is running on http://localhost:{Port} in a new terminal", serverPort);
            logger.LogInformation("Close the new terminal window to stop the server.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start Mock Tooling Server: {Message}", ex.Message);
        }
    }
}