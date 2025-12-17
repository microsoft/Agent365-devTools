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

        var helpOption = new Option<bool>(
            ["-?", "--help"],
            description: "Show help and usage information"
        );
        command.AddOption(helpOption);

        command.SetHandler(async (port, verbose, dryRun) => await HandleStartServer(port, verbose, dryRun, logger, commandExecutor, processService), portOption, verboseOption, dryRunOption);

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
    public static async Task HandleStartServer(int? port, bool verbose, bool dryRun, ILogger logger, CommandExecutor commandExecutor, IProcessService processService)
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
            logger.LogInformation("[DRY RUN] Would search for MockToolingServer DLL in CLI assembly directory");
            logger.LogInformation("[DRY RUN] Would execute: dotnet \"MockToolingServer.dll\" --urls http://localhost:{Port}", serverPort);
            logger.LogInformation("[DRY RUN] Would start server in new terminal window");
            return;
        }

        if (verbose)
        {
            logger.LogInformation("Verbose logging enabled");
        }

        logger.LogInformation("Starting Mock Tooling Server on port {Port}...", serverPort);

        try
        {
            // Find the bundled MockToolingServer executable
            var assemblyDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (assemblyDir == null)
            {
                logger.LogError("Unable to determine CLI assembly location");
                return;
            }

            if (verbose)
            {
                logger.LogInformation("CLI assembly directory: {AssemblyDir}", assemblyDir);
            }

            var mockServerDll = Path.Combine(assemblyDir, "Microsoft.Agents.A365.DevTools.MockToolingServer.dll");

            if (verbose)
            {
                logger.LogInformation("Looking for MockToolingServer DLL at: {DllPath}", mockServerDll);
            }

            // Use dotnet to run the DLL as it properly resolves dependencies in the same directory
            if (!File.Exists(mockServerDll))
            {
                logger.LogError("Mock Tooling Server DLL not found in CLI package.");
                logger.LogError("Expected location: {DllPath}", mockServerDll);
                logger.LogError("Please ensure the Mock Tooling Server is properly packaged with the CLI.");
                return;
            }

            var executableCommand = "dotnet";
            var arguments = $"\"{mockServerDll}\" --urls http://localhost:{serverPort}";

            if (verbose)
            {
                logger.LogInformation("Command to execute: {Command} {Arguments}", executableCommand, arguments);
                logger.LogInformation("Working directory: {WorkingDir}", assemblyDir);
            }

            logger.LogInformation("Starting server on port {Port} in a new terminal window...", serverPort);

            if (!await StartServer(executableCommand, arguments, assemblyDir, verbose, logger, commandExecutor, processService))
            {
                logger.LogError("Failed to start Mock Tooling Server.");
                return;
            }

            logger.LogInformation("The server is running on http://localhost:{Port}", serverPort);
            logger.LogInformation("Close the terminal window or press Ctrl+C in it to stop the server.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start Mock Tooling Server: {Message}", ex.Message);
        }
    }

    private static async Task<bool> StartServer(string executableCommand, string arguments, string assemblyDir, bool verbose, ILogger logger, CommandExecutor commandExecutor, IProcessService processService)
    {
        // Start the mock server in a new terminal window
        if (verbose)
        {
            logger.LogInformation("Attempting to start server in new terminal window...");
        }

        if (StartServerInNewTerminal(executableCommand, arguments, assemblyDir, logger, processService))
        {
            logger.LogInformation("Mock Tooling Server started successfully in a new terminal window.");
            return true;
        }

        logger.LogWarning("Failed to start Mock Tooling Server in a new terminal window.");

        // Fallback to running in current terminal using CommandExecutor
        logger.LogInformation("Falling back to running server in current terminal...");

        if (verbose)
        {
            logger.LogInformation("Using CommandExecutor with interactive mode enabled");
        }

        var result = await commandExecutor.ExecuteWithStreamingAsync(
            executableCommand,
            arguments,
            assemblyDir,
            "MockServer: ",
            interactive: true);

        if (result.ExitCode != 0)
        {
            logger.LogError("Mock Tooling Server exited with code {ExitCode}", result.ExitCode);
            logger.LogError("Error output: {ErrorOutput}", result.StandardError);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Starts the Mock Tooling Server in a new terminal window
    /// </summary>
    /// <param name="command">The command to execute (dotnet)</param>
    /// <param name="arguments">The arguments for the command</param>
    /// <param name="workingDirectory">Working directory for the process</param>
    /// <param name="logger">Logger for output</param>
    /// <param name="processService">Process service for starting processes</param>
    /// <returns>True if the process was started successfully, false otherwise</returns>
    private static bool StartServerInNewTerminal(string command, string arguments, string workingDirectory, ILogger logger, IProcessService processService)
    {
        return processService.StartInNewTerminal(command, arguments, workingDirectory, logger);
    }
}