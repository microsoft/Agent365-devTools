// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
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
    // Per-scope (tool or server) the agent may leave some items unscored on a given
    // pass, especially "pass if no issues" prompts the model hedges on. Re-invoke up
    // to this many times; we stop as soon as everything is scored.
    private const int MaxAttempts = 3;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // Tolerant reader options: coding agents sometimes produce trailing commas or comments
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    // Registered coding-agent launchers, in priority order (the registration order
    // in Program.cs). Auto walks this list; a specific engine is matched by Engine.
    private readonly IReadOnlyList<ICodingAgentLauncher> _launchers;
    private readonly ILogger<ChecklistEvaluator> _logger;

    public ChecklistEvaluator(IEnumerable<ICodingAgentLauncher> launchers, ILogger<ChecklistEvaluator> logger)
    {
        ArgumentNullException.ThrowIfNull(launchers);
        ArgumentNullException.ThrowIfNull(logger);
        _launchers = launchers.ToList();
        _logger = logger;
    }

    /// <summary>Finds the registered launcher for an engine, or null if none.</summary>
    private ICodingAgentLauncher? LauncherFor(EvalEngine engine)
        => _launchers.FirstOrDefault(l => l.Engine == engine);

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
            return new ChecklistEvaluationResult { Checklist = checklist, Outcome = EvaluationOutcome.Completed };
        }

        // User explicitly opted out of running an agent AND the checklist isn't fully scored:
        // persist what we have, print guidance, and stop.
        if (engine == EvalEngine.None)
        {
            await WriteChecklistAsync(checklist, checklistPath, cancellationToken);
            LogManualEvaluationInstructions(checklistPath, totalUnevaluatedBefore, engineNotFound: false, agentAttempted: false, requested: engine);
            return new ChecklistEvaluationResult { Checklist = checklist, Outcome = EvaluationOutcome.OptedOut };
        }

        // Persist the unscored checklist now so the user has a file to edit if no agent is available.
        await WriteChecklistAsync(checklist, checklistPath, cancellationToken);

        // Build the list of engines to try (for Auto, detect available; otherwise just the one requested)
        var enginesToTry = await BuildEngineList(engine, cancellationToken);

        if (enginesToTry.Count == 0)
        {
            LogManualEvaluationInstructions(checklistPath, totalUnevaluatedBefore, engineNotFound: true, agentAttempted: false, requested: engine);
            return new ChecklistEvaluationResult { Checklist = checklist, Outcome = EvaluationOutcome.CouldNotEvaluate };
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

        // Track the first engine that successfully produced evaluations across any
        // tool or server-check pass. Used to stamp the report with the engine that
        // actually did the work (rather than the user's "auto" request).
        EvalEngine? engineUsed = null;

        // Pick the scoring path from the engine. A per-check judge (Azure OpenAI) evaluates
        // each assertion independently with the full tool schema as context; subprocess coding
        // agents edit a whole-tool file. A per-check engine is explicit-only, so when chosen it
        // is the sole entry in enginesToTry.
        var primaryLauncher = enginesToTry.Count == 1 ? LauncherFor(enginesToTry[0]) : null;
        engineUsed = primaryLauncher?.ScoresPerCheck == true
            ? await EvaluatePerCheck(checklist, primaryLauncher, checklistPath, cancellationToken)
            : await EvaluatePerTool(checklist, enginesToTry, cancellationToken);

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
            LogManualEvaluationInstructions(checklistPath, remainingUnevaluated, engineNotFound: false, agentAttempted: true, requested: engine);
        }

        // Only treat evaluation as completed when nothing is left unscored.
        // Partial evaluations would skew scoring (Scorer treats unscored categories as 100).
        return new ChecklistEvaluationResult
        {
            Checklist = checklist,
            Outcome = remainingUnevaluated == 0 ? EvaluationOutcome.Completed : EvaluationOutcome.CouldNotEvaluate,
            EngineUsed = engineUsed
        };
    }

    /// <summary>
    /// Whole-tool scoring path for subprocess coding agents: extract each tool to a sandbox
    /// file, have the agent edit it, then merge results back. Tools run concurrently up to the
    /// engine's MaxConcurrency (1 for coding agents). Returns the engine that did the work.
    /// </summary>
    private async Task<EvalEngine?> EvaluatePerTool(EvaluationChecklist checklist, List<EvalEngine> enginesToTry, CancellationToken cancellationToken)
    {
        if (enginesToTry.Count == 0)
        {
            return null;
        }

        EvalEngine? engineUsed = null;

        // When several engines could be tried via fallthrough, use the most conservative dop.
        // Each tool writes to its own sandbox and mutates only its own ToolChecklist, so
        // concurrent iterations don't share state; only engineUsed needs guarding.
        int dop = enginesToTry.Count == 0
            ? 1
            : Math.Max(1, enginesToTry.Min(e => LauncherFor(e)?.MaxConcurrency ?? 1));

        var engineLock = new object();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, checklist.Tools.Count),
            new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                var tool = checklist.Tools[i];
                var unevaluated = CountUnevaluatedSemanticChecks(tool);
                if (unevaluated == 0)
                {
                    return;
                }

                // Heartbeat BEFORE the tool runs so the user sees forward motion immediately.
                _logger.LogInformation("      [{Current}/{Total}] {ToolName} ({CheckCount} checks) ... running",
                    i + 1, checklist.Tools.Count, tool.Name, unevaluated);

                var toolEngine = await EvaluateToolChecks(tool, enginesToTry, ct);
                if (toolEngine is not null)
                {
                    lock (engineLock)
                    {
                        engineUsed ??= toolEngine;
                    }
                    _logger.LogInformation("      [{Current}/{Total}] {ToolName} ... ok",
                        i + 1, checklist.Tools.Count, tool.Name);
                }
                else
                {
                    _logger.LogWarning("      [{Current}/{Total}] {ToolName} ... failed (continuing)",
                        i + 1, checklist.Tools.Count, tool.Name);
                }
            });

        var serverUnevaluated = checklist.ServerChecks.Count(c => c.Type == CheckType.Semantic && c.Score is null);
        if (serverUnevaluated > 0)
        {
            _logger.LogInformation("      server-level checks ({Count} checks) ... running", serverUnevaluated);
            var serverEngine = await EvaluateServerChecks(checklist, enginesToTry, cancellationToken);
            if (serverEngine is not null)
            {
                engineUsed ??= serverEngine;
                _logger.LogInformation("      server-level checks ... ok");
            }
            else
            {
                _logger.LogWarning("      server-level checks ... failed (continuing)");
            }
        }

        return engineUsed;
    }

    /// <summary>
    /// Per-check scoring path for a direct-API judge (Azure OpenAI): score every unscored
    /// Semantic check independently, passing the FULL tool schema as context for each single
    /// assertion, fanning out concurrently up to the engine's MaxConcurrency. Failed checks are
    /// retried up to <see cref="MaxAttempts"/> rounds. Results are collected concurrently then
    /// applied to the checklist items serially; the checklist is persisted after each round so an
    /// interrupted run resumes from the scored items rather than re-scoring everything.
    /// </summary>
    private async Task<EvalEngine?> EvaluatePerCheck(EvaluationChecklist checklist, ICodingAgentLauncher launcher, string checklistPath, CancellationToken cancellationToken)
    {
        var work = new List<(ChecklistItem Item, string Context)>();

        foreach (var tool in checklist.Tools)
        {
            var toolContext = BuildToolContext(tool);
            foreach (var item in tool.Checks.ToolName.Concat(tool.Checks.ToolDescription).Concat(tool.Checks.SchemaStructure))
            {
                if (item.Type == CheckType.Semantic && item.Score is null)
                {
                    work.Add((item, toolContext));
                }
            }

            foreach (var (paramName, paramChecks) in tool.Checks.Parameters)
            {
                var paramContext = $"{toolContext}\n\nParameter under evaluation: \"{paramName}\"";
                foreach (var item in paramChecks.ParamName.Concat(paramChecks.ParamDescription))
                {
                    if (item.Type == CheckType.Semantic && item.Score is null)
                    {
                        work.Add((item, paramContext));
                    }
                }
            }
        }

        var serverContext = BuildServerContext(checklist);
        foreach (var item in checklist.ServerChecks)
        {
            if (item.Type == CheckType.Semantic && item.Score is null)
            {
                work.Add((item, serverContext));
            }
        }

        if (work.Count == 0)
        {
            return launcher.Engine;
        }

        int dop = Math.Max(1, launcher.MaxConcurrency);
        _logger.LogInformation("      Scoring {Count} check(s) independently — check-by-check, concurrency {Dop}", work.Count, dop);

        int succeeded = 0;
        var pending = work;
        for (int attempt = 1; attempt <= MaxAttempts && pending.Count > 0; attempt++)
        {
            // Score concurrently but do NOT mutate the shared ChecklistItem objects inside the
            // parallel body — collect (item, result) pairs, then apply them serially afterward.
            var scored = new ConcurrentBag<(ChecklistItem Item, CheckEvaluation Result)>();
            var failed = new ConcurrentBag<(ChecklistItem Item, string Context)>();
            int doneInRound = 0;
            int roundTotal = pending.Count;
            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = cancellationToken },
                async (w, ct) =>
                {
                    var result = await launcher.ScoreCheckAsync(w.Context, w.Item.Prompt, ct);
                    if (result is not null)
                    {
                        scored.Add((w.Item, result));
                    }
                    else
                    {
                        failed.Add(w);
                    }

                    var n = Interlocked.Increment(ref doneInRound);
                    if (n % 50 == 0 || n == roundTotal)
                    {
                        _logger.LogInformation("      ... {Done}/{Total} checks scored", n, roundTotal);
                    }
                });

            // Apply results serially — no concurrent writes to ChecklistItem fields.
            foreach (var (item, result) in scored)
            {
                item.Score = result.Score;
                item.Reason = result.Reason;
            }

            succeeded += scored.Count;
            pending = failed.ToList();

            // Checkpoint after each round so an interrupted run resumes from the persisted scores
            // rather than re-scoring everything.
            await WriteChecklistAsync(checklist, checklistPath, cancellationToken);

            if (pending.Count > 0 && attempt < MaxAttempts)
            {
                _logger.LogInformation("      {Count} check(s) failed; retrying (attempt {Next}/{Max})", pending.Count, attempt + 1, MaxAttempts);
            }
        }

        if (pending.Count > 0)
        {
            _logger.LogWarning("      {Count} check(s) could not be scored after {Max} attempt(s)", pending.Count, MaxAttempts);
        }

        return succeeded > 0 ? launcher.Engine : null;
    }

    /// <summary>Full tool schema string passed as per-check context for a tool's checks.</summary>
    private static string BuildToolContext(ToolChecklist tool)
    {
        var schema = tool.InputSchema.HasValue
            ? JsonSerializer.Serialize(tool.InputSchema.Value, WriteOptions)
            : "{}";
        return $"Tool name: {tool.Name}\nTool description: {tool.Description}\nInput schema (JSON):\n{schema}";
    }

    /// <summary>Tool-set summary passed as per-check context for server-level checks.</summary>
    private static string BuildServerContext(EvaluationChecklist checklist)
    {
        var summary = string.Join("\n", checklist.Tools.Select(t => $"- {t.Name}: {t.Description}"));
        return $"Evaluate against the full tool set of this MCP server:\n{summary}";
    }

    /// <summary>
    /// Extracts a single tool to a temp file, invokes the coding agent to evaluate
    /// its semantic checks, then merges the scored results back into the tool object.
    /// The temp file lives in an isolated directory under the system temp path to
    /// reduce the blast radius of the agent's file tools: the agent's cwd is the
    /// sandbox, and each engine's path-verification (Copilot's default, Claude's
    /// --add-dir allowlist) bounds cwd-relative file access to it. Absolute paths
    /// remain reachable, so this is a reduced-surface defense, not a full jail.
    /// </summary>
    private async Task<EvalEngine?> EvaluateToolChecks(
        ToolChecklist tool,
        List<EvalEngine> engines,
        CancellationToken cancellationToken)
    {
        var sandbox = CreateSandboxDir();
        var tempFile = Path.Combine(sandbox, $".eval_tool_{Guid.NewGuid():N}.json");
        try
        {
            var fullPath = Path.GetFullPath(tempFile);
            EvalEngine? firstSuccessfulEngine = null;

            // Up to MaxAttempts agent passes. Each pass, we re-serialize the current
            // tool state (with any scores merged from prior passes) so the agent only
            // sees the items that are still null. Stops early once everything is scored.
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var toolJson = JsonSerializer.Serialize(tool, WriteOptions);
                await File.WriteAllTextAsync(tempFile, toolJson, cancellationToken);

                // Scale the per-attempt timeout to the remaining work: a tool with
                // 46 unscored checks legitimately needs longer than one with 18.
                var perAttemptTimeout = CodingAgentLauncherBase.TimeoutForChecks(CountUnevaluatedSemanticChecks(tool));

                var successEngine = await TryEvaluateWithFallthrough(
                    engines,
                    tempFile,
                    engine => SemanticCheckPrompts.BuildToolEvaluationPrompt(fullPath, tool.Name, ToolsetFor(engine)),
                    perAttemptTimeout,
                    cancellationToken);

                if (successEngine is not null)
                {
                    firstSuccessfulEngine ??= successEngine;

                    // Re-read the evaluated tool and merge scores back.
                    // Coding agents sometimes produce slightly malformed JSON: missing
                    // commas (handled by RepairJson), or structurally invalid items
                    // where a check is an abbreviated object or wrong type. Those will
                    // throw from Deserialize — treat as "agent made no usable progress
                    // this attempt" and let the retry loop try again.
                    try
                    {
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
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogDebug(ex,
                            "Tool {ToolName}: attempt {Attempt} produced JSON that failed to deserialize (path: {Path}); will retry if attempts remain",
                            tool.Name, attempt, ex.Path ?? "unknown");
                    }
                }
                else
                {
                    // Subprocess failed this attempt (timeout or non-zero exit).
                    // We still retry — we've observed that timeouts on Haiku are
                    // non-deterministic: a tool that times out on attempt 1 often
                    // completes on attempt 2 or 3. Giving up fast loses winnable runs.
                    // Info-level so the user sees the in-flight retry, not silence.
                    _logger.LogInformation(
                        "        {ToolName}: attempt {Attempt} subprocess failed; will retry if attempts remain",
                        tool.Name, attempt);
                }

                if (CountUnevaluatedSemanticChecks(tool) == 0)
                {
                    return firstSuccessfulEngine;
                }

                if (attempt < MaxAttempts)
                {
                    // Info-level so the user sees forward motion when a tool needs >1 pass.
                    _logger.LogInformation("        {ToolName}: attempt {Attempt} left {Count} check(s) unscored, retrying",
                        tool.Name, attempt, CountUnevaluatedSemanticChecks(tool));
                }
            }

            // All MaxAttempts used. If at least one attempt produced exit-0 output
            // (even if some items remain null), treat as "agent ran" — the outer
            // pipeline will see the unscored items and fall back to manual scoring.
            // If no attempt ever succeeded (e.g. all 3 hit timeout), report failure
            // so the tool shows up as "failed (continuing)" in the pipeline log.
            return firstSuccessfulEngine;
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
    private async Task<EvalEngine?> EvaluateServerChecks(
        EvaluationChecklist checklist,
        List<EvalEngine> engines,
        CancellationToken cancellationToken)
    {
        var sandbox = CreateSandboxDir();
        var tempFile = Path.Combine(sandbox, $".eval_server_{Guid.NewGuid():N}.json");
        try
        {
            var fullPath = Path.GetFullPath(tempFile);
            EvalEngine? firstSuccessfulEngine = null;
            var docOptions = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                // Re-build the input each attempt so the agent sees the current
                // (partially scored) state — previously-scored items are preserved.
                var serverData = new
                {
                    tool_summaries = checklist.Tools.Select(t => new { t.Name, t.Description }).ToList(),
                    server_checks = checklist.ServerChecks
                };
                var dataJson = JsonSerializer.Serialize(serverData, WriteOptions);
                await File.WriteAllTextAsync(tempFile, dataJson, cancellationToken);

                var serverRemaining = checklist.ServerChecks.Count(c => c.Type == CheckType.Semantic && c.Score is null);
                var perAttemptTimeout = CodingAgentLauncherBase.TimeoutForChecks(serverRemaining);

                var successEngine = await TryEvaluateWithFallthrough(
                    engines,
                    tempFile,
                    engine => SemanticCheckPrompts.BuildServerChecksEvaluationPrompt(fullPath, ToolsetFor(engine)),
                    perAttemptTimeout,
                    cancellationToken);

                if (successEngine is not null)
                {
                    firstSuccessfulEngine ??= successEngine;

                    try
                    {
                        var updatedJson = RepairJson(await File.ReadAllTextAsync(tempFile, cancellationToken));
                        using var doc = JsonDocument.Parse(updatedJson, docOptions);
                        if (doc.RootElement.TryGetProperty("server_checks", out var checksElement))
                        {
                            var updatedChecks = JsonSerializer.Deserialize<List<ChecklistItem>>(checksElement.GetRawText(), ReadOptions);
                            if (updatedChecks is not null)
                            {
                                MergeScores(checklist.ServerChecks, updatedChecks);
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogDebug(ex,
                            "Server checks: attempt {Attempt} produced JSON that failed to deserialize (path: {Path}); will retry if attempts remain",
                            attempt, ex.Path ?? "unknown");
                    }
                }
                else
                {
                    // Subprocess failed this attempt (timeout / non-zero exit).
                    // Retry — the failure is often transient on Haiku.
                    _logger.LogDebug("Server checks: attempt {Attempt} subprocess failed; will retry if attempts remain",
                        attempt);
                }

                var remaining = checklist.ServerChecks.Count(c => c.Type == CheckType.Semantic && c.Score is null);
                if (remaining == 0)
                {
                    return firstSuccessfulEngine;
                }

                if (attempt < MaxAttempts)
                {
                    _logger.LogDebug("Server checks: attempt {Attempt} left {Count} check(s) unscored, retrying",
                        attempt, remaining);
                }
            }

            return firstSuccessfulEngine;
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
    /// Agent output can contain duplicate or empty ids; drop empties and take
    /// last-wins on duplicates so a malformed batch is handled like other
    /// agent-JSON quirks (treated as "no usable progress, retry") rather than
    /// crashing the run.
    /// </summary>
    private static void MergeScores(List<ChecklistItem> original, List<ChecklistItem> evaluated)
    {
        var evaluatedById = evaluated
            .Where(e => !string.IsNullOrEmpty(e.Id))
            .GroupBy(e => e.Id)
            .ToDictionary(g => g.Key, g => g.Last());
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
    /// Attempts to repair common JSON issues produced by coding agents by
    /// inserting missing commas between properties or array elements.
    /// Trailing commas are tolerated separately via AllowTrailingCommas in ReadOptions.
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
    /// Returns the engine that succeeded, or null if every candidate failed.
    /// Builds the prompt per engine so we can name the engine's exact tools in the
    /// instructions (Copilot: view/create, Claude Code: Read/Write).
    /// </summary>
    private async Task<EvalEngine?> TryEvaluateWithFallthrough(
        List<EvalEngine> engines,
        string filePath,
        Func<EvalEngine, string> promptBuilder,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var workingDirectory = Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory();
        foreach (var candidate in engines)
        {
            var launcher = LauncherFor(candidate);
            if (launcher is null)
            {
                _logger.LogDebug("No launcher registered for {Engine}, skipping", candidate);
                continue;
            }

            var prompt = promptBuilder(candidate);
            var success = await launcher.LaunchAsync(prompt, workingDirectory, timeout, cancellationToken);
            if (success)
            {
                return candidate;
            }

            _logger.LogDebug("{Engine} failed, trying next", candidate);
        }

        return null;
    }

    /// <summary>
    /// Maps an engine to the concrete tool names it exposes. Edit-style tools are
    /// deliberately omitted: we've observed models thrashing between edit and create
    /// strategies when both are available, so the runner only exposes read + an
    /// edit (string-replace) tool. We deliberately do NOT expose a whole-file
    /// write tool: Copilot's `create` refuses to overwrite existing files, which
    /// sends the agent on long workaround loops, and a mix of edit+create tempts
    /// the model to oscillate between strategies.
    /// </summary>
    private SemanticCheckPrompts.AgentToolset ToolsetFor(EvalEngine engine)
        => LauncherFor(engine)?.Toolset
           ?? new SemanticCheckPrompts.AgentToolset(ReadToolName: "read", EditToolName: "edit");

    /// <summary>
    /// Builds the ordered list of engines to try based on user's choice.
    /// For Auto: detect which are available, always Copilot first.
    /// For a specific engine: return it only if its CLI is available; otherwise
    /// an empty list so the caller takes the same "engine not found" path as Auto
    /// with nothing installed (instead of looping through failures and surfacing
    /// a misleading "agent ran but left checks unscored" message).
    /// Caller should have handled None earlier.
    /// </summary>
    private async Task<List<EvalEngine>> BuildEngineList(EvalEngine requested, CancellationToken cancellationToken = default)
    {
        if (requested != EvalEngine.Auto)
        {
            var launcher = LauncherFor(requested);
            if (launcher is not null && await launcher.IsAvailableAsync(cancellationToken))
            {
                return [requested];
            }

            _logger.LogDebug("Requested engine {Engine} is not available on PATH", requested);
            return [];
        }

        // Auto: detect all available engines, preserving the registered priority order.
        // Skip explicit-only engines (e.g. a remote API judge) so auto never selects one
        // the user didn't ask for — those run only via an explicit --eval-engine value.
        var available = new List<EvalEngine>();
        foreach (var launcher in _launchers)
        {
            if (!launcher.AutoDetectable)
            {
                continue;
            }

            if (await launcher.IsAvailableAsync(cancellationToken))
            {
                _logger.LogDebug("Detected {Engine}", launcher.Engine);
                available.Add(launcher.Engine);
            }
        }

        return available;
    }

    /// <summary>
    /// Returns a user-friendly display name for an engine. Real engine names come
    /// from the registered launcher; the Auto/None meta-values are formatted here.
    /// </summary>
    public string FormatEngineName(EvalEngine engine)
    {
        var launcher = LauncherFor(engine);
        if (launcher is not null)
        {
            return launcher.DisplayName;
        }

        return engine switch
        {
            EvalEngine.Auto => "auto",
            EvalEngine.None => "none",
            _ => engine.ToString()
        };
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

    private void LogManualEvaluationInstructions(string checklistPath, int unscoredCount, bool engineNotFound, bool agentAttempted, EvalEngine requested)
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
            if (requested == EvalEngine.Auto)
            {
                // Built from the registry so a newly added engine appears here automatically.
                // Only auto-detectable engines are probed under `auto`, so list just those.
                var probed = string.Join(" and ", _launchers.Where(l => l.AutoDetectable).Select(l => $"{l.DisplayName} (`{l.CliCommand}`)"));
                _logger.LogWarning("      No coding agent CLI detected (looked for {Probed}). Run with -v to see why each probe failed.", probed);
            }
            else
            {
                // The user asked for one specific engine; name only that one, not both.
                var launcher = LauncherFor(requested);
                var name = launcher?.DisplayName ?? FormatEngineName(requested);
                var hint = launcher?.AvailabilityHint ?? $"`{requested}`";
                _logger.LogWarning("      {Name} is not available — needs {Hint}. Run with -v for details.", name, hint);
            }
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
