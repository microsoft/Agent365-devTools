// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Generates an evaluation checklist from discovered MCP tool schemas.
/// The checklist is the intermediate artifact between schema discovery and evaluation.
/// Deterministic checks are pre-filled with scores; semantic checks have null scores
/// to be evaluated later by a coding agent or human reviewer.
/// </summary>
public interface IChecklistGenerator
{
    /// <summary>
    /// Generates a complete evaluation checklist for the given tool schemas.
    /// </summary>
    /// <param name="tools">The tool schemas discovered from the MCP server.</param>
    /// <param name="serverName">Display name of the MCP server being evaluated.</param>
    /// <param name="serverUrl">Connection URL or path used to discover the server.</param>
    /// <returns>
    /// An <see cref="EvaluationChecklist"/> containing per-tool checks (deterministic and semantic)
    /// and server-level checks. Deterministic checks have pre-filled scores; semantic checks have null scores.
    /// </returns>
    EvaluationChecklist Generate(List<ToolSchema> tools, string serverName, string serverUrl);
}
