// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Computes per-category, per-tool, and overall scores for MCP server evaluation.
/// Category scores use pass-rate (passed / evaluated * 100). Null scores are excluded.
/// Tool scores use weighted category averages.
/// Overall score blends mean tool score (0.85) with toolset score (0.15).
/// </summary>
public static class Scorer
{
    /// <summary>
    /// Category weights for computing weighted tool scores. Must sum to 1.0.
    /// </summary>
    public static IReadOnlyDictionary<string, float> CategoryWeights { get; } = new Dictionary<string, float>
    {
        ["tool_name"] = 0.15f,
        ["tool_description"] = 0.35f,
        ["param_name"] = 0.10f,
        ["param_description"] = 0.25f,
        ["schema_structure"] = 0.15f,
    };

    /// <summary>
    /// Weight applied to the mean of tool-level scores in the overall formula.
    /// </summary>
    public const float ToolWeight = 0.85f;

    /// <summary>
    /// Weight applied to the toolset-level score in the overall formula.
    /// </summary>
    public const float ToolsetWeight = 0.15f;

    /// <summary>
    /// Computes the score (0-100) for a single category from its check items.
    /// Formula: (passed / evaluated) * 100. Checks with null Score are excluded
    /// from both numerator and denominator. Returns 100 if no checks are evaluated.
    /// </summary>
    /// <param name="checks">Check items for a single category.</param>
    /// <returns>Score from 0 to 100, rounded to 1 decimal place.</returns>
    public static float ComputeCategoryScore(List<ChecklistItem> checks)
    {
        if (checks is null || checks.Count == 0)
        {
            return 100f;
        }

        var evaluated = checks.Where(c => c.Score is not null).ToList();
        if (evaluated.Count == 0)
        {
            return 100f;
        }

        int passed = evaluated.Count(c => c.Score == true);
        float score = (float)passed / evaluated.Count * 100f;
        return MathF.Round(score, 1);
    }

    /// <summary>
    /// Computes a tool-level score as a weighted sum of category scores.
    /// Missing categories default to 100 (no deductions).
    /// </summary>
    /// <param name="categoryScores">
    /// Per-category scores keyed by category name (e.g., "tool_name", "tool_description").
    /// </param>
    /// <returns>Weighted score from 0 to 100, rounded to 1 decimal place.</returns>
    public static float ComputeToolScore(Dictionary<string, float> categoryScores)
    {
        if (categoryScores is null)
        {
            return 100f;
        }

        float overall = 0f;
        foreach (var (category, weight) in CategoryWeights)
        {
            float catScore = categoryScores.GetValueOrDefault(category, 100f);
            overall += catScore * weight;
        }

        return MathF.Round(overall, 1);
    }

    /// <summary>
    /// Computes the overall server score blending tool-level and toolset-level scores.
    /// Formula: (meanToolScore * 0.85) + (toolsetScore * 0.15).
    /// Returns toolsetScore * 0.15 if there are no tools.
    /// </summary>
    /// <param name="toolResults">Evaluation results for each tool.</param>
    /// <param name="toolsetScore">Score from toolset-level (cross-tool) checks.</param>
    /// <returns>Overall score from 0 to 100, rounded to 1 decimal place.</returns>
    public static float ComputeOverallScore(List<ToolEvalResult> toolResults, float toolsetScore)
    {
        if (toolResults is null || toolResults.Count == 0)
        {
            return MathF.Round(toolsetScore * ToolsetWeight, 1);
        }

        float meanToolScore = toolResults.Average(t => t.Score);
        float overall = (meanToolScore * ToolWeight) + (toolsetScore * ToolsetWeight);
        return MathF.Round(overall, 1);
    }

    /// <summary>
    /// Computes average category scores across all tool results.
    /// Each category is averaged independently across all tools that have a score for it.
    /// </summary>
    /// <param name="toolResults">Evaluation results for each tool.</param>
    /// <returns>Dictionary of category name to average score, rounded to 1 decimal.</returns>
    public static Dictionary<string, float> ComputeCategoryAverages(List<ToolEvalResult> toolResults)
    {
        if (toolResults is null || toolResults.Count == 0)
        {
            return [];
        }

        var accumulator = new Dictionary<string, List<float>>();
        foreach (var toolResult in toolResults)
        {
            foreach (var (category, score) in toolResult.CategoryScores)
            {
                if (!accumulator.TryGetValue(category, out var scores))
                {
                    scores = [];
                    accumulator[category] = scores;
                }

                scores.Add(score);
            }
        }

        return accumulator.ToDictionary(
            kvp => kvp.Key,
            kvp => MathF.Round(kvp.Value.Average(), 1));
    }
}
