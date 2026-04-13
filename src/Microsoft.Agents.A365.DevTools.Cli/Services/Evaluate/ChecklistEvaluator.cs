// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Evaluates semantic checks by writing the checklist to a file, invoking a
/// coding agent CLI as a subprocess, and re-reading the updated file.
///
/// Tries engines in order: GitHub Copilot -> Claude Code.
/// If the user specifies an engine explicitly, only that engine is tried.
/// If Auto, tries all available engines in order until one succeeds.
/// </summary>
internal sealed class ChecklistEvaluator : IChecklistEvaluator
{
    // Engine priority order: always try Copilot first
    private static readonly EvalEngine[] EnginePriority = [EvalEngine.GithubCopilot, EvalEngine.ClaudeCode];

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly CodingAgentRunner _agentRunner;
    private readonly ILogger<ChecklistEvaluator> _logger;

    public ChecklistEvaluator(CodingAgentRunner agentRunner, ILogger<ChecklistEvaluator> logger)
    {
        ArgumentNullException.ThrowIfNull(agentRunner);
        ArgumentNullException.ThrowIfNull(logger);
        _agentRunner = agentRunner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ChecklistEvaluationResult> EvaluateAsync(
        EvaluationChecklist checklist,
        string checklistPath,
        EvalEngine engine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checklist);
        ArgumentException.ThrowIfNullOrWhiteSpace(checklistPath);

        // Write full checklist to file (auditable artifact)
        var json = JsonSerializer.Serialize(checklist, WriteOptions);
        var dir = Path.GetDirectoryName(checklistPath) ?? ".";
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(checklistPath, json, cancellationToken);
        _logger.LogInformation("Checklist written to {Path}", checklistPath);

        // Count unevaluated semantic checks before starting
        int totalUnevaluatedBefore = CountTotalUnevaluatedSemanticChecks(checklist);

        // Build the list of engines to try
        var enginesToTry = await BuildEngineList(engine, cancellationToken);

        if (enginesToTry.Count == 0)
        {
            // If nothing was unevaluated to begin with, that's success (all already scored)
            if (totalUnevaluatedBefore == 0)
            {
                return new ChecklistEvaluationResult { Checklist = checklist, SemanticEvaluationCompleted = true };
            }

            LogManualEvaluationInstructions(checklistPath);
            return new ChecklistEvaluationResult { Checklist = checklist, SemanticEvaluationCompleted = false };
        }

        _logger.LogInformation("Engines available: {Engines}", string.Join(", ", enginesToTry));

        int toolsEvaluated = 0;
        int toolsFailed = 0;

        // Evaluate each tool using extract-evaluate-merge pattern.
        // The full checklist is ~1MB which is too large for coding agents.
        // Instead, extract each tool to a small temp file (~25KB), have the
        // agent evaluate it, then merge the results back into the checklist.
        for (int i = 0; i < checklist.Tools.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tool = checklist.Tools[i];
            var unevaluated = CountUnevaluatedSemanticChecks(tool);
            if (unevaluated == 0)
            {
                continue;
            }

            _logger.LogInformation("[{Current}/{Total}] Evaluating \"{ToolName}\" ({CheckCount} semantic checks)...",
                i + 1, checklist.Tools.Count, tool.Name, unevaluated);

            var success = await EvaluateToolChecks(tool, dir, enginesToTry, cancellationToken);
            if (success)
            {
                toolsEvaluated++;
            }
            else
            {
                toolsFailed++;
                _logger.LogWarning("Failed to evaluate \"{ToolName}\", continuing...", tool.Name);
            }
        }

        // Evaluate server-level checks (extract server_checks + tool list summary)
        var serverUnevaluated = checklist.ServerChecks.Count(c => c.Type == CheckType.Semantic && c.Score is null);
        if (serverUnevaluated > 0)
        {
            _logger.LogInformation("Evaluating server-level checks ({CheckCount} semantic checks)...", serverUnevaluated);
            await EvaluateServerChecks(checklist, dir, enginesToTry, cancellationToken);
        }

        // Write the updated checklist back (with all merged results)
        var updatedJson = JsonSerializer.Serialize(checklist, WriteOptions);
        await File.WriteAllTextAsync(checklistPath, updatedJson, cancellationToken);

        var semanticCount = CountEvaluatedSemanticChecks(checklist);
        _logger.LogInformation("Evaluation complete: {Evaluated} tools succeeded, {Failed} failed, {SemanticCount} semantic checks scored",
            toolsEvaluated, toolsFailed, semanticCount);

        // Completed if nothing needed evaluation OR at least one tool was evaluated
        var allAlreadyScored = totalUnevaluatedBefore == 0;
        return new ChecklistEvaluationResult
        {
            Checklist = checklist,
            SemanticEvaluationCompleted = allAlreadyScored || toolsEvaluated > 0
        };
    }

    /// <summary>
    /// Extracts a single tool to a temp file, invokes the coding agent to evaluate
    /// its semantic checks, then merges the scored results back into the tool object.
    /// </summary>
    private async Task<bool> EvaluateToolChecks(
        ToolChecklist tool,
        string workingDir,
        List<EvalEngine> engines,
        CancellationToken cancellationToken)
    {
        var tempFile = Path.Combine(workingDir, $".eval_tool_{Guid.NewGuid():N}.json");
        try
        {
            // Write just this tool to a small temp file
            var toolJson = JsonSerializer.Serialize(tool, WriteOptions);
            await File.WriteAllTextAsync(tempFile, toolJson, cancellationToken);

            var fullPath = Path.GetFullPath(tempFile);
            var prompt = SemanticCheckPrompts.BuildToolEvaluationPrompt(fullPath, tool.Name);
            var success = await TryEvaluateWithFallthrough(engines, tempFile, prompt, CodingAgentRunner.PerToolTimeout, cancellationToken);

            if (!success)
            {
                return false;
            }

            // Re-read the evaluated tool and merge scores back
            var updatedJson = await File.ReadAllTextAsync(tempFile, cancellationToken);
            var updatedTool = JsonSerializer.Deserialize<ToolChecklist>(updatedJson, WriteOptions);

            if (updatedTool is not null)
            {
                MergeScores(tool.Checks.ToolName, updatedTool.Checks.ToolName);
                MergeScores(tool.Checks.ToolDescription, updatedTool.Checks.ToolDescription);
                MergeScores(tool.Checks.SchemaStructure, updatedTool.Checks.SchemaStructure);
                foreach (var (paramName, paramChecks) in tool.Checks.Parameters)
                {
                    if (updatedTool.Checks.Parameters.TryGetValue(paramName, out var updatedParam))
                    {
                        MergeScores(paramChecks.ParamName, updatedParam.ParamName);
                        MergeScores(paramChecks.ParamDescription, updatedParam.ParamDescription);
                    }
                }
            }

            return true;
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Extracts server-level checks with a tool name summary to a temp file,
    /// invokes the coding agent, then merges results back.
    /// </summary>
    private async Task<bool> EvaluateServerChecks(
        EvaluationChecklist checklist,
        string workingDir,
        List<EvalEngine> engines,
        CancellationToken cancellationToken)
    {
        var tempFile = Path.Combine(workingDir, $".eval_server_{Guid.NewGuid():N}.json");
        try
        {
            // Build a lightweight object with tool summaries and server checks
            var serverData = new
            {
                tool_summaries = checklist.Tools.Select(t => new { t.Name, t.Description }).ToList(),
                server_checks = checklist.ServerChecks
            };
            var dataJson = JsonSerializer.Serialize(serverData, WriteOptions);
            await File.WriteAllTextAsync(tempFile, dataJson, cancellationToken);

            var fullPath = Path.GetFullPath(tempFile);
            var prompt = SemanticCheckPrompts.BuildServerChecksEvaluationPrompt(fullPath);
            var success = await TryEvaluateWithFallthrough(engines, tempFile, prompt, CodingAgentRunner.PerToolTimeout, cancellationToken);

            if (!success)
            {
                return false;
            }

            // Re-read and merge server check scores
            var updatedJson = await File.ReadAllTextAsync(tempFile, cancellationToken);
            using var doc = JsonDocument.Parse(updatedJson);
            if (doc.RootElement.TryGetProperty("server_checks", out var checksElement))
            {
                var updatedChecks = JsonSerializer.Deserialize<List<ChecklistItem>>(checksElement.GetRawText(), WriteOptions);
                if (updatedChecks is not null)
                {
                    MergeScores(checklist.ServerChecks, updatedChecks);
                }
            }

            return true;
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Merges scores from evaluated items back into the original list.
    /// Only copies score/reason for items that were null and are now filled.
    /// </summary>
    private static void MergeScores(List<ChecklistItem> original, List<ChecklistItem> evaluated)
    {
        var evaluatedById = evaluated.ToDictionary(e => e.Id);
        foreach (var item in original)
        {
            if (item.Score is not null)
            {
                continue; // Already scored (deterministic or previously evaluated)
            }

            if (evaluatedById.TryGetValue(item.Id, out var updated) && updated.Score is not null)
            {
                item.Score = updated.Score;
                item.Reason = updated.Reason;
            }
        }
    }

    /// <summary>
    /// Tries each engine in order for a single evaluation call until one succeeds.
    /// </summary>
    private async Task<bool> TryEvaluateWithFallthrough(
        List<EvalEngine> engines,
        string filePath,
        string prompt,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in engines)
        {
            var success = await _agentRunner.EvaluateChecklistAsync(filePath, prompt, candidate, timeout, cancellationToken);
            if (success)
            {
                return true;
            }

            _logger.LogWarning("{Engine} failed for this evaluation, trying next engine...", candidate);
        }

        return false;
    }

    /// <summary>
    /// Builds the ordered list of engines to try based on user's choice.
    /// For Auto: detect which are available, always Copilot first.
    /// For a specific engine: just that one.
    /// For None: empty list.
    /// </summary>
    private async Task<List<EvalEngine>> BuildEngineList(EvalEngine requested, CancellationToken cancellationToken = default)
    {
        if (requested == EvalEngine.None)
        {
            return [];
        }

        if (requested != EvalEngine.Auto)
        {
            // User explicitly chose an engine
            return [requested];
        }

        // Auto: detect all available engines, preserving priority order
        _logger.LogInformation("Detecting available coding agents...");
        var available = new List<EvalEngine>();
        foreach (var engine in EnginePriority)
        {
            if (await _agentRunner.IsEngineAvailableAsync(engine, cancellationToken))
            {
                _logger.LogDebug("Detected {Engine}", engine);
                available.Add(engine);
            }
        }

        if (available.Count == 0)
        {
            _logger.LogWarning("No coding agent CLI detected (tried copilot, claude)");
        }
        else
        {
            _logger.LogInformation("Available engines: {Engines}", string.Join(", ", available));
        }

        return available;
    }

    private static int CountTotalUnevaluatedSemanticChecks(EvaluationChecklist checklist)
    {
        int count = 0;
        foreach (var tool in checklist.Tools)
        {
            count += CountUnevaluatedSemanticChecks(tool);
        }
        count += checklist.ServerChecks.Count(c => c.Type == CheckType.Semantic && c.Score is null);
        return count;
    }

    private static int CountUnevaluatedSemanticChecks(ToolChecklist tool)
    {
        int count = 0;
        count += tool.Checks.ToolName.Count(i => i.Type == CheckType.Semantic && i.Score is null);
        count += tool.Checks.ToolDescription.Count(i => i.Type == CheckType.Semantic && i.Score is null);
        count += tool.Checks.SchemaStructure.Count(i => i.Type == CheckType.Semantic && i.Score is null);
        foreach (var param in tool.Checks.Parameters.Values)
        {
            count += param.ParamName.Count(i => i.Type == CheckType.Semantic && i.Score is null);
            count += param.ParamDescription.Count(i => i.Type == CheckType.Semantic && i.Score is null);
        }
        return count;
    }

    private void LogManualEvaluationInstructions(string checklistPath)
    {
        var fullPath = Path.GetFullPath(checklistPath);
        var prompt = SemanticCheckPrompts.BuildEvaluationPrompt(fullPath);

        _logger.LogWarning("");
        _logger.LogWarning("Semantic checks were not evaluated automatically.");
        _logger.LogWarning("To complete the evaluation, pass the checklist to your coding agent:");
        _logger.LogWarning("");
        _logger.LogWarning("  Option 1 - GitHub Copilot CLI:");
        _logger.LogWarning("    copilot -p \"{Prompt}\" --allow-all-tools", EscapeForDisplay(prompt));
        _logger.LogWarning("");
        _logger.LogWarning("  Option 2 - Claude Code CLI:");
        _logger.LogWarning("    claude -p \"{Prompt}\" --allowedTools Read,Edit", EscapeForDisplay(prompt));
        _logger.LogWarning("");
        _logger.LogWarning("  Option 3 - Any coding agent:");
        _logger.LogWarning("    Copy the prompt below and pass it to your preferred coding agent.");
        _logger.LogWarning("");
        _logger.LogWarning("--- START PROMPT ---");
        _logger.LogWarning("{Prompt}", prompt);
        _logger.LogWarning("--- END PROMPT ---");
        _logger.LogWarning("");
        _logger.LogWarning("After the agent updates the checklist, re-run:");
        _logger.LogWarning("  a365 evaluate <server-url> --eval-engine none");
        _logger.LogWarning("to generate the final report from the updated checklist.");
        _logger.LogWarning("");
    }

    private static string EscapeForDisplay(string prompt)
    {
        var firstLine = prompt.Split('\n')[0].Trim();
        if (firstLine.Length > 60)
        {
            firstLine = firstLine[..57] + "...";
        }
        return firstLine;
    }

    private static int CountEvaluatedSemanticChecks(EvaluationChecklist checklist)
    {
        int count = 0;
        foreach (var tool in checklist.Tools)
        {
            count += CountEvaluated(tool.Checks.ToolName);
            count += CountEvaluated(tool.Checks.ToolDescription);
            count += CountEvaluated(tool.Checks.SchemaStructure);
            foreach (var param in tool.Checks.Parameters.Values)
            {
                count += CountEvaluated(param.ParamName);
                count += CountEvaluated(param.ParamDescription);
            }
        }
        count += CountEvaluated(checklist.ServerChecks);
        return count;
    }

    private static int CountEvaluated(List<ChecklistItem> items) =>
        items.Count(i => i.Type == CheckType.Semantic && i.Score is not null);
}
