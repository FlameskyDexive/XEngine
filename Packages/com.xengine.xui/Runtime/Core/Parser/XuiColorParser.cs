// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace XEngine.XUI.Core;

/// <summary>
/// Parses a <c>color=</c> value: a hex literal (<c>#rgb</c> / <c>#rrggbb</c> / <c>#rrggbbaa</c>), a CSS
/// color name, or a theme token. Anything that is not hex and not a known CSS name is treated as a
/// token — the theme store resolves it at apply time, and lint reports tokens no theme defines.
/// </summary>
public static class XuiColorParser
{
    private static readonly Dictionary<string, uint> CssColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = 0x000000FF,
        ["white"] = 0xFFFFFFFF,
        ["red"] = 0xFF0000FF,
        ["green"] = 0x008000FF,
        ["lime"] = 0x00FF00FF,
        ["blue"] = 0x0000FFFF,
        ["yellow"] = 0xFFFF00FF,
        ["cyan"] = 0x00FFFFFF,
        ["magenta"] = 0xFF00FFFF,
        ["gray"] = 0x808080FF,
        ["grey"] = 0x808080FF,
        ["silver"] = 0xC0C0C0FF,
        ["maroon"] = 0x800000FF,
        ["olive"] = 0x808000FF,
        ["navy"] = 0x000080FF,
        ["purple"] = 0x800080FF,
        ["teal"] = 0x008080FF,
        ["orange"] = 0xFFA500FF,
        ["clear"] = 0x00000000,
        ["transparent"] = 0x00000000,
    };

    /// <summary>True when the text is a syntactically valid hex literal or a known CSS name.</summary>
    public static bool IsLiteral(string text)
        => TryParseLiteral(text, out _);

    public static bool TryParseLiteral(string text, out XuiColor color)
    {
        color = default;
        if (string.IsNullOrEmpty(text)) return false;

        if (text[0] == '#')
            return TryParseHex(text, out color);

        if (CssColors.TryGetValue(text, out uint packed))
        {
            color = FromPacked(packed);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Parses any color attribute. Non-literals become a token reference rather than an error, so a
    /// document that names a theme token still produces a usable IR before the theme is resolved.
    /// </summary>
    public static XuiColor Parse(string text)
        => TryParseLiteral(text, out XuiColor literal) ? literal : XuiColor.FromToken(text);

    private static bool TryParseHex(string text, out XuiColor color)
    {
        color = default;
        ReadOnlySpan<char> digits = text.AsSpan(1);

        // #rgb and #rgba expand each nibble to a byte (0xF -> 0xFF), matching CSS.
        if (digits.Length is 3 or 4)
        {
            Span<char> expanded = stackalloc char[8];
            for (int i = 0; i < digits.Length; i++)
            {
                expanded[i * 2] = digits[i];
                expanded[i * 2 + 1] = digits[i];
            }
            if (digits.Length == 3)
            {
                expanded[6] = 'F';
                expanded[7] = 'F';
            }
            return TryParseHex8(expanded, out color);
        }

        if (digits.Length == 6)
        {
            Span<char> expanded = stackalloc char[8];
            digits.CopyTo(expanded);
            expanded[6] = 'F';
            expanded[7] = 'F';
            return TryParseHex8(expanded, out color);
        }

        if (digits.Length == 8)
            return TryParseHex8(digits, out color);

        return false;
    }

    private static bool TryParseHex8(ReadOnlySpan<char> digits, out XuiColor color)
    {
        color = default;
        if (!uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed))
            return false;
        color = FromPacked(packed);
        return true;
    }

    private static XuiColor FromPacked(uint rgba) => XuiColor.Literal(
        ((rgba >> 24) & 0xFF) / 255f,
        ((rgba >> 16) & 0xFF) / 255f,
        ((rgba >> 8) & 0xFF) / 255f,
        (rgba & 0xFF) / 255f);
}
