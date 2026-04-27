// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Validates agent-produced reason strings before they are merged into the
/// checklist (F-001 Layer 3 — output shape validation).
///
/// Rejects reasons that are implausibly long, contain URL exfiltration patterns,
/// or reproduce known injection markers — signals that the agent may have been
/// steered by adversarial content. Rejected items have their score and reason
/// cleared so the caller's retry loop can attempt a clean re-evaluation.
/// </summary>
internal static partial class ScoringSafetyFilter
{
    // Matches http/https/ftp URIs and data: URIs (no // for data scheme) — exfiltration
    // would embed a URL so a caller or downstream observer fetches it.
    [GeneratedRegex(@"(?i)((https?|ftp)://|data:)", RegexOptions.Compiled)]
    private static partial Regex ExfilUrlRegex();

    // Common XPIA instruction injection markers. Presence in a reason field means
    // the agent reproduced adversarial MCP content rather than writing its own judgment.
    // This is a heuristic signal layer — not a primary defense. Layers 1 and 2 prevent
    // the injection from reaching the agent; Layer 3 catches any that slip through.
    [GeneratedRegex(
        @"(?i)(ignore\s+(all\s+)?previous\s+instructions?|disregard\s+(all\s+)?(prior|previous)\s+instructions?|dismiss\s+(all\s+)?(prior|previous)\s+instructions?|supersede\s+(all\s+)?instructions?|replace\s+(all\s+)?(prior|previous)\s+instructions?|your\s+new\s+task\s+is|new\s+instructions?:|forget\s+(everything|all|instructions)|##\s*new\s+task\s*##|system\s+(override|prompt)|system\s*:|assistant\s*:|<\s*/?system\s*>|<\s*/?assistant\s*>)",
        RegexOptions.Compiled)]
    private static partial Regex InjectionMarkerRegex();

    /// <summary>
    /// Inspects every scored check item in <paramref name="items"/>. Items whose
    /// <c>Reason</c> fails validation have their <c>Score</c> and <c>Reason</c>
    /// cleared so the retry loop re-evaluates them.
    /// </summary>
    /// <param name="items">Check items that have just been merged from agent output.</param>
    /// <param name="toolName">Tool name — used only for log context.</param>
    /// <param name="logger">Logger; may be null (filter still runs, just silently).</param>
    /// <returns>Number of items that were cleared.</returns>
    public static int FilterAndClear(List<ChecklistItem> items, string toolName, ILogger? logger)
    {
        int cleared = 0;
        foreach (var item in items)
        {
            if (item.Score is null || string.IsNullOrEmpty(item.Reason))
            {
                continue;
            }

            var rejection = ClassifyReason(item.Reason);
            if (rejection is null)
            {
                continue;
            }

            logger?.LogWarning(
                "Safety filter cleared check {Id} on tool {Tool}: {Reason} ({RejectionType})",
                item.Id, toolName, item.Reason, rejection);

            item.Score = null;
            item.Reason = null;
            cleared++;
        }

        return cleared;
    }

    /// <summary>
    /// Returns a short rejection label if the reason string fails validation,
    /// or null when the reason is acceptable.
    /// </summary>
    internal static string? ClassifyReason(string reason)
    {
        if (ExfilUrlRegex().IsMatch(reason))
        {
            return "exfil_url";
        }

        if (InjectionMarkerRegex().IsMatch(reason))
        {
            return "injection_marker";
        }

        return null;
    }
}
