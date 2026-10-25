// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace XEngine.XUI.Core;

/// <summary>A malformed <c>.pxl</c> file, reported at the line that broke it.</summary>
public sealed class PxlParseException : Exception
{
    public PxlParseException(string message, int line, string? sourcePath = null)
        : base(Format(message, line, sourcePath))
    {
        Line = line;
        SourcePath = sourcePath;
        RawMessage = message;
    }

    public int Line { get; }
    public string? SourcePath { get; }
    public string RawMessage { get; }

    private static string Format(string message, int line, string? sourcePath)
        => (sourcePath ?? "<pxl>") + "(" + line.ToString(CultureInfo.InvariantCulture) + "): " + message;
}

/// <summary>
/// Parses the <c>.pxl</c> pixel-grid format: a header of directives, a character→colour table, and one
/// or more named grids.
/// </summary>
/// <remarks>
/// Every error names its line. That matters more here than almost anywhere else: a grid is a wall of
/// characters, and "row 7 is 11 wide, the rest are 12" is the difference between a one-character fix
/// and re-reading the whole file.
/// </remarks>
public static class PxlParser
{
    private const string DefaultSection = "default";

    public static PxlDocument ParseFile(string path, string? displayPath = null)
        => Parse(File.ReadAllText(path), displayPath ?? path);

    public static PxlDocument Parse(string text, string? sourcePath = null)
    {
        var document = new PxlDocument();
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        PxlSection? section = null;
        bool inChars = false;
        bool inGrid = false;

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNumber = i + 1;
            string raw = lines[i];
            string trimmed = raw.Trim();

            // A comment is a line whose first non-space character is '#'. There are deliberately no
            // trailing comments: "K: night  # outline" would make the colour name literally
            // "night  # outline", and silently so.
            if (trimmed.Length == 0)
            {
                if (inGrid) inGrid = false;
                continue;
            }
            if (trimmed[0] == '#') continue;

            if (trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
            {
                section = BeginSection(document, trimmed, lineNumber, sourcePath);
                inChars = false;
                inGrid = false;
                continue;
            }

            if (inGrid)
            {
                // Indentation is the grid's own; a row is the trimmed content.
                AddRow(section!, trimmed, lineNumber, sourcePath);
                continue;
            }

            if (inChars && raw.Length > 0 && char.IsWhiteSpace(raw[0]))
            {
                ParseCharEntry(document, trimmed, lineNumber, sourcePath);
                continue;
            }

            inChars = false;

            int colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                throw new PxlParseException(
                    "Expected a directive (name: value), a [section] header, or a comment.",
                    lineNumber, sourcePath);
            }

            string key = trimmed.Substring(0, colon).Trim();
            string value = trimmed.Substring(colon + 1).Trim();

            switch (key)
            {
                case "palette":
                    document.PaletteName = ParsePaletteName(value, lineNumber, sourcePath);
                    break;

                case "ppu":
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float ppu)
                        || ppu <= 0f)
                    {
                        throw new PxlParseException(
                            "ppu must be a positive number, got \"" + value + "\".", lineNumber, sourcePath);
                    }
                    document.PixelsPerUnit = ppu;
                    break;

                case "chars":
                    if (value.Length != 0)
                    {
                        throw new PxlParseException(
                            "chars: takes no value; the entries follow on indented lines.", lineNumber, sourcePath);
                    }
                    inChars = true;
                    break;

                case "border":
                    section = EnsureSection(document, section, lineNumber);
                    ParseBorder(section, value, lineNumber, sourcePath);
                    break;

                case "grid":
                    if (value.Length != 0)
                    {
                        throw new PxlParseException(
                            "grid: takes no value; the rows follow on indented lines.", lineNumber, sourcePath);
                    }
                    section = EnsureSection(document, section, lineNumber);
                    inGrid = true;
                    break;

                default:
                    throw new PxlParseException(
                        "Unknown directive \"" + key + "\". Expected palette, ppu, chars, border, or grid.",
                        lineNumber, sourcePath);
            }
        }

        if (document.Sections.Count == 0)
            throw new PxlParseException("The file declares no grid.", lines.Length, sourcePath);

        for (int i = 0; i < document.Sections.Count; i++)
        {
            PxlSection declared = document.Sections[i];
            if (declared.Rows.Count == 0)
            {
                throw new PxlParseException(
                    "Section \"" + declared.Name + "\" has no grid rows.", declared.Span.Line, sourcePath);
            }
            ValidateBorder(declared, sourcePath);
        }

        return document;
    }

    // ------------------------------------------------------------------

    private static PxlSection BeginSection(
        PxlDocument document, string header, int lineNumber, string? sourcePath)
    {
        string name = header.Substring(1, header.Length - 2).Trim();
        if (name.Length == 0 || !IsSectionName(name))
        {
            throw new PxlParseException(
                "Section name \"" + name + "\" must be letters, digits, - or _.", lineNumber, sourcePath);
        }
        if (document.FindSection(name) != null)
        {
            throw new PxlParseException(
                "Section \"" + name + "\" is declared twice.", lineNumber, sourcePath);
        }

        var section = new PxlSection(name, new XuiSourceSpan(lineNumber, 1));
        document.Sections.Add(section);
        return section;
    }

    /// <summary>A file with no <c>[section]</c> header still has one grid; it is named <c>default</c>.</summary>
    private static PxlSection EnsureSection(PxlDocument document, PxlSection? section, int lineNumber)
    {
        if (section != null) return section;
        var created = new PxlSection(DefaultSection, new XuiSourceSpan(lineNumber, 1));
        document.Sections.Add(created);
        return created;
    }

    private static void AddRow(PxlSection section, string row, int lineNumber, string? sourcePath)
    {
        if (section.Rows.Count > 0 && row.Length != section.Rows[0].Length)
        {
            throw new PxlParseException(
                "Row is " + row.Length.ToString(CultureInfo.InvariantCulture) + " characters wide but the grid is "
                + section.Rows[0].Length.ToString(CultureInfo.InvariantCulture)
                + ". Every row must be the same width.",
                lineNumber, sourcePath);
        }
        section.Rows.Add(row);
    }

    private static void ParseCharEntry(PxlDocument document, string entry, int lineNumber, string? sourcePath)
    {
        int colon = entry.IndexOf(':');
        if (colon != 1)
        {
            throw new PxlParseException(
                "Expected a single character, a colon, then a colour (for example \"K: #3a4466\").",
                lineNumber, sourcePath);
        }

        char key = entry[0];
        string value = entry.Substring(colon + 1).Trim();

        if (key == PxlDocument.TransparentChar)
        {
            throw new PxlParseException(
                "\".\" is always transparent and cannot be redefined.", lineNumber, sourcePath);
        }
        if (key == '#' || key == '[' || key == ']' || char.IsWhiteSpace(key))
        {
            throw new PxlParseException(
                "\"" + key + "\" cannot be a grid character; it would be ambiguous with the file's own syntax.",
                lineNumber, sourcePath);
        }
        if (document.Chars.ContainsKey(key) || document.UnresolvedNames.ContainsKey(key))
        {
            throw new PxlParseException(
                "Character \"" + key + "\" is defined twice.", lineNumber, sourcePath);
        }
        if (value.Length == 0)
        {
            throw new PxlParseException(
                "Character \"" + key + "\" has no colour.", lineNumber, sourcePath);
        }

        if (string.Equals(value, "transparent", StringComparison.Ordinal))
        {
            document.Chars[key] = XuiColor.Literal(0f, 0f, 0f, 0f);
            return;
        }

        if (XuiColorParser.TryParseLiteral(value, out XuiColor color))
        {
            document.Chars[key] = color;
            return;
        }

        // A name only resolves against a palette. Recording it rather than failing lets the file parse
        // and lets the caller report exactly which names the palette is missing.
        document.UnresolvedNames[key] = value;
    }

    private static void ParseBorder(PxlSection section, string value, int lineNumber, string? sourcePath)
    {
        string[] parts = value.Split(',');
        if (parts.Length != 4)
        {
            throw new PxlParseException(
                "border must be four numbers: top,right,bottom,left.", lineNumber, sourcePath);
        }

        section.BorderTop = ParseEdge(parts[0], lineNumber, sourcePath);
        section.BorderRight = ParseEdge(parts[1], lineNumber, sourcePath);
        section.BorderBottom = ParseEdge(parts[2], lineNumber, sourcePath);
        section.BorderLeft = ParseEdge(parts[3], lineNumber, sourcePath);
    }

    private static int ParseEdge(string text, int lineNumber, string? sourcePath)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            || value < 0)
        {
            throw new PxlParseException(
                "border edges must be non-negative integers, got \"" + text.Trim() + "\".",
                lineNumber, sourcePath);
        }
        return value;
    }

    /// <summary>
    /// A 9-slice border must leave a middle to stretch. Without this check the sprite imports and then
    /// renders as garbage at any size other than its native one.
    /// </summary>
    private static void ValidateBorder(PxlSection section, string? sourcePath)
    {
        if (!section.HasBorder) return;

        if (section.BorderLeft + section.BorderRight >= section.Width)
        {
            throw new PxlParseException(
                "Section \"" + section.Name + "\": left+right border ("
                + (section.BorderLeft + section.BorderRight).ToString(CultureInfo.InvariantCulture)
                + ") leaves no stretchable middle in a grid "
                + section.Width.ToString(CultureInfo.InvariantCulture) + " wide.",
                section.Span.Line, sourcePath);
        }
        if (section.BorderTop + section.BorderBottom >= section.Height)
        {
            throw new PxlParseException(
                "Section \"" + section.Name + "\": top+bottom border ("
                + (section.BorderTop + section.BorderBottom).ToString(CultureInfo.InvariantCulture)
                + ") leaves no stretchable middle in a grid "
                + section.Height.ToString(CultureInfo.InvariantCulture) + " tall.",
                section.Span.Line, sourcePath);
        }
    }

    private static string ParsePaletteName(string value, int lineNumber, string? sourcePath)
    {
        if (value.Length < 2 || value[0] != '@')
        {
            throw new PxlParseException(
                "palette must be @name, for example \"palette: @ui\".", lineNumber, sourcePath);
        }

        string name = value.Substring(1);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                throw new PxlParseException(
                    "Palette name \"" + name + "\" must be letters, digits, - or _.", lineNumber, sourcePath);
            }
        }
        return name;
    }

    private static bool IsSectionName(string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
        }
        return true;
    }
}
