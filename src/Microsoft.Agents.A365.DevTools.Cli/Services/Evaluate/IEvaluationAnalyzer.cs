// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Analyzes an evaluated checklist and produces the final <see cref="SchemaEvalResult"/>.
/// This is Step 4 of the evaluation pipeline: scoring, maturity determination,
/// action item generation, and smell aggregation.
/// </summary>
public interface IEvaluationAnalyzer
{
    /// <summary>
    /// Analyzes the evaluated checklist and produces a complete evaluation result.
    /// </summary>
    /// <param name="checklist">The evaluation checklist with all checks scored.</param>
    /// <param name="evalEngine">The evaluation engine used (e.g., "GithubCopilot", "None").</param>
    /// <returns>A fully populated <see cref="SchemaEvalResult"/>.</returns>
    SchemaEvalResult Analyze(EvaluationChecklist checklist, string evalEngine);
}
