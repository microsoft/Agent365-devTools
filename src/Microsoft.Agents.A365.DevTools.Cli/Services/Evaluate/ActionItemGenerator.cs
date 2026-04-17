// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Generates prioritized action items from failed evaluation checks.
/// Each failed check produces an action item with calculated score impact
/// and mapped issue impact descriptions from the taxonomy.
/// </summary>
public static class ActionItemGenerator
{
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
            // Toolset-level checks are scored separately from per-tool categories in Scorer.
            // Route them to ToolsetWeight explicitly so action-item impact stays aligned with scoring.
            float weight = check.Category == CheckCategory.ToolsetDesign
                ? Scorer.ToolsetWeight
                : Scorer.CategoryWeights.GetValueOrDefault(categoryKey, 0.15f);
            int categoryTotal = checksByCategory.TryGetValue(check.Category, out var catChecks)
                ? catChecks.Count
                : 1;
            float scoreImpact = MathF.Round((weight * 100f) / Math.Max(categoryTotal, 1), 1);

            List<string> issueLeadsTo = ResolveIssueImpacts(check.IssueIds);

            items.Add(new ActionItem
            {
                ToolName = toolName,
                ParamName = null,
                Priority = check.Severity,
                Title = check.Prompt,
                Description = check.Reason ?? string.Empty,
                IssueIds = check.IssueIds,
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
    /// Resolves issue ids to their human-readable impact descriptions
    /// using the IssueTaxonomy definitions.
    /// </summary>
    private static List<string> ResolveIssueImpacts(List<int> issueIds)
    {
        if (issueIds is null || issueIds.Count == 0)
        {
            return [];
        }

        var impacts = new List<string>();
        foreach (int issueId in issueIds)
        {
            if (IssueTaxonomy.Definitions.TryGetValue(issueId, out var issue))
            {
                impacts.Add(issue.Impact);
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
        _ => "unknown",
    };

    /// <summary>
    /// Compares two action items by priority ordinal (P0=0, P1=1, P2=2, P3=3).
    /// </summary>
    private static int CompareByPriority(ActionItem a, ActionItem b) => a.Priority.CompareTo(b.Priority);
}
