// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// A single coding-agent CLI (e.g. GitHub Copilot, Claude Code) that can report its
/// own availability and run a semantic-evaluation prompt as a subprocess.
///
/// One implementation per engine. Adding a new agent is a new file plus a DI
/// registration in <c>Program.cs</c> — not edits to switches spread across the
/// evaluator and a central runner. The registration order defines the priority
/// that <c>--eval-engine auto</c> walks.
/// </summary>
internal interface ICodingAgentLauncher
{
    /// <summary>The engine this launcher implements.</summary>
    EvalEngine Engine { get; }

    /// <summary>Human-readable name for reports and logs (e.g. "GitHub Copilot").</summary>
    string DisplayName { get; }

    /// <summary>
    /// The agent's concrete read/edit tool names, injected into the evaluation
    /// prompt so the instructions name the exact tools the agent exposes
    /// (Copilot: view/edit, Claude Code: Read/Edit).
    /// </summary>
    SemanticCheckPrompts.AgentToolset Toolset { get; }

    /// <summary>The CLI binary name on PATH (e.g. "copilot", "claude").</summary>
    string CliCommand { get; }

    /// <summary>
    /// Returns true when the agent's CLI is installed and responds on PATH.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the agent against <paramref name="prompt"/> with its working directory
    /// set to <paramref name="workingDirectory"/> (the evaluation sandbox).
    /// Returns true when the subprocess exits 0.
    /// </summary>
    Task<bool> LaunchAsync(string prompt, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default);
}
