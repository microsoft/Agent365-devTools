// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

/// <summary>
/// Regression tests for <see cref="SetupHelpers.DryRunRow(int, string)"/> formatting.
/// PadRight is a no-op when the input is already at or over the target width, which
/// caused the visible bug "Blueprint Permission GrantsPENDING" (32-char label, 30-char
/// column → no separator). These tests ensure every row label currently rendered by
/// the dry-run plan and Setup Summary leaves at least one space before the value
/// column so labels and values never collide.
/// </summary>
public class SetupHelpersDryRunRowTests
{
    // Labels actually emitted by NonDwBlueprintSetupOrchestrator.PrintDryRunPlan and
    // SetupHelpers.DisplaySetupSummary / PrintDwSetupAllDryRunPlan. Whenever a label is
    // added or renamed, add it here so the formatting invariant is enforced.
    public static readonly TheoryData<int, string> RenderedLabels = new()
    {
        { 1, "Prerequisites" },
        { 2, "Azure hosting" },
        { 2, "Blueprint" },
        { 3, "Blueprint" },
        { 3, "Inheritable Permissions" },
        { 4, "Inheritable Permissions" },
        { 4, "Blueprint Permission Grants" },
        { 5, "Blueprint Permission Grants" },
        { 5, "Agent identity" },
        { 6, "Agent Registration" },
        { 6, "Messaging endpoint" },
        { 7, "Messaging endpoint" },
        { 7, "Project settings" },
        { 8, "Project settings" },
    };

    [Theory]
    [MemberData(nameof(RenderedLabels))]
    public void DryRunRow_LabelFitsWithinColumn_LeavesAtLeastOneTrailingSpace(int step, string label)
    {
        var row = SetupHelpers.DryRunRow(step, label);

        row.Length.Should().Be(SetupHelpers.DryRunValCol,
            because: "DryRunRow pads to the fixed value column so subsequent values align; if PadRight is a no-op the row will be shorter than the value column or the label will run into the value (regression: 'Blueprint Permission GrantsPENDING')");
        row.Should().EndWith(" ",
            because: $"the rendered row '{row}' for label '{label}' at step {step} must end with at least one space so the next value does not collide with the label");
    }
}
