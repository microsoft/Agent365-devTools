// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.


using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.DevelopSubcommands;

/// <summary>
/// Subcommand to start the Mock Tooling Server
/// </summary>
internal static class StartMockToolingServerSubcommand
{
    /// <summary>
    /// Creates the start-mock-tooling-server subcommand to start the MockToolingServer for development
    /// </summary>
    /// <param name="logger">Logger for progress reporting</param>
    /// <param name="commandExecutor">Command Executor for running processes</param>
    /// <returns></returns>
    public static Command CreateCommand(
        ILogger logger,
        CommandExecutor commandExecutor)
    {
        var command = new Command("start-mock-tooling-server", "Start the Mock Tooling Server for local development and testing");

        var portOption = new Option<int?>(
            ["--port", "-p"],
            description: "Port number to run the server on (default: 5309)"
        );
        command.AddOption(portOption);

        command.SetHandler(async (port) =>
        {
            var serverPort = port ?? 5309;
            if (serverPort < 1 || serverPort > 65535)
            {
                logger.LogError("Invalid port number: {Port}. Port must be between 1 and 65535.", serverPort);
                return;
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

                var mockServerDll = Path.Combine(assemblyDir, "Microsoft.Agents.A365.DevTools.MockToolingServer.dll");

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

                logger.LogInformation("Found Mock Tooling Server at: {ServerPath}", mockServerDll);
                logger.LogInformation("Starting server on port {Port}...", serverPort);
                logger.LogInformation("Press Ctrl+C to stop the server");

                // Execute the mock server with streaming output so user can see real-time logs and interact with the server
                var result = await commandExecutor.ExecuteWithStreamingAsync(
                    executableCommand,
                    arguments,
                    workingDirectory: assemblyDir,
                    outputPrefix: "[MockServer] ",
                    interactive: true
                );

                if (result.Success)
                {
                    logger.LogInformation("Mock Tooling Server stopped successfully");
                }
                else
                {
                    logger.LogError("Mock Tooling Server failed with exit code: {ExitCode}", result.ExitCode);
                    if (!string.IsNullOrWhiteSpace(result.StandardError))
                    {
                        logger.LogError("Error details: {Error}", result.StandardError);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start Mock Tooling Server: {Message}", ex.Message);
            }
        }, portOption);

        return command;
    }
}