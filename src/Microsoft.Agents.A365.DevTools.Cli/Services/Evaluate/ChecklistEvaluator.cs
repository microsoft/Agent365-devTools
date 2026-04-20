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

        var dir = Path.GetDirectoryName(checklistPath) ?? ".";
        Directory.CreateDirectory(dir);

        // Count unevaluated semantic checks before starting.
        // The pipeline service is responsible for loading any pre-existing checklist
        // from disk, so `checklist` already reflects whatever scores the user has done.
        int totalUnevaluatedBefore = CountTotalUnevaluatedSemanticChecks(checklist);

        // Fast path: checklist is fully scored (this is the resume case after manual scoring,
        // or a second run where agents already filled everything last time).
        if (totalUnevaluatedBefore == 0)
        {
            _logger.LogInformation("      All semantic checks already scored — skipping agent invocation");
            await WriteChecklistAsync(checklist, checklistPath, cancellationToken);
            return new ChecklistEvaluationResult { Checklist = checklist, SemanticEvaluationCompleted = true };
        }

        // User explicitly opted out of running an agent AND the checklist isn't fully scored:
        // persist what we have, print guidance, and stop.
        if (engine == EvalEngine.None)
        {
            await WriteChecklistAsync(checklist, checklistPath, cancellationToken);
            LogManualEvaluationInstructions(checklistPath, totalUnevaluatedBefore, engineNotFound: false, agentAttempted: false);
            return new ChecklistEvaluationResult { Checklist = checklist, SemanticEvaluationCompleted = false };
        }

        // Persist the unscored checklist now so the user has a file to edit if no agent is available.
        await WriteChecklistAsync(checklist, checklistPath, cancellationToken);

        // Build the list of engines to try (for Auto, detect available; otherwise just the one requested)
        var enginesToTry = await BuildEngineList(engine, cancellationToken);

        if (enginesToTry.Count == 0)
        {
            LogManualEvaluationInstructions(checklistPath, totalUnevaluatedBefore, engineNotFound: true, agentAttempted: false);
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
        var remainingUnevaluated = CountTotalUnevaluatedSemanticChecks(checklist);
        _logger.LogInformation("      {Scored} of {Total} semantic checks scored", scoredSemantic, totalSemantic);
        if (remainingUnevaluated > 0)
        {
            _logger.LogWarning("      {Count} semantic check{Plural} remain unscored",
                remainingUnevaluated, remainingUnevaluated == 1 ? "" : "s");

            // The detected agent(s) didn't score enough to finish the run — it may have
            // hit tool-permission limits, timed out, or returned without edits. Rather
            // than silently producing an inflated report, give the user the same BYOL
            // fallback they'd get if no agent was installed at all.
            LogManualEvaluationInstructions(checklistPath, remainingUnevaluated, engineNotFound: false, agentAttempted: true);
        }

        // Only treat evaluation as completed when nothing is left unscored.
        // Partial evaluations would skew scoring (Scorer treats unscored categories as 100).
        return new ChecklistEvaluationResult
        {
            Checklist = checklist,
            SemanticEvaluationCompleted = remainingUnevaluated == 0
        };
    }

    /// <summary>
    /// Extracts a single tool to a temp file, invokes the coding agent to evaluate
    /// its semantic checks, then merges the scored results back into the tool object.
    /// The temp file lives in an isolated directory under the system temp path so
    /// the coding agent (which may run with broad tool permissions) cannot reach
    /// the user's source tree even if they invoked from a repo root.
    /// </summary>
    private async Task<bool> EvaluateToolChecks(
        ToolChecklist tool,
        string workingDir,
        List<EvalEngine> engines,
        CancellationToken cancellationToken)
    {
        var sandbox = CreateSandboxDir();
        var tempFile = Path.Combine(sandbox, $".eval_tool_{Guid.NewGuid():N}.json");
        try
        {
            // Write just this tool to a small temp file
            var toolJson = JsonSerializer.Serialize(tool, WriteOptions);
            await File.WriteAllTextAsync(tempFile, toolJson, cancellationToken);

            var fullPath = Path.GetFullPath(tempFile);
            var success = await TryEvaluateWithFallthrough(
                engines,
                tempFile,
                engine => SemanticCheckPrompts.BuildToolEvaluationPrompt(fullPath, tool.Name, ToolsetFor(engine)),
                CodingAgentRunner.PerToolTimeout,
                cancellationToken);

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
            DeleteSandboxDir(sandbox);
        }
    }

    /// <summary>
    /// Extracts server-level checks with a tool name summary to a temp file,
    /// invokes the coding agent, then merges results back. Runs inside an isolated
    /// sandbox directory for the same reason as EvaluateToolChecks.
    /// </summary>
    private async Task<bool> EvaluateServerChecks(
        EvaluationChecklist checklist,
        string workingDir,
        List<EvalEngine> engines,
        CancellationToken cancellationToken)
    {
        var sandbox = CreateSandboxDir();
        var tempFile = Path.Combine(sandbox, $".eval_server_{Guid.NewGuid():N}.json");
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
            var success = await TryEvaluateWithFallthrough(
                engines,
                tempFile,
                engine => SemanticCheckPrompts.BuildServerChecksEvaluationPrompt(fullPath, ToolsetFor(engine)),
                CodingAgentRunner.PerToolTimeout,
                cancellationToken);

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
            DeleteSandboxDir(sandbox);
        }
    }

    /// <summary>
    /// Creates a fresh isolated directory under the system temp path for a single
    /// agent invocation. The agent's working directory is set to this path, which
    /// bounds file-tool access to files that we place here ourselves.
    /// </summary>
    private static string CreateSandboxDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"a365-eval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteSandboxDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
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
    internal static string RepairJson(string json)
    {
        // Insert missing commas: a value-ending token followed by whitespace then a
        // value-starting token, with no comma in between.
        // Value endings:  }  ]  "  true  false  null  digits
        // Value beginnings: {  [  "
        return Regex.Replace(json, @"([\}\]""]|true|false|null|\d)(\s*\n\s*)([\{\[""])", "$1,$2$3");
    }

    /// <summary>
    /// Tries each engine in order for a single evaluation call until one succeeds.
    /// Builds the prompt per engine so we can name the engine's exact tools in the
    /// instructions (Copilot: view/create, Claude Code: Read/Write).
    /// </summary>
    private async Task<bool> TryEvaluateWithFallthrough(
        List<EvalEngine> engines,
        string filePath,
        Func<EvalEngine, string> promptBuilder,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in engines)
        {
            var prompt = promptBuilder(candidate);
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
    /// Maps an engine to the concrete tool names it exposes. Edit-style tools are
    /// deliberately omitted: we've observed models thrashing between edit and create
    /// strategies when both are available, so the runner only exposes view+create
    /// (or Read+Write) and the prompt describes only those.
    /// </summary>
    private static SemanticCheckPrompts.AgentToolset ToolsetFor(EvalEngine engine) => engine switch
    {
        EvalEngine.GithubCopilot => new SemanticCheckPrompts.AgentToolset(
            ReadToolName: "view",
            WriteToolName: "create"),
        EvalEngine.ClaudeCode => new SemanticCheckPrompts.AgentToolset(
            ReadToolName: "Read",
            WriteToolName: "Write"),
        _ => new SemanticCheckPrompts.AgentToolset(
            ReadToolName: "read",
            WriteToolName: "write")
    };

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

    private void LogManualEvaluationInstructions(string checklistPath, int unscoredCount, bool engineNotFound, bool agentAttempted)
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

        if (engineNotFound)
        {
            _logger.LogWarning("      No coding agent CLI detected (looked for `copilot` and `claude`)");
        }
        else if (agentAttempted)
        {
            // Agent was detected and invoked but didn't score enough of the checklist.
            // Could be a tool-permission issue, a timeout, or the model bailing out.
            _logger.LogWarning("      The coding agent ran but left {Count} check{Plural} unscored — falling back to manual scoring",
                unscoredCount, unscoredCount == 1 ? "" : "s");
        }
        else
        {
            _logger.LogInformation("      {Count} semantic check{Plural} still unscored (--eval-engine none skips automatic scoring)",
                unscoredCount, unscoredCount == 1 ? "" : "s");
        }

        _logger.LogInformation("");
        _logger.LogInformation("To finish this evaluation, pick one:");
        _logger.LogInformation("");

        if (engineNotFound)
        {
            _logger.LogInformation("  1. Install a coding agent CLI and re-run the same command:");
            _logger.LogInformation("       GitHub Copilot:  https://github.com/github/gh-copilot");
            _logger.LogInformation("       Claude Code:     https://docs.anthropic.com/claude-code");
            _logger.LogInformation("");
            _logger.LogInformation("  2. Score with your own LLM (ChatGPT, Gemini, an IDE assistant, etc.):");
        }
        else
        {
            _logger.LogInformation("  Score with your own LLM (ChatGPT, Gemini, an IDE assistant, etc.):");
        }

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
        _logger.LogInformation("       d. Save the file, then re-run the exact same command. The pipeline will detect the scored checklist and generate the report.");
        _logger.LogInformation("");

        if (string.IsNullOrEmpty(promptPath))
        {
            _logger.LogInformation("--- PROMPT ---");
            _logger.LogInformation("{Prompt}", prompt);
            _logger.LogInformation("--- END PROMPT ---");
        }
    }

    /// <summary>
    /// Serializes the checklist to disk at <paramref name="checklistPath"/>.
    /// </summary>
    private static async Task WriteChecklistAsync(EvaluationChecklist checklist, string checklistPath, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(checklist, WriteOptions);
        await File.WriteAllTextAsync(checklistPath, json, cancellationToken);
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
