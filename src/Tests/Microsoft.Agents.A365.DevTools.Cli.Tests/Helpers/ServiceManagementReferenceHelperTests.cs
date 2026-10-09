// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

public class ServiceManagementReferenceHelperTests
{
    private const string ReferenceId = "6f0e5d8a-3b1c-4c2d-9e7f-1a2b3c4d5e6f";
    private const string OtherReferenceId = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";

    [Theory]
    [InlineData(ReferenceId)]
    [InlineData("6F0E5D8A-3B1C-4C2D-9E7F-1A2B3C4D5E6F")]
    [InlineData("{6f0e5d8a-3b1c-4c2d-9e7f-1a2b3c4d5e6f}")]
    [InlineData("6f0e5d8a3b1c4c2d9e7f1a2b3c4d5e6f")]
    [InlineData("  6f0e5d8a-3b1c-4c2d-9e7f-1a2b3c4d5e6f  ")]
    public void TryNormalize_AnyGuidFormat_ReturnsCanonicalForm(string value)
    {
        ServiceManagementReferenceHelper.TryNormalize(value, out var normalized).Should().BeTrue();
        normalized.Should().Be(ReferenceId,
            because: "the value sent as serviceManagementReference is always the canonical hyphenated GUID");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void TryNormalize_BlankNonGuidOrZeroGuid_ReturnsFalse(string? value)
    {
        ServiceManagementReferenceHelper.TryNormalize(value, out var normalized).Should().BeFalse(
            because: "blank, non-GUID, and all-zero values are never a usable serviceManagementReference and must be rejected locally");
        normalized.Should().BeNull();
    }

    [Fact]
    public void Resolve_FlagValueOverridesConfig()
    {
        var config = new Agent365Config { ServiceManagementReference = OtherReferenceId };

        ServiceManagementReferenceHelper.Resolve(ReferenceId, config).Should().Be(ReferenceId,
            because: "--service-management-reference overrides serviceManagementReference in a365.config.json");
    }

    [Fact]
    public void Resolve_WithoutFlag_UsesConfigValue()
    {
        var config = new Agent365Config { ServiceManagementReference = OtherReferenceId.ToUpperInvariant() };

        ServiceManagementReferenceHelper.Resolve(null, config).Should().Be(OtherReferenceId,
            because: "serviceManagementReference in a365.config.json applies when the option is omitted");
    }

    [Fact]
    public void Resolve_WithNeitherValue_ReturnsNull()
    {
        ServiceManagementReferenceHelper.Resolve(null, new Agent365Config()).Should().BeNull(
            because: "without a value the create payload must stay unchanged");
        ServiceManagementReferenceHelper.Resolve(null, config: null).Should().BeNull();
    }
}
