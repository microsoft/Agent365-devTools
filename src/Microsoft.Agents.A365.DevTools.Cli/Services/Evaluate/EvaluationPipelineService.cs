// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Orchestrates the full MCP tool schema evaluation pipeline:
/// discovery, checklist generation, evaluation, analysis, and report generation.
/// </summary>
public sealed class EvaluationPipelineService : IEvaluationPipelineService
{
    private readonly ILogger<EvaluationPipelineService> _logger;
    private readonly ISchemaDiscoveryService _discoveryService;
    private readonly IChecklistGenerator _checklistGenerator;
    private readonly IChecklistEvaluator _checklistEvaluator;
    private readonly IEvaluationAnalyzer _evaluationAnalyzer;
    private readonly IReportGenerator _reportGenerator;

    public EvaluationPipelineService(
        ILogger<EvaluationPipelineService> logger,
        ISchemaDiscoveryService discoveryService,
        IChecklistGenerator checklistGenerator,
        IChecklistEvaluator checklistEvaluator,
        IEvaluationAnalyzer evaluationAnalyzer,
        IReportGenerator reportGenerator)
    {
        _logger = logger;
        _discoveryService = discoveryService;
        _checklistGenerator = checklistGenerator;
        _checklistEvaluator = checklistEvaluator;
        _evaluationAnalyzer = evaluationAnalyzer;
        _reportGenerator = reportGenerator;
    }

    /// <inheritdoc />
    public async Task RunAsync(string serverUrl, string outputDir, string evalEngine, string? authToken, CancellationToken cancellationToken)
    {
        try
        {
            var engine = ParseEvalEngine(evalEngine);

            // Step 1: Schema Discovery
            _logger.LogInformation("Discovering tools from {ServerUrl}...", serverUrl);
            var tools = await _discoveryService.DiscoverToolsAsync(serverUrl, authToken);

            // Step 2: Checklist Generation
            var serverName = DeriveServerName(serverUrl);
            _logger.LogInformation("Found {ToolCount} tools. Generating evaluation checklist...", tools.Count);
            var checklist = _checklistGenerator.Generate(tools, serverName, serverUrl);

            // Step 3: Evaluate (writes checklist to file, invokes coding agent, re-reads)
            var checklistPath = Path.Combine(outputDir, $"{serverName}_checklist.json");
            _logger.LogInformation("Evaluating checklist...");
            var evalResult = await _checklistEvaluator.EvaluateAsync(checklist, checklistPath, engine, cancellationToken);
            checklist = evalResult.Checklist;

            if (!evalResult.SemanticEvaluationCompleted && engine != EvalEngine.None)
            {
                // Semantic evaluation didn't run -- stop here, don't generate a partial report
                _logger.LogInformation(
                    "Checklist saved to {Path}. Complete the semantic evaluation above, then re-run to generate the report.",
                    Path.GetFullPath(checklistPath));
                return;
            }

            // Step 4: Analysis
            _logger.LogInformation("Analyzing results...");
            var engineName = engine.ToString();
            var result = _evaluationAnalyzer.Analyze(checklist, engineName);

            // Step 5: Report Generation
            _logger.LogInformation("Generating report...");
            await _reportGenerator.GenerateAsync(result, outputDir);

            _logger.LogInformation(
                "Evaluation complete! Score: {Score}/100 (Level {Level})",
                result.OverallScore.ToString("F0"),
                result.Maturity.Level);
        }
        catch (EvaluationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not Agent365Exception)
        {
            _logger.LogError(ex, "Evaluation failed unexpectedly: {Message}", ex.Message);
            throw new EvaluationException(
                ErrorCodes.EvaluationFailed,
                "Evaluation failed unexpectedly.",
                errorDetails: new List<string> { ex.Message },
                mitigationSteps: new List<string>
                {
                    "Verify the MCP server is running and accessible.",
                    "Check the output directory is writable."
                },
                innerException: ex);
        }
    }

    /// <summary>
    /// Parses an eval engine string into the corresponding <see cref="EvalEngine"/> enum value.
    /// </summary>
    internal static EvalEngine ParseEvalEngine(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "auto" => EvalEngine.Auto,
            "github-copilot" => EvalEngine.GithubCopilot,
            "claude-code" => EvalEngine.ClaudeCode,
            "none" => EvalEngine.None,
            _ => throw new EvaluationException(
                ErrorCodes.EvaluationFailed,
                $"Unknown eval engine: '{value}'.",
                mitigationSteps: new List<string>
                {
                    "Use one of: auto, github-copilot, claude-code, none"
                })
        };
    }

    /// <summary>
    /// Derives a filesystem-safe server name from the server URL (host part).
    /// </summary>
    internal static string DeriveServerName(string serverUrl)
    {
        try
        {
            var uri = new Uri(serverUrl);
            var host = uri.Host.Replace('.', '-').Replace(':', '-');

            if (!uri.IsDefaultPort)
            {
                host = $"{host}-{uri.Port}";
            }

            return host;
        }
        catch (UriFormatException)
        {
            var sanitized = serverUrl
                .Replace("://", "-")
                .Replace("/", "-")
                .Replace(":", "-")
                .Replace(".", "-")
                .TrimEnd('-');

            return string.IsNullOrWhiteSpace(sanitized) ? "unknown-server" : sanitized;
        }
    }
}
