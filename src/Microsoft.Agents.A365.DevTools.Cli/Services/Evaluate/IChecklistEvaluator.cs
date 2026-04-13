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
}

/// <summary>
/// Result of checklist evaluation, indicating whether semantic checks were evaluated.
/// </summary>
public class ChecklistEvaluationResult
{
    public EvaluationChecklist Checklist { get; init; } = new();
    public bool SemanticEvaluationCompleted { get; init; }
}
