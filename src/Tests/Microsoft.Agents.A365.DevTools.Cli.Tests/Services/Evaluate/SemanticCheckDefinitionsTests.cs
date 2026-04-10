// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Evaluate;

public class SemanticCheckDefinitionsTests
{
    // -----------------------------------------------------------------------
    // GetToolLevelChecks
    // -----------------------------------------------------------------------

    [Fact]
    public void GetToolLevelChecks_ReturnsExactly10Items()
    {
        var checks = SemanticCheckDefinitions.GetToolLevelChecks();
        checks.Should().HaveCount(10);
    }

    [Fact]
    public void GetToolLevelChecks_AllHaveSemanticType()
    {
        var checks = SemanticCheckDefinitions.GetToolLevelChecks();
        checks.Should().AllSatisfy(c => c.Type.Should().Be(CheckType.Semantic));
    }

    [Fact]
    public void GetToolLevelChecks_AllHaveNullScore()
    {
        var checks = SemanticCheckDefinitions.GetToolLevelChecks();
        checks.Should().AllSatisfy(c => c.Score.Should().BeNull());
    }

    [Fact]
    public void GetToolLevelChecks_AllHaveNullReason()
    {
        var checks = SemanticCheckDefinitions.GetToolLevelChecks();
        checks.Should().AllSatisfy(c => c.Reason.Should().BeNull());
    }

    [Fact]
    public void GetToolLevelChecks_AllHaveNonEmptyPrompt()
    {
        var checks = SemanticCheckDefinitions.GetToolLevelChecks();
        checks.Should().AllSatisfy(c => c.Prompt.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void GetToolLevelChecks_AllHaveNonEmptyId()
    {
        var checks = SemanticCheckDefinitions.GetToolLevelChecks();
        checks.Should().AllSatisfy(c => c.Id.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void GetToolLevelChecks_AllHaveNonEmptyRemediation()
    {
        var checks = SemanticCheckDefinitions.GetToolLevelChecks();
        checks.Should().AllSatisfy(c => c.Remediation.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void GetToolLevelChecks_AllHaveNonEmptySmellIds()
    {
        var checks = SemanticCheckDefinitions.GetToolLevelChecks();
        checks.Should().AllSatisfy(c => c.SmellIds.Should().NotBeEmpty());
    }

    [Fact]
    public void GetToolLevelChecks_AllHaveNonEmptyImpactAreas()
    {
        var checks = SemanticCheckDefinitions.GetToolLevelChecks();
        checks.Should().AllSatisfy(c => c.ImpactAreas.Should().NotBeEmpty());
    }

    [Fact]
    public void GetToolLevelChecks_ContainsExpectedCheckIds()
    {
        var checks = SemanticCheckDefinitions.GetToolLevelChecks();
        var ids = checks.Select(c => c.Id).ToList();

        ids.Should().Contain("tn_verb_prefix");
        ids.Should().Contain("tn_not_generic");
        ids.Should().Contain("tn_descriptive");
        ids.Should().Contain("td_has_purpose");
        ids.Should().Contain("td_not_name_echo");
        ids.Should().Contain("td_has_usage_guidelines");
        ids.Should().Contain("td_has_limitations");
        ids.Should().Contain("td_has_return_docs");
        ids.Should().Contain("td_has_examples");
        ids.Should().Contain("td_no_boilerplate");
    }

    [Fact]
    public void GetToolLevelChecks_HasExpectedCategories()
    {
        var checks = SemanticCheckDefinitions.GetToolLevelChecks();

        var toolNameChecks = checks.Where(c => c.Category == CheckCategory.ToolName).ToList();
        var toolDescChecks = checks.Where(c => c.Category == CheckCategory.ToolDescription).ToList();

        toolNameChecks.Should().HaveCount(3);
        toolDescChecks.Should().HaveCount(7);
    }

    [Fact]
    public void GetToolLevelChecks_HasExpectedSeverities()
    {
        var checks = SemanticCheckDefinitions.GetToolLevelChecks();
        var ids = checks.ToDictionary(c => c.Id, c => c.Severity);

        ids["tn_verb_prefix"].Should().Be(Priority.P1);
        ids["tn_not_generic"].Should().Be(Priority.P1);
        ids["tn_descriptive"].Should().Be(Priority.P2);
        ids["td_has_purpose"].Should().Be(Priority.P0);
        ids["td_not_name_echo"].Should().Be(Priority.P2);
        ids["td_has_usage_guidelines"].Should().Be(Priority.P1);
        ids["td_has_limitations"].Should().Be(Priority.P2);
        ids["td_has_return_docs"].Should().Be(Priority.P1);
        ids["td_has_examples"].Should().Be(Priority.P2);
        ids["td_no_boilerplate"].Should().Be(Priority.P1);
    }

    [Fact]
    public void GetToolLevelChecks_ReturnsNewInstanceEachCall()
    {
        var checks1 = SemanticCheckDefinitions.GetToolLevelChecks();
        var checks2 = SemanticCheckDefinitions.GetToolLevelChecks();

        checks1.Should().NotBeSameAs(checks2);
    }

    [Fact]
    public void GetToolLevelChecks_HasUniqueIds()
    {
        var checks = SemanticCheckDefinitions.GetToolLevelChecks();
        var ids = checks.Select(c => c.Id).ToList();
        ids.Should().OnlyHaveUniqueItems();
    }

    // -----------------------------------------------------------------------
    // GetParamLevelChecks
    // -----------------------------------------------------------------------

    [Fact]
    public void GetParamLevelChecks_ReturnsExactly4Items()
    {
        var checks = SemanticCheckDefinitions.GetParamLevelChecks("userId");
        checks.Should().HaveCount(4);
    }

    [Fact]
    public void GetParamLevelChecks_AllHaveSemanticType()
    {
        var checks = SemanticCheckDefinitions.GetParamLevelChecks("query");
        checks.Should().AllSatisfy(c => c.Type.Should().Be(CheckType.Semantic));
    }

    [Fact]
    public void GetParamLevelChecks_AllHaveNullScore()
    {
        var checks = SemanticCheckDefinitions.GetParamLevelChecks("query");
        checks.Should().AllSatisfy(c => c.Score.Should().BeNull());
    }

    [Fact]
    public void GetParamLevelChecks_ContainsExpectedCheckIds()
    {
        var checks = SemanticCheckDefinitions.GetParamLevelChecks("status");
        var ids = checks.Select(c => c.Id).ToList();

        ids.Should().Contain("pn_not_generic");
        ids.Should().Contain("pd_not_name_echo");
        ids.Should().Contain("pd_has_constraints");
        ids.Should().Contain("pd_enum_for_categorical");
    }

    [Fact]
    public void GetParamLevelChecks_IncludesParamNameInPrompts()
    {
        const string paramName = "messageId";
        var checks = SemanticCheckDefinitions.GetParamLevelChecks(paramName);

        checks.Should().AllSatisfy(c =>
            c.Prompt.Should().Contain(paramName, because: "prompts should reference the specific parameter"));
    }

    [Fact]
    public void GetParamLevelChecks_IncludesParamNameInRemediation()
    {
        const string paramName = "searchQuery";
        var checks = SemanticCheckDefinitions.GetParamLevelChecks(paramName);

        checks.Should().AllSatisfy(c =>
            c.Remediation.Should().Contain(paramName, because: "remediation should reference the specific parameter"));
    }

    [Fact]
    public void GetParamLevelChecks_HasExpectedCategories()
    {
        var checks = SemanticCheckDefinitions.GetParamLevelChecks("query");

        var paramNameChecks = checks.Where(c => c.Category == CheckCategory.ParamName).ToList();
        var paramDescChecks = checks.Where(c => c.Category == CheckCategory.ParamDescription).ToList();

        paramNameChecks.Should().HaveCount(1);
        paramDescChecks.Should().HaveCount(3);
    }

    [Fact]
    public void GetParamLevelChecks_HasUniqueIds()
    {
        var checks = SemanticCheckDefinitions.GetParamLevelChecks("test");
        var ids = checks.Select(c => c.Id).ToList();
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void GetParamLevelChecks_DifferentParamsProduceDifferentPrompts()
    {
        var checks1 = SemanticCheckDefinitions.GetParamLevelChecks("userId");
        var checks2 = SemanticCheckDefinitions.GetParamLevelChecks("status");

        // The prompts should differ because they contain the param name
        for (int i = 0; i < checks1.Count; i++)
        {
            checks1[i].Prompt.Should().NotBe(checks2[i].Prompt);
        }
    }

    // -----------------------------------------------------------------------
    // GetToolsetLevelChecks
    // -----------------------------------------------------------------------

    [Fact]
    public void GetToolsetLevelChecks_ReturnsExactly2Items()
    {
        var checks = SemanticCheckDefinitions.GetToolsetLevelChecks();
        checks.Should().HaveCount(2);
    }

    [Fact]
    public void GetToolsetLevelChecks_AllHaveSemanticType()
    {
        var checks = SemanticCheckDefinitions.GetToolsetLevelChecks();
        checks.Should().AllSatisfy(c => c.Type.Should().Be(CheckType.Semantic));
    }

    [Fact]
    public void GetToolsetLevelChecks_AllHaveNullScore()
    {
        var checks = SemanticCheckDefinitions.GetToolsetLevelChecks();
        checks.Should().AllSatisfy(c => c.Score.Should().BeNull());
    }

    [Fact]
    public void GetToolsetLevelChecks_ContainsExpectedCheckIds()
    {
        var checks = SemanticCheckDefinitions.GetToolsetLevelChecks();
        var ids = checks.Select(c => c.Id).ToList();

        ids.Should().Contain("ts_no_description_overlap");
        ids.Should().Contain("ts_crud_completeness");
    }

    [Fact]
    public void GetToolsetLevelChecks_AllInToolsetDesignCategory()
    {
        var checks = SemanticCheckDefinitions.GetToolsetLevelChecks();
        checks.Should().AllSatisfy(c =>
            c.Category.Should().Be(CheckCategory.ToolsetDesign));
    }

    [Fact]
    public void GetToolsetLevelChecks_HasExpectedSeverities()
    {
        var checks = SemanticCheckDefinitions.GetToolsetLevelChecks();
        var ids = checks.ToDictionary(c => c.Id, c => c.Severity);

        ids["ts_no_description_overlap"].Should().Be(Priority.P1);
        ids["ts_crud_completeness"].Should().Be(Priority.P2);
    }

    [Fact]
    public void GetToolsetLevelChecks_HasUniqueIds()
    {
        var checks = SemanticCheckDefinitions.GetToolsetLevelChecks();
        var ids = checks.Select(c => c.Id).ToList();
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void GetToolsetLevelChecks_ReturnsNewInstanceEachCall()
    {
        var checks1 = SemanticCheckDefinitions.GetToolsetLevelChecks();
        var checks2 = SemanticCheckDefinitions.GetToolsetLevelChecks();

        checks1.Should().NotBeSameAs(checks2);
    }
}
