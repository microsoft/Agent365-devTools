// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Detects available coding agent CLIs (GitHub Copilot, Claude Code) and invokes
/// them to evaluate semantic checks in an MCP tool schema checklist.
///
/// Detection order: GitHub Copilot first, then Claude Code.
/// Prompt delivery: Claude Code pipes via stdin on Unix and uses a temp file on
/// Windows (cmd.exe /c doesn't forward stdin); GitHub Copilot always uses a
/// temp file since it doesn't support stdin piping.
/// </summary>
internal class CodingAgentRunner
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan PerToolTimeout = TimeSpan.FromMinutes(6);

    private const string ClaudeCodeEnvVar = "CLAUDECODE";

    // Copilot requires an exact model ID (no aliases like "haiku").
    // Update this when a newer Haiku version becomes available.
    private const string CopilotModel = "claude-haiku-4.5";

    private readonly CommandExecutor _executor;
    private readonly ILogger<CodingAgentRunner> _logger;

    public CodingAgentRunner(CommandExecutor executor, ILogger<CodingAgentRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(logger);
        _executor = executor;
        _logger = logger;
    }

    public async Task<bool> IsEngineAvailableAsync(EvalEngine engine, CancellationToken cancellationToken = default)
    {
        return engine switch
        {
            EvalEngine.GithubCopilot => await ProbeCommandAsync("copilot", "--version", cancellationToken),
            EvalEngine.ClaudeCode => await ProbeCommandAsync("claude", "--version", cancellationToken),
            _ => false
        };
    }

    /// <summary>
    /// Runs the specified coding agent to evaluate semantic checks in the checklist file.
    /// Claude Code: prompt is piped via stdin (-p -).
    /// GitHub Copilot: prompt is written to a temp file and referenced via -p.
    /// </summary>
    public async Task<bool> EvaluateChecklistAsync(
        string checklistPath,
        string prompt,
        EvalEngine engine,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checklistPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        if (engine is EvalEngine.None)
        {
            _logger.LogError("Cannot evaluate checklist: no coding agent engine specified");
            return false;
        }

        var workingDirectory = Path.GetDirectoryName(checklistPath) ?? Directory.GetCurrentDirectory();
        var effectiveTimeout = timeout ?? DefaultTimeout;

        return engine switch
        {
            EvalEngine.ClaudeCode => await LaunchClaudeCodeAsync(prompt, workingDirectory, effectiveTimeout, cancellationToken),
            EvalEngine.GithubCopilot => await LaunchGithubCopilotAsync(prompt, workingDirectory, effectiveTimeout, cancellationToken),
            _ => LogUnsupportedEngine(engine)
        };
    }

    /// <summary>
    /// Launches Claude Code to evaluate semantic checks.
    /// On Windows, prompt is written to a temp file (cmd.exe /c does not forward stdin).
    /// On Unix, prompt is piped via stdin (-p -).
    /// Removes CLAUDECODE env var so Claude CLI works inside a Claude Code session.
    /// </summary>
    private async Task<bool> LaunchClaudeCodeAsync(
        string prompt,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await LaunchClaudeCodeViaFileAsync(prompt, workingDirectory, timeout, cancellationToken);
        }

        return await LaunchClaudeCodeViaStdinAsync(prompt, workingDirectory, timeout, cancellationToken);
    }

    /// <summary>
    /// Windows path: writes prompt to a temp file since cmd.exe /c does not forward stdin.
    /// </summary>
    private async Task<bool> LaunchClaudeCodeViaFileAsync(
        string prompt,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var promptFile = Path.Combine(workingDirectory, $".eval_prompt_{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(promptFile, prompt, cancellationToken);

            var metaPrompt = $"Read and follow the instructions in the file at: {promptFile}";
            var (fileName, fileArguments) = WrapForPlatform("claude", $"-p \"{metaPrompt}\" --model haiku --allowedTools Read,Edit");

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = fileArguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.Environment.Remove(ClaudeCodeEnvVar);

            return await RunProcessAsync(startInfo, EvalEngine.ClaudeCode, timeout, cancellationToken: cancellationToken);
        }
        finally
        {
            try { File.Delete(promptFile); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Unix path: pipes prompt via stdin (-p -).
    /// </summary>
    private async Task<bool> LaunchClaudeCodeViaStdinAsync(
        string prompt,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "claude",
            Arguments = "-p - --model haiku --allowedTools Read,Edit",
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.Environment.Remove(ClaudeCodeEnvVar);

        return await RunProcessAsync(startInfo, EvalEngine.ClaudeCode, timeout, stdinContent: prompt, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Launches GitHub Copilot with prompt written to a temp file.
    /// Copilot does not support stdin piping, so we write the prompt to a file
    /// and tell Copilot to read and follow its instructions.
    /// </summary>
    private async Task<bool> LaunchGithubCopilotAsync(
        string prompt,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // Write prompt to a temp file since Copilot doesn't support stdin piping
        var promptFile = Path.Combine(workingDirectory, $".eval_prompt_{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(promptFile, prompt, cancellationToken);

            var metaPrompt = $"Read and follow the instructions in the file at: {promptFile}";
            var (fileName, fileArguments) = WrapForPlatform("copilot", $"-p \"{metaPrompt}\" --model {CopilotModel} --allow-all-tools");

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = fileArguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            return await RunProcessAsync(startInfo, EvalEngine.GithubCopilot, timeout, cancellationToken: cancellationToken);
        }
        finally
        {
            // Clean up the temp prompt file
            try { File.Delete(promptFile); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Runs a process and waits for it to complete, capturing stdout/stderr.
    /// Optionally pipes content via stdin. Kills the process on timeout to
    /// prevent zombie processes from consuming resources or locking files.
    /// </summary>
    private async Task<bool> RunProcessAsync(
        ProcessStartInfo startInfo,
        EvalEngine engine,
        TimeSpan timeout,
        string? stdinContent = null,
        CancellationToken cancellationToken = default)
    {
        Process? process = null;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            process = new Process { StartInfo = startInfo };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Pipe content via stdin if provided
            if (stdinContent is not null && startInfo.RedirectStandardInput)
            {
                await process.StandardInput.WriteAsync(stdinContent);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode == 0)
            {
                _logger.LogDebug("Coding agent ({Engine}) completed successfully", engine);
                return true;
            }

            _logger.LogDebug("Coding agent ({Engine}) exited with code {ExitCode}", engine, process.ExitCode);
            if (stderr.Length > 0)
            {
                _logger.LogDebug("Agent stderr: {StdErr}", stderr.ToString().Trim());
            }
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Kill the timed-out process to prevent zombie processes
            KillProcess(process, engine);
            _logger.LogDebug("Coding agent ({Engine}) timed out after {Timeout}s", engine, timeout.TotalSeconds);
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private void KillProcess(Process? process, EvalEngine engine)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _logger.LogDebug("Killed timed-out {Engine} process tree", engine);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to kill {Engine} process", engine);
        }
    }

    private bool LogUnsupportedEngine(EvalEngine engine)
    {
        _logger.LogError("Unsupported eval engine: {Engine}", engine);
        return false;
    }

    /// <summary>
    /// Wraps command with cmd.exe /c on Windows for .cmd shim compatibility.
    /// </summary>
    private static (string fileName, string arguments) WrapForPlatform(string command, string arguments)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("cmd.exe", $"/c {command} {arguments}");
        }

        return (command, arguments);
    }

    /// <summary>
    /// Probes whether a CLI tool is available by running it with --version.
    /// </summary>
    private async Task<bool> ProbeCommandAsync(string command, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            var (cmd, args) = WrapForPlatform(command, arguments);

            var result = await _executor.ExecuteAsync(
                cmd, args,
                captureOutput: true,
                suppressErrorLogging: true,
                cancellationToken: cancellationToken);

            return result.Success;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Command} CLI detection failed", command);
            return false;
        }
    }
}
