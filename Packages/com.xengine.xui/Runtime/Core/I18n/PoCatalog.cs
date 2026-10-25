// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XEngine.XUI.Core;

/// <summary>Where a translatable string was found. Written into the <c>.po</c> as a <c>#:</c> line.</summary>
public sealed class PoReference
{
    public PoReference(string path, int line)
    {
        Path = path;
        Line = line;
    }

    /// <summary>Project-relative source path.</summary>
    public string Path { get; }

    public int Line { get; }

    public override string ToString()
        => Path + ":" + Line.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One translatable string and its translation.</summary>
public sealed class PoEntry
{
    public PoEntry(string id)
    {
        Id = id;
    }

    /// <summary>The source string — the <c>msgid</c>, and the key <see cref="XuiCatalog"/> looks up.</summary>
    public string Id { get; }

    /// <summary>The translation. Empty means untranslated; the source string is used as-is.</summary>
    public string Translation { get; set; } = string.Empty;

    /// <summary>Disambiguates two identical source strings that need different translations.</summary>
    public string? Context { get; set; }

    /// <summary>
    /// The translation is stale: the source string changed after it was written.
    /// </summary>
    /// <remarks>
    /// A fuzzy entry is still used at runtime. A slightly wrong translation on screen is more useful
    /// than an untranslated string, and it is the state a translator is meant to see and resolve.
    /// </remarks>
    public bool IsFuzzy { get; set; }

    /// <summary>
    /// No source references this string any more. Kept in the file (written as <c>#~</c>) so a
    /// translation is not lost to a temporary edit, and ignored at runtime.
    /// </summary>
    public bool IsObsolete { get; set; }

    /// <summary>Free-text notes a translator wrote. Preserved across extraction.</summary>
    public List<string> TranslatorComments { get; } = new();

    /// <summary>Where the string occurs. Rewritten on every extraction.</summary>
    public List<PoReference> References { get; } = new();

    /// <summary>The key a catalog is indexed by: context and id together.</summary>
    public string Key => XuiCatalog.MakeKey(Context, Id);
}

/// <summary>
/// A gettext <c>.po</c> file: the interchange format between the extractor and a translator.
/// </summary>
/// <remarks>
/// <para>
/// <c>.po</c> rather than a bespoke JSON table because it is what translation tooling already reads —
/// Poedit, Weblate, Crowdin — and because it carries the two things a hand-rolled format always ends
/// up needing anyway: where each string came from, and whether a translation went stale when the
/// source changed.
/// </para>
/// <para>
/// This is a deliberately partial implementation: singular strings, contexts, references, fuzzy and
/// obsolete markers. Plural forms are not supported, and are reported as an error rather than silently
/// dropped — a language with plural rules deserves a real implementation, not a first form quietly
/// standing in for all of them.
/// </para>
/// </remarks>
public sealed class PoCatalog
{
    private readonly List<PoEntry> _entries = new();

    /// <summary>Header fields from the <c>msgid ""</c> entry, in file order.</summary>
    public List<KeyValuePair<string, string>> Header { get; } = new();

    public IReadOnlyList<PoEntry> Entries => _entries;

    public void Add(PoEntry entry)
    {
        if (entry != null) _entries.Add(entry);
    }

    public PoEntry? Find(string? context, string id)
    {
        string key = XuiCatalog.MakeKey(context, id);
        for (int i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Key, key, StringComparison.Ordinal)) return _entries[i];
        }
        return null;
    }

    // ------------------------------------------------------------------
    //  Parsing
    // ------------------------------------------------------------------

    /// <summary>Reads a <c>.po</c> file. Throws <see cref="XuiParseException"/> on malformed input.</summary>
    /// <remarks>
    /// An entry's comments, references, and flags all appear <em>before</em> its <c>msgid</c>, so they
    /// are accumulated as "pending" and claimed by the next entry that opens. Attaching them as they
    /// arrive would give them to the entry that just closed — which is how a fuzzy flag ends up on the
    /// wrong string and a translator is sent to a file that never contained it.
    /// </remarks>
    public static PoCatalog Parse(string text, string sourcePath)
    {
        var catalog = new PoCatalog();
        string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');

        var pendingComments = new List<string>();
        var pendingReferences = new List<PoReference>();
        bool pendingFuzzy = false;

        var comments = new List<string>();
        var references = new List<PoReference>();
        bool fuzzy = false;
        bool obsolete = false;
        string? context = null;
        string? id = null;
        string? translation = null;
        char section = '\0'; // which of the three a bare quoted line continues

        void Emit()
        {
            if (id != null)
            {
                if (id.Length == 0 && context == null)
                {
                    // The entry with an empty msgid is the header, not a translatable string.
                    ParseHeader(catalog, translation ?? string.Empty);
                }
                else
                {
                    var entry = new PoEntry(id)
                    {
                        Translation = translation ?? string.Empty,
                        Context = context,
                        IsFuzzy = fuzzy,
                        IsObsolete = obsolete,
                    };
                    entry.TranslatorComments.AddRange(comments);
                    entry.References.AddRange(references);
                    catalog.Add(entry);
                }
            }

            id = null;
            context = null;
            translation = null;
            fuzzy = false;
            obsolete = false;
            section = '\0';
            comments.Clear();
            references.Clear();
        }

        void Open(bool markObsolete)
        {
            Emit();
            comments.AddRange(pendingComments);
            references.AddRange(pendingReferences);
            fuzzy = pendingFuzzy;
            obsolete = markObsolete;
            pendingComments.Clear();
            pendingReferences.Clear();
            pendingFuzzy = false;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i].Trim();
            int lineNumber = i + 1;
            bool lineObsolete = false;

            if (raw.StartsWith("#~", StringComparison.Ordinal))
            {
                lineObsolete = true;
                raw = raw.Substring(2).Trim();
            }

            if (raw.Length == 0)
            {
                Emit();
                continue;
            }

            if (!lineObsolete && raw[0] == '#')
            {
                if (raw.StartsWith("#:", StringComparison.Ordinal))
                    AddReferences(pendingReferences, raw.Substring(2));
                else if (raw.StartsWith("#,", StringComparison.Ordinal))
                    pendingFuzzy |= raw.IndexOf("fuzzy", StringComparison.Ordinal) >= 0;
                else
                    pendingComments.Add(raw.Length > 2 ? raw.Substring(2).Trim() : string.Empty);
                continue;
            }

            if (raw.StartsWith("msgid_plural", StringComparison.Ordinal)
                || raw.StartsWith("msgstr[", StringComparison.Ordinal))
            {
                throw new XuiParseException(
                    "plural forms are not supported; write one entry per string.",
                    new XuiSourceSpan(lineNumber, 1), sourcePath);
            }

            if (raw.StartsWith("msgctxt", StringComparison.Ordinal))
            {
                Open(lineObsolete);
                section = 'c';
                context = Unquote(raw.Substring(7), lineNumber, sourcePath);
                continue;
            }

            if (raw.StartsWith("msgid", StringComparison.Ordinal))
            {
                // A msgctxt already opened this entry; only a bare msgid starts a new one.
                if (section != 'c') Open(lineObsolete);
                else obsolete |= lineObsolete;
                section = 'i';
                id = Unquote(raw.Substring(5), lineNumber, sourcePath);
                continue;
            }

            if (raw.StartsWith("msgstr", StringComparison.Ordinal))
            {
                if (id == null)
                {
                    throw new XuiParseException(
                        "msgstr without a preceding msgid.",
                        new XuiSourceSpan(lineNumber, 1), sourcePath);
                }
                section = 's';
                translation = Unquote(raw.Substring(6), lineNumber, sourcePath);
                continue;
            }

            if (raw[0] == '"')
            {
                // gettext splits a long string across quoted continuation lines.
                string part = Unquote(raw, lineNumber, sourcePath);
                switch (section)
                {
                    case 'c': context += part; break;
                    case 'i': id += part; break;
                    case 's': translation += part; break;
                    default:
                        throw new XuiParseException(
                            "a quoted continuation line with nothing to continue.",
                            new XuiSourceSpan(lineNumber, 1), sourcePath);
                }
                continue;
            }

            throw new XuiParseException(
                "unrecognised line \"" + raw + "\".",
                new XuiSourceSpan(lineNumber, 1), sourcePath);
        }

        Emit();
        return catalog;
    }

    private static void ParseHeader(PoCatalog catalog, string headerText)
    {
        string[] lines = headerText.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            catalog.Header.Add(new KeyValuePair<string, string>(
                line.Substring(0, colon).Trim(),
                line.Substring(colon + 1).Trim()));
        }
    }

    private static void AddReferences(List<PoReference> into, string text)
    {
        string[] parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            int colon = part.LastIndexOf(':');
            if (colon > 0 && int.TryParse(part.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int line))
                into.Add(new PoReference(part.Substring(0, colon), line));
            else
                into.Add(new PoReference(part, 0));
        }
    }

    private static string Unquote(string text, int line, string sourcePath)
    {
        string trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '"' || trimmed[trimmed.Length - 1] != '"')
        {
            throw new XuiParseException(
                "expected a quoted string, found \"" + trimmed + "\".",
                new XuiSourceSpan(line, 1), sourcePath);
        }

        var builder = new StringBuilder(trimmed.Length - 2);
        for (int i = 1; i < trimmed.Length - 1; i++)
        {
            char c = trimmed[i];
            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            i++;
            if (i >= trimmed.Length - 1) break;
            switch (trimmed[i])
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                default: builder.Append(trimmed[i]); break;
            }
        }
        return builder.ToString();
    }

    // ------------------------------------------------------------------
    //  Writing
    // ------------------------------------------------------------------

    /// <summary>Renders the catalog as <c>.po</c> text, with a trailing newline.</summary>
    public string Write()
    {
        var builder = new StringBuilder();

        builder.Append("msgid \"\"\n").Append("msgstr \"\"\n");
        for (int i = 0; i < Header.Count; i++)
        {
            builder.Append('"').Append(Escape(Header[i].Key)).Append(": ")
                   .Append(Escape(Header[i].Value)).Append("\\n\"\n");
        }

        for (int i = 0; i < _entries.Count; i++)
        {
            PoEntry entry = _entries[i];
            builder.Append('\n');

            for (int c = 0; c < entry.TranslatorComments.Count; c++)
                builder.Append("# ").Append(entry.TranslatorComments[c]).Append('\n');

            if (entry.References.Count > 0)
            {
                builder.Append("#:");
                for (int r = 0; r < entry.References.Count; r++)
                    builder.Append(' ').Append(entry.References[r]);
                builder.Append('\n');
            }

            if (entry.IsFuzzy) builder.Append("#, fuzzy\n");

            string prefix = entry.IsObsolete ? "#~ " : string.Empty;
            if (entry.Context != null)
                builder.Append(prefix).Append("msgctxt \"").Append(Escape(entry.Context)).Append("\"\n");
            builder.Append(prefix).Append("msgid \"").Append(Escape(entry.Id)).Append("\"\n");
            builder.Append(prefix).Append("msgstr \"").Append(Escape(entry.Translation)).Append("\"\n");
        }

        return builder.ToString();
    }

    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                case '\r': builder.Append("\\r"); break;
                default: builder.Append(c); break;
            }
        }
        return builder.ToString();
    }
}
