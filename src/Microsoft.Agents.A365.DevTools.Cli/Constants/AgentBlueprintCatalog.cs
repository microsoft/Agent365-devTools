// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Constants;
/// <summary>
/// Well-known Microsoft first-party agent blueprints, so callers can find a blueprint ID by name
/// instead of having to know the GUID. Third-party and tenant-specific blueprints are not listed.
/// </summary>
public static class AgentBlueprintCatalog
{
    /// <summary>
    /// A first-party agent blueprint published by Microsoft.
    /// </summary>
    /// <param name="BlueprintId">The blueprint ID (GUID) passed to --agent-blueprint-id.</param>
    /// <param name="DisplayName">The product name shown to users.</param>
    public sealed record KnownBlueprint(string BlueprintId, string DisplayName);

    /// <summary>
    /// First-party blueprints, ordered by display name for stable output.
    /// </summary>
    public static readonly IReadOnlyList<KnownBlueprint> FirstPartyBlueprints =
    [
        new("eae28989-4f01-479b-8072-22902e554780", "Sales Development Agent"),
    ];

    /// <summary>
    /// Returns the display name for a known first-party blueprint, or null when the ID is not
    /// first-party. A null result is expected for tenant-specific blueprints and is not an error.
    /// </summary>
    public static string? TryGetDisplayName(string? blueprintId)
    {
        if (string.IsNullOrWhiteSpace(blueprintId))
        {
            return null;
        }

        foreach (var blueprint in FirstPartyBlueprints)
        {
            if (string.Equals(blueprint.BlueprintId, blueprintId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return blueprint.DisplayName;
            }
        }

        return null;
    }

    /// <summary>
    /// Formats the catalog as a single line for option help text.
    /// </summary>
    public static string FormatForHelp() =>
        string.Join("; ", FirstPartyBlueprints.Select(b => $"{b.DisplayName} ({b.BlueprintId})"));

    /// <summary>
    /// Formats the catalog as indented, name-aligned lines for terminal output.
    /// </summary>
    public static IReadOnlyList<string> FormatAsLines()
    {
        if (FirstPartyBlueprints.Count == 0)
        {
            return [];
        }

        var nameWidth = FirstPartyBlueprints.Max(b => b.DisplayName.Length);
        return FirstPartyBlueprints
            .Select(b => $"  {b.DisplayName.PadRight(nameWidth)}  {b.BlueprintId}")
            .ToList();
    }
}
