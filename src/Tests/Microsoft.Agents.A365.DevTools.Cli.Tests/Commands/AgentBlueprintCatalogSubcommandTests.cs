// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class AgentBlueprintCatalogSubcommandTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();

    [Fact]
    public async Task ListAgentBlueprints_Succeeds()
    {
        var command = AgentBlueprintCatalogSubcommand.CreateCommand(_logger);

        var exitCode = await command.InvokeAsync([]);

        exitCode.Should().Be(0,
            because: "listing the static catalog takes no input and cannot fail, so it must not " +
                     "report an error exit code that a script would treat as a failure");
    }

    [Fact]
    public void Catalog_IsNotEmpty()
    {
        AgentBlueprintCatalog.FirstPartyBlueprints.Should().NotBeEmpty(
            because: "the discovery command exists solely to surface these IDs - an empty catalog " +
                     "would make it useless and is the failure branch the command guards against");
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
        var ids = AgentBlueprintCatalog.FirstPartyBlueprints
            .Select(b => b.BlueprintId.ToLowerInvariant());

        ids.Should().OnlyHaveUniqueItems(
            because: "a duplicated ID under two names would make the listing ambiguous about which " +
                     "blueprint a user is targeting");
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
