// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Validation;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Validation.Tests.Validation;

public class ValidationContractsTests
{
    [Fact]
    public void Success_ReturnsValidOutcomeWithoutIssues()
    {
        // Act
        var outcome = ValidationOutcome.Success();

        // Assert
        outcome.IsValid.Should().BeTrue("a success outcome must indicate that validation passed");
        outcome.Issues.Should().BeEmpty("a successful validation should not carry issues");
    }

    [Fact]
    public void Failure_ReturnsInvalidOutcomeWithIssues()
    {
        // Arrange
        var issues = new[]
        {
            new ValidationIssue("MISSING_BLUEPRINT", "Blueprint ID is required"),
            new ValidationIssue("MISSING_MANIFEST", "ToolingManifest.json not found", ValidationSeverity.Warning)
        };

        // Act
        var outcome = ValidationOutcome.Failure(issues);

        // Assert
        outcome.IsValid.Should().BeFalse("a failure outcome must indicate validation did not pass");
        outcome.Issues.Should().ContainInOrder(issues, "the failure helper should preserve the supplied issues verbatim");
    }
}