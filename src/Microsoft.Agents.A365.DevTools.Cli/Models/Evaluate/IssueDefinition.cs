// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

/// <summary>
/// Definition of a schema-quality issue that a checklist check can surface,
/// used to link failed checks back to a human-readable name and impact.
/// </summary>
public class IssueDefinition
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public IssueCategory Category { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Impact { get; init; } = string.Empty;
    public List<ImpactArea> ImpactAreas { get; init; } = [];
}
