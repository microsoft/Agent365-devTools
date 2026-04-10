// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Generates prioritized action items from failed evaluation checks.
/// Each failed check produces an action item with calculated score impact
/// and mapped smell impact descriptions from the taxonomy.
/// </summary>
public static class ActionItemGenerator
{
    /// <summary>
    /// Generates action items from failed checks, sorted by priority (P0 first).
    /// For each check with Score == false, creates an ActionItem with calculated
    /// score impact and resolved smell impact descriptions.
    /// </summary>
    /// <param name="checks">All checks for the scope (tool or toolset).</param>
    /// <param name="toolName">Tool name, or null for toolset-level checks.</param>
    /// <param name="paramName">Parameter name, or null for tool-level checks.</param>
    /// <param name="categoryWeights">Category weight mapping (category name to weight 0-1).</param>
    /// <param name="totalChecksInCategory">
    /// Total number of checks in the category. Used to compute per-check score impact.
    /// </param>
    /// <returns>Action items sorted by priority (P0, P1, P2, P3).</returns>
    public static List<ActionItem> GenerateFromChecks(
        List<ChecklistItem> checks,
        string? toolName,
        string? paramName,
        Dictionary<string, float> categoryWeights,
        int totalChecksInCategory)
    {
        if (checks is null || checks.Count == 0)
        {
            return [];
        }

        categoryWeights ??= [];

        var items = new List<ActionItem>();

        foreach (var check in checks)
        {
            if (check.Score != false)
            {
                continue;
            }

            string categoryKey = CategoryToKey(check.Category);
            float weight = categoryWeights.GetValueOrDefault(categoryKey, 0.15f);
            int effectiveTotal = Math.Max(totalChecksInCategory, 1);
            float scoreImpact = MathF.Round((weight * 100f) / effectiveTotal, 1);

            List<string> issueLeadsTo = ResolveSmellImpacts(check.SmellIds);

            items.Add(new ActionItem
            {
                ToolName = toolName,
                ParamName = paramName,
                Priority = check.Severity,
                Title = check.Prompt,
                Description = check.Reason ?? string.Empty,
                SmellIds = check.SmellIds,
                ImpactAreas = check.ImpactAreas,
                Remediation = check.Remediation,
                ScoreImpact = scoreImpact,
                IssueLeadsTo = issueLeadsTo,
            });
        }

        items.Sort(CompareByPriority);
        return items;
    }

    /// <summary>
    /// Generates action items for a flat list of checks, computing category-level
    /// score impacts. Groups checks by category to determine per-check weight.
    /// </summary>
    /// <param name="checks">All checks for a tool or toolset scope.</param>
    /// <param name="toolName">Tool name, or null for toolset-level checks.</param>
    /// <returns>Action items sorted by priority (P0 first).</returns>
    public static List<ActionItem> GenerateFromAllChecks(
        List<ChecklistItem> checks,
        string? toolName)
    {
        if (checks is null || checks.Count == 0)
        {
            return [];
        }

        var items = new List<ActionItem>();
        var checksByCategory = checks.GroupBy(c => c.Category)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var check in checks)
        {
            if (check.Score != false)
            {
                continue;
            }

            string categoryKey = CategoryToKey(check.Category);
            float weight = Scorer.CategoryWeights.GetValueOrDefault(categoryKey, 0.15f);
            int categoryTotal = checksByCategory.TryGetValue(check.Category, out var catChecks)
                ? catChecks.Count
                : 1;
            float scoreImpact = MathF.Round((weight * 100f) / Math.Max(categoryTotal, 1), 1);

            List<string> issueLeadsTo = ResolveSmellImpacts(check.SmellIds);

            items.Add(new ActionItem
            {
                ToolName = toolName,
                ParamName = null,
                Priority = check.Severity,
                Title = check.Prompt,
                Description = check.Reason ?? string.Empty,
                SmellIds = check.SmellIds,
                ImpactAreas = check.ImpactAreas,
                Remediation = check.Remediation,
                ScoreImpact = scoreImpact,
                IssueLeadsTo = issueLeadsTo,
            });
        }

        items.Sort(CompareByPriority);
        return items;
    }

    /// <summary>
    /// Resolves smell IDs to their human-readable impact descriptions
    /// using the SmellTaxonomy definitions.
    /// </summary>
    private static List<string> ResolveSmellImpacts(List<int> smellIds)
    {
        if (smellIds is null || smellIds.Count == 0)
        {
            return [];
        }

        var impacts = new List<string>();
        foreach (int smellId in smellIds)
        {
            if (SmellTaxonomy.Definitions.TryGetValue(smellId, out var smell))
            {
                impacts.Add(smell.Impact);
            }
        }

        return impacts;
    }

    /// <summary>
    /// Converts a <see cref="CheckCategory"/> enum value to the snake_case key
    /// used in category weight dictionaries.
    /// </summary>
    private static string CategoryToKey(CheckCategory category) => category switch
    {
        CheckCategory.ToolName => "tool_name",
        CheckCategory.ToolDescription => "tool_description",
        CheckCategory.ParamName => "param_name",
        CheckCategory.ParamDescription => "param_description",
        CheckCategory.SchemaStructure => "schema_structure",
        CheckCategory.ToolsetDesign => "toolset_design",
        _ => "schema_structure",
    };

    /// <summary>
    /// Compares two action items by priority ordinal (P0=0, P1=1, P2=2, P3=3).
    /// </summary>
    private static int CompareByPriority(ActionItem a, ActionItem b) => a.Priority.CompareTo(b.Priority);
}
