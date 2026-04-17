// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;
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

    // Tolerant reader options: coding agents sometimes produce trailing commas or comments
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

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
        _logger.LogDebug("Checklist written to {Path}", checklistPath);

        // Count unevaluated semantic checks before starting
        int totalUnevaluatedBefore = CountTotalUnevaluatedSemanticChecks(checklist);

        // Handle the explicit --eval-engine none case up-front
        if (engine == EvalEngine.None)
        {
            if (totalUnevaluatedBefore == 0)
            {
                _logger.LogInformation("      All semantic checks already scored in checklist — proceeding with analysis");
                return new ChecklistEvaluationResult { Checklist = checklist, SemanticEvaluationCompleted = true };
            }
            _logger.LogInformation("      Semantic evaluation disabled (--eval-engine none) — skipping {Count} semantic check{Plural}",
                totalUnevaluatedBefore, totalUnevaluatedBefore == 1 ? "" : "s");
            return new ChecklistEvaluationResult { Checklist = checklist, SemanticEvaluationCompleted = false };
        }

        // Build the list of engines to try (for Auto, detect available; otherwise just the one requested)
        var enginesToTry = await BuildEngineList(engine, cancellationToken);

        if (enginesToTry.Count == 0)
        {
            if (totalUnevaluatedBefore == 0)
            {
                return new ChecklistEvaluationResult { Checklist = checklist, SemanticEvaluationCompleted = true };
            }

            LogManualEvaluationInstructions(checklistPath);
            return new ChecklistEvaluationResult { Checklist = checklist, SemanticEvaluationCompleted = false };
        }

        // Announce the active engine (and fallback if any)
        if (enginesToTry.Count == 1)
        {
            _logger.LogInformation("      Using {Engine}", FormatEngineName(enginesToTry[0]));
        }
        else
        {
            _logger.LogInformation("      Using {Primary} (fallback: {Fallback})",
                FormatEngineName(enginesToTry[0]),
                string.Join(", ", enginesToTry.Skip(1).Select(FormatEngineName)));
        }

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

            var success = await EvaluateToolChecks(tool, dir, enginesToTry, cancellationToken);
            if (success)
            {
                toolsEvaluated++;
                _logger.LogInformation("      [{Current}/{Total}] {ToolName} ({CheckCount} checks) ... ok",
                    i + 1, checklist.Tools.Count, tool.Name, unevaluated);
            }
            else
            {
                toolsFailed++;
                _logger.LogWarning("      [{Current}/{Total}] {ToolName} ({CheckCount} checks) ... failed (continuing)",
                    i + 1, checklist.Tools.Count, tool.Name, unevaluated);
            }
        }

        // Evaluate server-level checks (extract server_checks + tool list summary)
        var serverUnevaluated = checklist.ServerChecks.Count(c => c.Type == CheckType.Semantic && c.Score is null);
        if (serverUnevaluated > 0)
        {
            var serverSuccess = await EvaluateServerChecks(checklist, dir, enginesToTry, cancellationToken);
            if (serverSuccess)
            {
                _logger.LogInformation("      server-level checks ({Count} checks) ... ok", serverUnevaluated);
            }
            else
            {
                _logger.LogWarning("      server-level checks ({Count} checks) ... failed (continuing)", serverUnevaluated);
            }
        }

        // Write the updated checklist back (with all merged results)
        var updatedJson = JsonSerializer.Serialize(checklist, WriteOptions);
        await File.WriteAllTextAsync(checklistPath, updatedJson, cancellationToken);

        var scoredSemantic = CountEvaluatedSemanticChecks(checklist);
        var totalSemantic = CountTotalSemanticChecks(checklist);
        _logger.LogInformation("      {Scored} of {Total} semantic checks scored", scoredSemantic, totalSemantic);

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

            // Re-read the evaluated tool and merge scores back.
            // Coding agents sometimes produce slightly malformed JSON (missing commas, trailing commas).
            var updatedJson = RepairJson(await File.ReadAllTextAsync(tempFile, cancellationToken));
            var updatedTool = JsonSerializer.Deserialize<ToolChecklist>(updatedJson, ReadOptions);

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
            var updatedJson = RepairJson(await File.ReadAllTextAsync(tempFile, cancellationToken));
            var docOptions = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };
            using var doc = JsonDocument.Parse(updatedJson, docOptions);
            if (doc.RootElement.TryGetProperty("server_checks", out var checksElement))
            {
                var updatedChecks = JsonSerializer.Deserialize<List<ChecklistItem>>(checksElement.GetRawText(), ReadOptions);
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
    /// Attempts to repair common JSON issues produced by coding agents:
    /// missing commas between properties/array elements, trailing commas.
    /// </summary>
    private static string RepairJson(string json)
    {
        // Insert missing commas: a value-ending token followed by whitespace then a
        // value-starting token, with no comma in between.
        // Value endings:  }  ]  "  true  false  null  digits
        // Value beginnings: {  [  "
        return Regex.Replace(json, @"([\}\]""]|true|false|null|\d)(\s*\n\s*)([\{\[""])", "$1,$2$3");
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

            _logger.LogDebug("{Engine} failed, trying next", candidate);
        }

        return false;
    }

    /// <summary>
    /// Builds the ordered list of engines to try based on user's choice.
    /// For Auto: detect which are available, always Copilot first.
    /// For a specific engine: just that one (caller should have handled None earlier).
    /// </summary>
    private async Task<List<EvalEngine>> BuildEngineList(EvalEngine requested, CancellationToken cancellationToken = default)
    {
        if (requested != EvalEngine.Auto)
        {
            return [requested];
        }

        // Auto: detect all available engines, preserving priority order
        var available = new List<EvalEngine>();
        foreach (var engine in EnginePriority)
        {
            if (await _agentRunner.IsEngineAvailableAsync(engine, cancellationToken))
            {
                _logger.LogDebug("Detected {Engine}", engine);
                available.Add(engine);
            }
        }

        return available;
    }

    /// <summary>
    /// Returns a user-friendly display name for an engine.
    /// </summary>
    private static string FormatEngineName(EvalEngine engine) => engine switch
    {
        EvalEngine.GithubCopilot => "GitHub Copilot",
        EvalEngine.ClaudeCode => "Claude Code",
        EvalEngine.Auto => "auto",
        EvalEngine.None => "none",
        _ => engine.ToString()
    };

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

    private static int CountTotalSemanticChecks(EvaluationChecklist checklist)
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

    private void LogManualEvaluationInstructions(string checklistPath)
    {
        var fullPath = Path.GetFullPath(checklistPath);
        var promptPath = Path.Combine(Path.GetDirectoryName(fullPath) ?? ".", "semantic_eval_prompt.txt");
        var prompt = SemanticCheckPrompts.BuildEvaluationPrompt(fullPath);

        try
        {
            File.WriteAllText(promptPath, prompt);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write prompt file to {Path}", promptPath);
            promptPath = string.Empty;
        }

        _logger.LogWarning("      No coding agent CLI detected (looked for `copilot` and `claude`)");
        _logger.LogInformation("");
        _logger.LogInformation("To score semantic checks, choose one option:");
        _logger.LogInformation("");
        _logger.LogInformation("  1. Install a coding agent CLI and re-run this command:");
        _logger.LogInformation("       GitHub Copilot:  https://github.com/github/gh-copilot");
        _logger.LogInformation("       Claude Code:     https://docs.anthropic.com/claude-code");
        _logger.LogInformation("");
        _logger.LogInformation("  2. Score with your own LLM (ChatGPT, Gemini, an IDE assistant, etc.):");
        _logger.LogInformation("       a. Open:   {ChecklistPath}", fullPath);
        if (!string.IsNullOrEmpty(promptPath))
        {
            _logger.LogInformation("       b. Paste the prompt from: {PromptPath}", promptPath);
        }
        else
        {
            _logger.LogInformation("       b. Paste the prompt shown below into your LLM");
        }
        _logger.LogInformation("       c. Have the LLM fill in every null `score` (true/false) with a one-sentence `reason`");
        _logger.LogInformation("       d. Re-run:  a365 develop-mcp evaluate <server-url> --eval-engine none");
        _logger.LogInformation("");

        if (string.IsNullOrEmpty(promptPath))
        {
            _logger.LogInformation("--- PROMPT ---");
            _logger.LogInformation("{Prompt}", prompt);
            _logger.LogInformation("--- END PROMPT ---");
        }
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
