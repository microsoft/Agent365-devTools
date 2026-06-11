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
                IssueIds = [],
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
                IssueIds = [],
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
                IssueIds = [],
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
                IssueIds = [],
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
                IssueIds = [],
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
                IssueIds = [],
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
                IssueIds = [],
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
                IssueIds = [],
                ImpactAreas = [],
                Remediation = "Fix.",
            },
        };

        var result = ActionItemGenerator.GenerateFromAllChecks(checks, null);

        result[0].ToolName.Should().BeNull();
    }
}
