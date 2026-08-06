// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Validates that the agent is producing telemetry traces via the console exporter
/// by analyzing the agent's console output log file captured during the conversation step.
/// For local validation, this checks that the console exporter is active and that the
/// required GenAI semantic convention spans (invoke_agent, chat, execute_tool) appear.
/// </summary>
public class TelemetryRequirementCheck : RequirementCheck
{
    private readonly string? _agentConsoleLogPath;

    /// <summary>
    /// Maximum number of log lines to analyze from the end of the agent console output.
    /// </summary>
    internal const int MaxTelemetryLines = 200;

    /// <summary>
    /// Required GenAI semantic convention operation names that must ALL appear in traces.
    /// These correspond to gen_ai.operation.name values for agent orchestration spans.
    /// </summary>
    internal static readonly string[] RequiredGenAiSpans = new[]
    {
        "invoke_agent",
        "chat",
        "execute_tool"
    };

    /// <summary>
    /// All recognized GenAI operation names used to filter relevant span blocks.
    /// Only span blocks with one of these values in gen_ai.operation.name are considered.
    /// </summary>
    internal static readonly string[] RecognizedGenAiOperations = new[]
    {
        "invoke_agent",
        "chat",
        "execute_tool",
        "output_messages"
    };

    /// <summary>
    /// Operation names that must have a non-empty parentId to verify proper trace hierarchy.
    /// These are child spans that should be linked to a parent invoke_agent span.
    /// </summary>
    internal static readonly string[] ChildSpanOperations = new[]
    {
        "chat",
        "execute_tool"
    };

    /// <summary>
    /// Matches any line containing a parent span identifier key (parentId, parentSpanId, parent_id, etc.)
    /// followed by a separator and a non-empty hex value. Handles all known exporter formats:
    /// Activity.ParentSpanId, JSON quoted keys, YAML-style, and equals-sign separators.
    /// </summary>
    internal static readonly Regex ParentSpanPattern = new(
        @"(?:parent[\._]?(?:span)?[\._]?(?:id|context))[""']?\s*[=:]\s*[""']?\s*(?:0x)?([0-9a-f]{2,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);


    public TelemetryRequirementCheck(string? agentConsoleLogPath)
    {
        _agentConsoleLogPath = agentConsoleLogPath;
    }

    /// <inheritdoc />
    public override string Name => "Telemetry";

    /// <inheritdoc />
    public override string Description => "Validates that telemetry traces are being exported to Agent365";

    /// <inheritdoc />
    public override string Category => "Observability";

    /// <inheritdoc />
    public override async Task<RequirementCheckResult> CheckAsync(
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteCheckWithLoggingAsync(config, logger, CheckImplementationAsync, cancellationToken);
    }

    private Task<RequirementCheckResult> CheckImplementationAsync(
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_agentConsoleLogPath) || !File.Exists(_agentConsoleLogPath))
        {
            return Task.FromResult(RequirementCheckResult.Warning(
                "No agent console log file available to analyze for telemetry",
                details: "Telemetry check requires agent console output from the conversation step"));
        }

        logger.LogDebug("Analyzing agent console log at {LogPath}", _agentConsoleLogPath);

        string[] logLines;
        try
        {
            logLines = File.ReadLines(_agentConsoleLogPath)
                .TakeLast(MaxTelemetryLines)
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read agent console log file");
            return Task.FromResult(RequirementCheckResult.Warning(
                "Could not read agent console log file",
                details: $"Failed to read {_agentConsoleLogPath}: {ex.Message}"));
        }

        // Split log into span blocks
        var spanBlocks = SplitIntoSpanBlocks(logLines);
        if (spanBlocks.Count == 0)
        {
            return Task.FromResult(RequirementCheckResult.Failure(
                "No console exporter span output detected in agent logs",
                "Enable the OpenTelemetry console exporter in your agent so that spans are written to stdout.",
                details: "Expected to find span blocks containing traceId in agent console logs."));
        }

        // Filter to only span blocks that have a recognized gen_ai.operation.name
        var relevantBlocks = spanBlocks
            .Where(block => ExtractOperationNames(block)
                .Any(op => RecognizedGenAiOperations.Contains(op, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        if (relevantBlocks.Count == 0)
        {
            return Task.FromResult(RequirementCheckResult.Failure(
                "No GenAI operation spans found",
                "Ensure your agent instruments spans with gen_ai.operation.name set to invoke_agent, chat, or execute_tool.",
                details: $"Found {spanBlocks.Count} span block(s) in console output, " +
                    $"but none had a recognized gen_ai.operation.name value."));
        }

        logger.LogDebug("Found {Count} relevant span blocks out of {Total} total",
            relevantBlocks.Count, spanBlocks.Count);

        // Extract gen_ai.operation.name values from relevant spans
        var foundOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in relevantBlocks)
        {
            foreach (var op in ExtractOperationNames(block))
            {
                foundOperations.Add(op);
            }
        }

        var missingOperations = RequiredGenAiSpans
            .Where(op => !foundOperations.Contains(op))
            .ToList();

        if (missingOperations.Count > 0)
        {
            var foundList = foundOperations.Count > 0 ? string.Join(", ", foundOperations) : "none";
            return Task.FromResult(RequirementCheckResult.Failure(
                $"Missing required GenAI operation spans: {string.Join(", ", missingOperations)}",
                "Ensure your agent instruments invoke_agent, chat, and execute_tool operations with OpenTelemetry.",
                details: $"Found {relevantBlocks.Count} relevant span(s). " +
                    $"Found operations: {foundList}. " +
                    $"Missing operations: {string.Join(", ", missingOperations)}. " +
                    $"All three gen_ai.operation.name values (invoke_agent, chat, execute_tool) are required."));
        }

        // Additional OTel semantic convention checks (collected as warnings)
        var warnings = new List<string>();

        // Check parent links: execute_tool and chat spans must have non-empty parentId
        var childOpsWithoutParent = GetChildSpansMissingParent(relevantBlocks);
        if (childOpsWithoutParent.Count > 0)
        {
            warnings.Add($"child spans missing parentId: {string.Join(", ", childOpsWithoutParent)} — " +
                "these spans should be children of an invoke_agent span");
        }

        var detailsBuilder = $"Console exporter active with {relevantBlocks.Count} relevant span(s). " +
            $"All required GenAI operation spans detected: {string.Join(", ", RequiredGenAiSpans)}.";

        if (warnings.Count > 0)
        {
            detailsBuilder += $" Warnings: {string.Join("; ", warnings)}";
            return Task.FromResult(RequirementCheckResult.Warning(
                "Telemetry spans detected but with OTel semantic convention gaps",
                details: detailsBuilder));
        }

        return Task.FromResult(RequirementCheckResult.Success(details: detailsBuilder));
    }

    /// <summary>
    /// Splits console exporter output into span blocks.
    /// Each span block is a group of lines belonging to one span.
    /// Blocks are delimited by lines starting with '{' (span object boundary).
    /// Falls back to traceId-based splitting if no '{' delimiters are found.
    /// </summary>
    internal static List<List<string>> SplitIntoSpanBlocks(string[] logLines)
    {
        // Try brace-delimited first (standard console exporter format)
        var braceBlocks = SplitOnBraces(logLines);
        if (braceBlocks.Count > 0)
            return braceBlocks;

        // Fallback: split on traceId lines
        return SplitOnTraceId(logLines);
    }

    private static List<List<string>> SplitOnBraces(string[] logLines)
    {
        var blocks = new List<List<string>>();
        List<string>? currentBlock = null;

        foreach (var line in logLines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var trimmed = line.TrimStart();
            if (trimmed == "{")
            {
                currentBlock = new List<string>();
                blocks.Add(currentBlock);
            }

            currentBlock?.Add(line);
        }

        // Only return if we found blocks that look like spans (contain traceId)
        var spanBlocks = blocks.Where(b => b.Any(l =>
            l.TrimStart().StartsWith("traceId:", StringComparison.OrdinalIgnoreCase) ||
            l.TrimStart().StartsWith("\"traceId\":", StringComparison.OrdinalIgnoreCase) ||
            l.TrimStart().StartsWith("\"trace_id\":", StringComparison.OrdinalIgnoreCase) ||
            l.TrimStart().StartsWith("trace_id:", StringComparison.OrdinalIgnoreCase))).ToList();

        return spanBlocks;
    }

    private static List<List<string>> SplitOnTraceId(string[] logLines)
    {
        var blocks = new List<List<string>>();
        List<string>? currentBlock = null;
        var pendingLines = new List<string>();

        foreach (var line in logLines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var trimmed = line.TrimStart();
            bool isTraceIdLine = trimmed.StartsWith("traceId:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("\"traceId\":", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("'traceId':", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("trace_id:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("\"trace_id\":", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("'trace_id':", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Activity.TraceId:", StringComparison.OrdinalIgnoreCase);

            if (isTraceIdLine)
            {
                // Include any pending lines (e.g., instrumentationScope before traceId)
                currentBlock = new List<string>(pendingLines) { line };
                blocks.Add(currentBlock);
                pendingLines.Clear();
            }
            else if (currentBlock is not null)
            {
                currentBlock.Add(line);
            }
            else
            {
                // Lines before the first traceId — may include instrumentationScope
                pendingLines.Add(line);
            }
        }

        return blocks;
    }

    /// <summary>
    /// Extracts gen_ai.operation.name values from a span block.
    /// </summary>
    internal static List<string> ExtractOperationNames(List<string> spanBlock)
    {
        var operations = new List<string>();

        foreach (var line in spanBlock)
        {
            var idx = line.IndexOf("gen_ai.operation.name", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                continue;

            // Extract the value after the key — supports formats like:
            //   'gen_ai.operation.name': 'chat'
            //   "gen_ai.operation.name": "invoke_agent"
            //   gen_ai.operation.name=execute_tool
            var afterKey = line.Substring(idx + "gen_ai.operation.name".Length).TrimStart();

            // Skip separator characters (: = ' ")
            var valueStart = 0;
            while (valueStart < afterKey.Length && (afterKey[valueStart] == ':' || afterKey[valueStart] == '='
                || afterKey[valueStart] == '\'' || afterKey[valueStart] == '"' || afterKey[valueStart] == ' '))
            {
                valueStart++;
            }

            if (valueStart >= afterKey.Length)
                continue;

            // Read until the next delimiter
            var valueEnd = valueStart;
            while (valueEnd < afterKey.Length && afterKey[valueEnd] != '\''
                && afterKey[valueEnd] != '"' && afterKey[valueEnd] != ','
                && afterKey[valueEnd] != ' ' && afterKey[valueEnd] != '}')
            {
                valueEnd++;
            }

            if (valueEnd > valueStart)
            {
                operations.Add(afterKey.Substring(valueStart, valueEnd - valueStart));
            }
        }

        return operations;
    }

    /// <summary>
    /// Identifies child span operations (chat, execute_tool) that are missing a parentId/parentSpanId.
    /// Returns the list of operation names that should have parent links but don't.
    /// </summary>
    internal static List<string> GetChildSpansMissingParent(List<List<string>> spanBlocks)
    {
        var missingParent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var block in spanBlocks)
        {
            var ops = ExtractOperationNames(block);
            var isChildSpan = ops.Any(op => ChildSpanOperations.Contains(op, StringComparer.OrdinalIgnoreCase));
            if (!isChildSpan)
                continue;

            var hasParent = block.Any(line => ParentSpanPattern.IsMatch(line));

            if (!hasParent)
            {
                foreach (var op in ops.Where(o => ChildSpanOperations.Contains(o, StringComparer.OrdinalIgnoreCase)))
                {
                    missingParent.Add(op);
                }
            }
        }

        return missingParent.OrderBy(o => o).ToList();
    }

}
