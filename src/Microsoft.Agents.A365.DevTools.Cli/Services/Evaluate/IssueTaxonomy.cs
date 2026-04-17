// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Catalog of known schema-quality issues for MCP tool schemas, each with an
/// id, category, description, and the areas it impacts. Checklist items
/// reference these ids via <c>IssueIds</c> so the report can link every
/// failed check back to the concrete issue it represents.
/// </summary>
internal static class IssueTaxonomy
{
    /// <summary>
    /// All known issues indexed by their id.
    /// </summary>
    public static readonly Dictionary<int, IssueDefinition> Definitions = new()
    {
        // -- Accuracy --

        [1] = new IssueDefinition
        {
            Id = 1,
            Name = "Incorrect parameter semantics",
            Category = IssueCategory.Accuracy,
            Description = "Description says one thing, tool does another",
            Impact = "LLM provides structurally valid but semantically wrong arguments",
            ImpactAreas = [ImpactArea.ParamAccuracy],
        },
        [2] = new IssueDefinition
        {
            Id = 2,
            Name = "Misleading behavior claims",
            Category = IssueCategory.Accuracy,
            Description = "Tool can't do what description promises",
            Impact = "LLM selects tool for unsupported operations, causing failures",
            ImpactAreas = [ImpactArea.ToolSelection],
        },
        [3] = new IssueDefinition
        {
            Id = 3,
            Name = "Wrong default values documented",
            Category = IssueCategory.Accuracy,
            Description = "Actual defaults differ from described defaults",
            Impact = "LLM omits parameters expecting documented default, gets unexpected behavior",
            ImpactAreas = [ImpactArea.ParamAccuracy],
        },

        // -- Functionality --

        [4] = new IssueDefinition
        {
            Id = 4,
            Name = "Missing purpose statement",
            Category = IssueCategory.Functionality,
            Description = "No verb phrase explaining what the tool does",
            Impact = "LLM cannot determine when to use the tool; selection drops sharply",
            ImpactAreas = [ImpactArea.ToolSelection],
        },
        [5] = new IssueDefinition
        {
            Id = 5,
            Name = "Missing usage guidelines",
            Category = IssueCategory.Functionality,
            Description = "No 'use this when...' conditional guidance",
            Impact = "LLM applies tool in wrong context (e.g., search vs list)",
            ImpactAreas = [ImpactArea.ToolSelection],
        },
        [6] = new IssueDefinition
        {
            Id = 6,
            Name = "Missing limitation statements",
            Category = IssueCategory.Functionality,
            Description = "No 'this tool does not...' negation",
            Impact = "LLM attempts impossible operations (e.g., delete via read-only tool)",
            ImpactAreas = [ImpactArea.ToolSelection, ImpactArea.Completeness],
        },
        [7] = new IssueDefinition
        {
            Id = 7,
            Name = "Missing error behavior documentation",
            Category = IssueCategory.Functionality,
            Description = "No failure mode or error response descriptions",
            Impact = "LLM cannot handle errors gracefully or retry appropriately",
            ImpactAreas = [ImpactArea.Completeness],
        },

        // -- Completeness --

        [8] = new IssueDefinition
        {
            Id = 8,
            Name = "Missing return value documentation",
            Category = IssueCategory.Completeness,
            Description = "No output description for tool results",
            Impact = "LLM misinterprets output, causing cascading failures in multi-step chains",
            ImpactAreas = [ImpactArea.Completeness],
        },
        [9] = new IssueDefinition
        {
            Id = 9,
            Name = "Missing parameter descriptions",
            Category = IssueCategory.Completeness,
            Description = "Parameters without explanation",
            Impact = "LLM must guess what each parameter means from name alone",
            ImpactAreas = [ImpactArea.ParamAccuracy, ImpactArea.Completeness],
        },
        [10] = new IssueDefinition
        {
            Id = 10,
            Name = "Missing examples",
            Category = IssueCategory.Completeness,
            Description = "No concrete usage demonstrations",
            Impact = "Reduced comprehension for complex input structures or unusual formats",
            ImpactAreas = [ImpactArea.ParamAccuracy, ImpactArea.Completeness],
        },
        [11] = new IssueDefinition
        {
            Id = 11,
            Name = "Missing format specifications",
            Category = IssueCategory.Completeness,
            Description = "Date/time/ID formats undocumented",
            Impact = "LLM guesses format -- '2026-03-23' vs 'March 23' vs '03/23/26'",
            ImpactAreas = [ImpactArea.ParamAccuracy],
        },
        [12] = new IssueDefinition
        {
            Id = 12,
            Name = "Missing prerequisite documentation",
            Category = IssueCategory.Completeness,
            Description = "Dependencies and prerequisites unstated",
            Impact = "LLM invokes tool without required prior steps, causing failures",
            ImpactAreas = [ImpactArea.Completeness],
        },

        // -- Conciseness --

        [13] = new IssueDefinition
        {
            Id = 13,
            Name = "Tool name repeated in description",
            Category = IssueCategory.Conciseness,
            Description = "Description restates tool name without adding info",
            Impact = "Zero added information; wastes context window tokens",
            ImpactAreas = [ImpactArea.Conciseness],
        },
        [14] = new IssueDefinition
        {
            Id = 14,
            Name = "Excessive boilerplate",
            Category = IssueCategory.Conciseness,
            Description = "Generic text not specific to the tool",
            Impact = "Dilutes useful information and inflates step count for over-specified descriptions",
            ImpactAreas = [ImpactArea.Conciseness],
        },
        [15] = new IssueDefinition
        {
            Id = 15,
            Name = "Redundant parameter re-description",
            Category = IssueCategory.Conciseness,
            Description = "Tool description re-describes parameters already described in schema",
            Impact = "Wastes tokens, may create conflicting descriptions",
            ImpactAreas = [ImpactArea.Conciseness],
        },
        [16] = new IssueDefinition
        {
            Id = 16,
            Name = "Overly technical jargon",
            Category = IssueCategory.Conciseness,
            Description = "Implementation details instead of behavior descriptions",
            Impact = "LLM focuses on internal mechanics rather than user-facing outcomes",
            ImpactAreas = [ImpactArea.Conciseness, ImpactArea.ToolSelection],
        },

        // -- Cross-tool consistency --

        [17] = new IssueDefinition
        {
            Id = 17,
            Name = "Inconsistent terminology across tools",
            Category = IssueCategory.Accuracy,
            Description = "Same concept named differently in different tools",
            Impact = "LLM uses wrong parameter values when chaining tools together",
            ImpactAreas = [ImpactArea.ParamAccuracy, ImpactArea.ToolSelection],
        },
        [18] = new IssueDefinition
        {
            Id = 18,
            Name = "Ambiguous scope of operation",
            Category = IssueCategory.Functionality,
            Description = "Unclear whether tool operates on single item, collection, or hierarchy",
            Impact = "LLM calls tool with wrong cardinality expectations",
            ImpactAreas = [ImpactArea.ToolSelection, ImpactArea.ParamAccuracy],
        },
    };

    /// <summary>
    /// Returns an impact map keyed by issue id (as string) for the HTML report.
    /// Each entry provides the issue name, category, impact description, and affected areas.
    /// </summary>
    public static Dictionary<string, IssueImpactInfo> GetImpactMap()
    {
        var map = new Dictionary<string, IssueImpactInfo>();
        foreach (var (id, issue) in Definitions)
        {
            map[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new IssueImpactInfo
            {
                Name = issue.Name,
                Category = issue.Category.ToString(),
                Impact = issue.Impact,
                Areas = issue.ImpactAreas.Select(a => a.ToString()).ToList(),
            };
        }

        return map;
    }
}
