// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Evaluate;

public class ScorerTests
{
    // =======================================================================
    // ComputeCategoryScore
    // =======================================================================

    [Fact]
    public void ComputeCategoryScore_AllPass_Returns100()
    {
        var checks = new List<ChecklistItem>
        {
            new() { Score = true },
            new() { Score = true },
            new() { Score = true },
        };

        float result = Scorer.ComputeCategoryScore(checks);

        result.Should().Be(100f);
    }

    [Fact]
    public void ComputeCategoryScore_AllFail_Returns0()
    {
        var checks = new List<ChecklistItem>
        {
            new() { Score = false },
            new() { Score = false },
            new() { Score = false },
        };

        float result = Scorer.ComputeCategoryScore(checks);

        result.Should().Be(0f);
    }

    [Fact]
    public void ComputeCategoryScore_MixedResults_ReturnsCorrectPercentage()
    {
        var checks = new List<ChecklistItem>
        {
            new() { Score = true },
            new() { Score = false },
            new() { Score = true },
        };

        float result = Scorer.ComputeCategoryScore(checks);

        // 2/3 * 100 = 66.7
        result.Should().BeApproximately(66.7f, 0.1f);
    }

    [Fact]
    public void ComputeCategoryScore_NullScoresExcluded_CountsOnlyEvaluated()
    {
        var checks = new List<ChecklistItem>
        {
            new() { Score = true },
            new() { Score = null },
            new() { Score = false },
            new() { Score = null },
        };

        float result = Scorer.ComputeCategoryScore(checks);

        // Only 2 evaluated: 1 pass / 2 = 50%
        result.Should().Be(50f);
    }

    [Fact]
    public void ComputeCategoryScore_AllNull_Returns100()
    {
        var checks = new List<ChecklistItem>
        {
            new() { Score = null },
            new() { Score = null },
        };

        float result = Scorer.ComputeCategoryScore(checks);

        result.Should().Be(100f);
    }

    [Fact]
    public void ComputeCategoryScore_EmptyList_Returns100()
    {
        float result = Scorer.ComputeCategoryScore([]);

        result.Should().Be(100f);
    }

    // =======================================================================
    // ComputeToolScore
    // =======================================================================

    [Fact]
    public void ComputeToolScore_AllCategoriesPerfect_Returns100()
    {
        var categoryScores = new Dictionary<string, float>
        {
            ["tool_name"] = 100f,
            ["tool_description"] = 100f,
            ["param_name"] = 100f,
            ["param_description"] = 100f,
            ["schema_structure"] = 100f,
        };

        float result = Scorer.ComputeToolScore(categoryScores);

        result.Should().Be(100f);
    }

    [Fact]
    public void ComputeToolScore_AllCategoriesZero_Returns0()
    {
        var categoryScores = new Dictionary<string, float>
        {
            ["tool_name"] = 0f,
            ["tool_description"] = 0f,
            ["param_name"] = 0f,
            ["param_description"] = 0f,
            ["schema_structure"] = 0f,
        };

        float result = Scorer.ComputeToolScore(categoryScores);

        result.Should().Be(0f);
    }

    [Fact]
    public void ComputeToolScore_VerifyWeights()
    {
        // Set one category to 100 and all others to 0 to verify individual weights
        var categories = new[] { "tool_name", "tool_description", "param_name", "param_description", "schema_structure" };
        var expectedWeights = new Dictionary<string, float>
        {
            ["tool_name"] = 0.15f,
            ["tool_description"] = 0.35f,
            ["param_name"] = 0.10f,
            ["param_description"] = 0.25f,
            ["schema_structure"] = 0.15f,
        };

        foreach (string category in categories)
        {
            var scores = categories.ToDictionary(c => c, c => c == category ? 100f : 0f);
            float result = Scorer.ComputeToolScore(scores);

            float expectedWeight = expectedWeights[category] * 100f;
            result.Should().BeApproximately(expectedWeight, 0.1f,
                because: $"category '{category}' should have weight {expectedWeights[category]}");
        }
    }

    [Fact]
    public void ComputeToolScore_MissingCategories_DefaultTo100()
    {
        // Only one category present: tool_description=50, rest default to 100
        var categoryScores = new Dictionary<string, float>
        {
            ["tool_description"] = 50f,
        };

        float result = Scorer.ComputeToolScore(categoryScores);

        // 100*0.15 + 50*0.35 + 100*0.10 + 100*0.25 + 100*0.15 = 15 + 17.5 + 10 + 25 + 15 = 82.5
        result.Should().BeApproximately(82.5f, 0.1f);
    }

    [Fact]
    public void CategoryWeights_SumTo1()
    {
        float sum = Scorer.CategoryWeights.Values.Sum();

        sum.Should().BeApproximately(1.0f, 0.001f);
    }

    // =======================================================================
    // ComputeOverallScore
    // =======================================================================

    [Fact]
    public void ComputeOverallScore_VerifyBlend()
    {
        var toolResults = new List<ToolEvalResult>
        {
            new() { Score = 80f },
            new() { Score = 60f },
        };
        float toolsetScore = 90f;

        float result = Scorer.ComputeOverallScore(toolResults, toolsetScore);

        // meanTool = (80+60)/2 = 70
        // overall = 70 * 0.85 + 90 * 0.15 = 59.5 + 13.5 = 73.0
        result.Should().BeApproximately(73.0f, 0.1f);
    }

    [Fact]
    public void ComputeOverallScore_SingleTool_CorrectBlend()
    {
        var toolResults = new List<ToolEvalResult>
        {
            new() { Score = 100f },
        };
        float toolsetScore = 100f;

        float result = Scorer.ComputeOverallScore(toolResults, toolsetScore);

        // 100 * 0.85 + 100 * 0.15 = 100
        result.Should().Be(100f);
    }

    [Fact]
    public void ComputeOverallScore_EmptyTools_ReturnsToolsetOnly()
    {
        float toolsetScore = 80f;

        float result = Scorer.ComputeOverallScore([], toolsetScore);

        // 80 * 0.15 = 12.0
        result.Should().BeApproximately(12.0f, 0.1f);
    }

    [Fact]
    public void ToolWeight_Is085()
    {
        Scorer.ToolWeight.Should().Be(0.85f);
    }

    [Fact]
    public void ToolsetWeight_Is015()
    {
        Scorer.ToolsetWeight.Should().Be(0.15f);
    }

    // =======================================================================
    // ComputeCategoryAverages
    // =======================================================================

    [Fact]
    public void ComputeCategoryAverages_SingleTool_ReturnsSameScores()
    {
        var toolResults = new List<ToolEvalResult>
        {
            new()
            {
                CategoryScores = new Dictionary<string, float>
                {
                    ["tool_name"] = 80f,
                    ["tool_description"] = 60f,
                },
            },
        };

        var result = Scorer.ComputeCategoryAverages(toolResults);

        result["tool_name"].Should().Be(80f);
        result["tool_description"].Should().Be(60f);
    }

    [Fact]
    public void ComputeCategoryAverages_MultipleTools_AveragesCorrectly()
    {
        var toolResults = new List<ToolEvalResult>
        {
            new()
            {
                CategoryScores = new Dictionary<string, float>
                {
                    ["tool_name"] = 80f,
                    ["tool_description"] = 40f,
                },
            },
            new()
            {
                CategoryScores = new Dictionary<string, float>
                {
                    ["tool_name"] = 60f,
                    ["tool_description"] = 80f,
                },
            },
        };

        var result = Scorer.ComputeCategoryAverages(toolResults);

        result["tool_name"].Should().Be(70f);     // (80+60)/2
        result["tool_description"].Should().Be(60f); // (40+80)/2
    }

    [Fact]
    public void ComputeCategoryAverages_EmptyList_ReturnsEmptyDict()
    {
        var result = Scorer.ComputeCategoryAverages([]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ComputeCategoryAverages_UnevenCategories_AveragesPerCategory()
    {
        // tool1 has tool_name, tool2 does not
        var toolResults = new List<ToolEvalResult>
        {
            new()
            {
                CategoryScores = new Dictionary<string, float>
                {
                    ["tool_name"] = 100f,
                    ["tool_description"] = 80f,
                },
            },
            new()
            {
                CategoryScores = new Dictionary<string, float>
                {
                    ["tool_description"] = 60f,
                },
            },
        };

        var result = Scorer.ComputeCategoryAverages(toolResults);

        result["tool_name"].Should().Be(100f);        // only 1 entry
        result["tool_description"].Should().Be(70f);   // (80+60)/2
    }
}
