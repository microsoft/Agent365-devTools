// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Evaluate;

/// <summary>
/// Tests for ChecklistEvaluator helpers, primarily RepairJson which fixes malformed
/// JSON produced by coding agents (missing commas, trailing commas) before deserialization.
/// </summary>
public class ChecklistEvaluatorTests
{
    [Fact]
    public void RepairJson_WellFormedJson_ReturnsUnchanged()
    {
        const string input = """
            {
              "id": "a",
              "score": true,
              "items": [1, 2, 3]
            }
            """;

        var result = ChecklistEvaluator.RepairJson(input);

        JsonDocument.Parse(result).Should().NotBeNull(
            because: "well-formed input must remain valid after RepairJson");
    }

    [Fact]
    public void RepairJson_MissingCommaBetweenObjects_InsertsComma()
    {
        // Agents sometimes forget the comma between adjacent object literals in an array.
        const string input = """
            [
              { "id": "a" }
              { "id": "b" }
            ]
            """;

        var result = ChecklistEvaluator.RepairJson(input);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetArrayLength().Should().Be(2,
            because: "RepairJson should make the two array elements parse as valid JSON");
    }

    [Fact]
    public void RepairJson_MissingCommaBeforeStringKey_InsertsComma()
    {
        // Pattern: "value" (no comma) followed by newline and next "key":.
        const string input = """
            {
              "a": "one"
              "b": "two"
            }
            """;

        var result = ChecklistEvaluator.RepairJson(input);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("a").GetString().Should().Be("one");
        doc.RootElement.GetProperty("b").GetString().Should().Be("two");
    }

    [Fact]
    public void RepairJson_MissingCommaAfterBooleanValue_InsertsComma()
    {
        const string input = """
            {
              "ok": true
              "next": "hi"
            }
            """;

        var result = ChecklistEvaluator.RepairJson(input);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("next").GetString().Should().Be("hi");
    }

    [Fact]
    public void RepairJson_EmptyString_ReturnsEmptyString()
    {
        var result = ChecklistEvaluator.RepairJson(string.Empty);

        result.Should().BeEmpty(
            because: "RepairJson should not throw on empty input; the caller handles parse failures");
    }
}
