// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// The 18-smell taxonomy for MCP tool schema evaluation.
/// Based on Li et al. (arXiv:2602.18914) -- 10,831 MCP servers analyzed.
/// Extended with structural and cross-tool smells from Hasan et al. (arXiv:2602.14878).
/// </summary>
internal static class SmellTaxonomy
{
    /// <summary>
    /// All 18 smells indexed by their ID.
    /// </summary>
    public static readonly Dictionary<int, SmellDefinition> Definitions = new()
    {
        // -- Accuracy (3) --

        [1] = new SmellDefinition
        {
            Id = 1,
            Name = "Incorrect parameter semantics",
            Category = SmellCategory.Accuracy,
            Description = "Description says one thing, tool does another",
            Impact = "LLM provides structurally valid but semantically wrong arguments",
            ImpactAreas = [ImpactArea.ParamAccuracy],
        },
        [2] = new SmellDefinition
        {
            Id = 2,
            Name = "Misleading behavior claims",
            Category = SmellCategory.Accuracy,
            Description = "Tool can't do what description promises",
            Impact = "LLM selects tool for unsupported operations, causing failures",
            ImpactAreas = [ImpactArea.ToolSelection],
        },
        [3] = new SmellDefinition
        {
            Id = 3,
            Name = "Wrong default values documented",
            Category = SmellCategory.Accuracy,
            Description = "Actual defaults differ from described defaults",
            Impact = "LLM omits parameters expecting documented default, gets unexpected behavior",
            ImpactAreas = [ImpactArea.ParamAccuracy],
        },

        // -- Functionality (4) --

        [4] = new SmellDefinition
        {
            Id = 4,
            Name = "Missing purpose statement",
            Category = SmellCategory.Functionality,
            Description = "No verb phrase explaining what tool does (56% prevalence)",
            Impact = "LLM cannot determine when to use the tool; selection drops sharply",
            ImpactAreas = [ImpactArea.ToolSelection],
        },
        [5] = new SmellDefinition
        {
            Id = 5,
            Name = "Missing usage guidelines",
            Category = SmellCategory.Functionality,
            Description = "No 'use this when...' conditional guidance",
            Impact = "LLM applies tool in wrong context (e.g., search vs list)",
            ImpactAreas = [ImpactArea.ToolSelection],
        },
        [6] = new SmellDefinition
        {
            Id = 6,
            Name = "Missing limitation statements",
            Category = SmellCategory.Functionality,
            Description = "No 'this tool does not...' negation",
            Impact = "LLM attempts impossible operations (e.g., delete via read-only tool)",
            ImpactAreas = [ImpactArea.ToolSelection, ImpactArea.Completeness],
        },
        [7] = new SmellDefinition
        {
            Id = 7,
            Name = "Missing error behavior documentation",
            Category = SmellCategory.Functionality,
            Description = "No failure mode or error response descriptions",
            Impact = "LLM cannot handle errors gracefully or retry appropriately",
            ImpactAreas = [ImpactArea.Completeness],
        },

        // -- Completeness (5) --

        [8] = new SmellDefinition
        {
            Id = 8,
            Name = "Missing return value documentation",
            Category = SmellCategory.Completeness,
            Description = "No output description for tool results",
            Impact = "LLM misinterprets output, causing cascading failures in multi-step chains",
            ImpactAreas = [ImpactArea.Completeness],
        },
        [9] = new SmellDefinition
        {
            Id = 9,
            Name = "Missing parameter descriptions",
            Category = SmellCategory.Completeness,
            Description = "Parameters without explanation (38% more omission errors)",
            Impact = "LLM must guess what each parameter means from name alone",
            ImpactAreas = [ImpactArea.ParamAccuracy, ImpactArea.Completeness],
        },
        [10] = new SmellDefinition
        {
            Id = 10,
            Name = "Missing examples",
            Category = SmellCategory.Completeness,
            Description = "No concrete usage demonstrations",
            Impact = "Reduced comprehension for complex input structures or unusual formats",
            ImpactAreas = [ImpactArea.ParamAccuracy, ImpactArea.Completeness],
        },
        [11] = new SmellDefinition
        {
            Id = 11,
            Name = "Missing format specifications",
            Category = SmellCategory.Completeness,
            Description = "Date/time/ID formats undocumented",
            Impact = "LLM guesses format -- '2026-03-23' vs 'March 23' vs '03/23/26'",
            ImpactAreas = [ImpactArea.ParamAccuracy],
        },
        [12] = new SmellDefinition
        {
            Id = 12,
            Name = "Missing prerequisite documentation",
            Category = SmellCategory.Completeness,
            Description = "Dependencies and prerequisites unstated",
            Impact = "LLM invokes tool without required prior steps, causing failures",
            ImpactAreas = [ImpactArea.Completeness],
        },

        // -- Conciseness (4) --

        [13] = new SmellDefinition
        {
            Id = 13,
            Name = "Tool name repeated in description",
            Category = SmellCategory.Conciseness,
            Description = "Description restates tool name without adding info (73% prevalence)",
            Impact = "Zero added information; wastes context window tokens",
            ImpactAreas = [ImpactArea.Conciseness],
        },
        [14] = new SmellDefinition
        {
            Id = 14,
            Name = "Excessive boilerplate",
            Category = SmellCategory.Conciseness,
            Description = "Generic text not specific to the tool",
            Impact = "Dilutes useful information; +67% more execution steps with over-specified descriptions",
            ImpactAreas = [ImpactArea.Conciseness],
        },
        [15] = new SmellDefinition
        {
            Id = 15,
            Name = "Redundant parameter re-description",
            Category = SmellCategory.Conciseness,
            Description = "Tool description re-describes parameters already described in schema",
            Impact = "Wastes tokens, may create conflicting descriptions",
            ImpactAreas = [ImpactArea.Conciseness],
        },
        [16] = new SmellDefinition
        {
            Id = 16,
            Name = "Overly technical jargon",
            Category = SmellCategory.Conciseness,
            Description = "Implementation details instead of behavior descriptions",
            Impact = "LLM focuses on internal mechanics rather than user-facing outcomes",
            ImpactAreas = [ImpactArea.Conciseness, ImpactArea.ToolSelection],
        },

        // -- Extended (2) -- derived from cross-tool analysis --

        [17] = new SmellDefinition
        {
            Id = 17,
            Name = "Inconsistent terminology across tools",
            Category = SmellCategory.Accuracy,
            Description = "Same concept named differently in different tools",
            Impact = "LLM uses wrong parameter values when chaining tools together",
            ImpactAreas = [ImpactArea.ParamAccuracy, ImpactArea.ToolSelection],
        },
        [18] = new SmellDefinition
        {
            Id = 18,
            Name = "Ambiguous scope of operation",
            Category = SmellCategory.Functionality,
            Description = "Unclear whether tool operates on single item, collection, or hierarchy",
            Impact = "LLM calls tool with wrong cardinality expectations",
            ImpactAreas = [ImpactArea.ToolSelection, ImpactArea.ParamAccuracy],
        },
    };

    /// <summary>
    /// Returns an impact map keyed by smell ID (as string) for the HTML report.
    /// Each entry provides the smell name, category, impact description, and affected areas.
    /// </summary>
    public static Dictionary<string, SmellImpactInfo> GetImpactMap()
    {
        var map = new Dictionary<string, SmellImpactInfo>();
        foreach (var (id, smell) in Definitions)
        {
            map[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new SmellImpactInfo
            {
                Name = smell.Name,
                Category = smell.Category.ToString(),
                Impact = smell.Impact,
                Areas = smell.ImpactAreas.Select(a => a.ToString()).ToList(),
            };
        }

        return map;
    }
}
