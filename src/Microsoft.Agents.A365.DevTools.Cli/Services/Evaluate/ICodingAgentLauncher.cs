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
    /// How many tool evaluations may run concurrently with this engine. Subprocess agents
    /// return 1 (serial, to avoid spawning many heavy CLI processes); a direct-API judge
    /// returns a higher value to score many tools in parallel.
    /// </summary>
    int MaxConcurrency { get; }

    /// <summary>
    /// Whether <c>--eval-engine auto</c> may select this engine once its prerequisites
    /// are present. Local coding agents are auto-detectable; engines that call a remote
    /// model endpoint (and may incur cost) are explicit-only and return <c>false</c>.
    /// </summary>
    bool AutoDetectable { get; }

    /// <summary>
    /// Human-readable description of what must be present for this engine to run, used in
    /// the "engine not available" guidance (e.g. "the copilot CLI on PATH", or the
    /// required environment variables for an API-based engine).
    /// </summary>
    string AvailabilityHint { get; }

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

    /// <summary>
    /// Whether this engine scores each checklist item independently — one model call per
    /// check, with the full tool schema passed as context — rather than editing a whole-tool
    /// file. A direct-API judge returns true; subprocess coding agents return false and use
    /// <see cref="LaunchAsync"/> instead.
    /// </summary>
    bool ScoresPerCheck { get; }

    /// <summary>
    /// Scores a single checklist item. <paramref name="context"/> is the full tool schema the
    /// assertion is evaluated against (or the tool-set summary for server-level checks).
    /// Returns the score and one-sentence reason, or null if the call failed. Only meaningful
    /// when <see cref="ScoresPerCheck"/> is true.
    /// </summary>
    Task<CheckEvaluation?> ScoreCheckAsync(string context, string checkPrompt, CancellationToken cancellationToken = default);
}

/// <summary>Result of scoring one checklist item: pass/fail plus a one-sentence reason.</summary>
internal sealed record CheckEvaluation(bool Score, string Reason);
