// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Evaluate;

/// <summary>
/// Pure-function and property tests for <see cref="AzureOpenAiLauncher"/> — its response
/// parsing (<c>ParseEvaluation</c>/<c>ExtractJson</c>), its static metadata, and the
/// per-check prompt builder. None of these touch the environment or the network, so this
/// suite is parallel-safe. Availability (which reads environment variables) is covered
/// separately in a non-parallel collection.
/// </summary>
public class AzureOpenAiLauncherTests
{
    private static AzureOpenAiLauncher CreateLauncher() =>
        new(NullLogger<AzureOpenAiLauncher>.Instance);

    // -----------------------------------------------------------------------
    // ParseEvaluation - valid inputs
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("{\"score\": true, \"reason\": \"x\"}", true, "x")]
    [InlineData("{\"score\": false, \"reason\": \"nope\"}", false, "nope")]
    [InlineData("{\"score\": \"true\", \"reason\": \"stringified pass\"}", true, "stringified pass")]
    [InlineData("{\"score\": \"false\", \"reason\": \"stringified fail\"}", false, "stringified fail")]
    public void ParseEvaluation_WellFormedJson_ParsesScoreAndReason(string output, bool expectedScore, string expectedReason)
    {
        var result = AzureOpenAiLauncher.ParseEvaluation(output);

        result.Should().NotBeNull(because: "a well-formed {score, reason} object must parse into a CheckEvaluation");
        result!.Score.Should().Be(expectedScore, because: "the score must reflect the boolean (or stringified boolean) in the response");
        result.Reason.Should().Be(expectedReason, because: "the reason text must be carried through verbatim");
    }

    [Fact]
    public void ParseEvaluation_FencedJson_StripsFenceAndParses()
    {
        var output = "```json\n{\"score\": true, \"reason\": \"fenced response\"}\n```";

        var result = AzureOpenAiLauncher.ParseEvaluation(output);

        result.Should().NotBeNull(because: "a model that wraps its JSON in a Markdown code fence must still be parsed");
        result!.Score.Should().BeTrue();
        result.Reason.Should().Be("fenced response");
    }

    [Fact]
    public void ParseEvaluation_ProseWrappedJson_ExtractsObjectAndParses()
    {
        var output = "Here is my judgment: {\"score\": false, \"reason\": \"prose around the object\"} - hope that helps.";

        var result = AzureOpenAiLauncher.ParseEvaluation(output);

        result.Should().NotBeNull(because: "stray prose around the JSON object must be tolerated by narrowing to the outermost braces");
        result!.Score.Should().BeFalse();
        result.Reason.Should().Be("prose around the object");
    }

    [Fact]
    public void ParseEvaluation_ReasonContainingBraces_PreservesBraces()
    {
        var output = "{\"score\": true, \"reason\": \"Schema {ok} valid.\"}";

        var result = AzureOpenAiLauncher.ParseEvaluation(output);

        result.Should().NotBeNull(because: "braces inside the reason string must not break extraction of the outermost object");
        result!.Score.Should().BeTrue();
        result.Reason.Should().Be("Schema {ok} valid.",
            because: "the literal braces inside the reason value must be preserved, not truncated by brace matching");
    }

    // -----------------------------------------------------------------------
    // ParseEvaluation - invalid inputs return null
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no json here at all")]
    [InlineData("{\"reason\":\"missing score\"}")]
    public void ParseEvaluation_InvalidOrIncomplete_ReturnsNull(string? output)
    {
        var result = AzureOpenAiLauncher.ParseEvaluation(output);

        result.Should().BeNull(
            because: "without a usable score the evaluator must treat the response as unscored and retry, not record a bogus result");
    }

    [Fact]
    public void ParseEvaluation_PresentButNullScore_ParsesAsFalse()
    {
        // The "score" property is present (so it is not a missing-field case) but its value is
        // JSON null. ParseEvaluation only returns null when the property is absent; a present
        // score of any non-true/non-"true" kind collapses to false, which is the safe (fail)
        // default for a malformed score rather than discarding the response entirely.
        var result = AzureOpenAiLauncher.ParseEvaluation("{\"score\": null, \"reason\": \"explicit null score\"}");

        result.Should().NotBeNull(because: "a present score property means the object is parsed, not rejected");
        result!.Score.Should().BeFalse(because: "a null score is not a pass, so it conservatively collapses to false");
        result.Reason.Should().Be("explicit null score");
    }

    // -----------------------------------------------------------------------
    // ExtractJson
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractJson_NestedObject_PreservesInnerStructure()
    {
        var output = "{\"score\": true, \"reason\": \"ok\", \"nested\": {\"a\": 1}}";

        var json = AzureOpenAiLauncher.ExtractJson(output);

        json.Should().NotBeNull(because: "a well-formed JSON object must be extractable from plain input");
        json.Should().Contain("\"nested\"", because: "a nested object must be retained within the outermost braces");
        json.Should().Contain("{\"a\": 1}", because: "the inner object must not be truncated");
    }

    [Fact]
    public void ExtractJson_BraceInsideString_DoesNotTruncate()
    {
        var output = "{\"score\": true, \"reason\": \"has a } brace in text\"}";

        var json = AzureOpenAiLauncher.ExtractJson(output);

        json.Should().NotBeNull(because: "a single self-contained object with no trailing prose must be extractable");
        json.Should().Be("{\"score\": true, \"reason\": \"has a } brace in text\"}",
            because: "LastIndexOf finds the outermost closing brace, which is correct when no prose after the object contains additional braces");
    }

    [Fact]
    public void ExtractJson_FencedJson_StripsFences()
    {
        var output = "```json\n{\"score\": true, \"reason\": \"ok\"}\n```";

        var json = AzureOpenAiLauncher.ExtractJson(output);

        json.Should().NotBeNull(because: "a fenced JSON block must be returned after fence stripping");
        json.Should().NotContain("```", because: "Markdown code fences must be stripped before the JSON is returned");
        json.Should().Be("{\"score\": true, \"reason\": \"ok\"}",
            because: "only the inner JSON object should remain after fence and whitespace stripping");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no braces at all")]
    public void ExtractJson_NullBlankOrNoBrace_ReturnsNull(string? output)
    {
        var json = AzureOpenAiLauncher.ExtractJson(output);

        json.Should().BeNull(
            because: "with no recoverable JSON object the caller must get null rather than an unparseable candidate");
    }

    // -----------------------------------------------------------------------
    // Static metadata / properties
    // -----------------------------------------------------------------------

    [Fact]
    public void Engine_IsAzureOpenAi()
    {
        CreateLauncher().Engine.Should().Be(EvalEngine.AzureOpenAI);
    }

    [Fact]
    public void DisplayName_IsAzureOpenAi()
    {
        CreateLauncher().DisplayName.Should().Be("Azure OpenAI");
    }

    [Fact]
    public void ScoresPerCheck_IsTrue()
    {
        CreateLauncher().ScoresPerCheck.Should().BeTrue(
            because: "the Azure OpenAI judge scores each assertion independently with one model call per check");
    }

    [Fact]
    public void AutoDetectable_IsFalse()
    {
        CreateLauncher().AutoDetectable.Should().BeFalse(
            because: "--eval-engine auto must never select Azure OpenAI; a plain run must not spend tokens unless the user opts in");
    }

    [Fact]
    public void AvailabilityHint_NamesBothEnvironmentVariables()
    {
        var hint = CreateLauncher().AvailabilityHint;

        hint.Should().Contain(EvalModelConstants.AzureOpenAiEndpointEnvVar,
            because: "the hint must tell the user which endpoint environment variable to set");
        hint.Should().Contain(EvalModelConstants.AzureOpenAiDeploymentEnvVar,
            because: "the hint must tell the user which deployment environment variable to set");
    }

    // -----------------------------------------------------------------------
    // BuildSingleCheckPrompt
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildSingleCheckPrompt_IncludesContextCheckAndStrictJsonFormat()
    {
        const string context = "TOOL_SCHEMA_MARKER_42";
        const string checkPrompt = "CHECK_PROMPT_MARKER_99";

        var prompt = SemanticCheckPrompts.BuildSingleCheckPrompt(context, checkPrompt);

        prompt.Should().Contain(context, because: "the model needs the tool schema as grounding for the single check");
        prompt.Should().Contain(checkPrompt, because: "the assertion under evaluation must appear in the prompt");
        prompt.Should().Contain("{\"score\": true or false, \"reason\": \"one sentence\"}",
            because: "the strict output contract pins the exact JSON shape the parser expects");
    }

    [Fact]
    public void BuildSingleCheckPrompt_OmitsFileAndEditStrategyInstructions()
    {
        var prompt = SemanticCheckPrompts.BuildSingleCheckPrompt("some schema", "some check");

        prompt.Should().NotContain("JSON file",
            because: "per-check scoring inlines the schema in the prompt; there is no file for the model to read");
        prompt.Should().NotContain("EDIT STRATEGY",
            because: "the per-check prompt returns a small object directly and never edits a checklist file");
    }
}
