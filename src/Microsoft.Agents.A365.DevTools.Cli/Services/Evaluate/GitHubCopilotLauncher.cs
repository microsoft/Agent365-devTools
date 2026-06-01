// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Launches GitHub Copilot to score semantic checks. Copilot does not support stdin
/// piping, so the prompt is always written to a temp file and referenced via -p.
/// </summary>
internal sealed class GitHubCopilotLauncher : CodingAgentLauncherBase
{
    // Copilot requires an exact model ID (no aliases like "haiku").
    // Update this when a newer Haiku version becomes available.
    private const string CopilotModel = "claude-haiku-4.5";

    public GitHubCopilotLauncher(CommandExecutor executor, ILogger<GitHubCopilotLauncher> logger)
        : base(executor, logger)
    {
    }

    public override EvalEngine Engine => EvalEngine.GitHubCopilot;

    public override string DisplayName => "GitHub Copilot";

    public override SemanticCheckPrompts.AgentToolset Toolset => new(ReadToolName: "view", EditToolName: "edit");

    protected override string ProbeCommand => "copilot";

    public override async Task<bool> LaunchAsync(
        string prompt,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        // Write prompt to a temp file since Copilot doesn't support stdin piping
        var promptFile = Path.Combine(workingDirectory, $".eval_prompt_{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(promptFile, prompt, cancellationToken);

            var metaPrompt = $"Read and follow the instructions in the file at: {promptFile}";
            // Security model: allow the full tool set EXCEPT subprocess execution and
            // outbound network. The agent can pick any read/write/search strategy
            // against files in its sandboxed cwd, but cannot shell out, hit the web,
            // or exfiltrate the checklist to an arbitrary URL. Copilot's shell tool is
            // named `shell` on macOS/Linux and `powershell` on Windows (plus a family
            // of session helpers); we deny every variant so the flag is correct on
            // every platform. File access is already bounded by Copilot's default path
            // verification to the current working directory, which is an isolated temp
            // sandbox — so view/create/edit stay confined.
            var (fileName, fileArguments) = WrapForPlatform(
                "copilot",
                $"-p \"{metaPrompt}\" --model {CopilotModel} --allow-all-tools " +
                // Restrict visible tools to just read + edit. `create` is specifically
                // excluded because Copilot's create cannot overwrite existing files and
                // exposing it leads the model down workaround loops (sibling files,
                // retries, etc.) instead of the straightforward str_replace flow.
                "--available-tools=view,edit " +
                "--deny-tool=shell --deny-tool=write_shell --deny-tool=read_shell " +
                "--deny-tool=stop_shell --deny-tool=list_shell " +
                "--deny-tool=powershell --deny-tool=write_powershell --deny-tool=read_powershell " +
                "--deny-tool=stop_powershell --deny-tool=list_powershell " +
                "--deny-tool=web_fetch --deny-tool=web_search --no-ask-user");

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

            return await RunProcessAsync(startInfo, timeout, cancellationToken: cancellationToken);
        }
        finally
        {
            // Clean up the temp prompt file
            try { File.Delete(promptFile); } catch { /* best effort */ }
        }
    }
}
