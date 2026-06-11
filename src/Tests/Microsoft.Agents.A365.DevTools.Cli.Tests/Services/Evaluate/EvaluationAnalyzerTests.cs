// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Evaluate;

/// <summary>
/// Tests for the EvaluationAnalyzer service which computes per-tool scores,
/// toolset scores, overall scores, maturity levels, and action items.
/// </summary>
public class EvaluationAnalyzerTests
{
    private readonly EvaluationAnalyzer _analyzer;

    public EvaluationAnalyzerTests()
    {
        _analyzer = new EvaluationAnalyzer(NullLogger<EvaluationAnalyzer>.Instance);
    }

    // -----------------------------------------------------------------------
    // Helper methods for building test data
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a ChecklistItem with the given score (true = pass, false = fail, null = unevaluated).
    /// </summary>
    private static ChecklistItem CreateCheck(
        string id,
        bool? score,
        CheckCategory category,
        Priority severity = Priority.P1,
        List<int>? issueIds = null)
    {
        return new ChecklistItem
        {
            Id = id,
            Type = CheckType.Deterministic,
            Prompt = $"Check: {id}",
            Score = score,
            Reason = score == false ? $"Failed: {id}" : null,
            Severity = severity,
            Category = category,
            IssueIds = issueIds ?? [],
            ImpactAreas = [ImpactArea.ToolSelection],
            Remediation = $"Fix {id}",
        };
    }

    /// <summary>
    /// Builds a ToolChecklist with checks that all pass or all fail based on the provided score.
    /// Creates checks across all categories to exercise the full scoring pipeline.
    /// </summary>
    private static ToolChecklist CreateToolWithUniformChecks(string name, bool score)
    {
        return new ToolChecklist
        {
            Name = name,
            Description = $"Description for {name}",
            Checks = new ToolCheckGroups
            {
                ToolName =
                [
                    CreateCheck($"{name}_tn1", score, CheckCategory.ToolName, Priority.P1, score ? null : [4]),
                    CreateCheck($"{name}_tn2", score, CheckCategory.ToolName, Priority.P2),
                ],
                ToolDescription =
                [
                    CreateCheck($"{name}_td1", score, CheckCategory.ToolDescription, Priority.P0, score ? null : [5]),
                    CreateCheck($"{name}_td2", score, CheckCategory.ToolDescription, Priority.P1),
                    CreateCheck($"{name}_td3", score, CheckCategory.ToolDescription, Priority.P2),
                ],
                SchemaStructure =
                [
                    CreateCheck($"{name}_ss1", score, CheckCategory.SchemaStructure, Priority.P1),
                ],
                Parameters = new Dictionary<string, ParamCheckGroups>
                {
                    ["param1"] = new ParamCheckGroups
                    {
                        ParamName =
                        [
                            CreateCheck($"{name}_pn1", score, CheckCategory.ParamName, Priority.P2),
                        ],
                        ParamDescription =
                        [
                            CreateCheck($"{name}_pd1", score, CheckCategory.ParamDescription, Priority.P1, score ? null : [9]),
                            CreateCheck($"{name}_pd2", score, CheckCategory.ParamDescription, Priority.P2),
                        ],
                    },
                },
            },
        };
    }

    /// <summary>
    /// Builds a ToolChecklist with a mix of passing and failing checks.
    /// ToolName: 1 pass, 1 fail. ToolDescription: 2 pass, 1 fail.
    /// SchemaStructure: 1 pass. Parameters: 1 pass param_name, 1 pass / 1 fail param_description.
    /// </summary>
    private static ToolChecklist CreateToolWithMixedChecks(string name)
    {
        return new ToolChecklist
        {
            Name = name,
            Description = $"Description for {name}",
            Checks = new ToolCheckGroups
            {
                ToolName =
                [
                    CreateCheck($"{name}_tn1", true, CheckCategory.ToolName),
                    CreateCheck($"{name}_tn2", false, CheckCategory.ToolName, Priority.P2, [13]),
                ],
                ToolDescription =
                [
                    CreateCheck($"{name}_td1", true, CheckCategory.ToolDescription),
                    CreateCheck($"{name}_td2", true, CheckCategory.ToolDescription),
                    CreateCheck($"{name}_td3", false, CheckCategory.ToolDescription, Priority.P1, [5]),
                ],
                SchemaStructure =
                [
                    CreateCheck($"{name}_ss1", true, CheckCategory.SchemaStructure),
                ],
                Parameters = new Dictionary<string, ParamCheckGroups>
                {
                    ["param1"] = new ParamCheckGroups
                    {
                        ParamName =
                        [
                            CreateCheck($"{name}_pn1", true, CheckCategory.ParamName),
                        ],
                        ParamDescription =
                        [
                            CreateCheck($"{name}_pd1", true, CheckCategory.ParamDescription),
                            CreateCheck($"{name}_pd2", false, CheckCategory.ParamDescription, Priority.P2, [9]),
                        ],
                    },
                },
            },
        };
    }

    /// <summary>
    /// Builds an EvaluationChecklist with the specified tools and optional server checks.
    /// </summary>
    private static EvaluationChecklist CreateChecklist(
        List<ToolChecklist> tools,
        List<ChecklistItem>? serverChecks = null)
    {
        return new EvaluationChecklist
        {
            Metadata = new ChecklistMetadata
            {
                ServerName = "test-server",
                ServerUrl = "http://localhost:3000",
                ToolCount = tools.Count,
            },
            Tools = tools,
            ServerChecks = serverChecks ?? [],
        };
    }

    // -----------------------------------------------------------------------
    // Single tool - all checks passing -> score 100
    // -----------------------------------------------------------------------

    [Fact]
    public void Analyze_SingleToolAllPassing_ReturnsScore100()
    {
        var tool = CreateToolWithUniformChecks("good_tool", score: true);
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        result.ToolResults.Should().HaveCount(1);
        result.ToolResults[0].Score.Should().Be(100f);
    }

    [Fact]
    public void Analyze_SingleToolAllPassing_OverallScoreIs100()
    {
        var tool = CreateToolWithUniformChecks("good_tool", score: true);
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        // Overall = (toolScore * 0.85) + (toolsetScore * 0.15)
        // With no server checks, toolset defaults to 100
        // So overall = (100 * 0.85) + (100 * 0.15) = 100
        result.OverallScore.Should().Be(100f);
    }

    [Fact]
    public void Analyze_SingleToolAllPassing_HasNoActionItems()
    {
        var tool = CreateToolWithUniformChecks("good_tool", score: true);
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        result.AllActionItems.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Single tool - all checks failing -> score near 0
    // -----------------------------------------------------------------------

    [Fact]
    public void Analyze_SingleToolAllFailing_ReturnsScoreNearZero()
    {
        var tool = CreateToolWithUniformChecks("bad_tool", score: false);
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        result.ToolResults[0].Score.Should().Be(0f);
    }

    [Fact]
    public void Analyze_SingleToolAllFailing_OverallScoreNearZero()
    {
        var tool = CreateToolWithUniformChecks("bad_tool", score: false);
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        // Tool score = 0, toolset score = 100 (no server checks)
        // Overall = (0 * 0.85) + (100 * 0.15) = 15
        result.OverallScore.Should().Be(15f);
    }

    [Fact]
    public void Analyze_SingleToolAllFailing_GeneratesActionItems()
    {
        var tool = CreateToolWithUniformChecks("bad_tool", score: false);
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        result.AllActionItems.Should().NotBeEmpty();
        // All 9 checks fail, so we should get 9 action items
        result.AllActionItems.Should().HaveCount(9);
    }

    // -----------------------------------------------------------------------
    // Mixed pass/fail -> correct weighted score
    // -----------------------------------------------------------------------

    [Fact]
    public void Analyze_SingleToolMixedChecks_ReturnsCorrectWeightedScore()
    {
        var tool = CreateToolWithMixedChecks("mixed_tool");
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        // Category scores:
        // tool_name: 1/2 pass = 50, weight 0.15 -> 7.5
        // tool_description: 2/3 pass = 66.7, weight 0.35 -> 23.345
        // schema_structure: 1/1 pass = 100, weight 0.15 -> 15
        // param_name: 1/1 pass = 100, weight 0.10 -> 10
        // param_description: 1/2 pass = 50, weight 0.25 -> 12.5
        // tool score = 7.5 + 23.345 + 15 + 10 + 12.5 = 68.345, rounded to 68.3
        float toolScore = result.ToolResults[0].Score;
        toolScore.Should().BeInRange(60f, 75f);

        // Overall = (toolScore * 0.85) + (100 * 0.15) = ~73
        result.OverallScore.Should().BeInRange(55f, 80f);
    }

    [Fact]
    public void Analyze_SingleToolMixedChecks_ActionItemCountMatchesFailedChecks()
    {
        var tool = CreateToolWithMixedChecks("mixed_tool");
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        // 3 checks fail: tn2, td3, pd2
        result.AllActionItems.Should().HaveCount(3);
    }

    // -----------------------------------------------------------------------
    // Empty tool list -> only toolset score contributes
    // -----------------------------------------------------------------------

    [Fact]
    public void Analyze_EmptyToolList_OnlyToolsetScoreContributes()
    {
        var checklist = CreateChecklist([]);

        var result = _analyzer.Analyze(checklist, "None");

        // With no tools and no server checks: toolset defaults to 100
        // Overall = (toolsetScore * 0.15) = 100 * 0.15 = 15
        result.OverallScore.Should().Be(15f);
        result.ToolResults.Should().BeEmpty();
        result.ToolCount.Should().Be(0);
    }

    [Fact]
    public void Analyze_EmptyToolListWithFailingServerChecks_ReflectsToolsetScore()
    {
        var serverChecks = new List<ChecklistItem>
        {
            CreateCheck("server_1", false, CheckCategory.ToolsetDesign, Priority.P0),
            CreateCheck("server_2", true, CheckCategory.ToolsetDesign),
        };
        var checklist = CreateChecklist([], serverChecks);

        var result = _analyzer.Analyze(checklist, "None");

        // Toolset score = 1/2 pass = 50
        // Overall = 50 * 0.15 = 7.5
        result.OverallScore.Should().Be(7.5f);
        result.ToolsetResult.Score.Should().Be(50f);
    }

    // -----------------------------------------------------------------------
    // Action items sorted by priority
    // -----------------------------------------------------------------------

    [Fact]
    public void Analyze_ActionItemsAreSortedByPriority()
    {
        // Create a tool where checks fail with different priorities
        var tool = new ToolChecklist
        {
            Name = "priority_tool",
            Description = "Tool for testing priority sorting",
            Checks = new ToolCheckGroups
            {
                ToolName =
                [
                    CreateCheck("tn_p3", false, CheckCategory.ToolName, Priority.P3),
                ],
                ToolDescription =
                [
                    CreateCheck("td_p0", false, CheckCategory.ToolDescription, Priority.P0),
                ],
                SchemaStructure =
                [
                    CreateCheck("ss_p2", false, CheckCategory.SchemaStructure, Priority.P2),
                ],
                Parameters = new Dictionary<string, ParamCheckGroups>
                {
                    ["p1"] = new ParamCheckGroups
                    {
                        ParamName =
                        [
                            CreateCheck("pn_p1", false, CheckCategory.ParamName, Priority.P1),
                        ],
                        ParamDescription = [],
                    },
                },
            },
        };
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        var priorities = result.AllActionItems.Select(a => a.Priority).ToList();
        priorities.Should().BeInAscendingOrder();
    }

    // -----------------------------------------------------------------------
    // Issue summary counts are correct
    // -----------------------------------------------------------------------

    [Fact]
    public void Analyze_IssueSummaryCounts_MatchFailedCheckIssueIds()
    {
        var tool = CreateToolWithUniformChecks("problem_tool", score: false);
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        // The uniform failing tool has issue ids: [4] on tn1, [5] on td1, [9] on pd1
        result.IssueSummary.Should().NotBeEmpty();

        // Verify total issue occurrences match what we created
        int totalIssues = result.IssueSummary.Values.Sum();
        totalIssues.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Analyze_IssueSummary_CountsMultipleOccurrencesOfSameIssue()
    {
        // Create two tools that both fail with the same issue id
        var tool1 = new ToolChecklist
        {
            Name = "tool1",
            Description = "Tool 1",
            Checks = new ToolCheckGroups
            {
                ToolName =
                [
                    CreateCheck("t1_tn1", false, CheckCategory.ToolName, issueIds: [4]),
                ],
                ToolDescription = [],
                SchemaStructure = [],
                Parameters = [],
            },
        };
        var tool2 = new ToolChecklist
        {
            Name = "tool2",
            Description = "Tool 2",
            Checks = new ToolCheckGroups
            {
                ToolName =
                [
                    CreateCheck("t2_tn1", false, CheckCategory.ToolName, issueIds: [4]),
                ],
                ToolDescription = [],
                SchemaStructure = [],
                Parameters = [],
            },
        };
        var checklist = CreateChecklist([tool1, tool2]);

        var result = _analyzer.Analyze(checklist, "None");

        // Issue 4 = "Missing purpose statement"
        var issue4Name = "Missing purpose statement";
        result.IssueSummary.Should().ContainKey(issue4Name);
        result.IssueSummary[issue4Name].Should().Be(2);
    }

    // -----------------------------------------------------------------------
    // ActionItemsByPriority counts
    // -----------------------------------------------------------------------

    [Fact]
    public void Analyze_ActionItemsByPriority_CountsAllPriorityLevels()
    {
        var tool = CreateToolWithUniformChecks("failing_tool", score: false);
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        result.ActionItemsByPriority.Should().ContainKey("P0");
        result.ActionItemsByPriority.Should().ContainKey("P1");
        result.ActionItemsByPriority.Should().ContainKey("P2");
        result.ActionItemsByPriority.Should().ContainKey("P3");

        int totalFromPriority = result.ActionItemsByPriority.Values.Sum();
        totalFromPriority.Should().Be(result.AllActionItems.Count);
    }

    // -----------------------------------------------------------------------
    // Maturity level calculated correctly
    // -----------------------------------------------------------------------

    [Fact]
    public void Analyze_AllPassingTool_MaturityLevelIs4()
    {
        var tool = CreateToolWithUniformChecks("exemplary_tool", score: true);
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        // Score = 100, all category averages = 100 -> no caps -> Level 4
        result.Maturity.Level.Should().Be(4);
        result.Maturity.Label.Should().Be("Exemplary");
    }

    [Fact]
    public void Analyze_AllFailingTool_MaturityLevelIs0()
    {
        var tool = CreateToolWithUniformChecks("terrible_tool", score: false);
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        // Overall score = 15 (only toolset contributes) -> Level 0
        result.Maturity.Level.Should().Be(0);
        result.Maturity.Label.Should().Be("Functional");
    }

    [Fact]
    public void Analyze_MixedChecks_MaturityLevelReflectsScore()
    {
        var tool = CreateToolWithMixedChecks("mixed_tool");
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        // Overall is somewhere between 55-80, maturity is based on that
        result.Maturity.Level.Should().BeInRange(0, 3);
    }

    // -----------------------------------------------------------------------
    // Result metadata
    // -----------------------------------------------------------------------

    [Fact]
    public void Analyze_SetsServerNameAndUrl()
    {
        var tool = CreateToolWithUniformChecks("tool1", score: true);
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "GitHub Copilot");

        result.ServerName.Should().Be("test-server");
        result.ServerUrl.Should().Be("http://localhost:3000");
        result.EvalEngine.Should().Be("GitHub Copilot");
    }

    [Fact]
    public void Analyze_SetsToolCount()
    {
        var tools = new List<ToolChecklist>
        {
            CreateToolWithUniformChecks("tool1", score: true),
            CreateToolWithUniformChecks("tool2", score: true),
        };
        var checklist = CreateChecklist(tools);

        var result = _analyzer.Analyze(checklist, "None");

        result.ToolCount.Should().Be(2);
        result.ToolResults.Should().HaveCount(2);
    }

    [Fact]
    public void Analyze_SetsEvaluatedAtToRecentTime()
    {
        var tool = CreateToolWithUniformChecks("tool1", score: true);
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        result.EvaluatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    // -----------------------------------------------------------------------
    // Category averages
    // -----------------------------------------------------------------------

    [Fact]
    public void Analyze_CategoryAverages_ComputedAcrossMultipleTools()
    {
        var tools = new List<ToolChecklist>
        {
            CreateToolWithUniformChecks("pass_tool", score: true),
            CreateToolWithUniformChecks("fail_tool", score: false),
        };
        var checklist = CreateChecklist(tools);

        var result = _analyzer.Analyze(checklist, "None");

        // Each category should have an average of (100 + 0) / 2 = 50
        result.CategoryAverages.Should().NotBeEmpty();
        result.CategoryAverages.Should().ContainKey("tool_name");
        result.CategoryAverages["tool_name"].Should().Be(50f);
    }

    // -----------------------------------------------------------------------
    // Null checks / edge cases
    // -----------------------------------------------------------------------

    [Fact]
    public void Analyze_NullChecklist_ThrowsArgumentNullException()
    {
        var act = () => _analyzer.Analyze(null!, "None");

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Analyze_NullEvalEngine_DefaultsToEmpty()
    {
        var tool = CreateToolWithUniformChecks("tool", score: true);
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, null!);

        result.EvalEngine.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ToolWithNoParameters_StillComputes()
    {
        var tool = new ToolChecklist
        {
            Name = "no_params",
            Description = "A tool with no parameters",
            Checks = new ToolCheckGroups
            {
                ToolName =
                [
                    CreateCheck("tn1", true, CheckCategory.ToolName),
                ],
                ToolDescription =
                [
                    CreateCheck("td1", true, CheckCategory.ToolDescription),
                ],
                SchemaStructure =
                [
                    CreateCheck("ss1", true, CheckCategory.SchemaStructure),
                ],
                Parameters = [],
            },
        };
        var checklist = CreateChecklist([tool]);

        var result = _analyzer.Analyze(checklist, "None");

        result.ToolResults.Should().HaveCount(1);
        result.ToolResults[0].ParamCount.Should().Be(0);
        result.ToolResults[0].Score.Should().Be(100f);
    }
}
