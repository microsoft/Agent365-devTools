// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Launches Claude Code to score semantic checks.
/// On Windows the prompt is written to a temp file (cmd.exe /c does not forward
/// stdin); on Unix it is piped via stdin (-p -). Removes the CLAUDECODE env var so
/// the Claude CLI works even when invoked from inside a Claude Code session.
/// </summary>
internal sealed class ClaudeCodeLauncher : CodingAgentLauncherBase
{
    private const string ClaudeCodeEnvVar = "CLAUDECODE";

    public ClaudeCodeLauncher(CommandExecutor executor, ILogger<ClaudeCodeLauncher> logger)
        : base(executor, logger)
    {
    }

    public override EvalEngine Engine => EvalEngine.ClaudeCode;

    public override string DisplayName => "Claude Code";

    public override SemanticCheckPrompts.AgentToolset Toolset => new(ReadToolName: "Read", EditToolName: "Edit");

    public override string CliCommand => "claude";

    public override async Task<bool> LaunchAsync(
        string prompt,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await LaunchViaFileAsync(prompt, workingDirectory, timeout, cancellationToken);
        }

        return await LaunchViaStdinAsync(prompt, workingDirectory, timeout, cancellationToken);
    }

    /// <summary>
    /// Windows path: writes prompt to a temp file since cmd.exe /c does not forward stdin.
    /// </summary>
    private async Task<bool> LaunchViaFileAsync(
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
            var (fileName, fileArguments) = WrapForPlatform("claude", $"-p \"{metaPrompt}\" --model {EvalModelConstants.ClaudeModel} --allowedTools Read,Edit");

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

            return await RunProcessAsync(startInfo, timeout, cancellationToken: cancellationToken);
        }
        finally
        {
            try { File.Delete(promptFile); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Unix path: pipes prompt via stdin (-p -).
    /// </summary>
    private async Task<bool> LaunchViaStdinAsync(
        string prompt,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "claude",
            Arguments = $"-p - --model {EvalModelConstants.ClaudeModel} --allowedTools Read,Edit",
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.Environment.Remove(ClaudeCodeEnvVar);

        return await RunProcessAsync(startInfo, timeout, stdinContent: prompt, cancellationToken: cancellationToken);
    }
}
