// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Evaluates an <see cref="EvaluationChecklist"/> by running semantic checks
/// through a coding agent CLI (Claude Code or GitHub Copilot).
/// This is Step 3 of the evaluation pipeline.
/// </summary>
public interface IChecklistEvaluator
{
    /// <summary>
    /// Evaluates semantic checks in the checklist using a coding agent CLI.
    /// </summary>
    /// <param name="checklist">The checklist with deterministic checks already scored.</param>
    /// <param name="checklistPath">Path where the checklist JSON file will be written for the agent to read.</param>
    /// <param name="engine">The evaluation engine to use for semantic checks.</param>
    /// <param name="cancellationToken">Token to cancel the evaluation.</param>
    /// <returns>Result containing the checklist and whether semantic evaluation completed.</returns>
    Task<ChecklistEvaluationResult> EvaluateAsync(EvaluationChecklist checklist, string checklistPath, EvalEngine engine, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the user-facing display name for an engine (e.g. "GitHub Copilot",
    /// "auto"). Engine names are sourced from the registered launchers so adding a
    /// new agent does not require editing a name switch.
    /// </summary>
    string FormatEngineName(EvalEngine engine);
}

/// <summary>
/// Why semantic evaluation stopped, so callers can map the outcome to an exit code.
/// </summary>
public enum EvaluationOutcome
{
    /// <summary>All semantic checks were scored; a report can be produced.</summary>
    Completed,

    /// <summary>
    /// The user passed <c>--eval-engine none</c> to score the checklist with their
    /// own LLM. An intentional stop, not a failure — the run exits 0.
    /// </summary>
    OptedOut,

    /// <summary>
    /// The evaluation could not be performed as requested: no coding agent was
    /// available, or an agent ran but left checks unscored. A failure — exit 1.
    /// </summary>
    CouldNotEvaluate
}

/// <summary>
/// Result of checklist evaluation, indicating whether semantic checks were evaluated.
/// </summary>
public class ChecklistEvaluationResult
{
    public EvaluationChecklist Checklist { get; init; } = new();

    /// <summary>Why evaluation stopped. Drives the command's exit code.</summary>
    public EvaluationOutcome Outcome { get; init; }

    /// <summary>
    /// True only when every semantic check was scored. Derived from
    /// <see cref="Outcome"/>; the pipeline gates report generation on this.
    /// </summary>
    public bool SemanticEvaluationCompleted => Outcome == EvaluationOutcome.Completed;

    /// <summary>
    /// The engine that actually produced successful evaluations (first in priority
    /// order among engines that ran successfully). Null when no agent ran or all
    /// engines failed. Callers can use this to stamp reports with the engine that
    /// actually did the work, rather than whatever the user requested (e.g. "auto").
    /// </summary>
    public EvalEngine? EngineUsed { get; init; }
}
