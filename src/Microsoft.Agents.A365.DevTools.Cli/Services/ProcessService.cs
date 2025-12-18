// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

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

    public bool StartInNewTerminal(string command, string[] arguments, string workingDirectory, ILogger logger)
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
                logger.LogError("Failed to configure terminal for starting the process.");
                return false;
            }

            processStartInfo.WorkingDirectory = workingDirectory;
            processStartInfo.UseShellExecute = true;
            processStartInfo.CreateNoWindow = false;

            var process = Start(processStartInfo);
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
    private ProcessStartInfo ConfigureWindowsTerminal(string command, string[] arguments)
    {
        var processStartInfo = new ProcessStartInfo();

        // Use Windows Terminal if available, otherwise fall back to cmd
        var windowsTerminalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WindowsApps\wt.exe");

        if (File.Exists(windowsTerminalPath))
        {
            // Use Windows Terminal with ArgumentList for proper escaping
            processStartInfo.FileName = windowsTerminalPath;
            processStartInfo.ArgumentList.Add("--");
            processStartInfo.ArgumentList.Add(command);
        }
        else
        {
            // Fallback to cmd with ArgumentList for proper escaping
            processStartInfo.FileName = "cmd.exe";
            processStartInfo.ArgumentList.Add("/k");
            processStartInfo.ArgumentList.Add(command);
        }

        // Add each argument separately
        foreach (var arg in arguments)
        {
            processStartInfo.ArgumentList.Add(arg);
        }

        return processStartInfo;
    }

    /// <summary>
    /// Configures ProcessStartInfo for macOS terminal
    /// </summary>
    /// <param name="command">The command to execute</param>
    /// <param name="arguments">The command arguments</param>
    /// <returns>Configured ProcessStartInfo</returns>
    private ProcessStartInfo ConfigureMacOSTerminal(string command, string[] arguments)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "osascript"
        };

        // Use ArgumentList for proper escaping of AppleScript command
        processStartInfo.ArgumentList.Add("-e");
        var escapedCommand = EscapeAppleScriptString(command);
        var escapedArguments = EscapeAppleScriptString(string.Join(" ", arguments));
        processStartInfo.ArgumentList.Add($"tell application \"Terminal\" to do script \"{escapedCommand} {escapedArguments}\"");

        return processStartInfo;
    }

    /// <summary>
    /// Configures ProcessStartInfo for Linux terminal
    /// </summary>
    /// <param name="command">The command to execute</param>
    /// <param name="arguments">The command arguments</param>
    /// <param name="logger">Logger for error reporting</param>
    /// <returns>Configured ProcessStartInfo or null if no suitable terminal found</returns>
    private ProcessStartInfo? ConfigureLinuxTerminal(string command, string[] arguments, ILogger logger)
    {
        // Try common terminal emulators
        var terminals = new[] { "gnome-terminal", "xterm", "konsole", "x-terminal-emulator" };
        string? foundTerminal = null;

        foreach (var terminal in terminals)
        {
            try
            {
                var which = Start(new ProcessStartInfo
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
            catch (Exception ex)
            {
                // Continue to next terminal
                logger.LogDebug(ex, $"Failed check for terminal '{terminal}'. Continuing to next terminal.");
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

        // Use ArgumentList for proper escaping based on terminal type
        if (foundTerminal == "gnome-terminal")
        {
            processStartInfo.ArgumentList.Add("--");
            processStartInfo.ArgumentList.Add(command);
        }
        else
        {
            processStartInfo.ArgumentList.Add("-e");
            processStartInfo.ArgumentList.Add(command);
        }

        // Add each argument separately
        foreach (var arg in arguments)
        {
            processStartInfo.ArgumentList.Add(arg);
        }

        return processStartInfo;
    }

    /// <summary>
    /// Escapes a string for safe use within AppleScript double-quoted strings
    /// </summary>
    /// <param name="input">The string to escape</param>
    /// <returns>The escaped string safe for AppleScript</returns>
    private static string EscapeAppleScriptString(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        return input
            .Replace("\\", "\\\\")  // Escape backslashes first
            .Replace("\"", "\\\"")  // Escape double quotes
            .Replace("\n", "\\n")   // Escape newlines
            .Replace("\r", "\\r")   // Escape carriage returns
            .Replace("\t", "\\t");  // Escape tabs
    }

}