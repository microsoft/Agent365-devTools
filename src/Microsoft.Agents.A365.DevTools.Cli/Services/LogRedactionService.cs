// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;

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

    public LogRedactionResult Redact(string logContent, string sourceFilePath)
    {
        ArgumentNullException.ThrowIfNull(logContent);

        // Consistent alias maps: same value always maps to the same placeholder
        var emailMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var guidMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usernameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

        // 4. Redact OS path usernames; preserve the rest of the path for debugging context
        content = PathUsernamePattern.Replace(content, m =>
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

        // Apply path username redaction to the source path written in the header (same alias map
        // as content so the same username gets the same alias if it appears in both places).
        var redactedSourcePath = PathUsernamePattern.Replace(sourceFilePath, m =>
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

        var header = BuildHeader(redactedSourcePath, emailMap.Count, guidMap.Count, tokenCount, usernameMap.Count);
        return new LogRedactionResult(
            RedactedContent: header + content,
            EmailsRedacted: emailMap.Count,
            IdsRedacted: guidMap.Count,
            TokensRedacted: tokenCount,
            UsernamesRedacted: usernameMap.Count);
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
