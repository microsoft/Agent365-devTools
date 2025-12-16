// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.


using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;

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

        command.SetHandler((port) =>
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

                logger.LogInformation("Starting server on port {Port} in a new terminal window...", serverPort);

                // Start the mock server in a new terminal window
                if (!StartServerInNewTerminal(executableCommand, arguments, assemblyDir, logger))
                {
                    logger.LogError("Failed to start Mock Tooling Server in a new terminal window.");
                }

                logger.LogInformation("Mock Tooling Server started successfully in a new terminal window.");
                logger.LogInformation("The server is running on http://localhost:{Port}", serverPort);
                logger.LogInformation("Close the terminal window or press Ctrl+C in it to stop the server.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start Mock Tooling Server: {Message}", ex.Message);
            }
        }, portOption);

        return command;
    }

    /// <summary>
    /// Starts the Mock Tooling Server in a new terminal window
    /// </summary>
    /// <param name="command">The command to execute (dotnet)</param>
    /// <param name="arguments">The arguments for the command</param>
    /// <param name="workingDirectory">Working directory for the process</param>
    /// <param name="logger">Logger for output</param>
    /// <returns>True if the process was started successfully, false otherwise</returns>
    private static bool StartServerInNewTerminal(string command, string arguments, string workingDirectory, ILogger logger)
    {
        try
        {
            ProcessStartInfo? processStartInfo = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                processStartInfo = ConfigureWindowsTerminal(command, arguments);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                processStartInfo = ConfigureMacOSTerminal(command, arguments);
            }
            else
            {
                processStartInfo = ConfigureLinuxTerminal(command, arguments, logger);
            }

            if (processStartInfo == null)
            {
                logger.LogError("Failed to configure terminal for starting the Mock Tooling Server.");
                return false;
            }

            processStartInfo.WorkingDirectory = workingDirectory;
            processStartInfo.UseShellExecute = true;
            processStartInfo.CreateNoWindow = false;

            var process = Process.Start(processStartInfo);
            return process != null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start process in new terminal: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Configures ProcessStartInfo for Windows terminal
    /// </summary>
    /// <param name="command">The command to execute</param>
    /// <param name="arguments">The command arguments</param>
    /// <returns>Configured ProcessStartInfo</returns>
    private static ProcessStartInfo ConfigureWindowsTerminal(string command, string arguments)
    {
        var processStartInfo = new ProcessStartInfo();

        // Use Windows Terminal if available, otherwise fall back to cmd
        var windowsTerminalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WindowsApps\wt.exe");

        if (File.Exists(windowsTerminalPath))
        {
            // Use Windows Terminal
            processStartInfo.FileName = windowsTerminalPath;
            processStartInfo.Arguments = $"--title \"Mock Tooling Server\" -- {command} {arguments}";
        }
        else
        {
            // Fallback to cmd
            processStartInfo.FileName = "cmd.exe";
            processStartInfo.Arguments = $"/k \"{command} {arguments}\"";
        }

        return processStartInfo;
    }

    /// <summary>
    /// Configures ProcessStartInfo for macOS terminal
    /// </summary>
    /// <param name="command">The command to execute</param>
    /// <param name="arguments">The command arguments</param>
    /// <returns>Configured ProcessStartInfo</returns>
    private static ProcessStartInfo ConfigureMacOSTerminal(string command, string arguments)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "osascript",
            Arguments = $"-e \"tell application \\\"Terminal\\\" to do script \\\"{command} {arguments}\\\"\""
        };

        return processStartInfo;
    }

    /// <summary>
    /// Configures ProcessStartInfo for Linux terminal
    /// </summary>
    /// <param name="command">The command to execute</param>
    /// <param name="arguments">The command arguments</param>
    /// <param name="logger">Logger for error reporting</param>
    /// <returns>Configured ProcessStartInfo or null if no suitable terminal found</returns>
    private static ProcessStartInfo? ConfigureLinuxTerminal(string command, string arguments, ILogger logger)
    {
        // Try common terminal emulators
        var terminals = new[] { "gnome-terminal", "xterm", "konsole", "x-terminal-emulator" };
        string? foundTerminal = null;

        foreach (var terminal in terminals)
        {
            try
            {
                var which = Process.Start(new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = terminal,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                which?.WaitForExit();
                if (which?.ExitCode == 0)
                {
                    foundTerminal = terminal;
                    break;
                }
            }
            catch
            {
                // Continue to next terminal
            }
        }

        if (foundTerminal == null)
        {
            logger.LogError("No suitable terminal emulator found on this Linux system");
            return null;
        }

        var processStartInfo = new ProcessStartInfo
        {
            FileName = foundTerminal
        };

        if (foundTerminal == "gnome-terminal")
        {
            processStartInfo.Arguments = $"--title=\"Mock Tooling Server\" -- {command} {arguments}";
        }
        else
        {
            processStartInfo.Arguments = $"-e \"{command} {arguments}\"";
        }

        return processStartInfo;
    }
}