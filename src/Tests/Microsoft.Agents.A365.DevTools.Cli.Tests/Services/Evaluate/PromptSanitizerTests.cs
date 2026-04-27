// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Evaluate;

/// <summary>
/// Tests for PromptSanitizer (F-001 Layer 1).
/// All non-printable/Unicode characters use (char)0xNNNN to avoid source-encoding ambiguity.
/// </summary>
public class PromptSanitizerTests
{
    // -----------------------------------------------------------------
    // Null / empty passthrough
    // -----------------------------------------------------------------

    [Fact]
    public void SanitizeField_Null_ReturnsEmpty()
    {
        PromptSanitizer.SanitizeField(null).Should().Be(string.Empty);
    }

    [Fact]
    public void SanitizeField_Empty_ReturnsEmpty()
    {
        PromptSanitizer.SanitizeField(string.Empty).Should().Be(string.Empty);
    }

    // -----------------------------------------------------------------
    // Clean strings pass through unchanged
    // -----------------------------------------------------------------

    [Fact]
    public void SanitizeField_PlainAscii_Unchanged()
    {
        const string input = "get_user_profile";
        PromptSanitizer.SanitizeField(input).Should().Be(input);
    }

    [Fact]
    public void SanitizeField_TabNewlineCarriageReturn_Preserved()
    {
        // HT (0x09), LF (0x0A), CR (0x0D) are valid and must not be stripped.
        var input = "line1" + (char)0x0A + "line2" + (char)0x09 + "tabbed" + (char)0x0D + (char)0x0A;
        PromptSanitizer.SanitizeField(input).Should().Be(input);
    }

    // -----------------------------------------------------------------
    // Bidi and zero-width character stripping
    // -----------------------------------------------------------------

    [Fact]
    public void SanitizeField_ZeroWidthSpace_Stripped()
    {
        // U+200B ZERO WIDTH SPACE
        var input = "get" + (char)0x200B + "user";
        PromptSanitizer.SanitizeField(input).Should().Be("getuser");
    }

    [Fact]
    public void SanitizeField_ZeroWidthNonJoiner_Stripped()
    {
        // U+200C ZERO WIDTH NON-JOINER
        var input = "get" + (char)0x200C + "user";
        PromptSanitizer.SanitizeField(input).Should().Be("getuser");
    }

    [Fact]
    public void SanitizeField_ZeroWidthJoiner_Stripped()
    {
        // U+200D ZERO WIDTH JOINER
        var input = "get" + (char)0x200D + "user";
        PromptSanitizer.SanitizeField(input).Should().Be("getuser");
    }

    [Fact]
    public void SanitizeField_LeftToRightMark_Stripped()
    {
        // U+200E LEFT-TO-RIGHT MARK
        var input = "get" + (char)0x200E + "user";
        PromptSanitizer.SanitizeField(input).Should().Be("getuser");
    }

    [Fact]
    public void SanitizeField_RightToLeftMark_Stripped()
    {
        // U+200F RIGHT-TO-LEFT MARK
        var input = "get" + (char)0x200F + "user";
        PromptSanitizer.SanitizeField(input).Should().Be("getuser");
    }

    [Fact]
    public void SanitizeField_CombiningGraphemeJoiner_Stripped()
    {
        // U+034F COMBINING GRAPHEME JOINER
        var input = "get" + (char)0x034F + "user";
        PromptSanitizer.SanitizeField(input).Should().Be("getuser");
    }

    [Fact]
    public void SanitizeField_LeftToRightEmbedding_Stripped()
    {
        // U+202A LEFT-TO-RIGHT EMBEDDING
        var input = "get" + (char)0x202A + "user";
        PromptSanitizer.SanitizeField(input).Should().Be("getuser");
    }

    [Fact]
    public void SanitizeField_RightToLeftEmbedding_Stripped()
    {
        // U+202B RIGHT-TO-LEFT EMBEDDING
        var input = "get" + (char)0x202B + "user";
        PromptSanitizer.SanitizeField(input).Should().Be("getuser");
    }

    [Fact]
    public void SanitizeField_RightToLeftOverride_Stripped()
    {
        // U+202E RIGHT-TO-LEFT OVERRIDE — classic bidi-smuggling char
        // U+202C POP DIRECTIONAL FORMATTING
        var input = (char)0x202E + "get_user" + (char)0x202C;
        PromptSanitizer.SanitizeField(input).Should().Be("get_user");
    }

    [Fact]
    public void SanitizeField_WordJoiner_Stripped()
    {
        // U+2060 WORD JOINER — zero-width, appears in published LLM injection PoCs
        var input = "get" + (char)0x2060 + "user";
        PromptSanitizer.SanitizeField(input).Should().Be("getuser");
    }

    [Fact]
    public void SanitizeField_InvisibleSeparator_Stripped()
    {
        // U+2063 INVISIBLE SEPARATOR — zero-width, appears in published injection PoCs
        var input = "get" + (char)0x2063 + "user";
        PromptSanitizer.SanitizeField(input).Should().Be("getuser");
    }

    [Fact]
    public void SanitizeField_BidiIsolateChars_Stripped()
    {
        // U+2066 LEFT-TO-RIGHT ISOLATE, U+2069 POP DIRECTIONAL ISOLATE
        var input = "tool" + (char)0x2066 + "_name" + (char)0x2069;
        PromptSanitizer.SanitizeField(input).Should().Be("tool_name");
    }

    [Fact]
    public void SanitizeField_ByteOrderMark_Stripped()
    {
        // U+FEFF ZERO WIDTH NO-BREAK SPACE / BOM
        var input = (char)0xFEFF + "get_user";
        PromptSanitizer.SanitizeField(input).Should().Be("get_user");
    }

    [Fact]
    public void SanitizeField_MultipleDangerousCharsInOneString_AllStripped()
    {
        var input = (char)0x202E + "get" + (char)0x200B + "_user" + (char)0xFEFF;
        PromptSanitizer.SanitizeField(input).Should().Be("get_user");
    }

    // -----------------------------------------------------------------
    // Extended Unicode injection vectors (added to IsDangerous in Expert-2 pass)
    // -----------------------------------------------------------------

    [Fact]
    public void SanitizeField_C1ControlChar_Stripped()
    {
        // U+0080 — first C1 control char; all U+0080-U+009F should be stripped
        var input = "a" + (char)0x0080 + "b";
        PromptSanitizer.SanitizeField(input).Should().Be("ab");
    }

    [Fact]
    public void SanitizeField_C1ControlChar_LastInRange_Stripped()
    {
        // U+009F — last C1 control char
        var input = "a" + (char)0x009F + "b";
        PromptSanitizer.SanitizeField(input).Should().Be("ab");
    }

    [Fact]
    public void SanitizeField_HangulChoseongFiller_Stripped()
    {
        // U+115F HANGUL CHOSEONG FILLER — renders as zero-width
        var input = "a" + (char)0x115F + "b";
        PromptSanitizer.SanitizeField(input).Should().Be("ab");
    }

    [Fact]
    public void SanitizeField_HangulJungseongFiller_Stripped()
    {
        // U+1160 HANGUL JUNGSEONG FILLER — renders as zero-width
        var input = "a" + (char)0x1160 + "b";
        PromptSanitizer.SanitizeField(input).Should().Be("ab");
    }

    [Fact]
    public void SanitizeField_MongolianVowelSeparator_Stripped()
    {
        // U+180E MONGOLIAN VOWEL SEPARATOR — renders as blank in many contexts
        var input = "a" + (char)0x180E + "b";
        PromptSanitizer.SanitizeField(input).Should().Be("ab");
    }

    [Fact]
    public void SanitizeField_HangulFiller_Stripped()
    {
        // U+3164 HANGUL FILLER — zero-width equivalent used in LLM injection research
        var input = "a" + (char)0x3164 + "b";
        PromptSanitizer.SanitizeField(input).Should().Be("ab");
    }

    [Fact]
    public void SanitizeField_HalfwidthHangulFiller_Stripped()
    {
        // U+FFA0 HALFWIDTH HANGUL FILLER
        var input = "a" + (char)0xFFA0 + "b";
        PromptSanitizer.SanitizeField(input).Should().Be("ab");
    }

    // -----------------------------------------------------------------
    // Control character stripping
    // -----------------------------------------------------------------

    [Fact]
    public void SanitizeField_NullByte_Stripped()
    {
        // U+0000 NUL
        var input = "get" + (char)0x00 + "user";
        PromptSanitizer.SanitizeField(input).Should().Be("getuser");
    }

    [Fact]
    public void SanitizeField_Bel_Stripped()
    {
        // U+0007 BEL
        var input = "a" + (char)0x07 + "b";
        PromptSanitizer.SanitizeField(input).Should().Be("ab");
    }

    [Fact]
    public void SanitizeField_Escape_Stripped()
    {
        // U+001B ESC
        var input = "a" + (char)0x1B + "b";
        PromptSanitizer.SanitizeField(input).Should().Be("ab");
    }

    [Fact]
    public void SanitizeField_VerticalTab_Stripped()
    {
        // U+000B VERTICAL TAB — not in the HT/LF/CR allow-list
        var input = "a" + (char)0x0B + "b";
        PromptSanitizer.SanitizeField(input).Should().Be("ab");
    }

    [Fact]
    public void SanitizeField_Delete_Stripped()
    {
        // U+007F DEL
        var input = "get" + (char)0x7F + "user";
        PromptSanitizer.SanitizeField(input).Should().Be("getuser");
    }

    // -----------------------------------------------------------------
    // Tags block stripping (U+E0000-U+E01EF, surrogate pairs)
    // -----------------------------------------------------------------

    [Fact]
    public void SanitizeField_TagsBlockCharacter_Stripped()
    {
        // U+E0041 TAG LATIN CAPITAL LETTER A — encoded as surrogate pair 󠁁.
        // No legitimate use in tool metadata; used in steganographic injection PoCs.
        var tagsChar = new string(new char[] { (char)0xDB40, (char)0xDC41 });
        var input = "a" + tagsChar + "b";
        PromptSanitizer.SanitizeField(input).Should().Be("ab");
    }

    [Fact]
    public void SanitizeField_TagsBlockRangeStart_Stripped()
    {
        // U+E0000 (range start): high surrogate \uDB40 + low \uDC00.
        var tagsChar = new string(new char[] { (char)0xDB40, (char)0xDC00 });
        var input = "prefix" + tagsChar + "suffix";
        PromptSanitizer.SanitizeField(input).Should().Be("prefixsuffix");
    }

    [Fact]
    public void SanitizeField_SurrogateHighWithoutLow_PreservedNotCrashed()
    {
        // Lone high surrogate \uDB40 (not followed by the expected low surrogate range):
        // SanitizeField must not throw; it is treated as a non-tags-block surrogate and passed through.
        var input = "a" + (char)0xDB40 + (char)0xDFFF + "b"; // low is 0xDFFF, outside DC00-DDFF range
        var result = PromptSanitizer.SanitizeField(input);
        result.Should().Contain("a");
        result.Should().Contain("b");
    }

    // -----------------------------------------------------------------
    // Variation selector stripping (U+FE00-U+FE0F)
    // -----------------------------------------------------------------

    [Fact]
    public void SanitizeField_VariationSelector1_Stripped()
    {
        // U+FE00 VARIATION SELECTOR-1 — alters glyph rendering; used in LLM steganographic PoCs.
        var input = "a" + (char)0xFE00 + "b";
        PromptSanitizer.SanitizeField(input).Should().Be("ab");
    }

    [Fact]
    public void SanitizeField_VariationSelector16_Stripped()
    {
        // U+FE0F VARIATION SELECTOR-16 — last in the VS range; used to force emoji presentation.
        var input = "tool" + (char)0xFE0F + "name";
        PromptSanitizer.SanitizeField(input).Should().Be("toolname");
    }
}
