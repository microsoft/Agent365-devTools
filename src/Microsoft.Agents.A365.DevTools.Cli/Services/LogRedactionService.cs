// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

public sealed record LogRedactionResult(
    string RedactedContent,
    int EmailsRedacted,
    int IdsRedacted,
    int TokensRedacted,
    int UsernamesRedacted);

public interface ILogRedactionService
{
    LogRedactionResult Redact(string logContent, string sourceFilePath);
}

public sealed class LogRedactionService : ILogRedactionService
{
    // JWT: three base64url segments separated by dots, starting with eyJ (header always begins {"
    private static readonly Regex JwtPattern = new(
        @"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*",
        RegexOptions.Compiled);

    // Standard email addresses
    private static readonly Regex EmailPattern = new(
        @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled);

    // GUIDs in 8-4-4-4-12 format (case-insensitive)
    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    // OS path usernames: /Users/<name>/ or /home/<name>/ (macOS/Linux) and C:\Users\<name>\ (Windows)
    // Only the username segment is replaced; the rest of the path is preserved for debugging context.
    private static readonly Regex PathUsernamePattern = new(
        @"((?:/(?:Users|home)/|[A-Za-z]:\\Users\\))([^/\\\s]+)",
        RegexOptions.Compiled);

    // Diagnostic IDs that pair the log against server-side traces. These are random per-run
    // identifiers (not sensitive) and Microsoft support needs them to correlate with Graph
    // server logs, so preserve any GUID that appears immediately after one of these markers.
    private static readonly Regex DiagnosticIdPattern = new(
        @"(?:TraceId|CorrelationId|request-id|client-request-id)[""']?\s*[:=]\s*[""']?([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Well-known public Microsoft and Agent 365 resource app IDs. These appear verbatim in
    // product documentation and don't identify a specific tenant or user, so preserving them
    // is safe and makes the redacted log readable (the alias "<id-N>" tells you nothing about
    // which resource was being accessed). Values are sourced from AuthenticationConstants,
    // ConfigConstants, and PowerPlatformConstants — keep in sync if those constants change.
    // Tenant-specific service principal object IDs are NOT in this list and remain redacted.
    private static readonly HashSet<string> WellKnownPublicAppIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "00000003-0000-0000-c000-000000000000", // Microsoft Graph
        "5a807f24-c9de-44ee-a3a7-329e88a00ffc", // Agent 365 Messaging Bot API
        "9b975845-388f-4429-889e-eab1ef63949c", // Agent 365 Observability API
        "8578e004-a5c6-46e7-913e-12f58912df43", // Power Platform API (Connectivity)
        "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1", // Agent 365 Tools (MCP audience, production)
        AuthenticationConstants.WellKnownClientAppId, // Agent 365 CLI (well-known first-party application)
    };

    public LogRedactionResult Redact(string logContent, string sourceFilePath)
    {
        ArgumentNullException.ThrowIfNull(logContent);

        // Consistent alias maps: same value always maps to the same placeholder
        var emailMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var guidMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usernameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int tokenCount = 0;

        // Build the preserve-GUID set: well-known public appIds always, plus diagnostic IDs
        // (TraceId/CorrelationId/request-id/client-request-id) extracted from the log body.
        // The source path is unlikely to contain these markers, so the body is the right
        // input to scan.
        var preserveGuids = new HashSet<string>(WellKnownPublicAppIds, StringComparer.OrdinalIgnoreCase);
        foreach (Match m in DiagnosticIdPattern.Matches(logContent))
            preserveGuids.Add(m.Groups[1].Value);

        // The header line and the log body must run through the same redaction pipeline so that
        // emails, GUIDs, or tokens embedded in the source path (e.g. OneDrive workspace paths,
        // tenant-scoped folder names) don't leak in the header that claims to be redacted.
        var content = RedactAll(logContent, emailMap, guidMap, usernameMap, preserveGuids, ref tokenCount);
        var redactedSourcePath = RedactAll(sourceFilePath, emailMap, guidMap, usernameMap, preserveGuids, ref tokenCount);

        var header = BuildHeader(redactedSourcePath, emailMap.Count, guidMap.Count, tokenCount, usernameMap.Count);
        return new LogRedactionResult(
            RedactedContent: header + content,
            EmailsRedacted: emailMap.Count,
            IdsRedacted: guidMap.Count,
            TokensRedacted: tokenCount,
            UsernamesRedacted: usernameMap.Count);
    }

    private static string RedactAll(
        string input,
        Dictionary<string, string> emailMap,
        Dictionary<string, string> guidMap,
        Dictionary<string, string> usernameMap,
        HashSet<string> preserveGuids,
        ref int tokenCount)
    {
        // 1. JWT tokens first — they contain dots that could otherwise confuse other patterns.
        // C# lambdas cannot capture ref parameters, so the count is mirrored into a local for
        // the lambda's lifetime and written back to the ref parameter afterwards.
        int localTokenCount = tokenCount;
        var output = JwtPattern.Replace(input, _ =>
        {
            localTokenCount++;
            return "<JWT-TOKEN>";
        });
        tokenCount = localTokenCount;

        // 2. Emails with consistent aliases.
        output = EmailPattern.Replace(output, m =>
        {
            var key = m.Value.ToLowerInvariant();
            if (!emailMap.TryGetValue(key, out var alias))
            {
                alias = $"<email-{emailMap.Count + 1}>";
                emailMap[key] = alias;
            }
            return alias;
        });

        // 3. GUIDs with consistent aliases. Diagnostic IDs (TraceId, CorrelationId, Graph
        //    request IDs) and well-known public appIds are preserved verbatim so the log
        //    remains useful for debugging and support escalation.
        output = GuidPattern.Replace(output, m =>
        {
            if (preserveGuids.Contains(m.Value)) return m.Value;
            var key = m.Value.ToLowerInvariant();
            if (!guidMap.TryGetValue(key, out var alias))
            {
                alias = $"<id-{guidMap.Count + 1}>";
                guidMap[key] = alias;
            }
            return alias;
        });

        // 4. OS path usernames; preserve the rest of the path for debugging context.
        output = PathUsernamePattern.Replace(output, m =>
        {
            var prefix = m.Groups[1].Value;
            var key = m.Groups[2].Value.ToLowerInvariant();
            if (!usernameMap.TryGetValue(key, out var alias))
            {
                alias = $"<username-{usernameMap.Count + 1}>";
                usernameMap[key] = alias;
            }
            return prefix + alias;
        });

        return output;
    }

    private static string BuildHeader(string sourceFilePath, int emails, int ids, int tokens, int usernames)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Redacted by a365 logs export - {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine($"# {emails} email(s), {ids} id(s), {tokens} JWT token(s), {usernames} username(s) replaced");
        sb.AppendLine($"# Original: {sourceFilePath}");
        sb.AppendLine("#");
        return sb.ToString();
    }
}
