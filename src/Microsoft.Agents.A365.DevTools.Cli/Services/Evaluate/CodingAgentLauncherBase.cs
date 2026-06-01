// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Shared process-launching mechanism for coding-agent CLIs. Subclasses supply the
/// per-engine policy (which binary, which flags, model ID, stdin vs temp-file,
/// toolset); this base owns the parts every engine shares: per-attempt timeout
/// scaling, availability probing, the Windows cmd.exe wrapper, and running and
/// killing the subprocess.
/// </summary>
internal abstract class CodingAgentLauncherBase : ICodingAgentLauncher
{
    // Observed on Copilot + Haiku: a tool evaluation needs ~60-90s of fixed overhead
    // (CLI startup, session init, reading the checklist) plus ~15-20s per semantic
    // check (read + reason + write, with several thinking rounds). The constants
    // below give each attempt enough headroom without being so long that an agent
    // stuck in a loop stalls the whole run.
    private static readonly TimeSpan PerToolBaseTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PerCheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinPerToolTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MaxPerToolTimeout = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Returns a per-attempt timeout scaled to the number of semantic checks the
    /// agent has to score. Clamped to [<see cref="MinPerToolTimeout"/>,
    /// <see cref="MaxPerToolTimeout"/>].
    /// </summary>
    internal static TimeSpan TimeoutForChecks(int checkCount)
    {
        var scaled = PerToolBaseTimeout + TimeSpan.FromSeconds(PerCheckTimeout.TotalSeconds * checkCount);
        if (scaled < MinPerToolTimeout) return MinPerToolTimeout;
        if (scaled > MaxPerToolTimeout) return MaxPerToolTimeout;
        return scaled;
    }

    private readonly CommandExecutor _executor;

    /// <summary>Logger for the concrete launcher (used for process diagnostics).</summary>
    protected ILogger Logger { get; }

    protected CodingAgentLauncherBase(CommandExecutor executor, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(logger);
        _executor = executor;
        Logger = logger;
    }

    /// <inheritdoc />
    public abstract EvalEngine Engine { get; }

    /// <inheritdoc />
    public abstract string DisplayName { get; }

    /// <inheritdoc />
    public abstract SemanticCheckPrompts.AgentToolset Toolset { get; }

    /// <summary>The CLI binary probed with <c>--version</c> to detect availability.</summary>
    protected abstract string ProbeCommand { get; }

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => ProbeCommandAsync(ProbeCommand, "--version", cancellationToken);

    /// <inheritdoc />
    public abstract Task<bool> LaunchAsync(string prompt, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a process and waits for it to complete, capturing stdout/stderr.
    /// Optionally pipes content via stdin. Kills the process on timeout to
    /// prevent zombie processes from consuming resources or locking files.
    /// </summary>
    protected async Task<bool> RunProcessAsync(
        ProcessStartInfo startInfo,
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
                Logger.LogDebug("Coding agent ({Engine}) completed successfully", DisplayName);
                return true;
            }

            Logger.LogDebug("Coding agent ({Engine}) exited with code {ExitCode}", DisplayName, process.ExitCode);
            if (stderr.Length > 0)
            {
                Logger.LogDebug("Agent stderr: {StdErr}", stderr.ToString().Trim());
            }
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Kill the timed-out process to prevent zombie processes
            KillProcess(process);
            Logger.LogDebug("Coding agent ({Engine}) timed out after {Timeout}s", DisplayName, timeout.TotalSeconds);
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private void KillProcess(Process? process)
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
                Logger.LogDebug("Killed timed-out {Engine} process tree", DisplayName);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to kill {Engine} process", DisplayName);
        }
    }

    /// <summary>
    /// Wraps command with cmd.exe /c on Windows for .cmd shim compatibility.
    /// </summary>
    protected static (string fileName, string arguments) WrapForPlatform(string command, string arguments)
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
            Logger.LogDebug(ex, "{Command} CLI detection failed", command);
            return false;
        }
    }
}
