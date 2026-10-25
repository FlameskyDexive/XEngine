// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.XUI.Core;

/// <summary>
/// One named image in a <c>.pxl</c> file — a button's normal state, an icon, a 9-slice frame.
/// </summary>
public sealed class PxlSection
{
    internal PxlSection(string name, XuiSourceSpan span)
    {
        Name = name;
        Span = span;
    }

    /// <summary>Section name from <c>[name]</c>. A single-section file uses <c>default</c>.</summary>
    public string Name { get; }

    public XuiSourceSpan Span { get; }

    /// <summary>Grid rows, top to bottom, one character per pixel. All rows are the same length.</summary>
    public List<string> Rows { get; } = new();

    public int Width => Rows.Count == 0 ? 0 : Rows[0].Length;

    public int Height => Rows.Count;

    /// <summary>9-slice border in pixels: top, right, bottom, left. All zero means no border.</summary>
    public int BorderTop { get; internal set; }
    public int BorderRight { get; internal set; }
    public int BorderBottom { get; internal set; }
    public int BorderLeft { get; internal set; }

    public bool HasBorder
        => BorderTop != 0 || BorderRight != 0 || BorderBottom != 0 || BorderLeft != 0;
}

/// <summary>
/// A parsed <c>.pxl</c> file: a palette of single characters, and one or more character grids that
/// use it.
/// </summary>
/// <remarks>
/// The format exists so an agent can author UI art as text and then <em>read its own output back</em>.
/// A grid can be checked row by row and a single pixel corrected, which is not true of a generated
/// image — and <c>pxl_render</c> turns it into a PNG the agent can actually look at, so the loop is
/// write → render → look → revise.
/// </remarks>
public sealed class PxlDocument
{
    /// <summary>Pixels-per-unit for the generated sprites. Small values suit pixel art.</summary>
    public float PixelsPerUnit { get; internal set; } = 100f;

    /// <summary>Optional <c>palette: @name</c> reference to a shared <c>.gpl</c> palette.</summary>
    public string? PaletteName { get; internal set; }

    /// <summary>Character → colour. <c>.</c> is always transparent and is never listed here.</summary>
    public Dictionary<char, XuiColor> Chars { get; } = new();

    /// <summary>Characters whose colour came from a named palette entry rather than a hex literal.</summary>
    public Dictionary<char, string> UnresolvedNames { get; } = new();

    public List<PxlSection> Sections { get; } = new();

    /// <summary>The reserved transparent character. Redefining it is an error.</summary>
    public const char TransparentChar = '.';

    public PxlSection? FindSection(string name)
    {
        for (int i = 0; i < Sections.Count; i++)
        {
            if (string.Equals(Sections[i].Name, name, StringComparison.Ordinal)) return Sections[i];
        }
        return null;
    }

    /// <summary>
    /// Resolves a character to a colour. Unknown characters and <c>.</c> are transparent, so a grid
    /// with a typo still renders — the missing pixel is visible, which is the fastest way to see it.
    /// </summary>
    public XuiColor ColorOf(char c)
    {
        if (c == TransparentChar) return XuiColor.Literal(0f, 0f, 0f, 0f);
        return Chars.TryGetValue(c, out XuiColor color) ? color : XuiColor.Literal(0f, 0f, 0f, 0f);
    }
}
