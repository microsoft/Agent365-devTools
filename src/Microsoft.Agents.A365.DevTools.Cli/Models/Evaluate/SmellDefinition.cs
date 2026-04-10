// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

/// <summary>
/// Defines a single "smell" from the 18-smell taxonomy for MCP tool schemas.
/// Based on Li et al. (arXiv:2602.18914) and Hasan et al. (arXiv:2602.14878).
/// </summary>
public class SmellDefinition
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public SmellCategory Category { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Impact { get; init; } = string.Empty;
    public List<ImpactArea> ImpactAreas { get; init; } = [];
}
