// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Evaluate;

public class ActionItemGeneratorTests
{
    // =======================================================================
    // GenerateFromChecks - basic behavior
    // =======================================================================

    [Fact]
    public void GenerateFromChecks_FailedCheck_GeneratesActionItem()
    {
        var checks = new List<ChecklistItem>
        {
            new()
            {
                Id = "td_present",
                Score = false,
                Severity = Priority.P0,
                Prompt = "Description present",
                Reason = "Tool description is empty or missing.",
                Category = CheckCategory.ToolDescription,
                SmellIds = [4],
                ImpactAreas = [ImpactArea.ToolSelection],
                Remediation = "Add a description.",
            },
        };

        var weights = new Dictionary<string, float> { ["tool_description"] = 0.35f };
        var result = ActionItemGenerator.GenerateFromChecks(checks, "get_user", null, weights, 3);

        result.Should().ContainSingle();
        var item = result[0];
        item.ToolName.Should().Be("get_user");
        item.Priority.Should().Be(Priority.P0);
        item.Title.Should().Be("Description present");
        item.Remediation.Should().Contain("description");
    }

    [Fact]
    public void GenerateFromChecks_PassedCheck_GeneratesNoActionItem()
    {
        var checks = new List<ChecklistItem>
        {
            new()
            {
                Id = "td_present",
                Score = true,
                Severity = Priority.P0,
                Prompt = "Description present",
                Reason = "Tool has a description.",
                Category = CheckCategory.ToolDescription,
                SmellIds = [4],
                ImpactAreas = [ImpactArea.ToolSelection],
                Remediation = "Add a description.",
            },
        };

        var weights = new Dictionary<string, float> { ["tool_description"] = 0.35f };
        var result = ActionItemGenerator.GenerateFromChecks(checks, "get_user", null, weights, 3);

        result.Should().BeEmpty();
    }

    [Fact]
    public void GenerateFromChecks_NullScore_GeneratesNoActionItem()
    {
        var checks = new List<ChecklistItem>
        {
            new()
            {
                Id = "td_has_purpose",
                Score = null,
                Severity = Priority.P0,
                Prompt = "Has purpose statement",
                Category = CheckCategory.ToolDescription,
                SmellIds = [4],
                ImpactAreas = [ImpactArea.ToolSelection],
                Remediation = "Add purpose.",
            },
        };

        var weights = new Dictionary<string, float> { ["tool_description"] = 0.35f };
        var result = ActionItemGenerator.GenerateFromChecks(checks, "get_user", null, weights, 3);

        result.Should().BeEmpty();
    }

    // =======================================================================
    // Score impact calculation
    // =======================================================================

    [Fact]
    public void GenerateFromChecks_ScoreImpact_CalculatedCorrectly()
    {
        var checks = new List<ChecklistItem>
        {
            new()
            {
                Id = "td_present",
                Score = false,
                Severity = Priority.P0,
                Prompt = "Description present",
                Reason = "Missing.",
                Category = CheckCategory.ToolDescription,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Fix it.",
            },
        };

        // weight = 0.35, totalChecksInCategory = 3
        // scoreImpact = (0.35 * 100) / 3 = 11.7 (rounded to 1 decimal)
        var weights = new Dictionary<string, float> { ["tool_description"] = 0.35f };
        var result = ActionItemGenerator.GenerateFromChecks(checks, "test_tool", null, weights, 3);

        result[0].ScoreImpact.Should().BeApproximately(11.7f, 0.1f);
    }

    [Fact]
    public void GenerateFromChecks_ScoreImpact_ZeroTotalChecksHandled()
    {
        var checks = new List<ChecklistItem>
        {
            new()
            {
                Id = "td_present",
                Score = false,
                Severity = Priority.P0,
                Prompt = "Desc",
                Reason = "Missing.",
                Category = CheckCategory.ToolDescription,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Fix.",
            },
        };

        // totalChecksInCategory = 0 should be clamped to 1
        var weights = new Dictionary<string, float> { ["tool_description"] = 0.35f };
        var result = ActionItemGenerator.GenerateFromChecks(checks, "test_tool", null, weights, 0);

        // (0.35 * 100) / 1 = 35.0
        result[0].ScoreImpact.Should().BeApproximately(35.0f, 0.1f);
    }

    [Fact]
    public void GenerateFromChecks_UnknownCategory_DefaultsTo015Weight()
    {
        var checks = new List<ChecklistItem>
        {
            new()
            {
                Id = "custom_check",
                Score = false,
                Severity = Priority.P1,
                Prompt = "Custom check",
                Reason = "Failed.",
                Category = CheckCategory.ToolsetDesign,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Fix.",
            },
        };

        // toolset_design is not in the standard weight dict, defaults to 0.15
        var weights = new Dictionary<string, float>();
        var result = ActionItemGenerator.GenerateFromChecks(checks, null, null, weights, 1);

        // (0.15 * 100) / 1 = 15.0
        result[0].ScoreImpact.Should().BeApproximately(15.0f, 0.1f);
    }

    // =======================================================================
    // Sorting by priority
    // =======================================================================

    [Fact]
    public void GenerateFromChecks_SortedByPriority_P0First()
    {
        var checks = new List<ChecklistItem>
        {
            new()
            {
                Id = "check_p2",
                Score = false,
                Severity = Priority.P2,
                Prompt = "P2 check",
                Reason = "P2 reason",
                Category = CheckCategory.ToolName,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Fix P2.",
            },
            new()
            {
                Id = "check_p0",
                Score = false,
                Severity = Priority.P0,
                Prompt = "P0 check",
                Reason = "P0 reason",
                Category = CheckCategory.ToolName,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Fix P0.",
            },
            new()
            {
                Id = "check_p1",
                Score = false,
                Severity = Priority.P1,
                Prompt = "P1 check",
                Reason = "P1 reason",
                Category = CheckCategory.ToolName,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Fix P1.",
            },
        };

        var weights = new Dictionary<string, float> { ["tool_name"] = 0.15f };
        var result = ActionItemGenerator.GenerateFromChecks(checks, "tool", null, weights, 3);

        result.Should().HaveCount(3);
        result[0].Priority.Should().Be(Priority.P0);
        result[1].Priority.Should().Be(Priority.P1);
        result[2].Priority.Should().Be(Priority.P2);
    }

    // =======================================================================
    // Null/empty inputs
    // =======================================================================

    [Fact]
    public void GenerateFromChecks_NullChecks_ReturnsEmpty()
    {
        var result = ActionItemGenerator.GenerateFromChecks(null!, "tool", null, [], 1);

        result.Should().BeEmpty();
    }

    [Fact]
    public void GenerateFromChecks_EmptyChecks_ReturnsEmpty()
    {
        var result = ActionItemGenerator.GenerateFromChecks([], "tool", null, [], 1);

        result.Should().BeEmpty();
    }

    [Fact]
    public void GenerateFromChecks_NullWeights_HandledGracefully()
    {
        var checks = new List<ChecklistItem>
        {
            new()
            {
                Id = "td_present",
                Score = false,
                Severity = Priority.P0,
                Prompt = "Check",
                Reason = "Fail",
                Category = CheckCategory.ToolDescription,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Fix.",
            },
        };

        var result = ActionItemGenerator.GenerateFromChecks(checks, "tool", null, null!, 1);

        result.Should().ContainSingle();
    }

    // =======================================================================
    // Smell resolution
    // =======================================================================

    [Fact]
    public void GenerateFromChecks_ValidSmellIds_ResolvesToImpacts()
    {
        var checks = new List<ChecklistItem>
        {
            new()
            {
                Id = "td_present",
                Score = false,
                Severity = Priority.P0,
                Prompt = "Check",
                Reason = "Fail",
                Category = CheckCategory.ToolDescription,
                SmellIds = [1, 4],
                ImpactAreas = [],
                Remediation = "Fix.",
            },
        };

        var weights = new Dictionary<string, float> { ["tool_description"] = 0.35f };
        var result = ActionItemGenerator.GenerateFromChecks(checks, "tool", null, weights, 1);

        result[0].IssueLeadsTo.Should().NotBeEmpty();
        result[0].SmellIds.Should().Contain(1);
        result[0].SmellIds.Should().Contain(4);
    }

    // =======================================================================
    // Param/tool name propagation
    // =======================================================================

    [Fact]
    public void GenerateFromChecks_PropagatesToolAndParamNames()
    {
        var checks = new List<ChecklistItem>
        {
            new()
            {
                Id = "pd_present",
                Score = false,
                Severity = Priority.P0,
                Prompt = "Param desc present",
                Reason = "Missing.",
                Category = CheckCategory.ParamDescription,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Add.",
            },
        };

        var weights = new Dictionary<string, float> { ["param_description"] = 0.25f };
        var result = ActionItemGenerator.GenerateFromChecks(checks, "get_user", "userId", weights, 1);

        result[0].ToolName.Should().Be("get_user");
        result[0].ParamName.Should().Be("userId");
    }

    // =======================================================================
    // GenerateFromAllChecks
    // =======================================================================

    [Fact]
    public void GenerateFromAllChecks_FailedChecks_GeneratesItems()
    {
        var checks = new List<ChecklistItem>
        {
            new()
            {
                Id = "tn_present",
                Score = false,
                Severity = Priority.P0,
                Prompt = "Tool name present",
                Reason = "Missing.",
                Category = CheckCategory.ToolName,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Add name.",
            },
            new()
            {
                Id = "td_present",
                Score = true,
                Severity = Priority.P0,
                Prompt = "Description present",
                Reason = "Has description.",
                Category = CheckCategory.ToolDescription,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Add desc.",
            },
        };

        var result = ActionItemGenerator.GenerateFromAllChecks(checks, "tool1");

        result.Should().ContainSingle();
        result[0].Title.Should().Be("Tool name present");
        result[0].ToolName.Should().Be("tool1");
    }

    [Fact]
    public void GenerateFromAllChecks_NullChecks_ReturnsEmpty()
    {
        var result = ActionItemGenerator.GenerateFromAllChecks(null!, "tool1");

        result.Should().BeEmpty();
    }

    [Fact]
    public void GenerateFromAllChecks_EmptyChecks_ReturnsEmpty()
    {
        var result = ActionItemGenerator.GenerateFromAllChecks([], "tool1");

        result.Should().BeEmpty();
    }

    [Fact]
    public void GenerateFromAllChecks_UsesScorerCategoryWeights()
    {
        var checks = new List<ChecklistItem>
        {
            new()
            {
                Id = "td_present",
                Score = false,
                Severity = Priority.P0,
                Prompt = "Description present",
                Reason = "Missing.",
                Category = CheckCategory.ToolDescription,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Fix.",
            },
        };

        var result = ActionItemGenerator.GenerateFromAllChecks(checks, "tool1");

        // tool_description weight is 0.35, 1 check in category
        // (0.35 * 100) / 1 = 35.0
        result[0].ScoreImpact.Should().BeApproximately(35.0f, 0.1f);
    }

    [Fact]
    public void GenerateFromAllChecks_MultipleChecksInSameCategory_SplitsImpact()
    {
        var checks = new List<ChecklistItem>
        {
            new()
            {
                Id = "td_present",
                Score = false,
                Severity = Priority.P0,
                Prompt = "Desc present",
                Reason = "Missing.",
                Category = CheckCategory.ToolDescription,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Fix.",
            },
            new()
            {
                Id = "td_min_length",
                Score = false,
                Severity = Priority.P1,
                Prompt = "Min length",
                Reason = "Too short.",
                Category = CheckCategory.ToolDescription,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Fix.",
            },
        };

        var result = ActionItemGenerator.GenerateFromAllChecks(checks, "tool1");

        // 2 checks in tool_description: (0.35 * 100) / 2 = 17.5 each
        result.Should().HaveCount(2);
        result.Should().AllSatisfy(item =>
            item.ScoreImpact.Should().BeApproximately(17.5f, 0.1f));
    }

    [Fact]
    public void GenerateFromAllChecks_SortedByPriority()
    {
        var checks = new List<ChecklistItem>
        {
            new()
            {
                Id = "check_p3",
                Score = false,
                Severity = Priority.P3,
                Prompt = "P3",
                Reason = "Fail.",
                Category = CheckCategory.SchemaStructure,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Fix.",
            },
            new()
            {
                Id = "check_p0",
                Score = false,
                Severity = Priority.P0,
                Prompt = "P0",
                Reason = "Fail.",
                Category = CheckCategory.SchemaStructure,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Fix.",
            },
        };

        var result = ActionItemGenerator.GenerateFromAllChecks(checks, "tool1");

        result[0].Priority.Should().Be(Priority.P0);
        result[1].Priority.Should().Be(Priority.P3);
    }

    [Fact]
    public void GenerateFromAllChecks_NullToolName_SetsToolNameNull()
    {
        var checks = new List<ChecklistItem>
        {
            new()
            {
                Id = "ts_check",
                Score = false,
                Severity = Priority.P1,
                Prompt = "Toolset check",
                Reason = "Fail.",
                Category = CheckCategory.ToolsetDesign,
                SmellIds = [],
                ImpactAreas = [],
                Remediation = "Fix.",
            },
        };

        var result = ActionItemGenerator.GenerateFromAllChecks(checks, null);

        result[0].ToolName.Should().BeNull();
    }
}
