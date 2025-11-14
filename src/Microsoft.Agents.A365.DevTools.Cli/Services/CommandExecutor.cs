// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for executing external commands (dotnet, az, powershell, etc.)
/// </summary>
public class CommandExecutor
{
    private readonly ILogger<CommandExecutor> _logger;

    public CommandExecutor(ILogger<CommandExecutor> logger)
    {
        _logger = logger;
    }

    public virtual async Task<CommandResult> ExecuteAsync(
        string command,
        string arguments,
        string? workingDirectory = null,
        bool captureOutput = true,
        bool suppressErrorLogging = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing: {Command} {Arguments}", command, arguments);

        var fileName = command;
        var fileArguments = arguments;
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && 
            NeedsCmdWrapper(command))
        {
            _logger.LogTrace("Wrapping command with cmd.exe for Windows batch file");
            fileName = "cmd.exe";
            fileArguments = $"/c {command} {arguments}";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = fileArguments,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        if (captureOutput)
        {
            process.OutputDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    outputBuilder.AppendLine(args.Data);
                    _logger.LogTrace(args.Data);
                }
            };

            process.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    errorBuilder.AppendLine(args.Data);
                    _logger.LogTrace(args.Data);
                }
            };
        }

        process.Start();

        if (captureOutput)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        await process.WaitForExitAsync(cancellationToken);

        var result = new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = outputBuilder.ToString(),
            StandardError = errorBuilder.ToString()
        };

        if (result.ExitCode != 0 && !suppressErrorLogging)
        {
            _logger.LogError("Command failed with exit code {ExitCode}: {Error}", 
                result.ExitCode, result.StandardError);
        }

        return result;
    }

    /// <summary>
    /// Execute a command and stream output to console in real-time.
    /// If interactive is true, child's STDIN is attached to the parent console (no manual forwarding).
    /// </summary>
    public virtual async Task<CommandResult> ExecuteWithStreamingAsync(
        string command,
        string arguments,
        string? workingDirectory = null,
        string outputPrefix = "",
        bool interactive = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing with streaming: {Command} {Arguments} (Interactive={Interactive})", command, arguments, interactive);

        var fileName = command;
        var fileArguments = arguments;
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && 
            NeedsCmdWrapper(command))
        {
            _logger.LogTrace("Wrapping command with cmd.exe for Windows batch file");
            fileName = "cmd.exe";
            fileArguments = $"/c {command} {arguments}";
        }

        // In interactive mode we keep stdout/err redirected (so we can still display/prefix),
        // but we DO NOT redirect stdin so the child reads directly from the console.
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = fileArguments,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = !interactive, // only redirect if not interactive
            UseShellExecute = false,
            CreateNoWindow = !interactive // show window characteristics suitable for interactive mode
        };

        using var process = new Process { StartInfo = startInfo };
        
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, args) =>
        {
            if (args.Data != null)
            {
                outputBuilder.AppendLine(args.Data);
                Console.WriteLine($"{outputPrefix}{args.Data}");
            }
        };

        process.ErrorDataReceived += (sender, args) =>
        {
            if (args.Data != null)
            {
                errorBuilder.AppendLine(args.Data);
                // Azure CLI writes informational messages to stderr with "WARNING:" prefix
                // Strip it for cleaner output
                var cleanData = IsAzureCliCommand(command) 
                    ? StripAzureWarningPrefix(args.Data) 
                    : args.Data;
                Console.WriteLine($"{outputPrefix}{cleanData}");
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // If not interactive and we redirected stdin we could implement scripted input later.

        await process.WaitForExitAsync(cancellationToken);

        var result = new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = outputBuilder.ToString(),
            StandardError = errorBuilder.ToString()
        };

        if (result.ExitCode != 0)
        {
            _logger.LogError("Command failed with exit code {ExitCode}", result.ExitCode);
        }

        return result;
    }

    private bool NeedsCmdWrapper(string command)
    {
        var extension = Path.GetExtension(command).ToLowerInvariant();
        if (extension == ".cmd" || extension == ".bat")
        {
            return true;
        }

        var commandName = Path.GetFileNameWithoutExtension(command).ToLowerInvariant();
        var batchCommands = new[] { "az", "func", "npm", "npx", "node" };
        
        return batchCommands.Contains(commandName);
    }

    private bool IsAzureCliCommand(string command)
    {
        var commandName = Path.GetFileNameWithoutExtension(command).ToLowerInvariant();
        return commandName == "az";
    }

    private string StripAzureWarningPrefix(string message)
    {
        // Azure CLI writes normal informational output to stderr with "WARNING:" prefix
        // Strip this misleading prefix for cleaner output
        var trimmed = message.TrimStart();
        if (trimmed.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.Substring(8).TrimStart(); // Remove "WARNING:" and trim
        }
        return message;
    }
}

public class CommandResult
{
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public bool Success => ExitCode == 0;
}
