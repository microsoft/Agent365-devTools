// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Orchestrates Step 4 of the evaluation pipeline: takes an evaluated checklist
/// and produces a <see cref="SchemaEvalResult"/> containing per-tool scores,
/// toolset score, overall score, maturity level, and prioritized action items.
/// </summary>
internal sealed class EvaluationAnalyzer : IEvaluationAnalyzer
{
    private readonly ILogger<EvaluationAnalyzer> _logger;

    public EvaluationAnalyzer(ILogger<EvaluationAnalyzer> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public SchemaEvalResult Analyze(EvaluationChecklist checklist, string evalEngine)
    {
        ArgumentNullException.ThrowIfNull(checklist);
        evalEngine ??= string.Empty;

        _logger.LogDebug("Analyzing evaluation checklist for server {ServerName}", checklist.Metadata.ServerName);

        // Step 1: Build per-tool results
        var toolResults = new List<ToolEvalResult>();
        foreach (var tool in checklist.Tools)
        {
            var toolResult = AnalyzeTool(tool);
            toolResults.Add(toolResult);
        }

        // Step 2: Compute toolset (server-level) result
        var toolsetResult = AnalyzeToolset(checklist.ServerChecks);

        // Step 3: Compute overall score and category averages
        float overallScore = Scorer.ComputeOverallScore(toolResults, toolsetResult.Score);
        var categoryAverages = Scorer.ComputeCategoryAverages(toolResults);

        // Step 4: Determine maturity level
        var maturity = MaturityCalculator.DetermineLevel(overallScore, categoryAverages);

        // Step 5: Aggregate all action items, sorted by priority
        var allActionItems = new List<ActionItem>();
        foreach (var toolResult in toolResults)
        {
            allActionItems.AddRange(toolResult.ActionItems);
        }

        allActionItems.AddRange(toolsetResult.ActionItems);
        allActionItems.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        // Step 6: Compute smell summary (smell ID to count of occurrences)
        var smellSummary = ComputeSmellSummary(allActionItems);

        // Step 7: Compute action items by priority
        var actionItemsByPriority = ComputeActionItemsByPriority(allActionItems);

        _logger.LogDebug(
            "Analysis complete: overall score {OverallScore}, maturity level {MaturityLevel} ({MaturityLabel}), {ActionItemCount} action items",
            overallScore,
            maturity.Level,
            maturity.Label,
            allActionItems.Count);

        return new SchemaEvalResult
        {
            ServerName = checklist.Metadata.ServerName,
            ServerUrl = checklist.Metadata.ServerUrl,
            EvaluatedAt = DateTime.UtcNow,
            OverallScore = overallScore,
            Maturity = maturity,
            ToolCount = checklist.Tools.Count,
            ToolResults = toolResults,
            ToolsetResult = toolsetResult,
            AllActionItems = allActionItems,
            CategoryAverages = categoryAverages,
            ActionItemsByPriority = actionItemsByPriority,
            SmellSummary = smellSummary,
            EvalEngine = evalEngine,
        };
    }

    /// <summary>
    /// Analyzes a single tool's checklist, computing category scores, tool score,
    /// action items, and detected smells.
    /// </summary>
    private static ToolEvalResult AnalyzeTool(ToolChecklist tool)
    {
        // Flatten all checks across categories for this tool
        var allChecks = FlattenToolChecks(tool);

        // Compute per-category scores
        var categoryScores = new Dictionary<string, float>();

        categoryScores["tool_name"] = Scorer.ComputeCategoryScore(tool.Checks.ToolName);
        categoryScores["tool_description"] = Scorer.ComputeCategoryScore(tool.Checks.ToolDescription);
        categoryScores["schema_structure"] = Scorer.ComputeCategoryScore(tool.Checks.SchemaStructure);

        // Aggregate param_name and param_description scores across all parameters
        var allParamNameChecks = new List<ChecklistItem>();
        var allParamDescriptionChecks = new List<ChecklistItem>();

        foreach (var paramGroup in tool.Checks.Parameters.Values)
        {
            allParamNameChecks.AddRange(paramGroup.ParamName);
            allParamDescriptionChecks.AddRange(paramGroup.ParamDescription);
        }

        categoryScores["param_name"] = Scorer.ComputeCategoryScore(allParamNameChecks);
        categoryScores["param_description"] = Scorer.ComputeCategoryScore(allParamDescriptionChecks);

        // Compute tool score from category scores
        float toolScore = Scorer.ComputeToolScore(categoryScores);

        // Generate action items from all checks
        var actionItems = ActionItemGenerator.GenerateFromAllChecks(allChecks, tool.Name);

        // Collect unique smell IDs from action items, sorted
        var smellsDetected = actionItems
            .SelectMany(a => a.SmellIds)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        // Count parameters from the input schema
        int paramCount = tool.Checks.Parameters.Count;

        return new ToolEvalResult
        {
            ToolName = tool.Name,
            ToolDescription = tool.Description,
            ParamCount = paramCount,
            Score = toolScore,
            CategoryScores = categoryScores,
            Checks = allChecks,
            ActionItems = actionItems,
            SmellsDetected = smellsDetected,
            InputSchema = tool.InputSchema,
        };
    }

    /// <summary>
    /// Flattens all checks from a tool's check groups into a single list.
    /// Includes ToolName, ToolDescription, SchemaStructure, and all parameter checks.
    /// </summary>
    private static List<ChecklistItem> FlattenToolChecks(ToolChecklist tool)
    {
        var checks = new List<ChecklistItem>();

        checks.AddRange(tool.Checks.ToolName);
        checks.AddRange(tool.Checks.ToolDescription);
        checks.AddRange(tool.Checks.SchemaStructure);

        foreach (var paramGroup in tool.Checks.Parameters.Values)
        {
            checks.AddRange(paramGroup.ParamName);
            checks.AddRange(paramGroup.ParamDescription);
        }

        return checks;
    }

    /// <summary>
    /// Analyzes toolset-level (server/cross-tool) checks, computing score and action items.
    /// </summary>
    private static ToolsetEvalResult AnalyzeToolset(List<ChecklistItem> serverChecks)
    {
        if (serverChecks is null || serverChecks.Count == 0)
        {
            return new ToolsetEvalResult
            {
                Score = 100f,
                Checks = [],
                ActionItems = [],
            };
        }

        float score = Scorer.ComputeCategoryScore(serverChecks);
        var actionItems = ActionItemGenerator.GenerateFromAllChecks(serverChecks, null);

        return new ToolsetEvalResult
        {
            Score = score,
            Checks = serverChecks,
            ActionItems = actionItems,
        };
    }

    /// <summary>
    /// Computes a summary of smell occurrences across all action items.
    /// Returns a dictionary of smell name to occurrence count.
    /// </summary>
    private static Dictionary<string, int> ComputeSmellSummary(List<ActionItem> actionItems)
    {
        var smellCounts = new Dictionary<int, int>();
        foreach (var item in actionItems)
        {
            foreach (int smellId in item.SmellIds)
            {
                smellCounts[smellId] = smellCounts.GetValueOrDefault(smellId) + 1;
            }
        }

        var summary = new Dictionary<string, int>();
        foreach (var (smellId, count) in smellCounts.OrderByDescending(kvp => kvp.Value))
        {
            string name = SmellTaxonomy.Definitions.TryGetValue(smellId, out var smell)
                ? smell.Name
                : smellId.ToString(CultureInfo.InvariantCulture);
            summary[name] = count;
        }

        return summary;
    }

    /// <summary>
    /// Computes the count of action items per priority level.
    /// </summary>
    private static Dictionary<string, int> ComputeActionItemsByPriority(List<ActionItem> actionItems)
    {
        var counts = new Dictionary<string, int>
        {
            ["P0"] = 0,
            ["P1"] = 0,
            ["P2"] = 0,
            ["P3"] = 0,
        };

        foreach (var item in actionItems)
        {
            string key = item.Priority.ToString();
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts;
    }
}
