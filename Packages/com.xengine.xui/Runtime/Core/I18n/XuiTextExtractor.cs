// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.XUI.Core;

/// <summary>One translatable string found in a source file.</summary>
public sealed class XuiExtractedString
{
    public XuiExtractedString(string text, string sourcePath, int line, string origin)
    {
        Text = text;
        SourcePath = sourcePath;
        Line = line;
        Origin = origin;
    }

    public string Text { get; }
    public string SourcePath { get; }
    public int Line { get; }

    /// <summary>What produced it — an element id, an attribute name, or <c>Tr</c>.</summary>
    public string Origin { get; }
}

/// <summary>
/// Finds the strings a player reads, in documents and in C#.
/// </summary>
/// <remarks>
/// <para>
/// The rule is deliberately narrow: <b>inline text</b> on the tags that display it, and the two
/// <c>InputField</c> attributes that appear on screen (<c>placeholder</c>, <c>text</c>). Nothing else.
/// Widening it to "any attribute that looks like prose" is how ids, sprite paths, and theme tokens end
/// up in a translator's queue.
/// </para>
/// <para>
/// <c>translate="false"</c> opts a single element out, for a product name or a string that is really a
/// number. It is on the element rather than a separate file so the decision lives next to the text.
/// </para>
/// </remarks>
public static class XuiTextExtractor
{
    /// <summary>The attributes whose values a player reads.</summary>
    private static readonly string[] TranslatableAttributes = { "placeholder", "text" };

    /// <summary>
    /// Collects every translatable string in a parsed document, in document order.
    /// </summary>
    /// <remarks>
    /// Screens are walked <b>after template expansion</b>. A template body holds
    /// <c>placeholder="{hint}"</c>, which is not a string anyone reads; the string is the
    /// <c>hint="Account"</c> at the usage site, and it only becomes text when the two are combined.
    /// Extracting the unexpanded tree would silently miss every string passed into a template — the
    /// strings most likely to be shared across screens, and so the ones a translator most needs.
    /// </remarks>
    public static List<XuiExtractedString> Extract(XuiDocument document, string sourcePath)
    {
        var found = new List<XuiExtractedString>();
        if (document == null) return found;

        var templates = new XuiTemplateTable();
        templates.Add(document);

        for (int i = 0; i < document.Screens.Count; i++)
        {
            ScreenDef screen = document.Screens[i];
            ElementNode root = screen.Root;
            try
            {
                root = XuiTemplateExpander.Expand(screen.Root, templates, sourcePath);
            }
            catch (XuiParseException)
            {
                // A template this document cannot resolve on its own — an import, most likely. The
                // unexpanded tree still holds the screen's own literal text, and reporting nothing at
                // all because one usage could not be expanded would be the worse trade.
            }

            Walk(root, sourcePath, found, translate: true);
        }

        return found;
    }

    private static void Walk(
        ElementNode node, string sourcePath, List<XuiExtractedString> found, bool translate)
    {
        translate = ReadOptOut(node, translate);

        if (translate)
        {
            XuiTagInfo? tag = XuiTagLibrary.Find(node.Tag);
            if (tag is { AcceptsText: true } && !string.IsNullOrWhiteSpace(node.Text) && !HasPlaceholder(node.Text!))
                found.Add(new XuiExtractedString(node.Text!.Trim(), sourcePath, node.Span.Line, node.Id ?? node.Tag));

            for (int i = 0; i < TranslatableAttributes.Length; i++)
            {
                string name = TranslatableAttributes[i];
                string? value = node.Attribute(name);
                if (!string.IsNullOrWhiteSpace(value) && !HasPlaceholder(value!))
                    found.Add(new XuiExtractedString(value!, sourcePath, node.Span.Line, name));
            }
        }

        for (int i = 0; i < node.Children.Count; i++)
            Walk(node.Children[i], sourcePath, found, translate);
    }

    /// <summary>
    /// <c>translate</c> is inherited: setting it on a container covers everything inside, and a child
    /// may set it back to <c>true</c> for the one label that is prose after all.
    /// </summary>
    private static bool ReadOptOut(ElementNode node, bool inherited)
    {
        string? attribute = node.Attribute("translate");
        return attribute != null && XuiValueParser.TryParseBool(attribute, out bool value) ? value : inherited;
    }

    /// <summary>
    /// A value still holding a <c>{param}</c> belongs to a template body and is not a string in its own
    /// right — the expanded usages are what a player reads, and each of those is extracted separately.
    /// </summary>
    private static bool HasPlaceholder(string text) => text.IndexOf('{') >= 0;

    // ------------------------------------------------------------------
    //  C#
    // ------------------------------------------------------------------

    /// <summary>
    /// Finds <c>Xui.Tr("…")</c> calls in C# source.
    /// </summary>
    /// <remarks>
    /// A scan rather than a parse. The alternative is a Roslyn dependency in a package that is
    /// deliberately pure BCL so it can be linked into the offline Host, to gain accuracy on code shapes
    /// nobody writes — <c>Tr</c> takes a literal, because a runtime-built string cannot be extracted at
    /// all. Interpolated and concatenated arguments are skipped rather than half-read.
    /// </remarks>
    public static List<XuiExtractedString> ExtractFromCSharp(string source, string sourcePath)
    {
        var found = new List<XuiExtractedString>();
        if (string.IsNullOrEmpty(source)) return found;

        const string Marker = "Tr(";
        int index = 0;
        int line = 1;
        int scanned = 0;

        while ((index = source.IndexOf(Marker, index, StringComparison.Ordinal)) >= 0)
        {
            // Only Xui.Tr / Tr, never someone else's method that happens to end in "Tr".
            bool qualified = index >= 4 && string.CompareOrdinal(source, index - 4, "Xui.", 0, 4) == 0;
            bool bare = index == 0 || !(char.IsLetterOrDigit(source[index - 1]) || source[index - 1] == '_' || source[index - 1] == '.');
            if (!qualified && !bare)
            {
                index += Marker.Length;
                continue;
            }

            for (; scanned < index; scanned++)
            {
                if (source[scanned] == '\n') line++;
            }

            int cursor = index + Marker.Length;
            while (cursor < source.Length && char.IsWhiteSpace(source[cursor])) cursor++;
            if (cursor < source.Length && source[cursor] == '"' && !IsVerbatimOrInterpolated(source, cursor))
            {
                if (TryReadLiteral(source, cursor, out string text))
                    found.Add(new XuiExtractedString(text, sourcePath, line, "Tr"));
            }

            index += Marker.Length;
        }

        return found;
    }

    private static bool IsVerbatimOrInterpolated(string source, int quote)
    {
        if (quote == 0) return false;
        char before = source[quote - 1];
        return before == '@' || before == '$';
    }

    private static bool TryReadLiteral(string source, int quote, out string text)
    {
        var builder = new System.Text.StringBuilder();
        for (int i = quote + 1; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '\\')
            {
                i++;
                if (i >= source.Length) break;
                switch (source[i])
                {
                    case 'n': builder.Append('\n'); break;
                    case 't': builder.Append('\t'); break;
                    case 'r': builder.Append('\r'); break;
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    default: builder.Append(source[i]); break;
                }
                continue;
            }
            if (c == '"')
            {
                text = builder.ToString();
                return text.Length > 0;
            }
            if (c == '\n') break;
            builder.Append(c);
        }

        text = string.Empty;
        return false;
    }

    // ------------------------------------------------------------------
    //  Merge
    // ------------------------------------------------------------------

    /// <summary>
    /// Folds a fresh extraction into an existing catalog.
    /// </summary>
    /// <remarks>
    /// Never destructive. Existing translations are kept, new strings are appended untranslated, and a
    /// string that no source mentions any more is marked obsolete rather than deleted — a rename or a
    /// temporarily commented-out screen must not throw away work someone was paid for. Un-obsoleting is
    /// automatic when the string comes back.
    /// </remarks>
    public static XuiMergeReport Merge(PoCatalog catalog, IReadOnlyList<XuiExtractedString> extracted)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(extracted);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int added = 0;
        int revived = 0;

        for (int i = 0; i < extracted.Count; i++)
        {
            XuiExtractedString item = extracted[i];
            string key = XuiCatalog.MakeKey(null, item.Text);
            PoEntry? entry = catalog.Find(null, item.Text);

            if (entry == null)
            {
                entry = new PoEntry(item.Text);
                catalog.Add(entry);
                added++;
            }
            else if (entry.IsObsolete)
            {
                entry.IsObsolete = false;
                revived++;
            }

            if (seen.Add(key)) entry.References.Clear();
            entry.References.Add(new PoReference(item.SourcePath, item.Line));
        }

        int obsoleted = 0;
        for (int i = 0; i < catalog.Entries.Count; i++)
        {
            PoEntry entry = catalog.Entries[i];
            if (seen.Contains(entry.Key) || entry.IsObsolete) continue;
            entry.IsObsolete = true;
            entry.References.Clear();
            obsoleted++;
        }

        int translated = 0;
        for (int i = 0; i < catalog.Entries.Count; i++)
        {
            PoEntry entry = catalog.Entries[i];
            if (!entry.IsObsolete && entry.Translation.Length > 0) translated++;
        }

        return new XuiMergeReport(seen.Count, added, obsoleted, revived, translated);
    }
}

/// <summary>What one extraction run changed.</summary>
public sealed class XuiMergeReport
{
    public XuiMergeReport(int total, int added, int obsoleted, int revived, int translated)
    {
        Total = total;
        Added = added;
        Obsoleted = obsoleted;
        Revived = revived;
        Translated = translated;
    }

    /// <summary>Distinct strings the sources contain now.</summary>
    public int Total { get; }

    public int Added { get; }

    /// <summary>Entries no source mentions any more, kept but marked.</summary>
    public int Obsoleted { get; }

    /// <summary>Previously obsolete entries that came back.</summary>
    public int Revived { get; }

    /// <summary>Live entries that have a translation.</summary>
    public int Translated { get; }
}
