// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using Microsoft.Agents.A365.DevTools.MockToolingServer;

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
    /// <param name="commandExecutor">Command Executor for running processes</param>
    /// <param name="processService">Process service for starting processes</param>
    /// <returns>
    /// A <see cref="Command"/> object representing the 'start-mock-tooling-server'
    /// subcommand, used to start the Mock Tooling Server for local development and testing.
    /// </returns>
    public static Command CreateCommand(
        ILogger logger,
        CommandExecutor commandExecutor,
        IProcessService processService)
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

        var foregroundOption = new Option<bool>(
            ["--foreground", "-fg"],
            description: "Run the server in the foreground (blocks current terminal, default: opens new terminal)"
        );
        command.AddOption(foregroundOption);

        command.SetHandler(async (port, verbose, dryRun, foreground) => {
            await HandleStartServer(port, verbose, dryRun, foreground, logger, commandExecutor, processService);
        }, portOption, verboseOption, dryRunOption, foregroundOption);

        return command;
    }

    /// <summary>
    /// Handles the start server command execution
    /// </summary>
    /// <param name="port">The port number to run the server on</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="dryRun">Show what would be done without executing</param>
    /// <param name="logger">Logger for progress reporting</param>
    /// <param name="commandExecutor">Command executor for fallback execution</param>
    /// <param name="processService">Process service for starting processes</param>
    public static async Task HandleStartServer(int? port, bool verbose, bool dryRun, bool foreground, ILogger logger, CommandExecutor commandExecutor, IProcessService processService)
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
            logger.LogInformation("[DRY RUN] Foreground mode: {Foreground}", foreground);

            if (foreground)
            {
                logger.LogInformation("[DRY RUN] Would run MockToolingServer in foreground (blocking current terminal)");
                return;
            }

            var arguments = $"develop mts --port {serverPort} --foreground";
            if (verbose) arguments += " --verbose";
            logger.LogInformation("[DRY RUN] Would start in new terminal: a365 {Arguments}", arguments);

            return;
        }

        if (verbose)
        {
            logger.LogInformation("Verbose logging enabled");
        }

        logger.LogInformation("Starting Mock Tooling Server on port {Port}...", serverPort);

        try
        {
            if (foreground)
            {
                // Run in foreground (blocks current terminal) using MockToolingServer.Start()
                logger.LogInformation("Starting server in foreground mode...");
                logger.LogInformation("Press Ctrl+C to stop the server.");

                var args = new[] { "--urls", $"http://localhost:{serverPort}" };

                if (verbose)
                {
                    logger.LogInformation("Starting MockToolingServer with args: {Args}", string.Join(" ", args));
                }

                // This will run in foreground and block the current terminal until the server is stopped
                await MockToolingServer.Server.Start(args);
                return;
            }

            // Start in new terminal with same command + --foreground flag
            var arguments = new[] { "develop", "mts", "--port", serverPort.ToString(), "--foreground" };
            if (verbose)
            {
                arguments = [.. arguments, "--verbose"];
            }

            if (verbose)
            {
                logger.LogInformation("Starting in new terminal: a365 {Arguments}", arguments);
            }

            logger.LogInformation("Starting server on port {Port} in a new terminal...", serverPort);

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