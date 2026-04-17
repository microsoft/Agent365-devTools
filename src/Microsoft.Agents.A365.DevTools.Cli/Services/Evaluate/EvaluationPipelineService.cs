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
            _logger.LogInformation("[1/5] Discovering tools from {ServerUrl}", serverUrl);
            var tools = await _discoveryService.DiscoverToolsAsync(serverUrl, authToken, cancellationToken);
            _logger.LogInformation("      Found {ToolCount} tool{Plural}", tools.Count, tools.Count == 1 ? "" : "s");

            // Step 2: Checklist Generation
            var serverName = DeriveServerName(serverUrl);
            var checklist = _checklistGenerator.Generate(tools, serverName, serverUrl);
            var checklistPath = Path.Combine(outputDir, $"{serverName}_checklist.json");
            var totalSemanticChecks = CountSemanticChecks(checklist);
            _logger.LogInformation("[2/5] Generated evaluation checklist ({Count} semantic checks)", totalSemanticChecks);

            // Step 3: Semantic Evaluation
            _logger.LogInformation("[3/5] Running semantic evaluation");
            var evalResult = await _checklistEvaluator.EvaluateAsync(checklist, checklistPath, engine, cancellationToken);
            checklist = evalResult.Checklist;

            if (!evalResult.SemanticEvaluationCompleted && engine != EvalEngine.None)
            {
                // Semantic evaluation didn't run -- stop before the report so the user
                // can complete it manually and re-run.
                _logger.LogInformation("");
                _logger.LogInformation(
                    "Checklist saved at: {Path}",
                    Path.GetFullPath(checklistPath));
                _logger.LogInformation("After scoring the semantic checks, re-run with --eval-engine none to generate the report.");
                return;
            }

            // Step 4: Analysis
            var engineName = engine.ToString();
            var result = _evaluationAnalyzer.Analyze(checklist, engineName);
            _logger.LogInformation(
                "[4/5] Analysis complete: score {Score}/100, Level {Level} ({Label}), {ActionCount} action item{Plural}",
                result.OverallScore.ToString("F1"),
                result.Maturity.Level,
                result.Maturity.Label,
                result.AllActionItems.Count,
                result.AllActionItems.Count == 1 ? "" : "s");

            // Step 5: Report Generation
            _logger.LogInformation("[5/5] Writing reports");
            await _reportGenerator.GenerateAsync(result, outputDir);

            _logger.LogInformation("");
            _logger.LogInformation(
                "Done. Score: {Score}/100 | Level {Level} ({Label})",
                result.OverallScore.ToString("F0"),
                result.Maturity.Level,
                result.Maturity.Label);
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
    /// Counts semantic checks across the full checklist (tool-level + server-level).
    /// </summary>
    private static int CountSemanticChecks(EvaluationChecklist checklist)
    {
        int count = 0;
        foreach (var tool in checklist.Tools)
        {
            count += tool.Checks.ToolName.Count(c => c.Type == CheckType.Semantic);
            count += tool.Checks.ToolDescription.Count(c => c.Type == CheckType.Semantic);
            count += tool.Checks.SchemaStructure.Count(c => c.Type == CheckType.Semantic);
            foreach (var param in tool.Checks.Parameters.Values)
            {
                count += param.ParamName.Count(c => c.Type == CheckType.Semantic);
                count += param.ParamDescription.Count(c => c.Type == CheckType.Semantic);
            }
        }
        count += checklist.ServerChecks.Count(c => c.Type == CheckType.Semantic);
        return count;
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
