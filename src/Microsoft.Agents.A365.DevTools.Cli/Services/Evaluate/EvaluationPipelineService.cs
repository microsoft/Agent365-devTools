// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
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
    private readonly IAgent365ToolingService _toolingService;

    public EvaluationPipelineService(
        ILogger<EvaluationPipelineService> logger,
        ISchemaDiscoveryService discoveryService,
        IChecklistGenerator checklistGenerator,
        IChecklistEvaluator checklistEvaluator,
        IEvaluationAnalyzer evaluationAnalyzer,
        IReportGenerator reportGenerator,
        IAgent365ToolingService toolingService)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(discoveryService);
        ArgumentNullException.ThrowIfNull(checklistGenerator);
        ArgumentNullException.ThrowIfNull(checklistEvaluator);
        ArgumentNullException.ThrowIfNull(evaluationAnalyzer);
        ArgumentNullException.ThrowIfNull(reportGenerator);
        ArgumentNullException.ThrowIfNull(toolingService);
        _logger = logger;
        _discoveryService = discoveryService;
        _checklistGenerator = checklistGenerator;
        _checklistEvaluator = checklistEvaluator;
        _evaluationAnalyzer = evaluationAnalyzer;
        _reportGenerator = reportGenerator;
        _toolingService = toolingService;
    }

    /// <inheritdoc />
    public async Task<int> RunAsync(string serverUrl, string outputDir, string evalEngine, string? authToken, CancellationToken cancellationToken)
    {
        try
        {
            // Validate the output directory up front so an explicit empty value fails fast
            // with a targeted error instead of a deep exception from Directory.CreateDirectory
            // late in the run.
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                _logger.LogError("--output-dir cannot be empty or whitespace.");
                return 1;
            }

            // Best-effort usage telemetry, time-bounded so a slow or hung endpoint cannot
            // stall the user's run before discovery starts. Identity is extracted server-side
            // from the bearer token; the CLI does not pass the evaluated server URL (customer
            // content). Failures (swallowed inside the service) and the timeout are ignored.
            using (var telemetryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                telemetryCts.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await _toolingService.LogEvaluateUsageAsync(telemetryCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Telemetry timed out — non-fatal; continue with the evaluation.
                }
            }

            var engine = ParseEvalEngine(evalEngine);

            // Trust boundary: when an engine will run, the server's tool names and
            // descriptions are handed to it for scoring. Surface this once per run, and be
            // accurate about where the content goes — a locally running coding agent vs a
            // remote Azure OpenAI endpoint. (--eval-engine none keeps everything local.)
            if (engine == EvalEngine.AzureOpenAI)
            {
                _logger.LogWarning("The server's tool names and descriptions are sent to your configured Azure OpenAI endpoint for scoring. Only evaluate MCP servers you trust.");
            }
            else if (engine != EvalEngine.None)
            {
                _logger.LogWarning("The server's tool names and descriptions are sent to a locally running coding agent for scoring. Only evaluate MCP servers you trust.");
            }

            // Brief intro so first-time users know what backing service this needs.
            if (engine == EvalEngine.Auto)
            {
                _logger.LogInformation("Semantic checks are scored by a locally installed coding agent (GitHub Copilot or Claude Code).");
                _logger.LogInformation("If neither is installed, the run will stop after generating the checklist and print steps to score it with your own LLM.");
                _logger.LogInformation("");
            }

            // Derive checklist path first so we can detect an in-progress evaluation.
            // Run the derived name through the same sanitizer as the report filename so
            // any invalid-for-filesystem characters (?, *, <, etc.) from the fallback path
            // don't crash Path.Combine / File.Exists downstream.
            var serverName = DeriveServerName(serverUrl);
            var safeServerName = ReportGenerator.SanitizeFileName(serverName);
            var checklistPath = Path.Combine(outputDir, $"{safeServerName}_checklist.json");

            EvaluationChecklist checklist;

            if (File.Exists(checklistPath))
            {
                // Resume path: an earlier run wrote this checklist; treat it as the source of truth.
                // This is how the bring-your-own-LLM workflow round-trips: user scored the file,
                // re-runs the same command, and we pick up where they left off.
                _logger.LogInformation("[1/5] Resuming from existing checklist at {Path}", checklistPath);
                checklist = await LoadChecklistAsync(checklistPath, cancellationToken);
                _logger.LogInformation("      Loaded {ToolCount} tool{Plural} (skipping server discovery — delete the file to re-discover)",
                    checklist.Tools.Count, checklist.Tools.Count == 1 ? "" : "s");

                var totalSemanticChecks = CountSemanticChecks(checklist);
                _logger.LogInformation("[2/5] Checklist has {Count} semantic check{Plural}", totalSemanticChecks, totalSemanticChecks == 1 ? "" : "s");
            }
            else
            {
                // Fresh run: discover the server and generate a new checklist.
                _logger.LogInformation("[1/5] Discovering tools from {ServerUrl}", serverUrl);
                var tools = await _discoveryService.DiscoverToolsAsync(serverUrl, authToken, cancellationToken);
                _logger.LogInformation("      Found {ToolCount} tool{Plural}", tools.Count, tools.Count == 1 ? "" : "s");

                checklist = _checklistGenerator.Generate(tools, serverName, serverUrl);
                var totalSemanticChecks = CountSemanticChecks(checklist);
                _logger.LogInformation("[2/5] Generated evaluation checklist ({Count} semantic checks)", totalSemanticChecks);
            }

            // Step 3: Semantic Evaluation
            _logger.LogInformation("[3/5] Running semantic evaluation");
            var evalResult = await _checklistEvaluator.EvaluateAsync(checklist, checklistPath, engine, cancellationToken);
            checklist = evalResult.Checklist;

            if (evalResult.Outcome != EvaluationOutcome.Completed)
            {
                // Semantic evaluation couldn't complete (no agent, partial scoring, etc.).
                // Stop before analysis — proceeding with null scores would produce an
                // inflated report (Scorer treats unscored categories as 100).
                // ChecklistEvaluator has already printed the detailed "pick one" guidance;
                // here we just append the concrete re-run command that carries their flags.
                _logger.LogInformation("  Re-run command: a365 develop-mcp evaluate --server-url {Url} --output-dir {OutDir}",
                    serverUrl, outputDir);

                // OptedOut (--eval-engine none) is a deliberate bring-your-own-LLM stop, so
                // exit 0. "No agent" or "agent left checks unscored" means we could not do
                // what was asked — exit 1 so CI sees a failure, not a silent success.
                return evalResult.Outcome == EvaluationOutcome.OptedOut ? 0 : 1;
            }

            // Step 4: Analysis
            // Persist the human-readable display name ("GitHub Copilot", "Claude Code")
            // in the report instead of the raw enum identifier so downstream consumers
            // don't have to map "GitHubCopilot" back to something user-facing. Prefer
            // the engine that actually produced evaluations over the user's request,
            // so --eval-engine auto reports as "GitHub Copilot" (or whichever ran)
            // instead of the meaningless "auto".
            var engineName = _checklistEvaluator.FormatEngineName(evalResult.EngineUsed ?? engine);
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

            return 0;
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

    private static readonly JsonSerializerOptions ChecklistReadOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Loads an existing checklist from disk. Used on re-runs where the user has
    /// already scored (or partially scored) the file with their own LLM.
    /// </summary>
    private static async Task<EvaluationChecklist> LoadChecklistAsync(string path, CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new EvaluationException(
                ErrorCodes.EvaluationFailed,
                $"Failed to read existing checklist at '{path}'.",
                errorDetails: new List<string> { ex.Message },
                mitigationSteps: new List<string>
                {
                    "Verify the file is readable and not locked by another process.",
                    "Delete the file to force a fresh discovery on the next run."
                },
                innerException: ex);
        }

        EvaluationChecklist? checklist;
        try
        {
            checklist = JsonSerializer.Deserialize<EvaluationChecklist>(json, ChecklistReadOptions);
        }
        catch (JsonException ex)
        {
            throw new EvaluationException(
                ErrorCodes.EvaluationFailed,
                $"Existing checklist at '{path}' is not valid JSON.",
                errorDetails: new List<string> { ex.Message },
                mitigationSteps: new List<string>
                {
                    "Validate the JSON with your editor or an online linter.",
                    "Delete the file to force a fresh discovery on the next run."
                },
                innerException: ex);
        }

        if (checklist is null)
        {
            throw new EvaluationException(
                ErrorCodes.EvaluationFailed,
                $"Existing checklist at '{path}' deserialized to null.",
                mitigationSteps: new List<string>
                {
                    "Delete the file to force a fresh discovery on the next run."
                });
        }

        return checklist;
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
            "github-copilot" => EvalEngine.GitHubCopilot,
            "claude-code" => EvalEngine.ClaudeCode,
            "azure-openai" => EvalEngine.AzureOpenAI,
            "none" => EvalEngine.None,
            _ => throw new EvaluationException(
                ErrorCodes.EvaluationFailed,
                $"Unknown eval engine: '{value}'.",
                mitigationSteps: new List<string>
                {
                    "Use one of: auto, github-copilot, claude-code, azure-openai, none"
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
