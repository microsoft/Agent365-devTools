// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Orchestrates the full MCP tool schema evaluation pipeline:
/// discovery, checklist generation, evaluation, analysis, and report generation.
/// </summary>
public interface IEvaluationPipelineService
{
    /// <summary>
    /// Runs the evaluation pipeline against an MCP server.
    /// </summary>
    /// <param name="serverUrl">MCP server Streamable HTTP endpoint URL.</param>
    /// <param name="outputDir">Output directory for evaluation artifacts.</param>
    /// <param name="evalEngine">Coding agent engine name (auto, github-copilot, claude-code, none).</param>
    /// <param name="authToken">Optional bearer token for MCP server authentication.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RunAsync(string serverUrl, string outputDir, string evalEngine, string? authToken, CancellationToken cancellationToken);
}
