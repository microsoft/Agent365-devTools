// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

public sealed record LogRedactionResult(
    string RedactedContent,
    int EmailsRedacted,
    int IdsRedacted,
    int TokensRedacted);

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

    public LogRedactionResult Redact(string logContent, string sourceFilePath)
    {
        ArgumentNullException.ThrowIfNull(logContent);

        // Consistent alias maps: same value always maps to the same placeholder
        var emailMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var guidMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int tokenCount = 0;

        // 1. Redact JWT tokens first (they contain dots that could confuse other patterns)
        var content = JwtPattern.Replace(logContent, _ =>
        {
            tokenCount++;
            return "<JWT-TOKEN>";
        });

        // 2. Redact email addresses with consistent aliases
        content = EmailPattern.Replace(content, m =>
        {
            var key = m.Value.ToLowerInvariant();
            if (!emailMap.TryGetValue(key, out var alias))
            {
                alias = $"<email-{emailMap.Count + 1}>";
                emailMap[key] = alias;
            }
            return alias;
        });

        // 3. Redact GUIDs with consistent aliases
        content = GuidPattern.Replace(content, m =>
        {
            var key = m.Value.ToLowerInvariant();
            if (!guidMap.TryGetValue(key, out var alias))
            {
                alias = $"<id-{guidMap.Count + 1}>";
                guidMap[key] = alias;
            }
            return alias;
        });

        var header = BuildHeader(sourceFilePath, emailMap.Count, guidMap.Count, tokenCount);
        return new LogRedactionResult(
            RedactedContent: header + content,
            EmailsRedacted: emailMap.Count,
            IdsRedacted: guidMap.Count,
            TokensRedacted: tokenCount);
    }

    private static string BuildHeader(string sourceFilePath, int emails, int ids, int tokens)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Redacted by a365 logs export — {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine($"# {emails} email(s), {ids} id(s), {tokens} JWT token(s) replaced");
        sb.AppendLine($"# Original: {sourceFilePath}");
        sb.AppendLine("#");
        return sb.ToString();
    }
}
