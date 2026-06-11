// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Determines MCP server maturity level (0-4) from overall score and category averages.
/// Inspired by the Richardson Maturity Model for REST APIs, adapted for AI agent consumption.
/// Score thresholds map to levels, but weak critical categories cap the achievable level.
/// </summary>
public static class MaturityCalculator
{
    /// <summary>
    /// Level definitions with label and description.
    /// Index corresponds to the level number (0-4).
    /// </summary>
    private static readonly (string Label, string Description)[] LevelDefinitions =
    [
        (
            "Functional",
            "Tools exist with names and minimal schemas. " +
            "Major quality gaps make reliable AI agent usage unlikely."
        ),
        (
            "Described",
            "All tools and parameters have meaningful descriptions. " +
            "Input/output schemas are fully defined."
        ),
        (
            "Consistent",
            "Naming conventions followed across all tools. " +
            "Error handling documented. Cross-tool consistency maintained."
        ),
        (
            "Optimized for AI",
            "Descriptions tuned for LLM comprehension. " +
            "Disambiguation between similar tools. " +
            "Defensive parameter constraints. Structured output schemas."
        ),
        (
            "Exemplary",
            "Usage examples included. Semantic tool grouping. " +
            "Complete intent coverage for domain. " +
            "Versioned and backward-compatible."
        ),
    ];

    /// <summary>
    /// Determines the maturity level from the overall score and category averages.
    /// Score thresholds: Level 0 (&lt; 40), Level 1 (40-59), Level 2 (60-74), Level 3 (75-89), Level 4 (90+).
    /// Category caps prevent inflated levels when critical categories are weak:
    /// tool_description avg &lt; 50 caps at Level 1, param_description avg &lt; 60 caps at Level 2,
    /// tool_name avg &lt; 75 caps at Level 3.
    /// </summary>
    /// <param name="overallScore">Overall server score (0-100).</param>
    /// <param name="categoryAverages">Average scores per category across all tools.</param>
    /// <returns>Maturity level with label, description, and requirements for next level.</returns>
    public static MaturityLevel DetermineLevel(float overallScore, Dictionary<string, float> categoryAverages)
    {
        categoryAverages ??= [];

        // Determine score-based level
        int level;
        if (overallScore >= 90f)
        {
            level = 4;
        }
        else if (overallScore >= 75f)
        {
            level = 3;
        }
        else if (overallScore >= 60f)
        {
            level = 2;
        }
        else if (overallScore >= 40f)
        {
            level = 1;
        }
        else
        {
            level = 0;
        }

        // Apply category-based caps
        float descriptionAvg = categoryAverages.GetValueOrDefault("tool_description", 0f);
        float paramDescriptionAvg = categoryAverages.GetValueOrDefault("param_description", 0f);
        float nameAvg = categoryAverages.GetValueOrDefault("tool_name", 0f);

        // Cannot reach Level 2+ without decent tool descriptions
        if (descriptionAvg < 50f && level >= 2)
        {
            level = 1;
        }

        // Cannot reach Level 3+ without good parameter descriptions
        if (paramDescriptionAvg < 60f && level >= 3)
        {
            level = 2;
        }

        // Cannot reach Level 4 without strong naming
        if (nameAvg < 75f && level >= 4)
        {
            level = 3;
        }

        var definition = LevelDefinitions[level];
        var nextRequirements = GetNextLevelRequirements(level, categoryAverages);

        return new MaturityLevel
        {
            Level = level,
            Label = definition.Label,
            Description = definition.Description,
            NextLevelRequirements = nextRequirements,
        };
    }

    /// <summary>
    /// Builds the maturity ladder showing all 5 levels with the current level flagged.
    /// Used by the HTML report to render the visual maturity progression.
    /// </summary>
    /// <param name="currentLevel">The current maturity level (0-4).</param>
    /// <returns>All 5 maturity levels with <c>IsCurrent</c> set for the active level.</returns>
    public static List<MaturityLadderEntry> GetMaturityLadder(int currentLevel)
    {
        var ladder = new List<MaturityLadderEntry>(LevelDefinitions.Length);
        for (int i = 0; i < LevelDefinitions.Length; i++)
        {
            var definition = LevelDefinitions[i];
            ladder.Add(new MaturityLadderEntry
            {
                Level = i,
                Label = definition.Label,
                Description = definition.Description,
                IsCurrent = i == currentLevel,
            });
        }

        return ladder;
    }

    /// <summary>
    /// Generates concrete, actionable requirements for reaching the next maturity level.
    /// </summary>
    private static List<string> GetNextLevelRequirements(
        int currentLevel,
        Dictionary<string, float> categoryAverages)
    {
        if (currentLevel >= 4)
        {
            return ["Maintain current quality standards."];
        }

        var requirements = new List<string>();

        switch (currentLevel)
        {
            case 0:
                requirements.Add("Add meaningful descriptions to all tools (target: every tool describes its purpose).");
                requirements.Add("Ensure all parameters have type definitions in the schema.");
                requirements.Add("Add descriptions to all parameters.");
                break;

            case 1:
                requirements.Add("Standardize naming conventions across all tools (use consistent verb_noun pattern).");
                requirements.Add("Ensure cross-tool consistency in parameter naming and types.");
                if (categoryAverages.GetValueOrDefault("tool_description", 0f) < 70f)
                {
                    requirements.Add("Improve tool descriptions to include usage guidelines and limitations.");
                }

                break;

            case 2:
                requirements.Add("Add usage guidelines ('Use this when...') to all tool descriptions.");
                requirements.Add("Add limitation statements to all tool descriptions.");
                requirements.Add("Define enum constraints for categorical parameters.");
                if (categoryAverages.GetValueOrDefault("param_description", 0f) < 75f)
                {
                    requirements.Add("Improve parameter descriptions with format specifications and examples.");
                }

                break;

            case 3:
                requirements.Add("Add concrete usage examples to all tool descriptions.");
                requirements.Add("Ensure complete intent coverage for the server's domain.");
                requirements.Add("Add return value documentation to all tools.");
                break;
        }

        return requirements;
    }
}
