// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Evaluate;

public class MaturityCalculatorTests
{
    // =======================================================================
    // Score-based level thresholds
    // =======================================================================

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(30f, 0)]
    [InlineData(39.9f, 0)]
    public void DetermineLevel_BelowThreshold40_ReturnsLevel0(float score, int expectedLevel)
    {
        var allHigh = HighCategoryAverages();

        var result = MaturityCalculator.DetermineLevel(score, allHigh);

        result.Level.Should().Be(expectedLevel);
        result.Label.Should().Be("Functional");
    }

    [Theory]
    [InlineData(40f, 1)]
    [InlineData(50f, 1)]
    [InlineData(59.9f, 1)]
    public void DetermineLevel_Score40To59_ReturnsLevel1(float score, int expectedLevel)
    {
        var allHigh = HighCategoryAverages();

        var result = MaturityCalculator.DetermineLevel(score, allHigh);

        result.Level.Should().Be(expectedLevel);
        result.Label.Should().Be("Described");
    }

    [Theory]
    [InlineData(60f, 2)]
    [InlineData(65f, 2)]
    [InlineData(74.9f, 2)]
    public void DetermineLevel_Score60To74_ReturnsLevel2(float score, int expectedLevel)
    {
        var allHigh = HighCategoryAverages();

        var result = MaturityCalculator.DetermineLevel(score, allHigh);

        result.Level.Should().Be(expectedLevel);
        result.Label.Should().Be("Consistent");
    }

    [Theory]
    [InlineData(75f, 3)]
    [InlineData(80f, 3)]
    [InlineData(89.9f, 3)]
    public void DetermineLevel_Score75To89_ReturnsLevel3(float score, int expectedLevel)
    {
        var allHigh = HighCategoryAverages();

        var result = MaturityCalculator.DetermineLevel(score, allHigh);

        result.Level.Should().Be(expectedLevel);
        result.Label.Should().Be("Optimized for AI");
    }

    [Theory]
    [InlineData(90f, 4)]
    [InlineData(95f, 4)]
    [InlineData(100f, 4)]
    public void DetermineLevel_Score90Plus_ReturnsLevel4(float score, int expectedLevel)
    {
        var allHigh = HighCategoryAverages();

        var result = MaturityCalculator.DetermineLevel(score, allHigh);

        result.Level.Should().Be(expectedLevel);
        result.Label.Should().Be("Exemplary");
    }

    // =======================================================================
    // Category-based caps
    // =======================================================================

    [Fact]
    public void DetermineLevel_ToolDescriptionBelow50_CapsAtLevel1()
    {
        // Score 95 would be Level 4, but tool_description < 50 caps at Level 1
        var categoryAverages = new Dictionary<string, float>
        {
            ["tool_description"] = 49f,
            ["param_description"] = 100f,
            ["tool_name"] = 100f,
        };

        var result = MaturityCalculator.DetermineLevel(95f, categoryAverages);

        result.Level.Should().Be(1);
        result.Label.Should().Be("Described");
    }

    [Fact]
    public void DetermineLevel_ToolDescriptionExactly50_NoCap()
    {
        var categoryAverages = new Dictionary<string, float>
        {
            ["tool_description"] = 50f,
            ["param_description"] = 100f,
            ["tool_name"] = 100f,
        };

        var result = MaturityCalculator.DetermineLevel(95f, categoryAverages);

        // No cap from tool_description, so score 95 -> Level 4
        result.Level.Should().Be(4);
    }

    [Fact]
    public void DetermineLevel_ParamDescriptionBelow60_CapsAtLevel2()
    {
        var categoryAverages = new Dictionary<string, float>
        {
            ["tool_description"] = 100f,
            ["param_description"] = 59f,
            ["tool_name"] = 100f,
        };

        var result = MaturityCalculator.DetermineLevel(95f, categoryAverages);

        result.Level.Should().Be(2);
        result.Label.Should().Be("Consistent");
    }

    [Fact]
    public void DetermineLevel_ParamDescriptionExactly60_NoCap()
    {
        var categoryAverages = new Dictionary<string, float>
        {
            ["tool_description"] = 100f,
            ["param_description"] = 60f,
            ["tool_name"] = 100f,
        };

        var result = MaturityCalculator.DetermineLevel(95f, categoryAverages);

        result.Level.Should().Be(4);
    }

    [Fact]
    public void DetermineLevel_ToolNameBelow75_CapsAtLevel3()
    {
        var categoryAverages = new Dictionary<string, float>
        {
            ["tool_description"] = 100f,
            ["param_description"] = 100f,
            ["tool_name"] = 74f,
        };

        var result = MaturityCalculator.DetermineLevel(95f, categoryAverages);

        result.Level.Should().Be(3);
        result.Label.Should().Be("Optimized for AI");
    }

    [Fact]
    public void DetermineLevel_ToolNameExactly75_NoCap()
    {
        var categoryAverages = new Dictionary<string, float>
        {
            ["tool_description"] = 100f,
            ["param_description"] = 100f,
            ["tool_name"] = 75f,
        };

        var result = MaturityCalculator.DetermineLevel(95f, categoryAverages);

        result.Level.Should().Be(4);
    }

    [Fact]
    public void DetermineLevel_MultipleCaps_LowestWins()
    {
        // Both tool_description and param_description are low
        // tool_description < 50 caps at 1, param_description < 60 caps at 2
        // The tool_description cap of 1 should win (applied first, most restrictive)
        var categoryAverages = new Dictionary<string, float>
        {
            ["tool_description"] = 30f,
            ["param_description"] = 40f,
            ["tool_name"] = 50f,
        };

        var result = MaturityCalculator.DetermineLevel(95f, categoryAverages);

        result.Level.Should().Be(1);
    }

    [Fact]
    public void DetermineLevel_NullCategoryAverages_HandledGracefully()
    {
        // Null averages default to empty dict, all averages default to 0
        var result = MaturityCalculator.DetermineLevel(95f, null!);

        // tool_description=0 < 50 caps at Level 1
        result.Level.Should().Be(1);
    }

    [Fact]
    public void DetermineLevel_EmptyCategoryAverages_DefaultsApply()
    {
        var result = MaturityCalculator.DetermineLevel(95f, []);

        // tool_description defaults to 0 < 50, caps at Level 1
        result.Level.Should().Be(1);
    }

    // =======================================================================
    // Next-level requirements
    // =======================================================================

    [Fact]
    public void DetermineLevel_Level4_RequirementsMaintain()
    {
        var result = MaturityCalculator.DetermineLevel(95f, HighCategoryAverages());

        result.NextLevelRequirements.Should().ContainSingle()
            .Which.Should().Contain("Maintain");
    }

    [Fact]
    public void DetermineLevel_Level0_HasDescriptionRequirements()
    {
        var result = MaturityCalculator.DetermineLevel(30f, HighCategoryAverages());

        result.NextLevelRequirements.Should().NotBeEmpty();
        result.NextLevelRequirements.Should().Contain(r => r.Contains("description"));
    }

    [Fact]
    public void DetermineLevel_HasDescription()
    {
        var result = MaturityCalculator.DetermineLevel(50f, HighCategoryAverages());

        result.Description.Should().NotBeNullOrWhiteSpace();
    }

    // =======================================================================
    // GetMaturityLadder
    // =======================================================================

    [Fact]
    public void GetMaturityLadder_Returns5Entries()
    {
        var ladder = MaturityCalculator.GetMaturityLadder(2);

        ladder.Should().HaveCount(5);
    }

    [Fact]
    public void GetMaturityLadder_LevelsAre0Through4()
    {
        var ladder = MaturityCalculator.GetMaturityLadder(0);

        ladder.Select(e => e.Level).Should().BeEquivalentTo([0, 1, 2, 3, 4]);
    }

    [Fact]
    public void GetMaturityLadder_CorrectIsCurrentForLevel2()
    {
        var ladder = MaturityCalculator.GetMaturityLadder(2);

        ladder.Where(e => e.IsCurrent).Should().ContainSingle()
            .Which.Level.Should().Be(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void GetMaturityLadder_ExactlyOneIsCurrent(int currentLevel)
    {
        var ladder = MaturityCalculator.GetMaturityLadder(currentLevel);

        ladder.Where(e => e.IsCurrent).Should().ContainSingle();
        ladder.Single(e => e.IsCurrent).Level.Should().Be(currentLevel);
    }

    [Fact]
    public void GetMaturityLadder_AllEntriesHaveLabels()
    {
        var ladder = MaturityCalculator.GetMaturityLadder(0);

        ladder.Should().AllSatisfy(e =>
        {
            e.Label.Should().NotBeNullOrWhiteSpace();
            e.Description.Should().NotBeNullOrWhiteSpace();
        });
    }

    [Fact]
    public void GetMaturityLadder_ContainsExpectedLabels()
    {
        var ladder = MaturityCalculator.GetMaturityLadder(0);
        var labels = ladder.Select(e => e.Label).ToList();

        labels.Should().Contain("Functional");
        labels.Should().Contain("Described");
        labels.Should().Contain("Consistent");
        labels.Should().Contain("Optimized for AI");
        labels.Should().Contain("Exemplary");
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    /// <summary>
    /// Returns category averages that are high enough to avoid any caps.
    /// </summary>
    private static Dictionary<string, float> HighCategoryAverages()
    {
        return new Dictionary<string, float>
        {
            ["tool_description"] = 100f,
            ["param_description"] = 100f,
            ["tool_name"] = 100f,
        };
    }
}
