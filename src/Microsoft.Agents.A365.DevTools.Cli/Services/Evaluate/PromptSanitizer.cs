// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Sanitizes untrusted MCP server content before it is embedded in agent prompts
/// or written to evaluation files (F-001 Layer 1).
///
/// Removes bidi-override and zero-width characters that can be used to hide
/// injected instructions, strips C0/C1 control characters that have no
/// legitimate use in tool metadata, and caps field length to bound prompt size.
/// </summary>
internal static class PromptSanitizer
{
    /// <summary>
    /// Sanitizes a single field value from an untrusted MCP server (tool name,
    /// description, parameter name, parameter description, etc.).
    /// Returns an empty string when the input is null or empty.
    /// </summary>
    public static string SanitizeField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        StringBuilder? sb = null;
        int safeStart = 0;

        for (int i = 0; i < value.Length; i++)
        {
            // Tags block U+E0000-U+E01EF (no legitimate use in tool metadata):
            // Encoded as surrogate pairs: high surrogate \uDB40 + low \uDC00-\uDDEF.
            if (value[i] == '\uDB40' && i + 1 < value.Length
                && value[i + 1] >= '\uDC00' && value[i + 1] <= '\uDDEF')
            {
                sb ??= new StringBuilder(value.Length);
                sb.Append(value, safeStart, i - safeStart);
                safeStart = i + 2; // skip both surrogate code units
                i++;               // advance past the low surrogate
                continue;
            }

            if (IsDangerous(value[i]))
            {
                // Lazy-init: only allocate when we first strip a character.
                sb ??= new StringBuilder(value.Length);
                sb.Append(value, safeStart, i - safeStart);
                safeStart = i + 1;
            }
        }

        if (sb is null)
        {
            return value;
        }

        sb.Append(value, safeStart, value.Length - safeStart);
        return sb.ToString();
    }

    /// <summary>
    /// Returns true for characters with no legitimate use in MCP tool metadata
    /// that are commonly exploited in bidi-smuggling or prompt injection attacks.
    /// All comparisons use integer codepoint values to avoid any source-encoding
    /// ambiguity with embedded Unicode literals.
    /// </summary>
    private static bool IsDangerous(char c)
    {
        int cp = c;

        // C0 control chars except HT (0x09), LF (0x0A), CR (0x0D)
        if (cp <= 0x08) return true;
        if (cp is 0x0B or 0x0C) return true;
        if (cp >= 0x0E && cp <= 0x1F) return true;
        if (cp == 0x7F) return true;

        // C1 control chars: U+0080-U+009F — not valid in JSON tool metadata
        if (cp >= 0x0080 && cp <= 0x009F) return true;

        // Combining grapheme joiner: U+034F
        if (cp == 0x034F) return true;

        // Hangul choseong/jungseong fillers: U+115F, U+1160
        if (cp is 0x115F or 0x1160) return true;

        // Mongolian vowel separator: U+180E — renders blank in many contexts
        if (cp == 0x180E) return true;

        // Zero-width space through RTL mark: U+200B-U+200F
        if (cp >= 0x200B && cp <= 0x200F) return true;

        // LTR/RTL embedding, pop direction format, overrides: U+202A-U+202E
        if (cp >= 0x202A && cp <= 0x202E) return true;

        // Word joiner, invisible math operators, and bidi isolates: U+2060-U+2069
        // U+2060 (WORD JOINER) and U+2063 (INVISIBLE SEPARATOR) appear in published injection PoCs.
        // Extending the range to cover the full block for defence depth.
        if (cp >= 0x2060 && cp <= 0x2069) return true;

        // Hangul filler: U+3164 — zero-width equivalent used in LLM injection research
        if (cp == 0x3164) return true;

        // Halfwidth Hangul filler: U+FFA0
        if (cp == 0xFFA0) return true;

        // Variation selectors: U+FE00-U+FE0F — alter glyph rendering; used in LLM steganographic PoCs
        if (cp >= 0xFE00 && cp <= 0xFE0F) return true;

        // Zero-width no-break space / byte-order mark: U+FEFF
        if (cp == 0xFEFF) return true;

        return false;
    }
}
