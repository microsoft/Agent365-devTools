// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Linq;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Constants;

public class AgentBlueprintCatalogTests
{
    [Fact]
    public void Catalog_IsNotEmpty()
    {
        AgentBlueprintCatalog.FirstPartyBlueprints.Should().NotBeEmpty(
            because: "the catalog is the only source of blueprint IDs surfaced in option help and " +
                     "error output - if it empties, users lose every discovery path");
    }

    [Fact]
    public void Catalog_EveryBlueprintIdIsAGuid()
    {
        foreach (var blueprint in AgentBlueprintCatalog.FirstPartyBlueprints)
        {
            Guid.TryParse(blueprint.BlueprintId, out _).Should().BeTrue(
                because: $"'{blueprint.DisplayName}' is copied straight into --agent-blueprint-id, " +
                         "which rejects anything that is not a GUID - a malformed entry would ship " +
                         "a value that can never work");
        }
    }

    [Fact]
    public void Catalog_BlueprintIdsAreUnique()
    {
        AgentBlueprintCatalog.FirstPartyBlueprints
            .Select(b => b.BlueprintId.ToLowerInvariant())
            .Should().OnlyHaveUniqueItems(
                because: "a duplicated ID under two names would make the listing ambiguous about " +
                         "which blueprint a user is targeting");
    }

    [Fact]
    public void Catalog_NamesAreNotBlank()
    {
        foreach (var blueprint in AgentBlueprintCatalog.FirstPartyBlueprints)
        {
            blueprint.DisplayName.Should().NotBeNullOrWhiteSpace(
                because: "the name is the only thing that makes the ID discoverable");
        }
    }

    [Fact]
    public void FormatForHelp_ContainsEveryNameAndId()
    {
        var help = AgentBlueprintCatalog.FormatForHelp();

        foreach (var blueprint in AgentBlueprintCatalog.FirstPartyBlueprints)
        {
            help.Should().Contain(blueprint.DisplayName);
            help.Should().Contain(blueprint.BlueprintId,
                because: "--help is a primary discovery surface, so every catalog entry must appear " +
                         "there rather than only in the catalog source");
        }
    }

    [Fact]
    public void FormatForHelp_IsSingleLine()
    {
        AgentBlueprintCatalog.FormatForHelp()
            .Should().MatchRegex(@"^[^\r\n]*$",
                because: "System.CommandLine wraps option descriptions itself, and embedded newlines " +
                         "break the alignment of the generated help output");
    }

    [Fact]
    public void FormatAsLines_ContainsEveryNameAndId()
    {
        var lines = AgentBlueprintCatalog.FormatAsLines();

        lines.Should().HaveCount(AgentBlueprintCatalog.FirstPartyBlueprints.Count);

        foreach (var blueprint in AgentBlueprintCatalog.FirstPartyBlueprints)
        {
            lines.Should().Contain(l => l.Contains(blueprint.BlueprintId) && l.Contains(blueprint.DisplayName),
                because: "the invalid-ID error prints these lines so the user can copy an ID without " +
                         "running anything else");
        }
    }

    [Fact]
    public void FormatAsLines_AlignsIdsAcrossEntries()
    {
        var lines = AgentBlueprintCatalog.FormatAsLines();

        var idColumns = lines
            .Select(l => l.IndexOf(l.Trim().Split("  ", StringSplitOptions.RemoveEmptyEntries).Last(), StringComparison.Ordinal))
            .Distinct();

        idColumns.Should().HaveCount(1,
            because: "names are padded to a common width so IDs form a single readable column");
    }

    [Fact]
    public void TryGetDisplayName_KnownId_ReturnsName()
    {
        var known = AgentBlueprintCatalog.FirstPartyBlueprints[0];

        AgentBlueprintCatalog.TryGetDisplayName(known.BlueprintId)
            .Should().Be(known.DisplayName);
    }

    [Fact]
    public void TryGetDisplayName_IsCaseAndWhitespaceInsensitive()
    {
        var known = AgentBlueprintCatalog.FirstPartyBlueprints[0];

        AgentBlueprintCatalog.TryGetDisplayName($"  {known.BlueprintId.ToUpperInvariant()}  ")
            .Should().Be(known.DisplayName,
                because: "GUIDs pasted from portals and docs vary in casing and carry stray " +
                         "whitespace, and none of that changes which blueprint is meant");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    public void TryGetDisplayName_UnknownOrInvalidId_ReturnsNull(string? blueprintId)
    {
        AgentBlueprintCatalog.TryGetDisplayName(blueprintId)
            .Should().BeNull(
                because: "tenant-specific blueprints are legitimate and simply absent from the " +
                         "first-party catalog, so an unknown ID must be reported as unknown rather " +
                         "than throwing or guessing a name");
    }
}
