// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// Command for evaluating MCP server tool schema quality.
/// Runs a 5-step pipeline: discovery, checklist generation, evaluation,
/// analysis, and report generation.
/// </summary>
public static class EvaluateCommand
{
    private static readonly JsonSerializerOptions ChecklistSerializerOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Creates the evaluate command with options for server URL, output directory, and eval engine.
    /// </summary>
    public static Command CreateCommand(
        ILogger logger,
        ISchemaDiscoveryService discoveryService,
        IChecklistGenerator checklistGenerator,
        IChecklistEvaluator checklistEvaluator,
        IEvaluationAnalyzer evaluationAnalyzer,
        IReportGenerator reportGenerator)
    {
        var command = new Command("evaluate", "Evaluate MCP server tool schema quality and generate an HTML report");

        // Positional argument for server URL
        var serverUrlArg = new Argument<string>("server-url", "MCP server Streamable HTTP endpoint URL");
        command.AddArgument(serverUrlArg);

        // Optional options with defaults
        var outputDirOption = new Option<string>(
            ["--output-dir", "-o"],
            getDefaultValue: () => ".",
            "Output directory for evaluation artifacts");

        var evalEngineOption = new Option<string>(
            "--eval-engine",
            getDefaultValue: () => "auto",
            "Coding agent for semantic evaluation (auto, github-copilot, claude-code, none)");

        var authTokenOption = new Option<string?>(
            "--auth-token",
            "Bearer token for MCP server authentication");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            "Enable verbose logging");

        command.AddOption(outputDirOption);
        command.AddOption(evalEngineOption);
        command.AddOption(authTokenOption);
        command.AddOption(verboseOption);

        command.SetHandler(async (serverUrl, outputDir, evalEngine, authToken, verbose) =>
        {
            try
            {
                // Parse eval engine
                var engine = ParseEvalEngine(evalEngine);

                // Step 1: Schema Discovery
                logger.LogInformation("Discovering tools from {ServerUrl}...", serverUrl);
                var tools = await discoveryService.DiscoverToolsAsync(serverUrl, authToken);

                // Step 2: Checklist Generation
                var serverName = DeriveServerName(serverUrl);
                logger.LogInformation("Found {ToolCount} tools. Generating evaluation checklist...", tools.Count);
                var checklist = checklistGenerator.Generate(tools, serverName, serverUrl);

                // Step 3: Evaluate (writes checklist to file, invokes coding agent, re-reads)
                var checklistPath = Path.Combine(outputDir, $"{serverName}_checklist.json");
                logger.LogInformation("Evaluating checklist...");
                var evalResult = await checklistEvaluator.EvaluateAsync(checklist, checklistPath, engine);
                checklist = evalResult.Checklist;

                if (!evalResult.SemanticEvaluationCompleted && engine != EvalEngine.None)
                {
                    // Semantic evaluation didn't run -- stop here, don't generate a partial report
                    logger.LogInformation(
                        "Checklist saved to {Path}. Complete the semantic evaluation above, then re-run to generate the report.",
                        Path.GetFullPath(checklistPath));
                    return;
                }

                // Step 4: Analysis
                logger.LogInformation("Analyzing results...");
                var engineName = engine.ToString();
                var result = evaluationAnalyzer.Analyze(checklist, engineName);

                // Step 5: Report Generation
                logger.LogInformation("Generating report...");
                await reportGenerator.GenerateAsync(result, outputDir);

                logger.LogInformation(
                    "Evaluation complete! Score: {Score}/100 (Level {Level})",
                    result.OverallScore.ToString("F0"),
                    result.Maturity.Level);
            }
            catch (EvaluationException)
            {
                // EvaluationException is an Agent365Exception and will be handled
                // by the global exception handler in Program.cs
                Environment.ExitCode = 1;
                throw;
            }
            catch (Exception ex) when (ex is not Agent365Exception)
            {
                logger.LogError(ex, "Evaluation failed unexpectedly: {Message}", ex.Message);
                Environment.ExitCode = 1;
                throw new EvaluationException(
                    ErrorCodes.EvaluationFailed,
                    "Evaluation failed unexpectedly.",
                    errorDetails: new List<string> { ex.Message },
                    mitigationSteps: new List<string>
                    {
                        "Verify the MCP server is running and accessible.",
                        "Check the output directory is writable.",
                        "Run with --verbose for more details."
                    },
                    innerException: ex);
            }
        }, serverUrlArg, outputDirOption, evalEngineOption, authTokenOption, verboseOption);

        return command;
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
            // Use host, replace dots and colons with hyphens for filesystem safety
            var host = uri.Host.Replace('.', '-').Replace(':', '-');

            // Include port if non-standard
            if (!uri.IsDefaultPort)
            {
                host = $"{host}-{uri.Port}";
            }

            return host;
        }
        catch (UriFormatException)
        {
            // Fallback: sanitize the raw input
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
